using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

/// <summary>
/// Writes or removes JeekRemoteManager MCP entries in a user-chosen project folder.
/// The user picks a directory and which agent configs to keep; Write syncs that
/// selection (unchecked entries are removed, empty files/folders deleted) and
/// Remove all clears every related file.
/// </summary>
public sealed class McpProjectLinkDialog : Window
{
    private readonly TextBox _directoryBox;
    private readonly TextBlock _status;
    private readonly Button _writeButton;
    private readonly Button _removeAllButton;
    private readonly Button _lastUsedButton;
    private readonly Button _selectAllButton;
    private readonly Button _selectNoneButton;
    private readonly Button _cancelButton;
    private readonly List<(AgentMcpConfigCatalog.Target Target, CheckBox Box)> _agents = [];
    private readonly Func<string, IReadOnlyList<string>> _detectWritten;
    private readonly Func<string, IReadOnlyList<string>, string> _write;
    private readonly Func<string, string> _removeAll;
    private readonly Action<string> _rememberDirectory;
    private readonly Action<IReadOnlyList<string>> _rememberWrittenAgents;
    private IReadOnlyList<string> _lastWrittenAgents;
    private bool _suppressDirectoryScan;

    private McpProjectLinkDialog(
        string title,
        string? lastDirectory,
        Action<string> rememberDirectory,
        IReadOnlyList<string> lastWrittenAgents,
        Action<IReadOnlyList<string>> rememberWrittenAgents,
        Func<string, IReadOnlyList<string>> detectWritten,
        Func<string, IReadOnlyList<string>, string> write,
        Func<string, string> removeAll)
    {
        _detectWritten = detectWritten;
        _write = write;
        _removeAll = removeAll;
        _rememberDirectory = rememberDirectory;
        _rememberWrittenAgents = rememberWrittenAgents;
        _lastWrittenAgents = KnownTargetPaths(lastWrittenAgents);

        Name = "McpProjectLinkDialog";
        Title = title;
        Width = 560;
        MinWidth = 480;
        Height = 620;
        MinHeight = 480;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _directoryBox = new TextBox
        {
            Name = "McpTargetDirectory",
            PlaceholderText = Localizer.Get("McpLinkDirectoryWatermark"),
        };
        var browse = new Button
        {
            Name = "McpBrowseButton",
            Content = Localizer.Get("McpLinkBrowse"),
            MinWidth = 80,
        };
        browse.Click += async (_, _) => await BrowseAsync();

        _lastUsedButton = new Button
        {
            Name = "McpLastUsedButton",
            Content = Localizer.Get("McpLinkLastUsed"),
            MinWidth = 88,
            IsEnabled = _lastWrittenAgents.Count > 0,
        };
        _selectAllButton = new Button
        {
            Name = "McpSelectAllButton",
            Content = Localizer.Get("McpLinkSelectAll"),
            MinWidth = 72,
        };
        _selectNoneButton = new Button
        {
            Name = "McpSelectNoneButton",
            Content = Localizer.Get("McpLinkSelectNone"),
            MinWidth = 72,
        };
        _lastUsedButton.Click += (_, _) => ApplyLastUsed();
        _selectAllButton.Click += (_, _) => SetAllChecked(true);
        _selectNoneButton.Click += (_, _) => SetAllChecked(false);

        var agentList = new StackPanel
        {
            Name = "McpAgentList",
            Spacing = 6,
        };
        foreach (var target in AgentMcpConfigCatalog.All
                     .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
        {
            var box = new CheckBox
            {
                Name = "McpAgent_" + AgentSlug(target.RelativePath),
                Content = new StackPanel
                {
                    Spacing = 0,
                    Children =
                    {
                        new TextBlock { Text = target.Label, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = target.RelativePath,
                            Classes = { "hint" },
                            FontSize = 11,
                        },
                    },
                },
            };
            _agents.Add((target, box));
            agentList.Children.Add(box);
        }

        _status = new TextBlock
        {
            Name = "McpLinkStatus",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "hint" },
        };

        _writeButton = new Button
        {
            Name = "McpWriteButton",
            Content = Localizer.Get("McpLinkWrite"),
            MinWidth = 88,
            IsDefault = true,
            Classes = { "accent" },
        };
        _removeAllButton = new Button
        {
            Name = "McpRemoveAllButton",
            Content = Localizer.Get("McpLinkRemoveAll"),
            MinWidth = 88,
        };
        _cancelButton = new Button
        {
            Name = "McpCancelButton",
            Content = Localizer.Get("DialogCancel"),
            MinWidth = 80,
            IsCancel = true,
        };
        _writeButton.Click += (_, _) => CommitWrite();
        _removeAllButton.Click += (_, _) => CommitRemoveAll();
        _cancelButton.Click += (_, _) => Close();

        var directoryRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(_directoryBox, 0);
        Grid.SetColumn(browse, 1);
        directoryRow.Children.Add(_directoryBox);
        directoryRow.Children.Add(browse);

        var agentHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var agentLabel = new TextBlock
        {
            Text = Localizer.Get("McpLinkAgents"),
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "label" },
        };
        Grid.SetColumn(agentLabel, 0);
        Grid.SetColumn(_lastUsedButton, 1);
        Grid.SetColumn(_selectAllButton, 2);
        Grid.SetColumn(_selectNoneButton, 3);
        agentHeader.Children.Add(agentLabel);
        agentHeader.Children.Add(_lastUsedButton);
        agentHeader.Children.Add(_selectAllButton);
        agentHeader.Children.Add(_selectNoneButton);

        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(_removeAllButton, 0);
        Grid.SetColumn(_writeButton, 2);
        Grid.SetColumn(_cancelButton, 3);
        buttons.Children.Add(_removeAllButton);
        buttons.Children.Add(_writeButton);
        buttons.Children.Add(_cancelButton);

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            LastChildFill = true,
            Children =
            {
                Place(new TextBlock
                {
                    Text = Localizer.Get("McpLinkTargetDirectory"),
                    Classes = { "label" },
                }, Dock.Top),
                Place(new Thickness(0, 4, 0, 12), directoryRow, Dock.Top),
                Place(agentHeader, Dock.Top),
                Place(new Thickness(0, 10, 0, 0), buttons, Dock.Bottom),
                Place(new Thickness(0, 8, 0, 0), _status, Dock.Bottom),
                Place(new Thickness(0, 4, 0, 0), new TextBlock
                {
                    Text = Localizer.Get("McpLinkAgentsHint"),
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "hint" },
                }, Dock.Bottom),
                new ScrollViewer
                {
                    Margin = new Thickness(0, 8, 0, 8),
                    Content = agentList,
                },
            },
        };

        _directoryBox.LostFocus += (_, _) => ScanDirectory();
        _directoryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                ScanDirectory();
                e.Handled = true;
            }
        };

        if (!string.IsNullOrWhiteSpace(lastDirectory))
            ApplyDirectory(lastDirectory);
        else
            UpdateActionState();
    }

    /// <summary>Application-wide product MCP write dialog.</summary>
    public static McpProjectLinkDialog CreateApplication(MainWindowViewModel vm) =>
        new(
            Localizer.Get("McpLinkDialogApplicationTitle"),
            vm.LastMcpProjectDirectory,
            path => vm.LastMcpProjectDirectory = path,
            vm.LastMcpWrittenTargetPaths,
            paths => vm.LastMcpWrittenTargetPaths = paths,
            AgentProjectLink.ListWrittenApplicationTargetPaths,
            (directory, selected) =>
            {
                var project = AgentProjectLink.WriteApplicationInto(
                    directory,
                    vm.AiAutoApproveDangerousCommands,
                    selected);
                vm.StatusMessage = string.Format(
                    Localizer.Get("AiLinkApplicationProjectDone"),
                    project);
                return project;
            },
            directory =>
            {
                var project = AgentProjectLink.RemoveApplicationFrom(directory);
                vm.StatusMessage = string.Format(
                    Localizer.Get("AiUnlinkApplicationProjectDone"),
                    project);
                return project;
            });

    /// <summary>Per-connection MCP write dialog.</summary>
    public static McpProjectLinkDialog CreateConnection(
        AgentCliPanelViewModel panel,
        MainWindowViewModel settings) =>
        new(
            Localizer.Get("McpLinkDialogConnectionTitle"),
            settings.LastMcpProjectDirectory,
            path => settings.LastMcpProjectDirectory = path,
            settings.LastMcpWrittenTargetPaths,
            paths => settings.LastMcpWrittenTargetPaths = paths,
            directory =>
            {
                if (panel.ResolveLinkContext?.Invoke() is not { } link)
                    return [];
                return AgentProjectLink.ListWrittenTargetPaths(directory, link.ProjectMcpServerName);
            },
            (directory, selected) =>
            {
                if (!panel.WriteToProject(directory, selected))
                    throw new InvalidOperationException(panel.StatusText);
                return directory;
            },
            directory =>
            {
                if (!panel.RemoveFromProject(directory))
                    throw new InvalidOperationException(panel.StatusText);
                return directory;
            });

    public static Task ShowApplicationAsync(Window owner, MainWindowViewModel vm) =>
        CreateApplication(vm).ShowDialog(owner);

    public static Task ShowConnectionAsync(
        Window owner,
        AgentCliPanelViewModel panel,
        MainWindowViewModel settings) =>
        CreateConnection(panel, settings).ShowDialog(owner);

    /// <summary>Current folder text, for Debug MCP.</summary>
    public string TargetDirectoryText
    {
        get => _directoryBox.Text ?? "";
        set => ApplyDirectory(value);
    }

    /// <summary>Relative paths of the currently checked agents.</summary>
    public IReadOnlyList<string> SelectedTargetPaths =>
        _agents
            .Where(item => item.Box.IsChecked == true)
            .Select(item => item.Target.RelativePath)
            .ToArray();

    /// <summary>Agent labels shown in the list, for Debug MCP.</summary>
    public IReadOnlyList<string> AgentLabels =>
        _agents.Select(item => item.Target.Label).ToArray();

    /// <summary>Latest status line shown under the agent list.</summary>
    public string StatusText => _status.Text ?? "";

    /// <summary>Whether Write is enabled (an existing folder is selected).</summary>
    public bool WriteEnabled => _writeButton.IsEnabled;

    /// <summary>Whether Remove all is enabled (an existing folder is selected).</summary>
    public bool RemoveAllEnabled => _removeAllButton.IsEnabled;

    public bool LastUsedEnabled => _lastUsedButton.IsEnabled;

    public void ClickLastUsed() =>
        _lastUsedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public void ClickSelectAll() =>
        _selectAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public void ClickSelectNone() =>
        _selectNoneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public void ClickWrite() =>
        _writeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public void ClickRemoveAll() =>
        _removeAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public void ClickCancel() =>
        _cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Types a folder path into the box and scans, the same as losing focus or pressing Enter.
    /// </summary>
    public void EnterDirectoryFromTextBox(string path)
    {
        _directoryBox.Text = path;
        ScanDirectory();
    }

    /// <summary>Sets the folder and ticks agents already written there.</summary>
    public void ApplyDirectory(string? path)
    {
        _suppressDirectoryScan = true;
        _directoryBox.Text = path ?? "";
        _suppressDirectoryScan = false;
        ScanDirectory();
    }

    /// <summary>Checks only the given catalog relative paths.</summary>
    public void SetSelectedTargetPaths(IEnumerable<string> relativePaths)
    {
        var selected = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var (target, box) in _agents)
            box.IsChecked = selected.Contains(target.RelativePath);
    }

    /// <summary>Writes the checked agents and removes the rest. Returns the project path.</summary>
    public string CommitWrite()
    {
        var directory = RequireDirectory();
        if (directory is null)
            return "";

        try
        {
            var selected = SelectedTargetPaths;
            var project = _write(directory, selected);
            Remember(project);
            if (selected.Count > 0)
                RememberWrittenAgents(selected);
            SetStatus(string.Format(Localizer.Get("McpLinkWritten"), project), error: false);
            Close();
            return project;
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(Localizer.Get("AiLinkProjectFailed"), ex.Message), error: true);
            return "";
        }
    }

    /// <summary>Removes every JeekRemoteManager MCP entry from the folder.</summary>
    public string CommitRemoveAll()
    {
        var directory = RequireDirectory();
        if (directory is null)
            return "";

        try
        {
            var project = _removeAll(directory);
            Remember(project);
            SetStatus(string.Format(Localizer.Get("McpLinkRemoved"), project), error: false);
            Close();
            return project;
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(Localizer.Get("AiLinkProjectFailed"), ex.Message), error: true);
            return "";
        }
    }

    private async Task BrowseAsync()
    {
        var options = new FolderPickerOpenOptions
        {
            Title = Localizer.Get("McpLinkPickDirectoryTitle"),
            AllowMultiple = false,
        };
        var current = TargetDirectoryText.Trim();
        if (current.Length > 0 && Directory.Exists(current))
        {
            try
            {
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(current);
            }
            catch
            {
                // Best-effort — proceed without a suggestion.
            }
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            Remember(path);
            ApplyDirectory(path);
        }
    }

    private void ScanDirectory()
    {
        if (_suppressDirectoryScan)
            return;

        var directory = TargetDirectoryText.Trim();
        if (directory.Length > 0 && Directory.Exists(directory))
        {
            try
            {
                SetSelectedTargetPaths(_detectWritten(directory));
            }
            catch
            {
                SetAllChecked(false);
            }
        }
        else
        {
            SetAllChecked(false);
        }

        UpdateActionState();
    }

    private void ApplyLastUsed()
    {
        var paths = KnownTargetPaths(_lastWrittenAgents);
        if (paths.Count == 0)
            return;
        SetSelectedTargetPaths(paths);
    }

    private void RememberWrittenAgents(IReadOnlyList<string> paths)
    {
        _lastWrittenAgents = KnownTargetPaths(paths);
        _lastUsedButton.IsEnabled = _lastWrittenAgents.Count > 0;
        _rememberWrittenAgents(_lastWrittenAgents);
    }

    private static IReadOnlyList<string> KnownTargetPaths(IEnumerable<string>? paths)
    {
        if (paths is null)
            return [];

        var catalog = new HashSet<string>(
            AgentMcpConfigCatalog.All.Select(target => target.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && catalog.Contains(path.Trim()))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetAllChecked(bool value)
    {
        foreach (var (_, box) in _agents)
            box.IsChecked = value;
    }

    private string? RequireDirectory()
    {
        var directory = TargetDirectoryText.Trim();
        if (directory.Length == 0 || !Directory.Exists(directory))
        {
            SetStatus(Localizer.Get("McpLinkDirectoryMissing"), error: true);
            UpdateActionState();
            return null;
        }

        return directory;
    }

    private void Remember(string directory)
    {
        if (directory.Length == 0)
            return;
        _rememberDirectory(directory);
    }

    private void UpdateActionState()
    {
        var enabled = Directory.Exists(TargetDirectoryText.Trim());
        _writeButton.IsEnabled = enabled;
        _removeAllButton.IsEnabled = enabled;
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Classes.Set("hint", !error);
        if (error)
            _status[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("DangerBrush");
        else
            _status.ClearValue(TextBlock.ForegroundProperty);
    }

    private static Control Place(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Control Place(Thickness margin, Control control, Dock dock)
    {
        control.Margin = margin;
        return Place(control, dock);
    }

    private static string AgentSlug(string relativePath)
    {
        var chars = relativePath
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars);
    }
}
