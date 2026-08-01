using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;
using JeekTools;

namespace JeekRemoteManager.Services;

/// <summary>
/// Persists four fixed login-command fragments once per automatically detected
/// endpoint/credential identity. Templates contain commands and a safe endpoint
/// label only; passwords and encrypted blobs never enter this file.
/// </summary>
public sealed class BastionLoginProfileStore
{
    private const string FileName = "bastion-login-templates.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private Dictionary<string, BastionLoginProfile> _profiles = new(StringComparer.Ordinal);

    public BastionLoginProfileStore(string connectionsRoot) => SetRoot(connectionsRoot);

    public string FilePath { get; private set; } = "";

    public IReadOnlyList<BastionLoginProfile> Profiles
    {
        get
        {
            lock (_gate)
                return _profiles.Values.Select(Clone).OrderBy(p => p.EndpointLabel).ToArray();
        }
    }

    public void SetRoot(string connectionsRoot)
    {
        var configRoot = Directory.GetParent(Path.GetFullPath(connectionsRoot))?.FullName
                         ?? Path.GetFullPath(connectionsRoot);
        FilePath = Path.Combine(configRoot, FileName);
        Reload();
    }

    public void Reload()
    {
        Dictionary<string, BastionLoginProfile> loaded = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath))
            {
                var model = JsonSerializer.Deserialize<TemplateFile>(
                    File.ReadAllText(FilePath),
                    JsonOptions);
                foreach (var profile in model?.Templates ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(profile.Id))
                    {
                        profile.NormalizeSegments();
                        loaded[profile.Id] = profile;
                    }
                }
            }
        }
        catch
        {
            // A malformed external edit must not destroy connection files. Keep
            // profiles unavailable until the file is corrected.
        }

        lock (_gate)
            _profiles = loaded;
    }

    public string AutomaticProfileId(Connection connection) =>
        BastionSessionPool.PoolKeyForDebug(connection);

    public BastionLoginProfile? Get(string id)
    {
        lock (_gate)
            return _profiles.TryGetValue(id, out var profile) ? Clone(profile) : null;
    }

    public BastionLoginProfile? FindAutomatic(Connection connection) =>
        Get(AutomaticProfileId(connection));

    public void Resolve(Connection connection)
    {
        if (!connection.IsSsh)
        {
            connection.ResolvedBastionProfile = null;
            return;
        }

        var id = AutomaticProfileId(connection);

        lock (_gate)
        {
            connection.ResolvedBastionProfile =
                _profiles.TryGetValue(id, out var profile)
                    ? profile
                    : new BastionLoginProfile
                    {
                        Id = id,
                        EndpointLabel = EndpointLabel(connection),
                    };
        }
    }

    public void Save(BastionLoginProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            throw new InvalidOperationException("A bastion login template must have an id.");
        profile.NormalizeSegments();
        for (var id = 1; id <= BastionLoginProfile.SegmentCount; id++)
        {
            profile.Segments[id - 1] =
                LoginCommandSequence.TrimSurroundingBlankLines(profile.GetSegment(id));
            if (LoginCommandSequence.ContainsTemplateDirective(profile.GetSegment(id)))
            {
                throw new InvalidOperationException(
                    $"Template fragment {id} cannot contain #template.");
            }
        }

        TemplateFile snapshot;
        lock (_gate)
        {
            if (_profiles.TryGetValue(profile.Id, out var current))
            {
                current.EndpointLabel = profile.EndpointLabel;
                current.Segments = (string[])profile.Segments.Clone();
            }
            else
            {
                _profiles[profile.Id] = Clone(profile);
            }
            snapshot = new TemplateFile
            {
                Templates = _profiles.Values
                    .OrderBy(item => item.EndpointLabel, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(Clone)
                    .ToList(),
            };
        }

        var configRoot = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(configRoot);
        using var lease = SharedDataFile.Acquire(configRoot);
        SharedDataFile.WriteAllTextAtomic(
            FilePath,
            JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private static string EndpointLabel(Connection connection)
    {
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        var user = connection.Username.Trim();
        return $"{(user.Length == 0 ? "" : user + "@")}{host}:{port}";
    }

    private static BastionLoginProfile Clone(BastionLoginProfile profile) =>
        new()
        {
            Id = profile.Id,
            EndpointLabel = profile.EndpointLabel,
            Segments = (string[])profile.Segments.Clone(),
        };

    private sealed class TemplateFile
    {
        public List<BastionLoginProfile> Templates { get; set; } = [];
    }

}
