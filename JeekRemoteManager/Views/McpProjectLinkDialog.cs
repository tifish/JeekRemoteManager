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
/// Writes or removes JeekRemoteManager MCP entries in either user-level global agent configs
/// (application-wide dialog only) or a user-chosen project folder. Write syncs the checked
/// selection (unchecked entries are removed) and Remove all clears every related entry.
/// </summary>
public sealed class McpProjectLinkDialog : Window
{
    private sealed record GlobalScopeOptions(
        bool InitialIsGlobal,
        Action<bool> RememberScope,
        IReadOnlyList<string> LastWrittenIds,
        Action<IReadOnlyList<string>> RememberWrittenIds,
        AgentMcpConfigCatalog.GlobalConfigEnvironment Environment,
        Func<IReadOnlyList<string>> DetectWritten,
        Action<IReadOnlyList<string>> Write,
        Action RemoveAll);

    private readonly GlobalScopeOptions? _globalOptions;
    private readonly RadioButton? _globalScopeRadio;
    private readonly RadioButton? _projectScopeRadio;
    private readonly StackPanel _directorySection;
    private readonly TextBox _directoryBox;
    private readonly StackPanel _agentList;
    private readonly TextBlock _agentsHint;
    private readonly TextBlock _status;
    private readonly Button _writeButton;
    private readonly Button _removeAllButton;
    private readonly Button _lastUsedButton;
    private readonly Button _selectAllButton;
    private readonly Button _selectNoneButton;
    private readonly Button _cancelButton;
    private readonly List<(string Key, string Label, string DisplayPath, CheckBox Box)> _agents = [];
    private readonly Func<string, IReadOnlyList<string>> _detectWritten;
    private readonly Func<string, IReadOnlyList<string>, string> _write;
    private readonly Func<string, string> _removeAll;
    private readonly Action<string> _rememberDirectory;
    private readonly Action<IReadOnlyList<string>> _rememberWrittenAgents;
    private IReadOnlyList<string> _lastWrittenAgents;
    private IReadOnlyList<string> _lastGlobalWrittenAgents;
    private bool _isGlobalScope;
    private bool _suppressDirectoryScan;
    private bool _suppressScopeChange;

    private McpProjectLinkDialog(
        string title,
        string? lastDirectory,
        Action<string> rememberDirectory,
        IReadOnlyList<string> lastWrittenAgents,
        Action<IReadOnlyList<string>> rememberWrittenAgents,
        Func<string, IReadOnlyList<string>> detectWritten,
        Func<string, IReadOnlyList<string>, string> write,
        Func<string, string> removeAll,
        GlobalScopeOptions? globalOptions = null)
    {
        _globalOptions = globalOptions;
        _detectWritten = detectWritten;
        _write = write;
        _removeAll = removeAll;
        _rememberDirectory = rememberDirectory;
        _rememberWrittenAgents = rememberWrittenAgents;
        _lastWrittenAgents = KnownTargetPaths(lastWrittenAgents);
        _lastGlobalWrittenAgents = globalOptions is null
            ? []
            : KnownGlobalTargetIds(globalOptions.LastWrittenIds);

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
            Classes = { "mono" },
            PlaceholderText = Localizer.Get("McpLinkDirectoryWatermark"),
        };
        var browse = new Button
        {
            Name = "McpBrowseButton",
            Content = Localizer.Get("McpLinkBrowse"),
            MinWidth = 88,
        };
        browse.Click += async (_, _) => await BrowseAsync();

        _lastUsedButton = new Button
        {
            Name = "McpLastUsedButton",
            Content = Localizer.Get("McpLinkLastUsed"),
            MinWidth = 84,
        };
        _selectAllButton = new Button
        {
            Name = "McpSelectAllButton",
            Content = Localizer.Get("McpLinkSelectAll"),
            MinWidth = 68,
        };
        _selectNoneButton = new Button
        {
            Name = "McpSelectNoneButton",
            Content = Localizer.Get("McpLinkSelectNone"),
            MinWidth = 68,
        };
        _lastUsedButton.Click += (_, _) => ApplyLastUsed();
        _selectAllButton.Click += (_, _) => SetAllChecked(true);
        _selectNoneButton.Click += (_, _) => SetAllChecked(false);

        _agentList = new StackPanel
        {
            Name = "McpAgentList",
            Spacing = 8,
            Margin = new Thickness(12, 8),
        };

