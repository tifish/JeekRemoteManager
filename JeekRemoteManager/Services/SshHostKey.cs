using System;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Services;

/// <summary>
/// Wires SSH.NET host-key verification against <see cref="KnownHostsStore"/>.
/// SSH.NET trusts every host by default; this enforces trust-on-first-use with
/// later mismatch confirmation. UI-free: the optional <paramref name="onUnknown"/>
/// and <paramref name="onMismatch"/> callbacks let an interactive caller prompt
/// the user. When they are null, a first-seen host is trusted and saved
/// automatically while a changed key is rejected — suitable for non-interactive
/// script runs.
/// </summary>
public static class SshHostKey
{
    /// <param name="onUnknown">(keyType, sha256Fingerprint) =&gt; trust? — prompt for a first-seen host; null = trust-on-first-use.</param>
    /// <param name="onMismatch">(keyType, savedFingerprint, presentedFingerprint) =&gt; replace? — prompt before replacing a remembered host key; null = reject.</param>
    /// <param name="onRejected">Invoked with a human-readable reason when the host is rejected.</param>
    /// <param name="onTrusted">Invoked with the SHA256 fingerprint when a host key is trusted and saved (lets a silent caller surface an audit line).</param>
    public static void Attach(
        BaseClient client,
        string host,
        int port,
        Func<string, string, bool>? onUnknown = null,
        Func<string, string, string, bool>? onMismatch = null,
        Action<string>? onRejected = null,
        Action<string>? onTrusted = null)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = e.FingerPrintSHA256;
            switch (KnownHostsStore.Check(host, port, fingerprint))
            {
                case KnownHostsStore.Status.Match:
                    e.CanTrust = true;
                    return;

                case KnownHostsStore.Status.Mismatch:
                    var saved = KnownHostsStore.TryGet(host, port, out var stored)
                        ? stored
                        : "(unavailable)";
                    var replace = onMismatch?.Invoke(e.HostKeyName ?? "ssh", saved, fingerprint) ?? false;
                    e.CanTrust = replace;
                    if (replace)
                    {
                        KnownHostsStore.Trust(host, port, fingerprint);
                        onTrusted?.Invoke(fingerprint);
                    }
                    else
                    {
                        onRejected?.Invoke(
                            $"host key changed for {host}:{port} — connection rejected (got SHA256:{fingerprint})");
                    }
                    return;

                default:
                    var trusted = onUnknown?.Invoke(e.HostKeyName ?? "ssh", fingerprint) ?? true;
                    e.CanTrust = trusted;
                    if (trusted)
                    {
                        KnownHostsStore.Trust(host, port, fingerprint);
                        onTrusted?.Invoke(fingerprint);
                    }
                    else
                    {
                        onRejected?.Invoke("host key not trusted — connection cancelled");
                    }
                    return;
            }
        };
    }
}
