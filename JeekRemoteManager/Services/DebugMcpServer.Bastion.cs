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

/// <summary>Bastion and login-command probes.</summary>
internal static partial class DebugMcpServer
{
    /// <summary>
    /// Verifies that server monitor sampling follows tab visibility: it runs for the
    /// visible tab, does NOT stop the moment the tab goes to the background (a grace
    /// period absorbs tab flipping), suspends once that period elapses, and resumes
    /// immediately when the tab comes back.
    /// </summary>
    private static async Task<JsonObject> MonitorSuspendCheckAsync()
    {
        TabItem? tab = null;
        try
        {
            var report = new StringBuilder();
            var passed = true;

            void Expect(bool condition, string label)
            {
                passed &= condition;
                report.Append(condition ? "  ok   " : "  FAIL ").Append(label).Append('\n');
            }

            var (view, main) = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow window)
                    throw new InvalidOperationException("MainWindow is not available.");

                tab = window.DebugCreateSshTerminalTabForMonitorProbe();
                var terminal = (TerminalView)tab.Content!;
                terminal.ToggleMonitorPanel();
                return (terminal, window);
            });

            var active = await OnUiAsync(() =>
                (view.IsMonitorPanelOpen, view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(active.IsMonitorPanelOpen, "monitor panel opened on the SSH probe tab");
            Expect(!active.IsMonitorSamplingSuspended, "sampling runs while the tab is visible");
            Expect(!active.IsMonitorSuspendPending, "no suspend is pending while the tab is visible");

            // Move to another tab: sampling must keep going for now.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(main.DebugEditorTab);
                return true;
            });
            await Task.Delay(100);
            var backgrounded = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(!backgrounded.IsMonitorSamplingSuspended,
                "sampling is NOT stopped the instant the tab goes to the background");
            Expect(backgrounded.IsMonitorSuspendPending, "a suspend is pending during the grace period");

            // Flipping back within the grace period must cancel the pending suspend.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(tab!);
                return true;
            });
            var flippedBack = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(!flippedBack.IsMonitorSamplingSuspended, "returning to the tab keeps sampling running");
            Expect(!flippedBack.IsMonitorSuspendPending, "returning to the tab cancels the pending suspend");

            // Background it again and let the grace period elapse.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(main.DebugEditorTab);
                return true;
            });
            await OnUiAsync(() =>
            {
                view.FlushPendingMonitorSuspend();
                return true;
            });
            var suspended = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(suspended.IsMonitorSamplingSuspended, "sampling is suspended once the grace period elapses");
            Expect(!suspended.IsMonitorSuspendPending, "no suspend stays pending after it fired");

            // Coming back resumes immediately.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(tab!);
                return true;
            });
            var resumed = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(!resumed.IsMonitorSamplingSuspended, "sampling resumes when the tab is shown again");
            Expect(!resumed.IsMonitorSuspendPending, "no suspend is pending after resuming");

            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: server monitor sampling follows tab visibility.\n{report}",
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

    /// <summary>
    /// End-to-end probe for the "#select &lt;name&gt;" login directive without a bastion:
    /// "open" adds a terminal tab attached to a local cmd.exe shell whose login commands
    /// print a numbered menu and then select an entry by name, "status" returns the
    /// scrollback (the typed number is visible in it), and "close" removes the tab.
    /// </summary>
    private static async Task<JsonObject> LoginMenuSelectProbeAsync(JsonObject args)
    {
        var action = args["action"]?.GetValue<string>() ?? "status";
        switch (action)
        {
            case "open":
                {
                    var view = await OnUiAsync(() =>
                    {
                        if (Desktop?.MainWindow is not Views.MainWindow main)
                            throw new InvalidOperationException("MainWindow is not available.");
                        if (_menuProbeView is not null)
                            return null;

                        var tabs = main.FindControl<TabControl>("RightTabs")
                                   ?? throw new InvalidOperationException("RightTabs not found.");
                        _menuProbeView = new TerminalView();
                        _menuProbeTab = new TabItem { Header = "Login menu probe", Content = _menuProbeView };
                        tabs.Items.Add(_menuProbeTab);
                        tabs.SelectedItem = _menuProbeTab;
                        return _menuProbeView;
                    });
                    if (view is null)
                        return ToolText("already open");

                    var shell = BuildMenuProbeShell(args["scenario"]?.GetValue<string>() ?? "single");
                    var loginCommands = args["login_commands"]?.GetValue<string>() is { Length: > 0 } custom
                        ? custom
                        : shell.LoginCommands;
                    // Let the tab lay out so the terminal has a real size before the PTY starts.
                    await Task.Delay(200);
                    await OnUiAsync(() => view.DebugStartLocalShellAsync(
                        new Models.Connection { Name = "login menu probe", LoginCommands = loginCommands },
                        shell.ExePath,
                        shell.Arguments,
                        shell.LoginPhases));
                    return ToolText("opened");
                }

            case "close":
                return ToolText(await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is not Views.MainWindow main || _menuProbeView is null)
                        return "not open";
                    var tabs = main.FindControl<TabControl>("RightTabs");
                    if (tabs is not null && _menuProbeTab is not null)
                        tabs.Items.Remove(_menuProbeTab);
                    _menuProbeView.Close();
                    _menuProbeView = null;
                    _menuProbeTab = null;
                    return "closed";
                }));

            default:
                {
                    return ToolText(await OnUiAsync(() =>
                        _menuProbeView is null
                            ? "not open"
                            : $"state={_menuProbeView.LoginSequenceState}\n--- visible ---\n"
                              + _menuProbeView.DebugVisibleTerminalText));
                }
        }
    }

    private static Task<JsonObject> LoginMenuSelectCheckAsync(JsonObject args)
    {
        var menu = args["menu"]?.GetValue<string>() ?? "";
        var name = args["name"]?.GetValue<string>() ?? "";
        var keyword = LoginCommandSequence.TryGetMenuSelectKeyword(name) ?? name;

        var sb = new StringBuilder();
        sb.AppendLine("entries:");
        foreach (var entry in LoginMenuSelection.ParseEntries(menu))
            sb.AppendLine($"  {entry.Choice} -> {entry.Label}");

        var result = LoginMenuSelection.Resolve(menu, keyword);
        sb.AppendLine(result.Success
            ? $"match: {keyword} -> types \"{result.Choice}\" ({result.MatchedLabel})"
            : $"no match: {result.Failure}");
        return Task.FromResult(ToolText(sb.ToString().TrimEnd()));
    }

    /// <summary>
    /// Spins up a real <see cref="LoginCommandsTextBox"/> on a temporary tab and checks
    /// that # prefix filtering, popup presentation, and accept-to-insert work.
    /// </summary>
    private static async Task<JsonObject> LoginCommandCompletionCheckAsync()
    {
        TabControl? tabs = null;
        object? originalSelection = null;
        TabItem? probeTab = null;
        LoginCommandsTextBox? editor = null;

        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tabs = main.FindControl<TabControl>("RightTabs")
                       ?? throw new InvalidOperationException("RightTabs not found.");
                originalSelection = tabs.SelectedItem;
                editor = new LoginCommandsTextBox
                {
                    Name = "LoginCommandCompletionProbeEditor",
                    AcceptsReturn = true,
                    MinHeight = 120,
                    Margin = new Thickness(24),
                };
                probeTab = new TabItem
                {
                    Header = "Login completion probe",
                    Content = editor,
                };
                tabs.Items.Add(probeTab);
                tabs.SelectedItem = probeTab;
                return true;
            });

            await Task.Delay(75);
            await OnUiAsync(() =>
            {
                var box = editor
                          ?? throw new InvalidOperationException("Completion probe editor missing.");
                box.Focus();
                box.Text = "#re";
                box.CaretIndex = box.Text.Length;
                return true;
            });
            // TextChanged defers open to Input priority so the caret is final first.
            await Task.Delay(100);

            var reuse = await OnUiAsync(() =>
            {
                var box = editor!;
                var snapshot = (
                    hasTemplate: box.Template is not null,
                    open: box.IsDirectiveCompletionOpen,
                    overlay: box.IsDirectiveCompletionUsingOverlayLayer,
                    background: box.HasDirectiveCompletionBackground,
                    bounds: box.DirectiveCompletionBounds,
                    renderedItems: box.DirectiveCompletionRenderedItemCount,
                    renderedItemDetails: box.DirectiveCompletionRenderedItems,
                    items: box.DirectiveCompletionItems);
                var accepted = box.AcceptDirectiveCompletion();
                return (
                    snapshot.hasTemplate,
                    snapshot.open,
                    closedAfterAccept: !box.IsDirectiveCompletionOpen,
                    snapshot.overlay,
                    snapshot.background,
                    snapshot.bounds,
                    snapshot.renderedItems,
                    snapshot.renderedItemDetails,
                    snapshot.items,
                    accepted,
                    text: box.Text);
            });

            await OnUiAsync(() =>
            {
                editor!.Text = "  #P";
                editor.CaretIndex = editor.Text.Length;
                return true;
            });
            await Task.Delay(100);

            var pageKey = await OnUiAsync(() =>
            {
                var openBefore = editor!.IsDirectiveCompletionOpen;
                var items = editor.DirectiveCompletionItems;
                var accepted = editor.AcceptDirectiveCompletion();
                return (
                    open: openBefore,
                    closedAfterAccept: !editor.IsDirectiveCompletionOpen,
                    items: items,
                    accepted: accepted,
                    text: editor.Text);
            });

            // Caret-only moves must not open the popup on an existing # line.
            await OnUiAsync(() =>
            {
                var box = editor!;
                box.Text = "#input\nother";
                box.CaretIndex = (box.Text ?? "").Length;
                return true;
            });
            await Task.Delay(100);
            var caretOnly = await OnUiAsync(() =>
            {
                var box = editor!;
                // Move onto the #input token without typing.
                box.CaretIndex = 3;
                return box.IsDirectiveCompletionOpen;
            });

            var passed = reuse.hasTemplate
                         && reuse.open
                         && reuse.closedAfterAccept
                         && reuse.overlay
                         && reuse.background
                         && reuse.bounds.Width > 0
                         && reuse.bounds.Height > 0
                         && reuse.renderedItems == reuse.items.Length
                         && reuse.items.SequenceEqual(["#reuse-enter", "#reuse-leave"])
                         && reuse.accepted
                         && reuse.text == "#reuse-enter"
                         && pageKey.open
                         && pageKey.closedAfterAccept
                         && pageKey.items.SequenceEqual(["#pagekey <key>"])
                         && pageKey.accepted
                         && pageKey.text == "  #pagekey "
                         && !caretOnly;

            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: login-command # marker completion\n"
                + $"reuse: template={reuse.hasTemplate} open={reuse.open} closedAfter={reuse.closedAfterAccept} "
                + $"overlay={reuse.overlay} background={reuse.background} bounds={reuse.bounds} "
                + $"renderedItems={reuse.renderedItems} [{reuse.renderedItemDetails}] "
                + $"items=[{string.Join(", ", reuse.items)}] accepted={reuse.accepted} text=\"{reuse.text}\"\n"
                + $"pagekey: open={pageKey.open} closedAfter={pageKey.closedAfterAccept} "
                + $"items=[{string.Join(", ", pageKey.items)}] "
                + $"accepted={pageKey.accepted} text=\"{pageKey.text}\"\n"
                + $"caretOnlyOpen={caretOnly}",
                isError: !passed);
        }
        finally
        {
            if (tabs is not null)
            {
                await OnUiAsync(() =>
                {
                    if (editor is not null)
                        editor.Text = "";

                    if (originalSelection is not null && tabs.Items.Contains(originalSelection))
                        tabs.SelectedItem = originalSelection;
                    else if (tabs.Items.Count > 0)
                        tabs.SelectedIndex = 0;

                    if (probeTab is not null)
                        tabs.Items.Remove(probeTab);
                    return true;
                });
            }
        }
    }

    private static Task<JsonObject> LoginCommandFlowCheckAsync(JsonObject args)
    {
        var commands = args["login_commands"]?.GetValue<string>()
                       ?? "#input\n#reuse-enter\n5\n#key Enter\n#duplicate\n#reuse-leave\nexit";
        var key = args["key"]?.GetValue<string>() ?? "Enter";
        var sb = new StringBuilder();
        sb.AppendLine($"structured={LoginCommandSequence.HasStructuredReuseWorkflow(commands)}");
        sb.AppendLine(LoginCommandSequence.BuildPreview(commands));
        var validation = LoginCommandSequence.Validate(commands);
        sb.AppendLine(validation.Count == 0
            ? "validation: ok"
            : "validation:\n  " + string.Join("\n  ", validation));
        if (LoginKeySequence.TryParse(key, out var sequence, out var error))
        {
            sb.AppendLine(
                $"key {key}: {Convert.ToHexString(Encoding.UTF8.GetBytes(sequence))}");
        }
        else
        {
            sb.AppendLine($"key {key}: error: {error}");
        }

        return Task.FromResult(ToolText(sb.ToString().TrimEnd()));
    }

    private static Task<JsonObject> BastionLoginTemplateCheckAsync()
    {
        var probeRoot = Path.Combine(
            DebugInstanceContext.Info.RuntimeTempRoot,
            "bastion-template-probe-" + Guid.NewGuid().ToString("N"));
        var connectionsRoot = Path.Combine(probeRoot, "Connections");
        try
        {
            var store = new ConnectionStore(connectionsRoot);
            var a = new Models.Connection
            {
                Name = "target-a",
                Host = "bastion.example.test",
                Port = 22,
                Username = "probe",
                LoginCommands =
                    "#template 1\n#reuse-enter\n#select {{name}}\n#duplicate\n#template 2\n#reuse-leave\n#template 4",
            };
            var b = new Models.Connection
            {
                Name = "target-b",
                Host = "BASTION.EXAMPLE.TEST.",
                Port = 22,
                Username = "probe",
                LoginCommands =
                    "#template 1\n#reuse-enter\n#select {{name}}\n#duplicate\n#template 2\n#reuse-leave\n#template 4",
            };
            var aPath = store.Save(a, connectionsRoot);
            var bPath = store.Save(b, connectionsRoot);
            var loadedA = store.Load(aPath);
            var loadedB = store.Load(bPath);
            var automaticTemplateId = loadedA.ResolvedBastionProfile!.Id;
            var defaultAssociation =
                store.BastionProfiles.Profiles.Count == 0
                && loadedA.TryResolveLoginCommands(out _, out _)
                && loadedB.TryResolveLoginCommands(out _, out _)
                && automaticTemplateId == loadedB.ResolvedBastionProfile!.Id;
            var editorA = ConnectionEditorViewModel.FromConnection(
                loadedA,
                store.BastionProfiles);
            editorA.LoginCommands = "\r\n " + editorA.LoginCommands + "\r\n\r\n";
            editorA.BastionTemplateSegment1 = "\r\n#input\r\n\r\n";
            editorA.BastionTemplateSegment2 = "";
            editorA.BastionTemplateSegment3 = "sudo -i";
            editorA.BastionTemplateSegment4 = "\nexit\n#key Enter\n ";
            editorA.ApplyTo(loadedA);
            store.SaveInPlace(loadedA, aPath);

            var editorB = ConnectionEditorViewModel.FromConnection(
                loadedB,
                store.BastionProfiles);
            store.SaveInPlace(loadedB, bPath);
            loadedA = store.Load(aPath);
            loadedB = store.Load(bPath);
            var profileText = File.ReadAllText(store.BastionProfiles.FilePath);
            var sameTemplate =
                loadedA.ResolvedBastionProfile!.Id == loadedB.ResolvedBastionProfile!.Id;

            var passed = defaultAssociation
                         && store.BastionProfiles.Profiles.Count == 1
                         && editorA.HasBastionProfile
                         && editorB.HasBastionProfile
                         && sameTemplate
                         && loadedA.UsesBastionProfile
                         && loadedB.UsesBastionProfile
                         && !File.ReadAllText(aPath)
                             .Contains("BastionTemplateId", StringComparison.Ordinal)
                         && !loadedA.LoginCommands.StartsWith(
                             Environment.NewLine,
                             StringComparison.Ordinal)
                         && !loadedA.LoginCommands.EndsWith(
                             Environment.NewLine,
                             StringComparison.Ordinal)
                         && loadedA.LoginCommands.Contains("#template 1", StringComparison.Ordinal)
                         && loadedB.LoginCommands.Contains("#template 4", StringComparison.Ordinal)
                         && loadedA.EffectiveLoginCommands.Contains("#input", StringComparison.Ordinal)
                         && loadedA.EffectiveLoginCommands.Contains("#key Enter", StringComparison.Ordinal)
                         && loadedA.EffectiveLoginCommands.Contains("#select target-a", StringComparison.Ordinal)
                         && loadedB.EffectiveLoginCommands.Contains("#select target-b", StringComparison.Ordinal)
                         && Path.GetFileName(store.BastionProfiles.FilePath)
                             == "bastion-login-templates.json"
                         && !profileText.Contains("target-a", StringComparison.Ordinal)
                         && !profileText.Contains("target-b", StringComparison.Ordinal);

            var report =
                $"{(passed ? "PASS" : "FAIL")}: shared bastion login template\n"
                + $"defaultAssociation={defaultAssociation}\n"
                + $"profiles={store.BastionProfiles.Profiles.Count}\n"
                + $"sameTemplate={sameTemplate}\n"
                + $"segmentCount={store.BastionProfiles.Profiles.Single().Segments.Length}\n"
                + $"surroundingBlankLinesTrimmed="
                + $"{!loadedA.LoginCommands.StartsWith(Environment.NewLine, StringComparison.Ordinal)}\n"
                + $"connectionReferencesPreserved="
                + $"{loadedA.LoginCommands.Contains("#template 1", StringComparison.Ordinal)}";
            return Task.FromResult(ToolText(report, isError: !passed));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolText(
                $"FAIL: shared bastion login template threw {ex.GetType().Name}: {ex.Message}",
                isError: true));
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeRoot))
                    Directory.Delete(probeRoot, recursive: true);
            }
            catch
            {
                // Probe cleanup is best effort inside the isolated Debug temp root.
            }
        }
    }

    private static async Task<JsonObject> BastionTemplatePresetCheckAsync()
    {
        var (passed, report) = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                return (false, "FAIL: MainWindow is not available.");

            var editor = new ConnectionEditorViewModel
            {
                Type = ConnectionType.Ssh,
                Name = "target-a",
                Host = "bastion.example.test",
                Port = 22,
                Username = "probe",
                HasBastionProfile = true,
                BastionTemplateId = "preset-check",
                BastionProfileEndpoint = "probe@bastion.example.test:22",
                BastionTemplateSegment1 = "keep-existing",
                LoginCommands = " \r\n ",
            };
            var approveOverwrite = false;
            var confirmationCount = 0;
            var localizedConfirmation = false;
            var dialog = main.CreateBastionTemplateEditorDialog(
                editor,
                confirmAsync: (title, prompt) =>
                {
                    confirmationCount++;
                    localizedConfirmation =
                        title == Localizer.Get("BastionTemplateOverwriteTitle")
                        && prompt == Localizer.Get("BastionTemplateOverwritePrompt");
                    return Task.FromResult(approveOverwrite);
                });
            try
            {
                dialog.Show(main);
                var descendants = dialog.GetVisualDescendants().OfType<Control>().ToArray();
                var insert = descendants
                    .OfType<Button>()
                    .FirstOrDefault(control =>
                        control.Name == "InsertTypicalBastionTemplateButton");
                var save = descendants
                    .OfType<Button>()
                    .FirstOrDefault(control =>
                        control.Name == "SaveBastionTemplateButton");
                var fragments = Enumerable.Range(1, BastionLoginProfile.SegmentCount)
                    .Select(id => descendants
                        .OfType<TextBox>()
                        .FirstOrDefault(control =>
                            control.Name == $"BastionTemplateSegment{id}"))
                    .ToArray();
                var hint = descendants
                    .OfType<TextBlock>()
                    .FirstOrDefault(control =>
                        control.Name == "TypicalBastionTemplateHint");

                insert?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var cancelledOverwritePreserved =
                    fragments[0]?.Text == "keep-existing"
                    && fragments.Skip(1).All(fragment =>
                        string.IsNullOrWhiteSpace(fragment?.Text))
                    && confirmationCount == 1;

                approveOverwrite = true;
                insert?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var dialogFilled = insert is not null
                                   && save is not null
                                   && fragments.All(fragment => fragment is not null)
                                   && Enumerable.Range(1, BastionLoginProfile.SegmentCount)
                                       .All(id =>
                                           fragments[id - 1]!.Text
                                               == BastionLoginTemplatePreset.GetSegment(id)
                                                   .ReplaceLineEndings(Environment.NewLine))
                                   && hint?.Text == Localizer.Get("BastionTemplateTypicalHint");
                var overwriteConfirmed =
                    confirmationCount == 2
                    && localizedConfirmation;
                var buttonLocalized =
                    insert?.Content?.ToString()
                    == Localizer.Get("BastionTemplateInsertTypical");
                var transactionalBeforeSave = string.IsNullOrWhiteSpace(editor.LoginCommands);

                save?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var commandsInserted =
                    editor.LoginCommands
                    == BastionLoginTemplatePreset.ConnectionLoginCommands
                        .ReplaceLineEndings(Environment.NewLine);
                var savedSegments = new[]
                {
                    editor.BastionTemplateSegment1,
                    editor.BastionTemplateSegment2,
                    editor.BastionTemplateSegment3,
                    editor.BastionTemplateSegment4,
                };
                var fragmentsSaved = Enumerable.Range(1, BastionLoginProfile.SegmentCount)
                    .All(id =>
                        savedSegments[id - 1]
                        == BastionLoginTemplatePreset.GetSegment(id)
                            .ReplaceLineEndings(Environment.NewLine));
                var existingPreserved =
                    BastionLoginTemplatePreset.UseConnectionCommandsWhenEmpty("custom")
                    == "custom";
                var typicalProfile = new BastionLoginProfile
                {
                    Id = editor.BastionTemplateId,
                    Segments = savedSegments,
                };
                var typicalResolved = LoginCommandSequence.TryResolve(
                    editor.LoginCommands,
                    typicalProfile,
                    new Models.Connection
                    {
                        Name = editor.Name,
                        Host = editor.Host,
                        Port = editor.Port,
                        Username = editor.Username,
                    },
                    out var typicalExpanded,
                    out _);
                var freshIncludesSudo = typicalResolved
                    && LoginCommandSequence.Select(typicalExpanded, LoginCommandSection.Fresh)
                        .Contains("sudo -i");
                var reuseEnterIncludesSudo = typicalResolved
                    && LoginCommandSequence.Select(typicalExpanded, LoginCommandSection.ReuseEnter)
                        .Contains("sudo -i");
                var duplicateIsSudo = typicalResolved
                    && LoginCommandSequence.Select(typicalExpanded, LoginCommandSection.Duplicate)
                        .SequenceEqual(["sudo -i"]);
                var ok = dialogFilled
                         && buttonLocalized
                         && cancelledOverwritePreserved
                         && overwriteConfirmed
                         && transactionalBeforeSave
                         && commandsInserted
                         && fragmentsSaved
                         && existingPreserved
                         && freshIncludesSudo
                         && reuseEnterIncludesSudo
                         && duplicateIsSudo;
                return (ok,
                    $"{(ok ? "PASS" : "FAIL")}: typical bastion template preset\n"
                    + $"buttonFound={insert is not null}\n"
                    + $"buttonLocalized={buttonLocalized}\n"
                    + $"cancelledOverwritePreserved={cancelledOverwritePreserved}\n"
                    + $"overwriteConfirmed={overwriteConfirmed}\n"
                    + $"dialogFilled={dialogFilled}\n"
                    + $"transactionalBeforeSave={transactionalBeforeSave}\n"
                    + $"commandsInserted={commandsInserted}\n"
                    + $"fragmentsSaved={fragmentsSaved}\n"
                    + $"existingCommandsPreserved={existingPreserved}\n"
                    + $"freshIncludesSudo={freshIncludesSudo}\n"
                    + $"reuseEnterIncludesSudo={reuseEnterIncludesSudo}\n"
                    + $"duplicateIsSudo={duplicateIsSudo}");
            }
            finally
            {
                if (dialog.IsVisible)
                    dialog.Close();
            }
        }).ConfigureAwait(false);

        return ToolText(report, isError: !passed);
    }

    private static Task<JsonObject> LoginCommandVariableCheckAsync()
    {
        var template = new BastionLoginProfile
        {
            Id = "variable-check",
            Segments =
            [
                "#select {{name}}\nssh {{username}}@{{host}} -p {{port}}\necho \\{{host}}",
                "",
                "",
                "",
            ],
        };
        var connection = new Models.Connection
        {
            Name = "target-b",
            Host = "bastion.example.test",
            Port = 0,
            Username = "probe",
            LoginCommands = "#template 1",
            ResolvedBastionProfile = template,
        };

        var resolvedOk = connection.TryResolveLoginCommands(out var resolved, out _);
        var unknownRejected = !LoginCommandSequence.TryResolve(
            "{{password}}",
            null,
            connection,
            out _,
            out var unknownError);
        var emptyUsername = new Models.Connection
        {
            Name = connection.Name,
            Host = connection.Host,
            Port = connection.Port,
            Username = "",
        };
        var emptyRejected = !LoginCommandSequence.TryResolve(
            "{{username}}",
            null,
            emptyUsername,
            out _,
            out var emptyError);
        var templateSourceReported = !LoginCommandSequence.TryResolve(
            "#template 1",
            new BastionLoginProfile
            {
                Id = "bad-variable",
                Segments = ["{{missing}}", "", "", ""],
            },
            connection,
            out _,
            out var templateError);

        var passed = resolvedOk
                     && resolved.ReplaceLineEndings("\n")
                         == "#select target-b\nssh probe@bastion.example.test -p 22\necho {{host}}"
                     && unknownRejected
                     && unknownError.Contains("unknown connection variable", StringComparison.Ordinal)
                     && emptyRejected
                     && emptyError.Contains("is empty", StringComparison.Ordinal)
                     && templateSourceReported
                     && templateError.StartsWith("Template fragment 1, line 1:", StringComparison.Ordinal);
        var report =
            $"{(passed ? "PASS" : "FAIL")}: login command variables\n"
            + $"resolved={resolvedOk}\n"
            + $"escapedLiteral={resolved.Contains("{{host}}", StringComparison.Ordinal)}\n"
            + $"unknownRejected={unknownRejected}\n"
            + $"emptyRejected={emptyRejected}\n"
            + $"templateSourceReported={templateSourceReported}\n"
            + "--- resolved ---\n"
            + resolved;
        return Task.FromResult(ToolText(report, isError: !passed));
    }

    private static async Task<JsonObject> BastionChannelLimitCheckAsync()
    {
        var capacity = new ShellChannelCapacityTracker();
        var startsUnknownAndAvailable = capacity.KnownLimit is null && capacity.HasCapacity;
        capacity.MarkOpened();
        capacity.MarkOpened();
        capacity.MarkOpened();
        var observedLimit = capacity.RecordObservedLimit();
        var fullAtObservedLimit = observedLimit == 3
                                  && capacity.ActiveChannels == 3
                                  && !capacity.HasCapacity;
        capacity.MarkClosed();
        var reusableAfterClose = capacity.ActiveChannels == 2 && capacity.HasCapacity;
        capacity.MarkOpened();
        var fullAgainWithoutProbe = capacity.ActiveChannels == 3 && !capacity.HasCapacity;

        // A timeout on a transport that never opened a channel must not be turned into a
        // ceiling: that would leave HasCapacity false forever and retire a healthy link.
        var coldTracker = new ShellChannelCapacityTracker();
        var coldTimeoutRecordsNothing = coldTracker.TryRecordTimedOutLimit() is null
                                        && coldTracker.KnownLimit is null
                                        && coldTracker.HasCapacity;
        coldTracker.MarkOpened();
        coldTracker.MarkOpened();
        var warmTimeoutRecordsLimit = coldTracker.TryRecordTimedOutLimit() == 2
                                      && !coldTracker.HasCapacity;

        // An outright refusal at zero open channels is the server's own answer, so the
        // ceiling stands — and the pool has to retire the transport instead of holding it.
        var refusedTracker = new ShellChannelCapacityTracker();
        var refusedAtZero = refusedTracker.RecordObservedLimit() == 0
                            && !refusedTracker.HasCapacity;

        var source = new TaskCompletionSource<LateChannelProbe>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;
        try
        {
            _ = await SharedSshClient.WaitForChannelOpenAsync(
                source.Task,
                TimeSpan.FromMilliseconds(30),
                CancellationToken.None,
                probe => probe.Dispose());
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        var lateProbe = new LateChannelProbe();
        source.TrySetResult(lateProbe);
        for (var attempt = 0; attempt < 10 && !lateProbe.IsDisposed; attempt++)
            await Task.Delay(10);

        var visibleWait = TerminalView.BastionPoolWaitingMessage.Contains(
            "Waiting for another session",
            StringComparison.Ordinal);
        // Bounded, but the bound has to grow with the queue: one borrower's switch takes
        // seconds, and giving up early costs a whole fresh login.
        var boundedQueue = TerminalView.BastionPoolWaitTimeoutSeconds > 0
                           && TerminalView.BastionPoolWaitCapSeconds
                           > TerminalView.BastionPoolWaitTimeoutSeconds
                           && TerminalView.BastionPoolWaitTimeoutMessage(45).Contains(
                               "Waited 45 seconds",
                               StringComparison.Ordinal)
                           && TerminalView.BastionPoolWaitTimeoutMessage(45).Contains(
                               "opening a fresh SSH connection",
                               StringComparison.Ordinal);
        var visibleFallback = TerminalView.BastionReuseFallbackMessage.Contains(
            "opening a fresh SSH connection",
            StringComparison.Ordinal);
        var visibleFull = TerminalView.BastionPoolFullMessage.Contains(
            "observed session limits",
            StringComparison.Ordinal)
                          && TerminalView.BastionPoolFullMessage.Contains(
                              "opening a fresh SSH connection",
                              StringComparison.Ordinal);

        var passed = timedOut
                     && lateProbe.IsDisposed
                     && startsUnknownAndAvailable
                     && fullAtObservedLimit
                     && reusableAfterClose
                     && fullAgainWithoutProbe
                     && coldTimeoutRecordsNothing
                     && warmTimeoutRecordsLimit
                     && refusedAtZero
                     && visibleWait
                     && boundedQueue
                     && visibleFull
                     && visibleFallback;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: bastion channel-limit handling\n"
            + $"shellOpenTimeoutSeconds={SharedSshClient.ShellOpenTimeoutSeconds}\n"
            + $"poolWaitTimeoutSeconds={TerminalView.BastionPoolWaitTimeoutSeconds}\n"
            + $"timeoutObserved={timedOut}\n"
            + $"lateChannelDisposed={lateProbe.IsDisposed}\n"
            + $"startsUnknownAndAvailable={startsUnknownAndAvailable}\n"
            + $"observedLimit={observedLimit}\n"
            + $"fullAtObservedLimit={fullAtObservedLimit}\n"
            + $"reusableAfterClose={reusableAfterClose}\n"
            + $"fullAgainWithoutProbe={fullAgainWithoutProbe}\n"
            + $"coldTimeoutRecordsNothing={coldTimeoutRecordsNothing}\n"
            + $"warmTimeoutRecordsLimit={warmTimeoutRecordsLimit}\n"
            + $"refusedAtZero={refusedAtZero}\n"
            + $"visibleWait={visibleWait}\n"
            + $"boundedQueue={boundedQueue}\n"
            + $"visibleFull={visibleFull}\n"
            + $"visibleFallback={visibleFallback}";
        return ToolText(report, isError: !passed);
    }

    private static JsonObject BastionReuseLandingCheck()
    {
        const string sourceCommands =
            "#input\n#reuse-enter\n#select source\n#duplicate\nsudo -i\n#reuse-leave\nexit\n#key Enter";
        const string targetCommands =
            "#input\n#reuse-enter\n#select 马良画卷AI能力中台测试环境\n#duplicate\nsudo -i\n#reuse-leave\nexit\n#key Enter";
        var menuText =
            "  49: 172.18.251.147                           马良画卷AI能力中台测试环境\n"
            + "  51: 14.18.249.113                            亚太-AI 代理机\n请选择目标资产：";
        var authText = "请输入二次验证密码：";
        var shellText = "kxjsa@yt-143-157:~ $";
        // What a real bastion channel delivers: an OSC title, bracketed paste, a
        // colored prompt, and an SGR reset after the "$".
        var ptyShellText =
            "\u001b]0;kxjsa@yt-143-157:~\u0007\u001b[?2004h"
            + "\u001b[1;32mkxjsa\u001b[0m@\u001b[1;36myt-143-157\u001b[0m:"
            + "\u001b[1;34m~\u001b[0m \u001b[1;33m$ \u001b[0m";
        var ptyAuthText = "\u001b[?2004l\r\n2nd Password:";

        var menuKind = BastionLanding.Classify(menuText);
        var authKind = BastionLanding.Classify(authText);
        var shellKind = BastionLanding.Classify(shellText);
        var ptyShellKind = BastionLanding.Classify(ptyShellText);
        var ptyAuthKind = BastionLanding.Classify(ptyAuthText);
        // The landing overrules a stale route: at the menu there is nothing to leave.
        var menuSkipsLeave = BastionLanding.SelectReusePhases(
                BastionLandingKind.Menu,
                BastionReuseStart.Switch,
                sourceCommands,
                targetCommands)
            is { Count: 1 } menuLanded
            && !menuLanded[0].Contains("exit", StringComparer.Ordinal);
        // An unreadable landing keeps the route's own plan rather than giving up.
        var unknownKeepsRoute = BastionLanding.SelectReusePhases(
                BastionLandingKind.Unknown,
                BastionReuseStart.Switch,
                sourceCommands,
                targetCommands) is { Count: 2 };
        var switchPhases = BastionLanding.SelectReusePhases(
            BastionReuseStart.Switch, sourceCommands, targetCommands);
        var sameTargetPhases = BastionLanding.SelectReusePhases(
            BastionReuseStart.Duplicate, sourceCommands, targetCommands);
        var entryPhases = BastionLanding.SelectReusePhases(
            BastionReuseStart.Enter, sourceCommands, targetCommands);
        // Nothing has been entered yet, so "exit" would land in the bastion's own menu.
        var entrySkipsLeave = entryPhases.Count == 1
                              && entryPhases[0].Contains("#select 马良画卷AI能力中台测试环境", StringComparer.Ordinal)
                              && !entryPhases[0].Contains("exit", StringComparer.Ordinal);
        var switchJoined = string.Join(" || ", switchPhases.Select(phase => string.Join(" | ", phase)));
        var switchRunsLeaveThenEnter = switchPhases.Count == 2
                                       && switchPhases[0].Contains("exit", StringComparer.Ordinal)
                                       && switchPhases[1].Contains("#select 马良画卷AI能力中台测试环境", StringComparer.Ordinal)
                                       && !switchPhases[1].Contains("#input", StringComparer.Ordinal);
        var sameTargetStartsAtDuplicate = sameTargetPhases.Count == 1
                                          && sameTargetPhases[0].Contains("sudo -i", StringComparer.Ordinal)
                                          && !sameTargetPhases[0].Contains("exit", StringComparer.Ordinal);
        var unknownRoute = BastionRoute.Unknown(sourceCommands);

        var passed = menuKind == BastionLandingKind.Menu
                     && authKind == BastionLandingKind.AuthPrompt
                     && shellKind == BastionLandingKind.Shell
                     && ptyShellKind == BastionLandingKind.Shell
                     && ptyAuthKind == BastionLandingKind.AuthPrompt
                     && menuSkipsLeave
                     && unknownKeepsRoute
                     && switchRunsLeaveThenEnter
                     && sameTargetStartsAtDuplicate
                     && entrySkipsLeave
                     && !unknownRoute.IsKnown
                     && unknownRoute.Position == BastionRoutePosition.Unknown
                     && BastionRoute.AtEntry(sourceCommands).Position == BastionRoutePosition.Entry;
        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: bastion reuse landing\n"
            + $"menu={menuKind}\n"
            + $"auth={authKind}\n"
            + $"shell={shellKind}\n"
            + $"ptyShell={ptyShellKind}\n"
            + $"ptyAuth={ptyAuthKind}\n"
            + $"menuSkipsLeave={menuSkipsLeave}\n"
            + $"unknownKeepsRoute={unknownKeepsRoute}\n"
            + $"switchPhases={switchJoined}\n"
            + $"switchRunsLeaveThenEnter={switchRunsLeaveThenEnter}\n"
            + $"sameTargetStartsAtDuplicate={sameTargetStartsAtDuplicate}\n"
            + $"entrySkipsLeave={entrySkipsLeave}\n"
            + $"unknownRouteKnown={unknownRoute.IsKnown}",
            isError: !passed);
    }

    /// <summary>
    /// Exercises the real window/tray lifetime with offline transports in an isolated
    /// window, without closing the user's main window or touching its sessions.
    /// </summary>
    private static async Task<JsonObject> BastionTrayLifecycleCheckAsync()
    {
        var result = await OnUiAsync(async () =>
        {
            var app = Application.Current as App
                      ?? throw new InvalidOperationException("App is not running.");
            var window = new MainWindow();
            var pool = window.DebugBastionSessionPool;
            var first = new Connection
            {
                ConnectionId = Guid.NewGuid().ToString(),
                Name = "tray target A",
                Host = "tray-probe.invalid",
                Username = "probe",
                LoginCommands = "#input\n#reuse-enter\n1\n#duplicate\n#reuse-leave\nexit",
            };
            var second = new Connection
            {
                ConnectionId = Guid.NewGuid().ToString(),
                Name = "tray target B",
                Host = first.Host,
                Username = first.Username,
                LoginCommands = first.LoginCommands,
            };
            var client = SharedSshClient.CreateDebugProbe();
            var freshClient = SharedSshClient.CreateDebugProbe();
            var closed = false;
            window.Closed += (_, _) => closed = true;
            // Same subscription order as startup: window handlers, then App's
            // cancellation handler. Checking e.Cancel in the former is too early.
            window.Closing += app.OnMainWindowClosing;
            try
            {
                window.Show();
                var registeredBeforeHide = pool.Register(client, first);
                window.Close();
                var hiddenNotClosed = !window.IsVisible && !closed;
                var retainedWhileHidden = pool.HasReusableSession(second)
                                          && client.ReferenceCount == 2;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var borrow = await pool.TryAcquireAsync(second, timeout.Token);
                var reusedSameTransport = borrow is { RequiresSwitch: true }
                                          && ReferenceEquals(borrow.Client, client);
                borrow?.KeepAndRelease();
                borrow?.Dispose();
                var registeredWhileHidden = pool.Register(freshClient, second);
                window.Show();
                var reusableAfterRestore = window.IsVisible && pool.HasReusableSession(second);
                window.Close();
                window.Show();
                var reusableAfterSecondHide = !closed && pool.HasReusableSession(first);

                // Remove only the tray cancellation handler: now Close really ends
                // this isolated window and must release both pool references.
                window.Closing -= app.OnMainWindowClosing;
                window.Close();
                var releasedOnActualClose = closed && pool.SessionCount == 0
                                            && client.ReferenceCount == 1
                                            && freshClient.ReferenceCount == 1;
                var rejectsAfterActualClose = !pool.Register(freshClient, second);
                var passed = registeredBeforeHide && hiddenNotClosed && retainedWhileHidden
                             && reusedSameTransport && registeredWhileHidden && reusableAfterRestore
                             && reusableAfterSecondHide && releasedOnActualClose && rejectsAfterActualClose;
                return ToolText(
                    $"{(passed ? "PASS" : "FAIL")}: bastion close-to-tray lifecycle\n"
                    + $"registeredBeforeHide={registeredBeforeHide}\n"
                    + $"hiddenNotClosed={hiddenNotClosed}\n"
                    + $"retainedWhileHidden={retainedWhileHidden}\n"
                    + $"reusedSameTransport={reusedSameTransport}\n"
                    + $"registeredWhileHidden={registeredWhileHidden}\n"
                    + $"reusableAfterRestore={reusableAfterRestore}\n"
                    + $"reusableAfterSecondHide={reusableAfterSecondHide}\n"
                    + $"releasedOnActualClose={releasedOnActualClose}\n"
                    + $"rejectsAfterActualClose={rejectsAfterActualClose}",
                    isError: !passed);
            }
            finally
            {
                window.Closing -= app.OnMainWindowClosing;
                if (!closed)
                    window.Close();
                pool.Dispose();
                client.Release();
                freshClient.Release();
            }
        });
        return await result;
    }

    /// <summary>
    /// Exercises pool bookkeeping offline: failed borrows retain authentication,
    /// completed borrows relearn the route, and only unusable transports are dropped.
    /// </summary>
    private static async Task<JsonObject> BastionPoolLeaseCheckAsync()
    {
        const string commands =
            "#input\n#reuse-enter\n#select {{name}}\n#duplicate\nsudo -i\n#reuse-leave\nexit\n#key Enter";
        static Connection Target(string id, string name) => new()
        {
            ConnectionId = id,
            Name = name,
            Host = "debug.invalid",
            Port = 22,
            Username = "probe",
            LoginCommands = commands,
        };

        var first = Target("11111111-1111-1111-1111-111111111111", "target-a");
        var second = Target("22222222-2222-2222-2222-222222222222", "target-b");

        using var pool = new BastionSessionPool();
        var client = SharedSshClient.CreateDebugProbe();
        var registered = pool.Register(client, first);
        var reusableAfterRegister = pool.HasReusableSession(second);

        using var cleanupPool = new BastionSessionPool();
        var cleanupClient = SharedSshClient.CreateDebugProbe();
        var cleanupRegistered = cleanupPool.Register(cleanupClient, first);
        cleanupClient.Release(); // Leave only the pool's reference.
        var removedWithoutExternalOwner = cleanupPool.ReleaseUnusedSessions() == 1
                                          && !cleanupPool.HasKnownSession(first);

        using var retainedPool = new BastionSessionPool();
        var retainedClient = SharedSshClient.CreateDebugProbe();
        var retainedRegistered = retainedPool.Register(retainedClient, first);
        var retainedBorrow = await retainedPool.TryAcquireAsync(second);
        var retainedBorrowed = retainedBorrow is not null;
        retainedBorrow?.CompleteAndTakeClient();
        retainedBorrow?.Dispose();
        retainedClient.Release(); // Drop the test owner's reference.
        var retainedBeforeExternalRelease = retainedPool.HasKnownSession(first);
        retainedClient.Release(); // Simulate the last terminal closing.
        var removedAfterLastOwner = retainedPool.ReleaseUnusedSessions() == 1
                                    && !retainedPool.HasKnownSession(first);

        // A borrow that fails must not cost the transport its place in the pool.
        var failed = await pool.TryAcquireAsync(second);
        var failedSwitches = failed is { RequiresSwitch: true };
        failed?.KeepAndRelease();
        failed?.Dispose();
        var keptAfterFailedBorrow = pool.HasKnownSession(first);

        // ...but the next borrower must not trust the old route either: unknown means
        // "leave whatever this transport is in, then enter", never "already there".
        var afterFailure = await pool.TryAcquireAsync(second);
        var routeUnknownAfterFailure = afterFailure is
        {
            SourceRoute.IsKnown: false,
            ReuseStart: BastionReuseStart.Switch,
        };
        afterFailure?.CompleteAndTakeClient();
        afterFailure?.Dispose();
        var relearned = await pool.TryAcquireAsync(second);
        var routeRelearned = relearned is
        {
            SourceRoute.IsKnown: true,
            ReuseStart: BastionReuseStart.Duplicate,
        };
        relearned?.KeepAndRelease();
        relearned?.Dispose();

        // Only an unusable transport is dropped.
        var dead = await pool.TryAcquireAsync(second);
        dead?.Abandon();
        dead?.Dispose();
        var droppedAfterAbandon = !pool.HasKnownSession(first);

        // A transport registered straight after authentication has entered nothing yet:
        // the next channel only has to enter, and "exit" would go into the menu.
        using var entryPool = new BastionSessionPool();
        var entryClient = SharedSshClient.CreateDebugProbe();
        var registeredAtEntry = entryPool.Register(
            entryClient,
            first,
            BastionRoute.AtEntry(first.EffectiveLoginCommands));
        var atEntry = await entryPool.TryAcquireAsync(second);
        var entryStartsWithEnter = atEntry is { ReuseStart: BastionReuseStart.Enter };
        atEntry?.CompleteAndTakeClient();
        atEntry?.Dispose();
        var arrived = await entryPool.TryAcquireAsync(second);
        var arrivalRelearnsRoute = arrived is { ReuseStart: BastionReuseStart.Duplicate };
        arrived?.KeepAndRelease();
        arrived?.Dispose();

        // While one connection authenticates, the others for that bastion identity must
        // wait for it rather than start a second login — that is a second 2FA code.
        using var loginPool = new BastionSessionPool();
        var reservation = loginPool.TryReserveFreshLogin(first);
        var reserved = reservation is not null;
        var secondClaimRefused = loginPool.TryReserveFreshLogin(second) is null;
        var pendingSeen = loginPool.HasPendingFreshLogin(second);
        var waiter = loginPool.WaitForFreshLoginAsync(second);
        var waiterParked = !waiter.IsCompleted;
        reservation?.Dispose();
        var waiterReleased = await Task.WhenAny(waiter, Task.Delay(2000)) == waiter && waiter.Result;
        var claimFreeAfterRelease = !loginPool.HasPendingFreshLogin(first)
                                    && loginPool.TryReserveFreshLogin(first) is not null;

        var passed = registered
                     && reusableAfterRegister
                     && failedSwitches
                     && keptAfterFailedBorrow
                     && routeUnknownAfterFailure
                     && routeRelearned
                     && droppedAfterAbandon
                     && cleanupRegistered
                     && removedWithoutExternalOwner
                     && retainedRegistered
                     && retainedBorrowed
                     && retainedBeforeExternalRelease
                     && removedAfterLastOwner
                     && registeredAtEntry
                     && entryStartsWithEnter
                     && arrivalRelearnsRoute
                     && reserved
                     && secondClaimRefused
                     && pendingSeen
                     && waiterParked
                     && waiterReleased
                     && claimFreeAfterRelease;
        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: bastion pool lease endings\n"
            + $"registered={registered}\n"
            + $"reusableAfterRegister={reusableAfterRegister}\n"
            + $"cleanupRegistered={cleanupRegistered}\n"
            + $"removedWithoutExternalOwner={removedWithoutExternalOwner}\n"
            + $"retainedRegistered={retainedRegistered}\n"
            + $"retainedBorrowed={retainedBorrowed}\n"
            + $"retainedBeforeExternalRelease={retainedBeforeExternalRelease}\n"
            + $"removedAfterLastOwner={removedAfterLastOwner}\n"
            + $"failedBorrowSwitches={failedSwitches}\n"
            + $"keptAfterFailedBorrow={keptAfterFailedBorrow}\n"
            + $"routeUnknownAfterFailure={routeUnknownAfterFailure}\n"
            + $"routeRelearned={routeRelearned}\n"
            + $"droppedAfterAbandon={droppedAfterAbandon}\n"
            + $"registeredAtEntry={registeredAtEntry}\n"
            + $"entryStartsWithEnter={entryStartsWithEnter}\n"
            + $"arrivalRelearnsRoute={arrivalRelearnsRoute}\n"
            + $"freshLoginReserved={reserved}\n"
            + $"secondClaimRefused={secondClaimRefused}\n"
            + $"pendingLoginVisible={pendingSeen}\n"
            + $"waiterParked={waiterParked}\n"
            + $"waiterReleased={waiterReleased}\n"
            + $"claimFreeAfterRelease={claimFreeAfterRelease}\n"
            + $"pendingLoginWaitSeconds={TerminalView.BastionPendingLoginWaitSeconds}",
            isError: !passed);
    }
}
