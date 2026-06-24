using System;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Services;

/// <summary>
/// Wires SSH.NET host-key verification against <see cref="KnownHostsStore"/>.
/// SSH.NET trusts every host by default; this enforces trust-on-first-use with
/// later mismatch rejection. UI-free: the optional <paramref name="onUnknown"/>
/// callback lets an interactive caller prompt the user for a first-seen host
/// (returning whether to trust). When it is null, a first-seen host is trusted
/// and saved automatically — suitable for non-interactive script runs.
/// </summary>
public static class SshHostKey
{
    /// <param name="onUnknown">(keyType, sha256Fingerprint) =&gt; trust? — prompt for a first-seen host; null = trust-on-first-use.</param>
    /// <param name="onRejected">Invoked with a human-readable reason when the host is rejected.</param>
    /// <param name="onTrusted">Invoked with the SHA256 fingerprint when a first-seen host is trusted and saved (lets a silent caller surface an audit line).</param>
    public static void Attach(
        BaseClient client,
        string host,
        int port,
        Func<string, string, bool>? onUnknown = null,
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
                    e.CanTrust = false;
                    onRejected?.Invoke(
                        $"host key changed for {host}:{port} — connection rejected (got SHA256:{fingerprint})");
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
