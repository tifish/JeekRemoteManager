using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using Renci.SshNet;
using SvcSystems.UI.Terminal;
using JeekTools;

namespace JeekRemoteManager.Views;

/// <summary>Session logging and find-in-terminal.</summary>
public partial class TerminalView
{
    // --- Session log ---

    private TerminalSessionLog? _sessionLog;

    /// <summary>True while this tab records its output to a log file.</summary>
    public bool IsSessionLogging => _sessionLog is not null;

    /// <summary>Path of the active log file, or null.</summary>
    public string? SessionLogPath => _sessionLog?.Path;

    /// <summary>
    /// Starts recording this tab's output (plain text, escape sequences removed). A log
    /// survives reconnects within the tab and ends when it is stopped or the tab closes.
    /// Returns the log file path.
    /// </summary>
    public string StartSessionLog(string? folder = null)
    {
        if (_sessionLog is { } running)
            return running.Path;

        var log = TerminalSessionLog.Create(_connection?.Name ?? "session", folder);
        _sessionLog = log;
        FeedLine($"\u001b[90m[{Jeek.Avalonia.Localization.Localizer.Get("SessionLogStarted")}: {log.Path}]\u001b[0m");
        return log.Path;
    }

    public void StopSessionLog()
    {
        var log = Interlocked.Exchange(ref _sessionLog, null);
        if (log is null)
            return;
        log.Dispose();
        if (!_disposed)
            FeedLine($"\u001b[90m[{Jeek.Avalonia.Localization.Localizer.Get("SessionLogStopped")}: {log.Path}]\u001b[0m");
    }

    /// <summary>Debug MCP: flushes the active log so its content can be read back.</summary>
    internal void DebugFlushSessionLog() => _sessionLog?.Flush();

    // --- Find in terminal ---

    /// <summary>
    /// Opens the find bar (Ctrl+Shift+F), seeded with the current single-line selection.
    /// Search runs over the whole buffer, scrollback included, and is case-insensitive.
    /// </summary>
    public void OpenFindBar()
    {
        var seed = Term.HasSelection ? GetTerminalSelectionText(Term.SelectedText).Trim() : "";
        FindBar.IsVisible = true;
        if (seed.Length > 0 && !seed.Contains('\n'))
            FindBox.Text = seed;
        FindBox.Focus();
        FindBox.SelectAll();
        RunFind();
    }

    private void CloseFindBar()
    {
        FindBar.IsVisible = false;
        _model.ClearSelection();
        FocusTerminal();
    }

    private void RunFind()
    {
        var text = FindBox.Text ?? "";
        if (text.Length == 0)
        {
            _model.ClearSelection();
            FindCountText.Text = "";
            return;
        }

        Term.Search(text);
        UpdateFindCount();
    }

    /// <summary>
    /// Moves to the next or previous hit. The control drops its hit list whenever the
    /// buffer changes (new output), so an empty list means "search again", not "no hits".
    /// </summary>
    private void StepFind(bool forward)
    {
        if (string.IsNullOrEmpty(FindBox.Text))
            return;

        var previous = _model.CurrentSearchResultIndex;
        var index = forward ? Term.SelectNextSearchResult() : Term.SelectPreviousSearchResult();
        if (index < 0 && Term.Search(FindBox.Text) is var total and > 0 && previous >= 0)
        {
            // Search restarts at the first hit; continue from where the user was instead.
            var target = ((forward ? previous + 1 : previous - 1) % total + total) % total;
            for (var i = 0; i < target; i++)
                Term.SelectNextSearchResult();
        }
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        var total = _model.SearchResultCount;
        FindCountText.Text = total == 0
            ? Jeek.Avalonia.Localization.Localizer.Get("FindNoResults")
            : $"{Math.Max(0, _model.CurrentSearchResultIndex) + 1}/{total}";
    }

    private void OnFindTextChanged(object? sender, TextChangedEventArgs e) => RunFind();

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.F3:
                StepFind(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFindBar();
                e.Handled = true;
                break;
        }
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e) => StepFind(forward: true);

    private void OnFindPreviousClick(object? sender, RoutedEventArgs e) => StepFind(forward: false);

    private void OnFindCloseClick(object? sender, RoutedEventArgs e) => CloseFindBar();

    private static bool IsFindGesture(KeyEventArgs e) =>
        e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Debug MCP: runs a search through the find bar and reports "current/total".</summary>
    internal string DebugFind(string text)
    {
        OpenFindBar();
        FindBox.Text = text;
        RunFind();
        return FindCountText.Text ?? "";
    }

    /// <summary>Debug MCP: steps the find bar and reports "current/total" plus the selection.</summary>
    internal string DebugFindStep(bool forward)
    {
        StepFind(forward);
        return $"{FindCountText.Text} selected={Term.SelectedText}";
    }

    /// <summary>Debug MCP: whether the find bar is showing.</summary>
    internal bool DebugFindBarOpen => FindBar.IsVisible;

    internal void DebugCloseFindBar() => CloseFindBar();
}
