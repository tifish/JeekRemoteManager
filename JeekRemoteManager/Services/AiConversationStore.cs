using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekRemoteManager.Services;

/// <summary>One persisted transcript entry. Provider-native session ids are kept alongside
/// the UI transcript so the backend context and the visible conversation can be restored
/// together.</summary>
public sealed class AiConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ScopeId { get; set; } = "";
    public string ConnectionLabel { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? NativeSessionId { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? DeletedAt { get; set; }
    public List<AiConversationMessage> Messages { get; set; } = new();
}

public sealed class AiConversationMessage
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsAwaitingDecision { get; set; }
    public string? DecisionText { get; set; }
}

/// <summary>Small immutable row used by the history picker and Debug MCP.</summary>
public sealed record AiConversationSummary(
    string Id,
    string Title,
    string Provider,
    string? Model,
    string ConnectionLabel,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    bool CanRestore,
    DateTimeOffset? DeletedAt);

/// <summary>Machine-local JSON store for AI conversation metadata and transcripts. Native
/// CLI session files are machine-local too, so keeping the mapping under LocalAppData avoids
/// roaming unusable session ids to another computer.</summary>
public sealed class AiConversationStore
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();

    public static string DefaultRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "AiConversations");

    public AiConversationStore(string? rootPath = null) =>
        RootPath = rootPath ?? DefaultRootPath;

    public string RootPath { get; }

    public IReadOnlyList<AiConversationSummary> LoadSummaries(string scopeId, bool deleted = false)
    {
        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            if (!Directory.Exists(RootPath))
                return Array.Empty<AiConversationSummary>();

            PurgeExpiredTrashLocked(DateTimeOffset.UtcNow);
            var result = new List<AiConversationSummary>();
            foreach (var path in Directory.EnumerateFiles(RootPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                var conversation = TryLoadPath(path);
                if (conversation is null
                    || !string.Equals(conversation.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase)
                    || (conversation.DeletedAt is not null) != deleted)
                {
                    continue;
                }

                result.Add(new AiConversationSummary(
                    conversation.Id,
                    conversation.Title,
                    conversation.Provider,
                    conversation.Model,
                    conversation.ConnectionLabel,
                    conversation.UpdatedAt,
                    conversation.Messages.Count,
                    !string.IsNullOrWhiteSpace(conversation.NativeSessionId),
                    conversation.DeletedAt));
            }

            return deleted
                ? result.OrderByDescending(item => item.DeletedAt).ToArray()
                : result.OrderByDescending(item => item.UpdatedAt).ToArray();
        }
    }

    /// <summary>Moves path/endpoint-based history records to a connection's stable GUID scope.</summary>
    public int MigrateScopes(
        string scopeId,
        IEnumerable<string>? legacyScopeIds,
        string? connectionLabel = null)
    {
        var legacyScopes = (legacyScopeIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, scopeId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (legacyScopes.Count == 0)
            return 0;

        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            if (!Directory.Exists(RootPath))
                return 0;

            var migrated = 0;
            foreach (var path in Directory.EnumerateFiles(RootPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                var conversation = TryLoadPath(path);
                if (conversation is null || !legacyScopes.Contains(conversation.ScopeId))
                    continue;

                conversation.ScopeId = scopeId;
                if (!string.IsNullOrWhiteSpace(connectionLabel))
                    conversation.ConnectionLabel = connectionLabel;
                SaveLocked(conversation);
                migrated++;
            }

            return migrated;
        }
    }

    public AiConversation? Load(string id)
    {
        if (!IsValidId(id))
            return null;

        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            return TryLoadPath(PathFor(id));
        }
    }

    public void Save(AiConversation conversation)
    {
        if (!IsValidId(conversation.Id))
            throw new ArgumentException("Conversation id must contain only letters, digits, '-' or '_'.", nameof(conversation));

        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            SaveLocked(conversation);
        }
    }

    /// <summary>Soft-deletes a conversation. The record remains restorable for 30 days.</summary>
    public bool Delete(string id)
        => MoveToTrash(id);

    public bool MoveToTrash(string id)
        => UpdateDeletedAt(id, DateTimeOffset.UtcNow, requireDeleted: false);

    public bool RestoreFromTrash(string id)
        => UpdateDeletedAt(id, deletedAt: null, requireDeleted: true);

    public bool DeletePermanently(string id)
    {
        if (!IsValidId(id))
            return false;

        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            var path = PathFor(id);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>Removes recycle-bin records whose 30-day retention window has elapsed.</summary>
    public int PurgeExpiredTrash(DateTimeOffset? now = null)
    {
        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            return PurgeExpiredTrashLocked(now ?? DateTimeOffset.UtcNow);
        }
    }

    private bool UpdateDeletedAt(string id, DateTimeOffset? deletedAt, bool requireDeleted)
    {
        if (!IsValidId(id))
            return false;

        lock (Gate)
        {
            using var lease = SharedDataFile.Acquire(RootPath);
            var conversation = TryLoadPath(PathFor(id));
            if (conversation is null || (conversation.DeletedAt is not null) != requireDeleted)
                return false;

            conversation.DeletedAt = deletedAt;
            SaveLocked(conversation);
            return true;
        }
    }

    private int PurgeExpiredTrashLocked(DateTimeOffset now)
    {
        if (!Directory.Exists(RootPath))
            return 0;

        var cutoff = now - TrashRetention;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(RootPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var conversation = TryLoadPath(path);
            if (conversation?.DeletedAt is not { } deletedAt || deletedAt > cutoff)
                continue;

            File.Delete(path);
            deleted++;
        }

        return deleted;
    }

    private void SaveLocked(AiConversation conversation)
    {
        Directory.CreateDirectory(RootPath);
        SharedDataFile.WriteAllTextAtomic(
            PathFor(conversation.Id),
            JsonSerializer.Serialize(conversation, JsonOptions));
    }

    private AiConversation? TryLoadPath(string path)
    {
        try
        {
            var conversation = JsonSerializer.Deserialize<AiConversation>(File.ReadAllText(path), JsonOptions);
            if (conversation is null || !IsValidId(conversation.Id))
                return null;
            conversation.Messages ??= new List<AiConversationMessage>();
            return conversation;
        }
        catch
        {
            // A corrupt or partially copied history entry must not prevent the panel opening.
            return null;
        }
    }

    private string PathFor(string id) => Path.Combine(RootPath, id + ".json");

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
