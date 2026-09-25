using System;
using System.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Services;

/// <summary>
/// Wires SSH.NET host-key verification against <see cref="KnownHostsStore"/>.
/// First-seen keys are trusted and saved automatically. A remembered key that
/// changes is accepted only when <paramref name="onMismatch"/> confirms replacing it.
/// </summary>
/// <remarks>
/// A server usually holds several host keys (ed25519, ecdsa, rsa) and presents whichever
/// family the client ranks first. If that ranking changes — an SSH.NET upgrade, a server
/// that dropped an algorithm — the server shows a different, perfectly legitimate key and
/// the fingerprint no longer matches. So, like OpenSSH, the family remembered for a host is
/// moved to the front of the offer. The mismatch check itself stays family-agnostic: a
/// different family is never trusted silently, since a spoofer chooses what to present.
/// </remarks>
public static class SshHostKey
{
    /// <param name="onMismatch">(keyType, savedFingerprint, presentedFingerprint) =&gt; replace? — prompt before replacing a remembered host key; null = reject.</param>
    /// <param name="onRejected">Invoked with a human-readable reason when the host is rejected.</param>
    /// <param name="onTrusted">Invoked with the SHA256 fingerprint when a host key is trusted and saved (lets a silent caller surface an audit line).</param>
    public static void Attach(
        BaseClient client,
        string host,
        int port,
        Func<string, string, string, bool>? onMismatch = null,
        Action<string>? onRejected = null,
        Action<string>? onTrusted = null)
    {
        PreferRememberedKeyType(client.ConnectionInfo, host, port);
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = Evaluate(
                host,
                port,
                e.HostKeyName ?? "ssh",
                e.FingerPrintSHA256,
                onMismatch,
                onRejected,
                onTrusted);
        };
    }

    /// <summary>
    /// Moves every host-key algorithm of the family remembered for this host to the front of
    /// the client's offer, keeping their relative order. No-op for unknown hosts.
    /// </summary>
    internal static void PreferRememberedKeyType(ConnectionInfo info, string host, int port)
    {
        if (!KnownHostsStore.TryGetKeyType(host, port, out var family))
            return;

        var algorithms = info.HostKeyAlgorithms;
        var preferred = algorithms
            .Where(pair => KnownHostsStore.KeyFamily(pair.Key) == family)
            .ToList();
        for (var i = preferred.Count - 1; i >= 0; i--)
        {
            algorithms.Remove(preferred[i].Key);
            algorithms.Insert(0, preferred[i].Key, preferred[i].Value);
        }
    }

    internal static bool Evaluate(
        string host,
        int port,
        string keyType,
        string fingerprint,
        Func<string, string, string, bool>? onMismatch = null,
        Action<string>? onRejected = null,
        Action<string>? onTrusted = null)
    {
        switch (KnownHostsStore.Check(host, port, keyType, fingerprint))
        {
            case KnownHostsStore.Status.Match:
                return true;

            case KnownHostsStore.Status.Mismatch:
                var saved = KnownHostsStore.TryGet(host, port, out var stored)
                    ? stored
                    : "(unavailable)";
                if (onMismatch?.Invoke(keyType, saved, fingerprint) == true)
                {
                    KnownHostsStore.Trust(host, port, fingerprint, keyType);
                    onTrusted?.Invoke(fingerprint);
                    return true;
                }

                onRejected?.Invoke(
                    $"host key changed for {host}:{port} — connection rejected (got SHA256:{fingerprint})");
                return false;

            default:
                KnownHostsStore.Trust(host, port, fingerprint, keyType);
                onTrusted?.Invoke(fingerprint);
                return true;
        }
    }
}