        _agentsHint = new TextBlock
        {
            Name = "McpAgentsHint",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "hint" },
        };

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
            Classes = { "danger" },
        };
        _cancelButton = new Button
        {
            Name = "McpCancelButton",
            Content = Localizer.Get("DialogCancel"),
            MinWidth = 88,
            IsCancel = true,
        };
        _writeButton.Click += (_, _) => CommitWrite();
        _removeAllButton.Click += (_, _) => CommitRemoveAll();
        _cancelButton.Click += (_, _) => Close();

        var directoryRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetColumn(_directoryBox, 0);
        Grid.SetColumn(browse, 1);
        directoryRow.Children.Add(_directoryBox);
        directoryRow.Children.Add(browse);

        _directorySection = new StackPanel
        {
            Name = "McpDirectorySection",
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new TextBlock
                {
                    Text = Localizer.Get("McpLinkTargetDirectory"),
                    Classes = { "label" },
                },
                directoryRow,
            },
        };

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

        var agentCard = new Border
        {
            Classes = { "panel" },
            Margin = new Thickness(0, 8, 0, 8),
            ClipToBounds = true,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _agentList,
            },
        };

        var layout = new DockPanel
        {
            Margin = new Thickness(20),
            LastChildFill = true,
        };

        if (_globalOptions is not null)
        {
            _globalScopeRadio = new RadioButton
            {
                Name = "McpScopeGlobalRadio",
                GroupName = "McpLinkScope",
                Content = Localizer.Get("McpLinkScopeGlobal"),
            };
            _projectScopeRadio = new RadioButton
            {
                Name = "McpScopeProjectRadio",
                GroupName = "McpLinkScope",
                Content = Localizer.Get("McpLinkScopeProject"),
            };
            _globalScopeRadio.IsCheckedChanged += (_, _) =>
            {
                if (!_suppressScopeChange && _globalScopeRadio.IsChecked == true)
                    ApplyScope(isGlobal: true);
            };
            _projectScopeRadio.IsCheckedChanged += (_, _) =>
            {
                if (!_suppressScopeChange && _projectScopeRadio.IsChecked == true)
                    ApplyScope(isGlobal: false);
            };

            var scopeSection = new StackPanel
            {
                Name = "McpScopeSection",
                Margin = new Thickness(0, 0, 0, 12),
                Children =
                {
                    new TextBlock
                    {
                        Text = Localizer.Get("McpLinkScope"),
                        Classes = { "label" },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 16,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { _globalScopeRadio, _projectScopeRadio },
                    },
                },
            };
            layout.Children.Add(Place(scopeSection, Dock.Top));
        }

        layout.Children.Add(Place(_directorySection, Dock.Top));
        layout.Children.Add(Place(agentHeader, Dock.Top));
        layout.Children.Add(Place(new Thickness(0, 12, 0, 0), buttons, Dock.Bottom));
        layout.Children.Add(Place(new Thickness(0, 8, 0, 0), _status, Dock.Bottom));
        layout.Children.Add(Place(new Thickness(0, 4, 0, 0), _agentsHint, Dock.Bottom));
        layout.Children.Add(agentCard);
        Content = layout;

        _directoryBox.TextChanged += (_, _) => UpdateActionState();
        _directoryBox.LostFocus += (_, _) => ScanDirectory();
        _directoryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                ScanDirectory();
                e.Handled = true;
            }
        };

        // Keep the remembered project folder ready even when opening in Global scope.
        _suppressDirectoryScan = true;
        _directoryBox.Text = lastDirectory ?? "";
        _suppressDirectoryScan = false;

        ApplyScope(_globalOptions?.InitialIsGlobal == true);
    }

    /// <summary>Application-wide product MCP write dialog (supports both Global and Project scopes).</summary>
    public static McpProjectLinkDialog CreateApplication(
        MainWindowViewModel vm,
        AgentMcpConfigCatalog.GlobalConfigEnvironment? globalEnvironment = null)
    {
        var env = globalEnvironment ?? AgentMcpConfigCatalog.GlobalConfigEnvironment.Current;
        return new(
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
            },
            new GlobalScopeOptions(
                InitialIsGlobal: vm.LastMcpWriteGlobal,
                RememberScope: isGlobal => vm.LastMcpWriteGlobal = isGlobal,
                LastWrittenIds: vm.LastMcpGlobalWrittenTargetIds,
                RememberWrittenIds: ids => vm.LastMcpGlobalWrittenTargetIds = ids,
                Environment: env,
                DetectWritten: () => AgentProjectLink.ListWrittenGlobalApplicationTargetIds(env),
                Write: selected =>
                {
                    AgentProjectLink.WriteGlobalApplication(selected, env);
                    vm.StatusMessage = selected.Count > 0
                        ? Localizer.Get("AiLinkApplicationGlobalDone")
                        : Localizer.Get("AiUnlinkApplicationGlobalDone");
                },
                RemoveAll: () =>
                {
                    AgentProjectLink.RemoveGlobalApplication(env);
                    vm.StatusMessage = Localizer.Get("AiUnlinkApplicationGlobalDone");
                }));
    }

    /// <summary>Per-connection MCP write dialog (project folder only).</summary>
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
                if (panel.ResolveLinkContext?.Invoke() is not { } link)
                    throw new InvalidOperationException(Localizer.Get("AiLinkProjectUnavailable"));
                var project = AgentProjectLink.WriteInto(link, directory, selected);
                panel.StatusText = string.Format(Localizer.Get("AiLinkProjectDone"), project);
                return project;
            },
            directory =>
            {
                if (panel.ResolveLinkContext?.Invoke() is not { } link)
                    throw new InvalidOperationException(Localizer.Get("AiLinkProjectUnavailable"));
                var project = AgentProjectLink.RemoveFrom(link, directory);
                panel.StatusText = string.Format(Localizer.Get("AiUnlinkProjectDone"), project);
                return project;
            });

    public static Task ShowApplicationAsync(Window owner, MainWindowViewModel vm) =>
        CreateApplication(vm).ShowDialog(owner);

    public static Task ShowConnectionAsync(
        Window owner,
        AgentCliPanelViewModel panel,
        MainWindowViewModel settings) =>
        CreateConnection(panel, settings).ShowDialog(owner);

    /// <summary>Whether this dialog offers the Global / Project scope switcher, for Debug MCP.</summary>
    public bool SupportsGlobalScope => _globalOptions is not null;

    /// <summary>True when Global scope is currently selected, for Debug MCP.</summary>
    public bool IsGlobalScope => _isGlobalScope;

    /// <summary>Whether the project-folder input section is currently visible, for Debug MCP.</summary>
    public bool TargetDirectoryVisible => _directorySection.IsVisible;

    /// <summary>Current folder text, for Debug MCP.</summary>
    public string TargetDirectoryText
    {
        get => _directoryBox.Text ?? "";
        set => ApplyDirectory(value);
    }

    /// <summary>
    /// Keys of the currently checked agents: global target IDs in Global scope, or catalog
    /// relative paths in Project scope.
    /// </summary>
    public IReadOnlyList<string> SelectedTargetPaths =>
        _agents
            .Where(item => item.Box.IsChecked == true)
            .Select(item => item.Key)
            .ToArray();

    /// <summary>Agent labels shown in the active list, for Debug MCP.</summary>
    public IReadOnlyList<string> AgentLabels =>
        _agents.Select(item => item.Label).ToArray();

    /// <summary>Config paths displayed beneath each agent label, for Debug MCP.</summary>
    public IReadOnlyList<string> AgentPaths =>
        _agents.Select(item => item.DisplayPath).ToArray();

    /// <summary>Latest status line shown under the agent list.</summary>
    public string StatusText => _status.Text ?? "";

    /// <summary>Whether Write is enabled.</summary>
    public bool WriteEnabled => _writeButton.IsEnabled;

    /// <summary>Whether Remove all is enabled.</summary>
    public bool RemoveAllEnabled => _removeAllButton.IsEnabled;

    public bool LastUsedEnabled => _lastUsedButton.IsEnabled;

    public void SelectGlobalScope()
    {
        if (_globalScopeRadio is not null)
            _globalScopeRadio.IsChecked = true;
    }

    public void SelectProjectScope()
    {
        if (_projectScopeRadio is not null)
            _projectScopeRadio.IsChecked = true;
    }

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

    /// <summary>Checks only the given keys (global target IDs or project relative paths).</summary>
    public void SetSelectedTargetPaths(IEnumerable<string> keys)
    {
        var selected = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, _, _, box) in _agents)
            box.IsChecked = selected.Contains(key);
    }

    /// <summary>Writes the checked agents and removes the rest. Returns the target label/path.</summary>
    public string CommitWrite()
    {
        if (_isGlobalScope && _globalOptions is not null)
        {
            try
            {
                var selected = SelectedTargetPaths;
                _globalOptions.Write(selected);
                _globalOptions.RememberScope(true);
                if (selected.Count > 0)
                    RememberGlobalWrittenAgents(selected);
                SetStatus(Localizer.Get("McpLinkGlobalWritten"), error: false);
                Close();
                return "global";
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Localizer.Get("AiLinkGlobalFailed"), ex.Message), error: true);
                return "";
            }
        }

        var directory = RequireDirectory();
        if (directory is null)
            return "";

        try
        {
            var selected = SelectedTargetPaths;
            var project = _write(directory, selected);
            Remember(project);
            _globalOptions?.RememberScope(false);
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

    /// <summary>Removes every JeekRemoteManager MCP entry from the active scope.</summary>
    public string CommitRemoveAll()
    {
        if (_isGlobalScope && _globalOptions is not null)
        {
            try
            {
                _globalOptions.RemoveAll();
                _globalOptions.RememberScope(true);
                SetStatus(Localizer.Get("McpLinkGlobalRemoved"), error: false);
                Close();
                return "global";
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Localizer.Get("AiLinkGlobalFailed"), ex.Message), error: true);
                return "";
            }
        }

        var directory = RequireDirectory();
        if (directory is null)
            return "";

        try
        {
            var project = _removeAll(directory);
            Remember(project);
            _globalOptions?.RememberScope(false);
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

    private void ApplyScope(bool isGlobal)
    {
        _isGlobalScope = isGlobal && _globalOptions is not null;

        _suppressScopeChange = true;
        if (_globalScopeRadio is not null)
            _globalScopeRadio.IsChecked = _isGlobalScope;
        if (_projectScopeRadio is not null)
            _projectScopeRadio.IsChecked = !_isGlobalScope;
        _suppressScopeChange = false;

        _directorySection.IsVisible = !_isGlobalScope;
        _agentsHint.Text = Localizer.Get(_isGlobalScope ? "McpLinkGlobalAgentsHint" : "McpLinkAgentsHint");
        SetStatus("", error: false);

        RebuildAgentList();
        UpdateLastUsedState();

        if (_isGlobalScope)
            ScanGlobalTargets();
        else
            ScanDirectory();
    }

    private void RebuildAgentList()
    {
        _agents.Clear();
        _agentList.Children.Clear();

        if (_isGlobalScope && _globalOptions is not null)
        {
            foreach (var target in AgentMcpConfigCatalog.GlobalAll
                         .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
            {
                var displayPath = target.ResolvePath(_globalOptions.Environment);
                AddAgentRow("McpGlobalAgent_" + AgentSlug(target.Id), target.Id, target.Label, displayPath);
            }
            return;
        }

        foreach (var target in AgentMcpConfigCatalog.All
                     .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
        {
            AddAgentRow("McpAgent_" + AgentSlug(target.RelativePath), target.RelativePath, target.Label, target.RelativePath);
        }
    }

    private void AddAgentRow(string name, string key, string label, string displayPath)
    {
        var box = new CheckBox
        {
            Name = name,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = displayPath,
                        TextWrapping = TextWrapping.Wrap,
                        Classes = { "hint", "mono" },
                        FontSize = 11,
                    },
                },
            },
        };
        _agents.Add((key, label, displayPath, box));
        _agentList.Children.Add(box);
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

    private void ScanGlobalTargets()
    {
        if (_globalOptions is null)
            return;

        try
        {
            SetSelectedTargetPaths(_globalOptions.DetectWritten());
        }
        catch
        {
            SetAllChecked(false);
        }

        UpdateActionState();
    }

    private void ScanDirectory()
    {
        if (_suppressDirectoryScan || _isGlobalScope)
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
        if (_isGlobalScope)
        {
            var ids = KnownGlobalTargetIds(_lastGlobalWrittenAgents);
            if (ids.Count > 0)
                SetSelectedTargetPaths(ids);
            return;
        }

        var paths = KnownTargetPaths(_lastWrittenAgents);
        if (paths.Count == 0)
            return;
        SetSelectedTargetPaths(paths);
    }

    private void RememberWrittenAgents(IReadOnlyList<string> paths)
    {
        _lastWrittenAgents = KnownTargetPaths(paths);
        UpdateLastUsedState();
        _rememberWrittenAgents(_lastWrittenAgents);
    }

    private void RememberGlobalWrittenAgents(IReadOnlyList<string> ids)
    {
        if (_globalOptions is null)
            return;

        _lastGlobalWrittenAgents = KnownGlobalTargetIds(ids);
        UpdateLastUsedState();
        _globalOptions.RememberWrittenIds(_lastGlobalWrittenAgents);
    }

    private void UpdateLastUsedState() =>
        _lastUsedButton.IsEnabled = _isGlobalScope
            ? _lastGlobalWrittenAgents.Count > 0
            : _lastWrittenAgents.Count > 0;

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

    private static IReadOnlyList<string> KnownGlobalTargetIds(IEnumerable<string>? ids)
    {
        if (ids is null)
            return [];

        var catalog = new HashSet<string>(
            AgentMcpConfigCatalog.GlobalAll.Select(target => target.Id),
            StringComparer.OrdinalIgnoreCase);
        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && catalog.Contains(id.Trim()))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetAllChecked(bool value)
    {
        foreach (var (_, _, _, box) in _agents)
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
        var enabled = _isGlobalScope || Directory.Exists(TargetDirectoryText.Trim());
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

    private static string AgentSlug(string key)
    {
        var chars = key
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars);
    }
}
