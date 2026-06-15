using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.ViewModels;

/// <summary>
/// A node in the connection tree: either a folder or a single connection.
/// <see cref="FullPath"/> is the file-system path it maps to.
/// </summary>
public partial class TreeNodeViewModel : ViewModelBase
{
    public TreeNodeViewModel(string fullPath, bool isFolder, Connection? connection = null)
    {
        FullPath = fullPath;
        IsFolder = isFolder;
        Connection = connection;
        _name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
        if (isFolder)
            _name = System.IO.Path.GetFileName(fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }

    public bool IsFolder { get; }

    public bool IsConnection => !IsFolder;

    /// <summary>Parent folder node, or null for top-level nodes. Set while building the tree.</summary>
    public TreeNodeViewModel? Parent { get; set; }

    /// <summary>The loaded connection for connection nodes; null for folders.</summary>
    public Connection? Connection { get; }

    /// <summary>Absolute path to the folder or connection file this node represents.</summary>
    [ObservableProperty]
    private string _fullPath;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>True while this node is the source of a pending "cut" operation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeOpacity))]
    private bool _isCut;

    /// <summary>Dimmed while cut, to signal the pending move.</summary>
    public double NodeOpacity => IsCut ? 0.5 : 1.0;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    /// <summary>Icon glyph shown in the tree for this node.</summary>
    public string Glyph => IsFolder
        ? "\U0001F4C1"                                   // folder
        : Connection?.Type == ConnectionType.Rdp
            ? "\U0001F5A5"                               // desktop computer (RDP)
            : "⌨";                                  // keyboard (SSH)
}
