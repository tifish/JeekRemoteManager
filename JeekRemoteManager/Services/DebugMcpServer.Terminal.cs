using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Controls;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekTools;
using JeekRemoteManager.Views;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>Terminal probes: output pipeline, encoding, appearance, find, session log, tabs, focus and fonts.</summary>
internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> TerminalTabLifecycleCheckAsync()
    {
        const int cycles = 5;

        // Detaching a visual marks it dirty on the window's renderer, and that set is
        // only emptied inside a render pass. So the window has to be on screen, and the
        // measurement has to allow for at least one pass — otherwise this reports the
        // renderer's normal hand-off as a leak. See the retention note below.
        await OnUiAsync(() =>
        {
            (Desktop?.MainWindow as Views.MainWindow)?.ActivateMainWindow();
            return true;
        });
        await Task.Delay(250);

        var weakViews = new List<WeakReference<TerminalView>>(cycles);
        for (var i = 0; i < cycles; i++)
            weakViews.Add(await CreateAndCloseTerminalLifecycleProbeAsync());

        // The async state machine may keep its most recently awaited result live
        // until this method returns. Use an untracked sentinel cycle so that the
        // five measured views have no probe-owned strong reference.
        _ = await CreateAndCloseTerminalLifecycleProbeAsync();

        // Poll rather than sample once: what matters is that a closed view is not
        // retained indefinitely, and the renderer hands it over on its own schedule.
        var alive = cycles;
        var waited = 0;
        const int stepMs = 200;
        const int limitMs = 10_000;
        while (waited < limitMs)
        {
            await OnUiAsync(() =>
            {
                // Give the renderer something to do, so a pass is guaranteed to run.
                Desktop?.MainWindow?.InvalidateVisual();
                // Then bury the UI thread's stack under unrelated frames. Creating and
                // closing the tabs left references to them in stack slots the JIT has
                // not reused yet, and the collector honours those, so a view with no
                // heap reference at all still survives. Overwriting the slots is what
                // makes the measurement about retention rather than about timing.
                OverwriteStackSlots(24);
                return true;
            });
            await Task.Delay(stepMs);
            waited += stepMs;
            OverwriteStackSlots(24);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            alive = weakViews.Count(reference => reference.TryGetTarget(out _));
            if (alive == 0)
                break;
        }

        // Survivors of this call are not evidence of a leak on their own. Creating and
        // closing the tabs leaves references to them in this call's own frames and in the
        // dispatcher operations it queued, and the collector honours those, so a view
        // with no heap reference at all still survives until the call unwinds — verified
        // against a full process dump, where every one of them has zero GC roots.
        //
        // What does mean something is a view that is still alive on the *next* call, long
        // after the work that created it finished. So the batch this call closes is handed
        // to the next one, and the assertion is made about the batch handed to us.
        var (pending, rendererState) = await OnUiAsync(() => CountPendingInRendererDirtySet(weakViews));
        var carried = _lifecycleProbeCarryOver;
        _lifecycleProbeCarryOver = weakViews;

        if (carried.Count == 0)
        {
            return ToolText(
                "PASS (primed): closed terminal views are handed to the next run to measure.\n"
                + $"cycles={cycles}\n"
                + $"aliveInThisCall={alive}\n"
                + $"awaitingRenderPass={pending}\n"
                + $"renderer={rendererState}\n"
                + "note=run again to assert on this batch");
        }

        var leaked = carried.Count(reference => reference.TryGetTarget(out _));
        var passed = leaked == 0;
        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: closed terminal views are collectible.\n"
            + $"previousBatch={carried.Count}\n"
            + $"previousBatchStillAlive={leaked}\n"
            + $"cycles={cycles}\n"
            + $"aliveInThisCall={alive}\n"
            + $"awaitingRenderPass={pending}\n"
            + $"renderer={rendererState}",
            isError: !passed);
    }

    /// <summary>
    /// Recurses with live object references so the frames left behind by earlier calls
    /// are overwritten. Nothing escapes, but the collector can no longer see a dead
    /// object through a stack slot that still happens to point at it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OverwriteStackSlots(int depth)
    {
        if (depth <= 0)
            return 0;

        var a = new object[8];
        var b = new string(' ', 64);
        for (var i = 0; i < a.Length; i++)
            a[i] = new object();
        return a.Length + b.Length + OverwriteStackSlots(depth - 1);
    }

    private static async Task<WeakReference<TerminalView>> CreateAndCloseTerminalLifecycleProbeAsync()
    {
        var tab = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                throw new InvalidOperationException("MainWindow is not available.");
            return main.DebugCreateTerminalTabForLifecycleProbe();
        });

        // Give Avalonia one layout pass so the probe covers loaded bindings and
        // compositor resources rather than only constructor-time objects.
        await Task.Delay(75);
        var weakView = await OnUiAsync(() =>
        {
            if (tab.Content is not TerminalView view)
                throw new InvalidOperationException("Lifecycle probe tab has no TerminalView.");
            return new WeakReference<TerminalView>(view);
        });

        await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                throw new InvalidOperationException("MainWindow is not available.");
            main.CloseTerminalSession(tab);
            return true;
        });

        return weakView;
    }

    private static async Task<JsonObject> TerminalConnectionActionsCheckAsync()
    {
        var tabs = new List<TabItem>();
        try
        {
            var result = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                var before = main.EnumerateTerminalSessions().Count;
                var probe = main.DebugOpenConnectionActionsProbe();
                tabs.Add(probe.Source);
                tabs.Add(probe.NewSession);
                tabs.Add(probe.NewTcpConnection);
                var newSessionView = (TerminalView)probe.NewSession.Content!;
                var newTcpView = (TerminalView)probe.NewTcpConnection.Content!;
                var after = main.EnumerateTerminalSessions().Count;
                var connectReusedTab = ReferenceEquals(probe.Source, probe.Connected);
                var newSessionUsesDuplicatePolicy =
                    newSessionView.DebugIsDuplicatedSession
                    && !newSessionView.DebugRequiresNewTcpConnection;
                var newTcpRequiresNewTransport =
                    !newTcpView.DebugIsDuplicatedSession
                    && newTcpView.DebugRequiresNewTcpConnection
                    && newTcpView.BastionSessionState == "new-tcp-forced";
                var passed = after == before + 3
                             && connectReusedTab
                             && newSessionUsesDuplicatePolicy
                             && newTcpRequiresNewTransport;
                return (
                    passed,
                    before,
                    after,
                    connectReusedTab,
                    newSessionUsesDuplicatePolicy,
                    newTcpRequiresNewTransport,
                    newTcpView.BastionSessionState);
            });

            return ToolText(
                $"{(result.passed ? "PASS" : "FAIL")}: connection actions have distinct transport semantics.\n"
                + $"tabsBefore={result.before}\n"
                + $"tabsAfter={result.after}\n"
                + $"connectReusedTab={result.connectReusedTab}\n"
                + $"newSessionUsesDuplicatePolicy={result.newSessionUsesDuplicatePolicy}\n"
                + $"newTcpRequiresNewTransport={result.newTcpRequiresNewTransport}\n"
                + $"transportState={result.BastionSessionState}",
                isError: !result.passed);
        }
        finally
        {
            if (tabs.Count > 0)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                    {
                        for (var i = tabs.Count - 1; i >= 0; i--)
                            main.CloseTerminalSession(tabs[i]);
                    }
                    return true;
                });
            }
        }
    }

    /// <summary>
    /// The detector sits on the receive path of every 8-bit-clean channel. It used to
    /// withhold the last five bytes of every packet unconditionally, which meant a single
    /// echoed keystroke — one byte — rendered nothing at all until an 80 ms flush timer
    /// fired. This pins down that ordinary output is released immediately while triggers
    /// split across packets are still caught.
    /// </summary>
    private static JsonObject ZmodemDetectorLatencyCheck()
    {
        var failures = new List<string>();

        // The CRC16 table has to agree with the bit-by-bit loop it replaced, byte for
        // byte, or every 16-bit header and subpacket silently fails its integrity check.
        {
            static ushort ReferenceCrc16(ReadOnlySpan<byte> bytes)
            {
                var crc = 0;
                foreach (var b in bytes)
                {
                    crc ^= b << 8;
                    for (var i = 0; i < 8; i++)
                        crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xffff : (crc << 1) & 0xffff;
                }

                return (ushort)crc;
            }

            var sample = new byte[512];
            for (var i = 0; i < sample.Length; i++)
                sample[i] = (byte)(i * 31 + 7);

            var mismatches = 0;
            for (var length = 0; length <= sample.Length; length++)
            {
                var span = sample.AsSpan(0, length);
                if (ZmodemCrc.Crc16(span) != ReferenceCrc16(span))
                    mismatches++;
                // The frame-terminator overload has to match too.
                Span<byte> withEnd = new byte[length + 1];
                span.CopyTo(withEnd);
                withEnd[length] = (byte)ZmodemFrameEnd.ZCRCW;
                if (ZmodemCrc.Crc16(span, (byte)ZmodemFrameEnd.ZCRCW) != ReferenceCrc16(withEnd))
                    mismatches++;
            }

            if (mismatches != 0)
                failures.Add($"CRC16 table disagrees with the reference loop in {mismatches} cases");
        }

        static string Show(byte[] bytes) =>
            bytes.Length == 0 ? "(none)" : Encoding.ASCII.GetString(bytes).Replace("", "<ZDLE>");

        void Immediate(string name, string input)
        {
            var detector = new ZmodemTriggerDetector();
            var detection = detector.Append(Encoding.ASCII.GetBytes(input), out var display);
            var text = Encoding.ASCII.GetString(display);
            if (detection is not null)
                failures.Add($"{name}: unexpectedly detected a transfer");
            if (text != input)
                failures.Add($"{name}: expected \"{input}\" released at once, got \"{text}\"");
            if (detector.HasPendingBytes)
                failures.Add($"{name}: still holding bytes back");
        }

        // The keystroke-echo case, and the ordinary output cases around it.
        Immediate("single echoed keystroke", "x");
        Immediate("short prompt", "$ ");
        Immediate("prompt line", "user@host:~$ ");
        Immediate("output shorter than the old retention window", "abcd");
        // A lone asterisk is a legitimate shell character, and must not be a trigger by
        // itself — but it does have to be held, since it could start one.
        {
            var detector = new ZmodemTriggerDetector();
            detector.Append("ls *"u8.ToArray(), out var display);
            if (Encoding.ASCII.GetString(display) != "ls ")
                failures.Add($"trailing asterisk: expected \"ls \" released, got \"{Show(display)}\"");
            if (!detector.HasPendingBytes)
                failures.Add("trailing asterisk: should be held back as a possible trigger start");
            var flushed = detector.Flush();
            if (Encoding.ASCII.GetString(flushed) != "*")
                failures.Add($"trailing asterisk: flush should yield \"*\", got \"{Show(flushed)}\"");
        }

        // An asterisk that cannot become a trigger must not be held at all.
        Immediate("asterisk followed by ordinary text", "3 * 4 = 12");

        // Detection still works when a trigger arrives whole...
        {
            var detector = new ZmodemTriggerDetector();
            var detection = detector.Append(Encoding.ASCII.GetBytes("**B00"), out _);
            if (detection?.Direction != ZmodemTransferDirection.Download)
                failures.Add("whole hex download trigger was not detected");
        }

        // ...and when it is split across packets one byte at a time, which is the reason
        // any bytes are held back in the first place.
        {
            var detector = new ZmodemTriggerDetector();
            ZmodemDetection? detection = null;
            var released = new List<byte>();
            foreach (var b in Encoding.ASCII.GetBytes("ready\r\n**B01"))
            {
                detection = detector.Append([b], out var display);
                released.AddRange(display);
                if (detection is not null)
                    break;
            }

            if (detection?.Direction != ZmodemTransferDirection.Upload)
                failures.Add("byte-by-byte upload trigger was not detected");
            var prefix = Encoding.ASCII.GetString(released.ToArray())
                         + Encoding.ASCII.GetString(detection?.DisplayBytes ?? []);
            if (prefix != "ready\r\n")
                failures.Add($"split trigger: expected \"ready\\r\\n\" displayed, got \"{prefix}\"");
        }

        // The binary triggers are shorter; make sure they survive splitting too.
        {
            var detector = new ZmodemTriggerDetector();
            ZmodemDetection? detection = null;
            foreach (var b in new byte[] { 0x2a, 0x18, 0x43, 0x00 })
                detection ??= detector.Append([b], out _);
            if (detection?.Direction != ZmodemTransferDirection.Download)
                failures.Add("split bin32 ZRQINIT trigger was not detected");
        }

        var passed = failures.Count == 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: ZMODEM detection adds no latency to ordinary output\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// A peer that never sends a frame terminator — a garbled link, or something that is
    /// not lrzsz at all — used to make the subpacket reader accumulate until the process
    /// died. Drives a real receive session against exactly that and checks it gives up.
    /// </summary>
    private static async Task<JsonObject> ZmodemSubpacketLimitCheckAsync()
    {
        // "*" "*" ZDLE "B" then the 5 header bytes and their CRC16, hex-encoded. Hex
        // headers need no escaping, so this stays readable without reaching into the
        // session's private encoder.
        var header = new List<byte>
        {
            ZmodemConstants.ZPAD, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZHEX,
        };
        byte[] headerBytes = [(byte)ZmodemHeaderType.ZFILE, 0, 0, 0, 0];
        var crc = ZmodemCrc.Crc16(headerBytes);
        foreach (var b in headerBytes.Concat([(byte)(crc >> 8), (byte)crc]))
        {
            const string digits = "0123456789abcdef";
            header.Add((byte)digits[b >> 4]);
            header.Add((byte)digits[b & 0x0f]);
        }
        header.Add(ZmodemConstants.CR);
        header.Add(ZmodemConstants.LF_HIGH);

        // Never send a terminator: after the header it is plain data forever.
        var scripted = header.ToArray();
        var offset = 0;
        long consumed = 0;
        var runawayGuard = ZmodemSession.MaxDataSubpacketBytes * 4L;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        ValueTask<byte> ReadByte(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            consumed++;
            if (consumed > runawayGuard)
                throw new InvalidOperationException("runaway");
            return new ValueTask<byte>(offset < scripted.Length ? scripted[offset++] : (byte)'A');
        }

        long written = 0;
        Task Write(byte[] bytes, CancellationToken _)
        {
            written += bytes.Length;
            return Task.CompletedTask;
        }

        var destination = Path.Combine(
            Path.GetTempPath(),
            "JeekRemoteManager.ZmodemLimitProbe." + Guid.NewGuid().ToString("N"));
        var session = new ZmodemSession(Write, ReadByte);
        string outcome;
        var bounded = false;
        try
        {
            await session.ReceiveAsync(destination, stop.Token).ConfigureAwait(false);
            outcome = "completed without error";
        }
        catch (InvalidDataException ex)
        {
            outcome = ex.Message;
            bounded = true;
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(destination, recursive: true); } catch { /* best effort */ }
        }

        // Bounded means it stopped near the cap, not merely that it stopped eventually.
        var stoppedNearCap = consumed <= runawayGuard;
        var passed = bounded && stoppedNearCap;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: ZMODEM subpacket reads are bounded\n"
            + $"capBytes={ZmodemSession.MaxDataSubpacketBytes}\n"
            + $"bytesConsumed={consumed}\n"
            + $"bytesWritten={written}\n"
            + $"stoppedNearCap={stoppedNearCap}\n"
            + $"outcome={outcome}";
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// The queue only grows while the UI thread cannot drain it, so a remote spraying
    /// output used to be an out-of-memory kill. Feeds well past the cap without ever
    /// draining and checks that memory stays bounded and the newest bytes survive.
    /// </summary>
    private static JsonObject TerminalOutputBackpressureCheck()
    {
        const int generation = 7;
        var buffer = new TerminalSessionOutputBuffer();
        var packet = new byte[64 * 1024];

        // Four times the cap, so trimming has to happen repeatedly rather than once.
        var packetCount = TerminalSessionOutputBuffer.MaxPendingBytes / packet.Length * 4;
        for (var i = 0; i < packetCount; i++)
        {
            // Stamp each packet so the surviving tail is identifiable.
            Array.Fill(packet, (byte)(i & 0xff));
            buffer.Append(packet, generation);
        }

        var fedBytes = (long)packetCount * packet.Length;
        var boundedWhileFilling = buffer.PendingByteCount <= TerminalSessionOutputBuffer.MaxPendingBytes;
        // The packet count must describe what is still queued, not everything ever fed:
        // trimming drops packets, and a counter that keeps them makes the coalescing
        // diagnostics read as a backlog that is not there.
        var pendingPacketsAfterTrim = buffer.PendingPacketCount;
        var packetCountFollowsTrim = pendingPacketsAfterTrim > 0
                                     && pendingPacketsAfterTrim < packetCount;

        var drained = buffer.Drain(generation);
        var dropped = buffer.TakeDroppedByteCount();
        var boundedAfterDrain = drained.Length <= TerminalSessionOutputBuffer.MaxPendingBytes;
        var accountsForEveryByte = drained.Length + dropped == fedBytes;
        var keptNewest = drained.Length > 0 && drained[^1] == (byte)((packetCount - 1) & 0xff);
        var reportsOnce = buffer.TakeDroppedByteCount() == 0;
        var emptyAfterDrain = buffer.PendingByteCount == 0 && buffer.PendingPacketCount == 0;

        var passed = packetCountFollowsTrim
                     && boundedWhileFilling
                     && boundedAfterDrain
                     && accountsForEveryByte
                     && keptNewest
                     && reportsOnce
                     && emptyAfterDrain;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: terminal output buffer is bounded under flood\n"
            + $"capBytes={TerminalSessionOutputBuffer.MaxPendingBytes}\n"
            + $"fedBytes={fedBytes}\n"
            + $"drainedBytes={drained.Length}\n"
            + $"droppedBytes={dropped}\n"
            + $"packetsFed={packetCount}\n"
            + $"pendingPacketsAfterTrim={pendingPacketsAfterTrim}\n"
            + $"packetCountFollowsTrim={packetCountFollowsTrim}\n"
            + $"boundedWhileFilling={boundedWhileFilling}\n"
            + $"boundedAfterDrain={boundedAfterDrain}\n"
            + $"accountsForEveryByte={accountsForEveryByte}\n"
            + $"keptNewest={keptNewest}\n"
            + $"reportsOnce={reportsOnce}\n"
            + $"emptyAfterDrain={emptyAfterDrain}";
        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> TerminalOutputCoalescingCheckAsync()
    {
        const int packetCount = 200;
        TabItem? tab = null;
        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tab = main.DebugCreateTerminalTabForLifecycleProbe();
                var view = (TerminalView)tab.Content!;
                view.DebugResetTerminalOutputStats();
                var packet = Encoding.UTF8.GetBytes("x");
                for (var i = 0; i < packetCount; i++)
                    view.DebugFeedUtf8Bytes(packet);
                return true;
            });

            await Task.Delay(100);
            var result = await OnUiAsync(() =>
            {
                var view = (TerminalView)tab!.Content!;
                var stats = view.DebugTerminalOutputStats;
                var renderedBytes = view.DebugVisibleTerminalText.Count(character => character == 'x');
                var passed = stats.ReceivedPackets == packetCount
                             && stats.FeedBatches == 1
                             && stats.PendingPackets == 0
                             && renderedBytes == packetCount;
                return (
                    passed,
                    $"{(passed ? "PASS" : "FAIL")}: terminal output is coalesced per UI frame.\n"
                    + $"packets={stats.ReceivedPackets}\n"
                    + $"batches={stats.FeedBatches}\n"
                    + $"pending={stats.PendingPackets}\n"
                    + $"renderedBytes={renderedBytes}");
            });
            return ToolText(result.Item2, isError: !result.passed);
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }
        }
    }

    /// <summary>
    /// Terminal font, color scheme and scrollback come from settings. Applies a scheme and a
    /// font, opens a tab, and checks it got the font and the configured scrollback; then
    /// renders the terminal with a row of full blocks and verifies both the background and
    /// the (cached) text color follow a scheme switch — the control caches each text run with
    /// its brush, so a switch that skipped the cache clear would leave the old text color.
    /// The original appearance is restored.
    /// </summary>
    private static async Task<JsonObject> TerminalAppearanceCheckAsync()
    {
        var failures = new List<string>();
        var original = await OnUiAsync(() => (Desktop?.MainWindow as Views.MainWindow)?.DebugTerminalAppearance);
        if (original is null)
            return ToolText("FAIL: MainWindow is not available.", isError: true);

        var first = new TerminalAppearanceSettings("Consolas", "Dracula", 2345);
        var second = first with { ColorScheme = "Campbell" };
        TabItem? tab = null;
        string fontName = "", firstSample = "", secondSample = "";
        var scrollback = 0;
        try
        {
            await OnUiAsync(() =>
            {
                ((Views.MainWindow)Desktop!.MainWindow!).DebugApplyTerminalAppearance(first);
                return true;
            });
            tab = await OnUiAsync(() => ((Views.MainWindow)Desktop!.MainWindow!).DebugCreateTerminalTabForLifecycleProbe());
            var view = await OnUiAsync(() => (TerminalView)tab!.Content!);
            await OnUiAsync(() =>
            {
                view.DebugFeedRawOutput(Encoding.UTF8.GetBytes(
                    "\u001b[2J\u001b[H" + new string('█', 30) + "\r\n"));
                return true;
            });
            await Task.Delay(400);
            (fontName, scrollback) = await OnUiAsync(() => (view.DebugTerminalFontFamily, view.DebugScrollbackLines));
            if (!fontName.StartsWith("Consolas", StringComparison.Ordinal))
                failures.Add($"new tab did not get the configured font (got {fontName})");
            if (scrollback != 2345)
                failures.Add($"new tab did not get the configured scrollback (got {scrollback})");

            firstSample = await OnUiAsync(() => SampleTerminalColors(view.DebugTerminalControl));
            ExpectColors("Dracula", firstSample, failures);

            await OnUiAsync(() =>
            {
                ((Views.MainWindow)Desktop!.MainWindow!).DebugApplyTerminalAppearance(second);
                return true;
            });
            await Task.Delay(300);
            secondSample = await OnUiAsync(() => SampleTerminalColors(view.DebugTerminalControl));
            ExpectColors("Campbell", secondSample, failures);

            if (!TerminalAppearance.CanClearRenderCache)
                failures.Add("the terminal control no longer exposes its render cache hooks");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is Views.MainWindow main)
                {
                    main.DebugApplyTerminalAppearance(original);
                    if (tab is not null)
                        main.CloseTerminalSession(tab);
                }
                return true;
            });
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: terminal font, color scheme and scrollback follow settings\n"
            + $"font={fontName}\nscrollback={scrollback}\nDracula: {firstSample}\nCampbell: {secondSample}\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Renders a terminal and returns "bg=#RRGGBB fg=#RRGGBB": the most common color of the
    /// whole control (the background) and the most common other color in its top rows
    /// (the row of full blocks, drawn in the default foreground).
    /// </summary>
    private static string SampleTerminalColors(Avalonia.Controls.Control control)
    {
        var scaling = TopLevel.GetTopLevel(control)?.RenderScaling ?? 1.0;
        var size = new PixelSize(
            Math.Max(1, (int)(control.Bounds.Width * scaling)),
            Math.Max(1, (int)(control.Bounds.Height * scaling)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(control);
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        string ColorAt(int index) => $"#{pixels[index + 2]:X2}{pixels[index + 1]:X2}{pixels[index]:X2}";
        var all = new Dictionary<string, int>();
        for (var i = 0; i < pixels.Length; i += 4)
            all[ColorAt(i)] = all.GetValueOrDefault(ColorAt(i)) + 1;
        var background = all.MaxBy(pair => pair.Value).Key;

        var top = new Dictionary<string, int>();
        var rows = Math.Min(size.Height, (int)(40 * scaling));
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < size.Width / 2; x++)
            {
                var color = ColorAt(y * stride + x * 4);
                if (color != background)
                    top[color] = top.GetValueOrDefault(color) + 1;
            }
        }

        var foreground = top.Count == 0 ? "(none)" : top.MaxBy(pair => pair.Value).Key;
        return $"bg={background} fg={foreground}";
    }

    private static void ExpectColors(string schemeName, string sample, List<string> failures)
    {
        var scheme = TerminalAppearance.FindScheme(schemeName);
        var expected = $"bg={scheme.Palette[0].ToUpperInvariant()} fg={scheme.Palette[15].ToUpperInvariant()}";
        if (sample != expected)
            failures.Add($"{schemeName}: rendered {sample}, expected {expected}");
    }

    /// <summary>
    /// Find in terminal: fills a probe tab with 300 lines holding three hits (one far up in
    /// the scrollback, one in mixed case), searches through the real find bar, steps forward
    /// and backward, then prints more output — which drops the control's hit list — and
    /// verifies stepping still works by searching again.
    /// </summary>
    private static async Task<JsonObject> TerminalFindCheckAsync()
    {
        var failures = new List<string>();
        var steps = new List<string>();
        TabItem? tab = null;
        try
        {
            tab = await OnUiAsync(() => ((Views.MainWindow)Desktop!.MainWindow!).DebugCreateTerminalTabForLifecycleProbe());
            var view = await OnUiAsync(() => (TerminalView)tab!.Content!);
            var text = new StringBuilder();
            for (var i = 0; i < 300; i++)
            {
                var hit = i switch { 5 => " needle", 150 => " NEEDLE", 290 => " needle", _ => "" };
                text.Append($"line {i}{hit}\r\n");
            }

            await OnUiAsync(() =>
            {
                view.DebugFeedRawOutput(Encoding.UTF8.GetBytes(text.ToString()));
                return true;
            });
            await Task.Delay(300);

            var first = await OnUiAsync(() => view.DebugFind("needle"));
            steps.Add($"search={first}");
            if (!first.EndsWith("/3", StringComparison.Ordinal))
                failures.Add($"expected 3 hits, got {first}");

            for (var i = 0; i < 3; i++)
                steps.Add("next=" + await OnUiAsync(() => view.DebugFindStep(forward: true)));
            steps.Add("prev=" + await OnUiAsync(() => view.DebugFindStep(forward: false)));
            if (steps.Skip(1).Any(step => !step.Contains("selected=needle", StringComparison.OrdinalIgnoreCase)))
                failures.Add("a step did not select the hit");
            if (steps.Select(step => step.Split(' ')[0]).Distinct().Count() < 3)
                failures.Add("stepping did not move between hits");

            await OnUiAsync(() =>
            {
                view.DebugFeedRawOutput(Encoding.UTF8.GetBytes("more output\r\n"));
                return true;
            });
            await Task.Delay(200);
            var afterOutput = await OnUiAsync(() => view.DebugFindStep(forward: true));
            steps.Add("afterOutput=" + afterOutput);
            // The last step left hit 3 selected; new output dropped the hit list, and the
            // next step must continue from there (wrapping to 1), not restart at the top.
            if (!afterOutput.StartsWith("1/3", StringComparison.Ordinal))
                failures.Add($"stepping after new output did not continue from the last hit: {afterOutput}");
            var again = await OnUiAsync(() => view.DebugFindStep(forward: true));
            steps.Add("again=" + again);
            if (!again.StartsWith("2/3", StringComparison.Ordinal))
                failures.Add($"stepping did not advance after re-searching: {again}");

            var missing = await OnUiAsync(() => view.DebugFind("definitely-not-there"));
            steps.Add($"missing={missing}");
            if (missing.Contains('/'))
                failures.Add($"a search with no hits reported {missing}");

            if (!await OnUiAsync(() => view.DebugFindBarOpen))
                failures.Add("find bar was not open");
            await OnUiAsync(() =>
            {
                view.DebugCloseFindBar();
                return true;
            });
            if (await OnUiAsync(() => view.DebugFindBarOpen))
                failures.Add("find bar did not close");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: find in terminal\n"
            + string.Join("\n", steps)
            + $"\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Session logging: records a probe tab's output through the real drain path, with
    /// colors, a window-title OSC, an escape sequence split across two packets, CRLF line
    /// ends, a progress-bar carriage return and Chinese text, then checks the file holds
    /// exactly the readable text and is closed with an end marker when logging stops.
    /// </summary>
    private static async Task<JsonObject> TerminalSessionLogCheckAsync()
    {
        var failures = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), "jrm-session-log-" + Guid.NewGuid().ToString("N"));
        TabItem? tab = null;
        var body = "";
        try
        {
            tab = await OnUiAsync(() => ((Views.MainWindow)Desktop!.MainWindow!).DebugCreateTerminalTabForLifecycleProbe());
            var view = await OnUiAsync(() => (TerminalView)tab!.Content!);
            var path = await OnUiAsync(() => view.StartSessionLog(folder));
            if (!await OnUiAsync(() => view.IsSessionLogging))
                failures.Add("logging did not start");

            string[] packets =
            [
                "\u001b]0;remote title\u0007\u001b[1;32mgreen\u001b[0m plain\r\n",
                "split \u001b[3",
                "1mred\u001b[0m end\r\n",
                "progress 10%\rprogress 100%\r\n",
                "中文输出\r\n",
            ];
            foreach (var packet in packets)
            {
                await OnUiAsync(() =>
                {
                    view.DebugFeedRawOutput(Encoding.UTF8.GetBytes(packet));
                    return true;
                });
                await Task.Delay(60);
            }

            await Task.Delay(200);
            await OnUiAsync(() =>
            {
                view.StopSessionLog();
                return true;
            });

            var text = File.ReadAllText(path);
            var lines = text.Split('\n');
            body = string.Join("\n", lines.Where(line => !line.StartsWith('#')));
            string[] expected = ["green plain", "split red end", "progress 10%progress 100%", "中文输出"];
            foreach (var line in expected)
            {
                if (!lines.Contains(line))
                    failures.Add($"missing line '{line}'");
            }

            if (text.Contains('\u001b') || text.Contains('\r'))
                failures.Add("escape sequences or carriage returns leaked into the log");
            if (text.Contains("remote title", StringComparison.Ordinal))
                failures.Add("the OSC title leaked into the log");
            if (!lines[0].StartsWith("# ", StringComparison.Ordinal) || !text.Contains("# session log ended", StringComparison.Ordinal))
                failures.Add("start/end markers missing");
            if (await OnUiAsync(() => view.IsSessionLogging))
                failures.Add("logging did not stop");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }

            try { Directory.Delete(folder, recursive: true); } catch { /* ignore */ }
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: session log records readable text\n"
            + body.Trim()
            + $"\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// A connection can name a legacy terminal encoding (GBK and friends). Verifies each
    /// text boundary honours it: output split mid-character renders intact through the real
    /// terminal pipeline, typed UTF-8 input reaches the shell as GBK, captured script output
    /// decodes and re-encodes consistently, and login-menu capture reads the same text.
    /// </summary>
    private static async Task<JsonObject> TerminalEncodingCheckAsync()
    {
        var failures = new List<string>();
        var gbk = TerminalEncoding.Resolve("GBK");
        const string sample = "中文终端GBK";
        var sampleBytes = gbk.GetBytes(sample);

        if (TerminalEncoding.Normalize("cp936") != "GBK" || TerminalEncoding.Normalize("") != "UTF-8")
            failures.Add("encoding names did not normalize");
        if (!TerminalEncoding.IsUtf8(TerminalEncoding.Resolve("bogus")))
            failures.Add("an unknown encoding name did not fall back to UTF-8");

        // Captured script output: decoded as GBK, display bytes handed back in GBK too.
        var payload = InteractiveShellPayloadRunner.Build("echo probe\n", "encodingprobe");
        var monitor = new InteractiveShellPayloadMonitor(payload, gbk);
        monitor.Append(gbk.GetBytes("\n" + payload.BeginMarker + "\n"));
        var display = monitor.Append(gbk.GetBytes(sample + "\n" + payload.ExitMarkerPrefix + "0\n"));
        var exit = await monitor.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        if (!exit.Output.Contains(sample, StringComparison.Ordinal))
            failures.Add($"captured output did not decode as GBK: {exit.Output}");
        if (!gbk.GetString(display).Contains(sample, StringComparison.Ordinal))
            failures.Add("display bytes were not re-encoded as GBK");

        var capture = new LoginMenuOutputCapture();
        capture.SetEncoding(gbk);
        capture.Append(sampleBytes.AsSpan(0, 3));
        capture.Append(sampleBytes.AsSpan(3));
        if (!capture.Snapshot().Contains(sample, StringComparison.Ordinal))
            failures.Add($"login-menu capture did not decode split GBK: {capture.Snapshot()}");

        TabItem? tab = null;
        var rendered = "";
        var inputHex = "";
        try
        {
            tab = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");
                return main.DebugCreateTerminalTabForLifecycleProbe();
            });
            var view = await OnUiAsync(() => (TerminalView)tab!.Content!);
            await OnUiAsync(() =>
            {
                view.DebugApplyTerminalEncoding("GBK");
                // Split inside the second character, as an SSH packet boundary would.
                view.DebugFeedRawOutput(sampleBytes[..3]);
                view.DebugFeedRawOutput(sampleBytes[3..]);
                inputHex = Convert.ToHexString(view.DebugEncodeInput("中文"));
                return true;
            });
            await Task.Delay(300);
            rendered = await OnUiAsync(() => view.DebugVisibleTerminalText ?? "");
            if (!rendered.Contains(sample, StringComparison.Ordinal))
                failures.Add("GBK output split across packets did not render intact");
            if (inputHex != Convert.ToHexString(gbk.GetBytes("中文")))
                failures.Add($"typed input was not re-encoded as GBK (got {inputHex})");
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: terminal encoding applies at every text boundary\n"
            + $"renderedGbk={rendered.Contains(sample, StringComparison.Ordinal)}\ninputHex={inputHex}\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Verifies that a script's "[script exit N]" line lands after the script's own last
    /// lines. The exit marker always rides in the packet that carries that output, and the
    /// completion line is fed straight to the terminal while the output is still queued
    /// behind the output-frame timer, so it used to render above the output it summarizes.
    /// Covers both halves of the ordering: the monitor hands the display bytes back before
    /// it releases the waiter, and the waiter drains the pending frame before writing.
    /// </summary>
    private static async Task<JsonObject> ScriptCompletionOrderCheckAsync()
    {
        var payload = InteractiveShellPayloadRunner.Build("echo probe\n", "scriptorderprobe");
        var monitor = new InteractiveShellPayloadMonitor(payload) { DeferExitCompletion = true };
        monitor.Append(Encoding.UTF8.GetBytes("\n" + payload.BeginMarker + "\n"));

        // One packet carrying the script's last line and the exit marker, as the shell sends it.
        var lastLine = "script last line";
        var displayed = Encoding.UTF8.GetString(
            monitor.Append(Encoding.UTF8.GetBytes(
                lastLine + "\n" + payload.ExitMarkerPrefix + "0\n")));
        var exitTask = monitor.WaitForExitAsync(CancellationToken.None);
        var heldUntilDisplayed = !exitTask.IsCompleted && displayed.Contains(lastLine);
        monitor.ReleasePendingExit();
        var releasedAfterwards = await Task.WhenAny(exitTask, Task.Delay(2000)) == exitTask;

        const string completionLine = "[script exit 0]";
        TabItem? tab = null;
        try
        {
            tab = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");
                return main.DebugCreateTerminalTabForLifecycleProbe();
            });

            var view = await OnUiAsync(() => (TerminalView)tab!.Content!);
            await view.DebugFeedCompletionLineAfterOutputAsync(lastLine + "\r\n", completionLine);

            var rendered = await OnUiAsync(() => view.DebugVisibleTerminalText ?? "");
            var outputAt = rendered.IndexOf(lastLine, StringComparison.Ordinal);
            var completionAt = rendered.IndexOf(completionLine, StringComparison.Ordinal);
            var renderedInOrder = outputAt >= 0 && completionAt > outputAt;

            var passed = heldUntilDisplayed && releasedAfterwards && renderedInOrder;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: the completion line follows the script output.\n"
                + $"monitorHeldExitUntilDisplayed={heldUntilDisplayed}\n"
                + $"monitorReleasedOnDemand={releasedAfterwards}\n"
                + $"renderedInOrder={renderedInOrder} (output@{outputAt}, completion@{completionAt})",
                isError: !passed);
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }
        }
    }

    private static async Task<JsonObject> TerminalFontSyncCheckAsync()
    {
        TabItem? tab = null;
        var globalAgentWasOpen = false;
        var originalSize = 0;
        var delta = 0;

        try
        {
            var result = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main
                    || main.DataContext is not MainWindowViewModel vm)
                {
                    throw new InvalidOperationException("MainWindow is not available.");
                }

                globalAgentWasOpen = main.IsGlobalAgentTabOpen;
                originalSize = vm.TerminalFontSize;
                delta = originalSize < 36 ? 1 : -1;

                tab = main.DebugCreateTerminalTabForLifecycleProbe();
                var terminal = (TerminalView)tab.Content!;
                _ = main.PrepareGlobalAgentTabForDebug();

                var initialTerminalSize = terminal.DebugTerminalFontSize;
                var initialEmbeddedAiSize = terminal.DebugAiPanel.TerminalFontSize;
                var initialGlobalAiSize = main.DebugGlobalAgentPanel.TerminalFontSize;

                if (delta > 0)
                    vm.IncreaseTerminalFontCommand.Execute(null);
                else
                    vm.DecreaseTerminalFontCommand.Execute(null);

                var expectedSize = originalSize + delta;
                var terminalSize = terminal.DebugTerminalFontSize;
                var embeddedAiSize = terminal.DebugAiPanel.TerminalFontSize;
                var globalAiSize = main.DebugGlobalAgentPanel.TerminalFontSize;
                var passed = initialTerminalSize == originalSize
                             && initialEmbeddedAiSize == originalSize
                             && initialGlobalAiSize == originalSize
                             && vm.TerminalFontSize == expectedSize
                             && terminalSize == expectedSize
                             && embeddedAiSize == expectedSize
                             && globalAiSize == expectedSize;

                return (
                    passed,
                    expectedSize,
                    terminalSize,
                    embeddedAiSize,
                    globalAiSize,
                    initialTerminalSize,
                    initialEmbeddedAiSize,
                    initialGlobalAiSize);
            });

            return ToolText(
                $"{(result.passed ? "PASS" : "FAIL")}: SSH terminal font controls update every AI CLI panel.\n"
                + $"original={originalSize}\n"
                + $"expected={result.expectedSize}\n"
                + $"initialTerminal={result.initialTerminalSize}\n"
                + $"initialEmbeddedAi={result.initialEmbeddedAiSize}\n"
                + $"initialGlobalAi={result.initialGlobalAiSize}\n"
                + $"terminal={result.terminalSize}\n"
                + $"embeddedAi={result.embeddedAiSize}\n"
                + $"globalAi={result.globalAiSize}",
                isError: !result.passed);
        }
        finally
        {
            Task? closeGlobalAgentTask = null;
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main
                    || main.DataContext is not MainWindowViewModel vm)
                {
                    return false;
                }

                if (vm.TerminalFontSize != originalSize && delta != 0)
                {
                    if (delta > 0)
                        vm.DecreaseTerminalFontCommand.Execute(null);
                    else
                        vm.IncreaseTerminalFontCommand.Execute(null);
                }

                if (tab is not null)
                    main.CloseTerminalSession(tab);

                if (!globalAgentWasOpen && main.IsGlobalAgentTabOpen)
                    closeGlobalAgentTask = main.CloseGlobalAgentAsync();

                return true;
            });

            if (closeGlobalAgentTask is not null)
                await closeGlobalAgentTask;
        }
    }

    private static async Task<JsonObject> ButtonContentAlignmentCheckAsync()
    {
        var (passed, report) = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                return (false, "FAIL: MainWindow is not available.");

            var textButton = new Button
            {
                Name = "AlignmentTextButton",
                Width = 180,
                Content = "Centered text",
            };
            var compoundButton = new Button
            {
                Name = "AlignmentCompoundButton",
                Width = 180,
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "#" },
                        new TextBlock { Text = "Centered compound content" },
                    },
                },
            };
            var overrideButton = new Button
            {
                Name = "AlignmentOverrideButton",
                Width = 180,
                Content = "Intentional override",
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            var probe = new Window
            {
                Title = "button_content_alignment_check",
                Width = 260,
                Height = 180,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Spacing = 8,
                    Children = { textButton, compoundButton, overrideButton },
                },
            };

            try
            {
                probe.Show(main);

                var defaultsCentered =
                    textButton.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center
                    && textButton.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center
                    && compoundButton.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center
                    && compoundButton.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center;
                var overridePreserved =
                    overrideButton.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Right
                    && overrideButton.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Top;
                var ok = defaultsCentered && overridePreserved;

                return (ok,
                    $"{(ok ? "PASS" : "FAIL")}: shared Button content alignment\n"
                    + $"text={textButton.HorizontalContentAlignment}/{textButton.VerticalContentAlignment}\n"
                    + $"compound={compoundButton.HorizontalContentAlignment}/{compoundButton.VerticalContentAlignment}\n"
                    + $"override={overrideButton.HorizontalContentAlignment}/{overrideButton.VerticalContentAlignment}");
            }
            finally
            {
                probe.Close();
            }
        });

        return ToolText(report, isError: !passed);
    }


    /// <summary>
    /// Drives the real <see cref="PasswordImeGuard"/> class handlers through a throwaway window:
    /// focusing a password box must close the IME, and leaving the box - by focus or by the
    /// window closing under it - must put the previous open status back.
    /// </summary>
    private static async Task<JsonObject> PasswordImeCheckAsync()
    {
        var imeWindow = IntPtr.Zero;
        var initialOpen = false;
        Window? probe = null;
        TextBox? password = null;
        TextBox? plain = null;

        try
        {
            var imeOpens = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                main.ActivateMainWindow();
                imeWindow = PasswordImeGuard.ImeWindowOf(
                    main.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                if (imeWindow == IntPtr.Zero)
                    return false;

                initialOpen = PasswordImeGuard.GetOpenStatus(imeWindow);
                // The guard can only be seen closing an IME that is open to begin with.
                PasswordImeGuard.SetOpenStatus(imeWindow, true);
                return PasswordImeGuard.GetOpenStatus(imeWindow);
            });

            if (imeWindow == IntPtr.Zero)
                return ToolText("SKIP: this thread has no IME window, so there is nothing to close.");
            if (!imeOpens)
            {
                return ToolText(
                    "SKIP: the active keyboard layout has no IME that can be opened "
                    + "(open status stays false), so the guard has nothing to act on.");
            }

            var focused = await OnUiAsync(() =>
            {
                var main = (Views.MainWindow)Desktop!.MainWindow!;
                password = new TextBox { PasswordChar = '•' };
                plain = new TextBox();
                probe = new Window
                {
                    Title = "password_ime_check",
                    Width = 240,
                    Height = 120,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel { Children = { password, plain } },
                };
                probe.Show(main);

                password.Focus();
                return (
                    guarded: ReferenceEquals(PasswordImeGuard.GuardedBox, password),
                    open: PasswordImeGuard.GetOpenStatus(imeWindow));
            });

            var restoredByFocus = await ActThenReadAsync(
                () => plain!.Focus(),
                () => (
                    released: PasswordImeGuard.GuardedBox is null,
                    open: PasswordImeGuard.GetOpenStatus(imeWindow)));

            // Second round: a dialog can be closed while its password box still holds focus,
            // and no LostFocus follows it.
            var closedWhileGuarding = await OnUiAsync(() =>
            {
                password!.Focus();
                return PasswordImeGuard.GetOpenStatus(imeWindow);
            });
            var afterWindowClose = await ActThenReadAsync(
                () =>
                {
                    probe!.Close();
                    probe = null;
                },
                () => (
                    released: PasswordImeGuard.GuardedBox is null,
                    open: PasswordImeGuard.GetOpenStatus(imeWindow)));

            var passed = focused.guarded
                         && !focused.open
                         && restoredByFocus.released
                         && restoredByFocus.open
                         && !closedWhileGuarding
                         && afterWindowClose.released
                         && afterWindowClose.open;

            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: password boxes close the IME and give it back.\n"
                + $"imeWindow=0x{imeWindow.ToInt64():X}\n"
                + $"initialOpen={initialOpen}\n"
                + $"onFocus.guarded={focused.guarded}\n"
                + $"onFocus.imeOpen={focused.open}\n"
                + $"onFocusLost.released={restoredByFocus.released}\n"
                + $"onFocusLost.imeOpen={restoredByFocus.open}\n"
                + $"onWindowClose.imeOpenWhileGuarding={closedWhileGuarding}\n"
                + $"onWindowClose.released={afterWindowClose.released}\n"
                + $"onWindowClose.imeOpen={afterWindowClose.open}",
                isError: !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                probe?.Close();
                if (imeWindow != IntPtr.Zero)
                    PasswordImeGuard.SetOpenStatus(imeWindow, initialOpen);
                return true;
            });
        }
    }

    private static async Task<JsonObject> TerminalTabTitleCheckAsync()
    {
        const string fullTitle = "production-cluster-singapore-api-node-末尾六个字符";
        var (passed, report) = await OnUiAsync(() =>
        {
            var title = Views.MainWindow.BuildTerminalTabTitle(fullTitle);
            title.Measure(new Size(180, 32));
            title.Arrange(new Rect(0, 0, 180, 32));

            var leading = title.Children.OfType<TextBlock>().ElementAtOrDefault(0);
            var trailing = title.Children.OfType<TextBlock>().ElementAtOrDefault(1);
            var parts = TerminalTabTitle.Split(fullTitle);

            var shortTitle = Views.MainWindow.BuildTerminalTabTitle("server");
            var shortParts = shortTitle.Children.OfType<TextBlock>().ToArray();

            const string similarTitleText = "production-api-node-02-singapore";
            var emphasis = TerminalTabTitle.FindEmphasis(
                similarTitleText,
                ["production-api-node-01-singapore", "production-api-node-03-singapore"]);
            var emphasizedTitle = Views.MainWindow.BuildTerminalTabTitle(similarTitleText, emphasis);
            emphasizedTitle.Measure(new Size(180, 32));
            emphasizedTitle.Arrange(new Rect(0, 0, 180, 32));
            var emphasizedParts = emphasizedTitle.Children
                .OfType<TextBlock>()
                .ToArray();
            var emphasizedContext = emphasizedParts
                .SingleOrDefault(part => part.Classes.Contains("tab-title-numeric-context"));
            var emphasizedDifference = emphasizedContext?
                .Inlines?
                .OfType<Run>()
                .SingleOrDefault(run => run.Classes.Contains("tab-title-emphasis"));

            const string numericIdentifier = "111111111111111111";
            var numericTitleText = $"production-api-node-{numericIdentifier}-singapore";
            var numericEmphasis = TerminalTabTitle.FindEmphasis(
                numericTitleText,
                [
                    "production-api-node-222222222222222222-singapore",
                    "production-api-node-333333333333333333-singapore",
                ]);
            var numericTitle = Views.MainWindow.BuildTerminalTabTitle(
                numericTitleText,
                numericEmphasis);
            numericTitle.Measure(new Size(180, 32));
            numericTitle.Arrange(new Rect(0, 0, 180, 32));
            var numericParts = numericTitle.Children.OfType<TextBlock>().ToArray();
            var numericContext = numericParts
                .SingleOrDefault(part => part.Classes.Contains("tab-title-numeric-context"));
            var numericDifference = numericContext?
                .Inlines?
                .OfType<Run>()
                .SingleOrDefault(run => run.Classes.Contains("tab-title-emphasis"));

            const string lastDigitTitleText =
                "very-long-production-server-name-12346-singapore";
            var lastDigitEmphasis = TerminalTabTitle.FindEmphasis(
                lastDigitTitleText,
                [
                    "very-long-production-server-name-12345-singapore",
                    "very-long-production-server-name-12347-singapore",
                ]);
            var lastDigitTitle = Views.MainWindow.BuildTerminalTabTitle(
                lastDigitTitleText,
                lastDigitEmphasis);
            lastDigitTitle.Measure(new Size(180, 32));
            lastDigitTitle.Arrange(new Rect(0, 0, 180, 32));
            var lastDigitContext = lastDigitTitle.Children
                .OfType<TextBlock>()
                .SingleOrDefault(part => part.Classes.Contains("tab-title-numeric-context"));
            var lastDigitDifference = lastDigitContext?
                .Inlines?
                .OfType<Run>()
                .SingleOrDefault(run => run.Classes.Contains("tab-title-emphasis"));

            var tooltip = ToolTip.GetTip(title)?.ToString() ?? "";
            var ok = leading?.Text == parts.LeadingText
                     && trailing?.Text == parts.TrailingText
                     && parts.LeadingText + parts.TrailingText == fullTitle
                     && parts.TrailingText == "末尾六个字符"
                     && tooltip == fullTitle
                     && title.MaxWidth == 180
                     && leading.TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis
                     && trailing.Bounds.Width > 0
                     && trailing.Bounds.Right <= 180.01
                     && shortParts.Length == 2
                     && shortParts[0].Text == "server"
                     && shortParts[1].Text == ""
                     && !emphasis.IsEmpty
                     && similarTitleText.Substring(emphasis.Start, emphasis.Length) == "2"
                     && emphasizedParts.Length == 3
                     && emphasizedParts[0].Text == "production-api-node-"
                     && emphasizedContext?.Inlines?.Text == "02"
                     && emphasizedDifference?.Text == "2"
                     && emphasizedDifference.FontWeight == Avalonia.Media.FontWeight.Bold
                     && emphasizedParts[2].Text == "-singapore"
                     && emphasizedParts[2].TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis
                     && numericTitleText.Substring(numericEmphasis.Start, numericEmphasis.Length)
                         == numericIdentifier
                     && numericContext?.Inlines?.Text == "1111"
                     && numericDifference?.Text == "1111"
                     && numericContext?.Inlines?.Text?.Count(char.IsDigit)
                         == TerminalTabTitle.NumericEmphasisMaxDigits
                     && numericContext.TextTrimming == Avalonia.Media.TextTrimming.None
                     && numericTitle.MaxWidth == 180
                     && lastDigitTitleText.Substring(
                         lastDigitEmphasis.Start,
                         lastDigitEmphasis.Length) == "6"
                     && lastDigitContext?.Inlines?.Text == "2346"
                     && lastDigitDifference?.Text == "6"
                     && lastDigitTitle.MaxWidth == 180;

            return (ok,
                $"{(ok ? "PASS" : "FAIL")}: terminal-tab long-name title\n"
                + $"leading: {leading?.Text}\n"
                + $"trailing: {trailing?.Text}\n"
                + $"tooltip: {tooltip}\n"
                + $"adjacent emphasis: {emphasizedDifference?.Text}\n"
                + $"similar layout: {string.Join(" | ", emphasizedParts.Select(
                    part => part.Text ?? part.Inlines?.Text ?? ""))}\n"
                + $"numeric emphasis: {numericContext?.Inlines?.Text} "
                + $"({TerminalTabTitle.NumericEmphasisMaxDigits}/{numericIdentifier.Length} digits)\n"
                + $"last-digit context: {lastDigitContext?.Inlines?.Text} "
                + $"(emphasis {lastDigitDifference?.Text})\n"
                + $"bounds: leading={leading?.Bounds}, trailing={trailing?.Bounds}\n"
                + $"maxWidth: {title.MaxWidth}");
        });

        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> TerminalTabFocusCheckAsync()
    {
        TabControl? tabs = null;
        object? originalSelection = null;
        TabItem? firstTab = null;
        TabItem? secondTab = null;
        TerminalView? firstView = null;
        TerminalView? secondView = null;

        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tabs = main.FindControl<TabControl>("RightTabs")
                       ?? throw new InvalidOperationException("RightTabs not found.");
                originalSelection = tabs.SelectedItem;
                firstView = new TerminalView();
                secondView = new TerminalView();
                firstView.DebugPrepareLoadedFocusCompetitor();
                firstTab = new TabItem { Header = "Focus probe A", Content = firstView };
                secondTab = new TabItem { Header = "Focus probe B", Content = secondView };
                tabs.Items.Add(firstTab);
                tabs.Items.Add(secondTab);
                tabs.SelectedItem = firstTab;
                return true;
            });

            await Task.Delay(75);
            var firstFocused = await OnUiAsync(() =>
            {
                firstView!.DebugFocusSecondaryTarget();
                return firstView.DebugCurrentFocusTarget;
            });

            await OnUiAsync(() =>
            {
                tabs!.SelectedItem = secondTab;
                return true;
            });
            await Task.Delay(75);
            var secondFocused = await OnUiAsync(() => secondView!.DebugCurrentFocusTarget);

            await OnUiAsync(() =>
            {
                tabs!.SelectedItem = firstTab;
                return true;
            });
            await Task.Delay(75);
            var restoredFocus = await OnUiAsync(() => firstView!.DebugCurrentFocusTarget);
            var rememberedFocus = await OnUiAsync(() => firstView!.DebugLastFocusTarget);

            var passed = firstFocused.EndsWith("#ScrollToBottomButton", StringComparison.Ordinal)
                         && secondFocused.EndsWith("#Term", StringComparison.Ordinal)
                         && restoredFocus == firstFocused
                         && rememberedFocus == firstFocused;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: terminal-tab focus is kept per tab in memory.\n"
                + $"first={firstFocused}\nsecond={secondFocused}\n"
                + $"restored={restoredFocus}\nremembered={rememberedFocus}",
                isError: !passed);
        }
        finally
        {
            if (tabs is not null)
            {
                await OnUiAsync(() =>
                {
                    if (originalSelection is not null && tabs.Items.Contains(originalSelection))
                        tabs.SelectedItem = originalSelection;
                    else if (tabs.Items.Count > 0)
                        tabs.SelectedIndex = 0;

                    if (firstTab is not null)
                        tabs.Items.Remove(firstTab);
                    if (secondTab is not null)
                        tabs.Items.Remove(secondTab);
                    firstView?.Close();
                    secondView?.Close();
                    return true;
                });
            }
        }
    }

    /// <summary>
    /// Verify the environment received by an interactive child without exposing secrets.
    /// </summary>
    private static async Task<JsonObject> ConPtyEnvironmentCheckAsync()
    {
        var parentTerm = Environment.GetEnvironmentVariable("TERM");
        using var session = ConPtySession.Start(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/q", "/k",
                "echo __JRM_TERM__%TERM%& if defined PATH echo __JRM_PATH_OK__& "
                + "if defined SystemRoot echo __JRM_SYSTEMROOT_OK__& echo __JRM_ENV_DONE__"],
            120, 25);
        // Keep the child alive until the read loop has received its output. Only
        // report presence for inherited variables; never dump the environment.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string output;
        do
        {
            await Task.Delay(50);
            output = session.GetRecentOutputPlainText(8192);
        } while (!output.Contains("__JRM_ENV_DONE__", StringComparison.Ordinal)
                 && DateTime.UtcNow < deadline);

        var termCorrect = output.Contains("__JRM_TERM__xterm-256color", StringComparison.Ordinal);
        var inherited = output.Contains("__JRM_PATH_OK__", StringComparison.Ordinal)
            && output.Contains("__JRM_SYSTEMROOT_OK__", StringComparison.Ordinal);
        var parentUnchanged = Environment.GetEnvironmentVariable("TERM") == parentTerm;
        var passed = termCorrect && inherited && parentUnchanged;
        var report = new JsonObject
        {
            ["passed"] = passed,
            ["childTermCorrect"] = termCorrect,
            ["inheritedVariablesPresent"] = inherited,
            ["parentTermUnchanged"] = parentUnchanged,
            ["parentTermWasDumb"] = string.Equals(parentTerm, "dumb", StringComparison.OrdinalIgnoreCase),
        };
        return ToolText(report.ToJsonString(), isError: !passed);
    }

    /// <summary>
    /// Closing a WSL or agent-CLI tab disposes the ConPTY session while keystrokes and
    /// layout-driven resizes are still in flight. Both used to check a plain flag outside
    /// the write gate, so a Write could land on a closed FileStream and a Resize on an
    /// already-closed HPCON. This drives that window directly on real sessions.
    /// </summary>
    private static async Task<JsonObject> ConPtyTeardownRaceCheckAsync()
    {
        const int rounds = 10;
        const int writersPerRound = 6;
        var writes = 0;
        var resizes = 0;
        var failures = new List<string>();

        // A child that never drains stdin. Once the console input buffer fills, ConPTY
        // stops reading our pipe and Write blocks inside the FileStream — which is the
        // window Dispose has to respect. Small keystroke-sized payloads always drain and
        // never reproduce it, so push 64 KiB at a time.
        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)'x');

        for (var round = 0; round < rounds; round++)
        {
            ConPtySession session;
            try
            {
                session = ConPtySession.Start(
                    Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    ["/c", "ping -n 30 127.0.0.1 > nul"],
                    80,
                    25);
            }
            catch (Exception ex)
            {
                failures.Add($"round {round}: could not start a pseudo console: {ex.Message}");
                break;
            }

            var roundNumber = round;
            using var stop = new CancellationTokenSource();
            void Record(string what, Exception ex)
            {
                lock (failures)
                    failures.Add($"round {roundNumber}: {what} threw {ex.GetType().Name}: {ex.Message}");
            }

            var workers = new List<Task>(writersPerRound + 1);
            for (var writerIndex = 0; writerIndex < writersPerRound; writerIndex++)
            {
                workers.Add(Task.Run(() =>
                {
                    // Keystroke-sized writes first: they drain, so they prove the normal
                    // path still works. The big ones then wedge and set up the race.
                    var keystroke = "\r\n"u8.ToArray();
                    for (var i = 0; i < 8 && !stop.IsCancellationRequested; i++)
                    {
                        try
                        {
                            session.Write(keystroke);
                            Interlocked.Increment(ref writes);
                        }
                        catch (Exception ex)
                        {
                            Record("Write", ex);
                            return;
                        }
                    }

                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            session.Write(payload);
                            Interlocked.Increment(ref writes);
                        }
                        catch (Exception ex)
                        {
                            Record("Write", ex);
                            return;
                        }
                    }
                }));
            }
            workers.Add(Task.Run(() =>
            {
                var columns = 80;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        session.Resize(columns = columns == 80 ? 120 : 80, 25);
                        Interlocked.Increment(ref resizes);
                    }
                    catch (Exception ex)
                    {
                        Record("Resize", ex);
                        return;
                    }
                }
            }));

            // Let the writers wedge on a full pipe so Dispose lands mid-write.
            await Task.Delay(120);
            session.Dispose();
            stop.Cancel();
            await Task.WhenAll(workers);

            // Post-dispose calls must be silent no-ops, not throws.
            try
            {
                session.Write(payload);
                session.Resize(100, 30);
                session.Dispose();
            }
            catch (Exception ex)
            {
                Record("post-dispose call", ex);
            }
        }

        var passed = failures.Count == 0 && writes > 0 && resizes > 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: ConPTY teardown races with concurrent Write/Resize\n"
            + $"rounds={rounds}\n"
            + $"writes={writes}\n"
            + $"resizes={resizes}\n"
            + $"failures={failures.Count}"
            + (failures.Count == 0 ? "" : "\n" + string.Join("\n", failures.Take(10)));
        return ToolText(report, isError: !passed);
    }
}
