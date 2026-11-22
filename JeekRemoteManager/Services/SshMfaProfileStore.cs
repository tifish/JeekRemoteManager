using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;
using JeekTools;

namespace JeekRemoteManager.Services;

/// <summary>Shared encrypted TOTP profiles in the active configuration directory.</summary>
internal sealed class SshMfaProfileStore(string configRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string ProfileDirectory => Path.Combine(configRoot, "SshMfaProfiles");

    internal sealed record Profile(Guid Id, string Name, string EncryptedCredential,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SshMfaInput? Input = null);

    public IReadOnlyList<SshMfaProfileChoice> List() => Read()
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(profile => new SshMfaProfileChoice(profile.Id, profile.Name))
        .ToArray();

    public string? TryGetEncryptedCredential(Guid id) => TryGetProfile(id)?.EncryptedCredential;

    internal Profile? TryGetProfile(Guid id)
    {
        try
        {
            return ReadProfile(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public Guid? Save(Guid? id, string name, string? encryptedCredential, SshMfaInput? input = null)
    {
        using var lease = AcquireWriteLock();
        var profiles = Read();
        var index = profiles.FindIndex(profile => profile.Id == id);
        if ((id.HasValue && index < 0)
            || profiles.Any(profile => profile.Id != id && profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var credential = encryptedCredential ?? (index >= 0 ? profiles[index].EncryptedCredential : "");
        if (string.IsNullOrWhiteSpace(name) || !MasterKeyService.IsPasswordBlob(credential))
            return null;

        var profile = new Profile(id ?? Guid.NewGuid(), name, credential,
            input ?? (index >= 0 ? profiles[index].Input : null));
        Write(profile);
        return profile.Id;
    }

    internal IReadOnlyList<Guid> AddBatch(IReadOnlyList<(string Name, string EncryptedCredential)> accounts,
        SshMfaInput? input = null)
    {
        if (accounts.Any(account => string.IsNullOrWhiteSpace(account.Name)
            || !MasterKeyService.IsPasswordBlob(account.EncryptedCredential)))
        {
            throw new ArgumentException("Each imported account needs a name and an encrypted credential.", nameof(accounts));
        }

        using var lease = AcquireWriteLock();
        var names = Read().Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Profile>(accounts.Count);
        foreach (var account in accounts)
        {
            var name = account.Name;
            var suffix = 2;
            while (!names.Add(name))
                name = $"{account.Name} ({suffix++})";
            pending.Add(new Profile(Guid.NewGuid(), name, account.EncryptedCredential, input));
        }

        var written = 0;
        try
        {
            for (; written < pending.Count; written++)
                Write(pending[written]);
        }
        catch
        {
            foreach (var profile in pending.Take(written))
            {
                try { File.Delete(ProfilePath(profile.Id)); } catch { /* best-effort rollback */ }
            }
            throw;
        }

        return pending.Select(profile => profile.Id).ToArray();
    }

    public void Remove(Guid id)
    {
        using var lease = AcquireWriteLock();
        if (Directory.Exists(ProfileDirectory))
            File.Delete(ProfilePath(id));
    }

    internal IDisposable AcquireWriteLock() => SharedDataFile.Acquire(configRoot);

    internal List<Profile> Read()
    {
        try
        {
            return Directory.EnumerateFiles(ProfileDirectory, "*.json").Select(path =>
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id))
                    throw new JsonException("Invalid SSH MFA profile filename.");
                return ReadProfile(id);
            }).OfType<Profile>().ToList();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    private Profile? ReadProfile(Guid id)
    {
        Profile? profile;
        try
        {
            using var stream = new FileStream(ProfilePath(id), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            profile = JsonSerializer.Deserialize<Profile>(stream, JsonOptions);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        if (profile is null || id == Guid.Empty || profile.Id != id
            || string.IsNullOrWhiteSpace(profile.Name) || !MasterKeyService.IsPasswordBlob(profile.EncryptedCredential))
        {
            throw new JsonException("Invalid SSH MFA profile.");
        }
        return profile;
    }

    // The caller holds the config directory lock, including during master password changes.
    internal void Write(Profile profile) =>
        SharedDataFile.WriteAllTextAtomic(ProfilePath(profile.Id), JsonSerializer.Serialize(profile, JsonOptions));

    private string ProfilePath(Guid id) => Path.Combine(ProfileDirectory, id.ToString("D") + ".json");
}
