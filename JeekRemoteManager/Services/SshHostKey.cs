using System;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Services;

/// <summary>
/// Wires SSH.NET host-key verification against <see cref="KnownHostsStore"/>.
/// First-seen keys are trusted and saved automatically. A remembered key that
/// changes is accepted only when <paramref name="onMismatch"/> confirms replacing it.
/// </summary>
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

    internal static bool Evaluate(
        string host,
        int port,
        string keyType,
        string fingerprint,
        Func<string, string, string, bool>? onMismatch = null,
        Action<string>? onRejected = null,
        Action<string>? onTrusted = null)
    {
        switch (KnownHostsStore.Check(host, port, fingerprint))
        {
            case KnownHostsStore.Status.Match:
                return true;

            case KnownHostsStore.Status.Mismatch:
                var saved = KnownHostsStore.TryGet(host, port, out var stored)
                    ? stored
                    : "(unavailable)";
                if (onMismatch?.Invoke(keyType, saved, fingerprint) == true)
                {
                    KnownHostsStore.Trust(host, port, fingerprint);
                    onTrusted?.Invoke(fingerprint);
                    return true;
                }

                onRejected?.Invoke(
                    $"host key changed for {host}:{port} — connection rejected (got SHA256:{fingerprint})");
                return false;

            default:
                KnownHostsStore.Trust(host, port, fingerprint);
                onTrusted?.Invoke(fingerprint);
                return true;
        }
    }
}
