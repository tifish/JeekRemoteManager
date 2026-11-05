using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MainWindow
{
    private static string? PrivateKeyDropPath(IDataTransfer data)
    {
        var files = data.TryGetFiles()?.Take(2).ToArray();
        if (files is not [IStorageFile file])
            return null;
        var path = file.TryGetLocalPath();
        return path is not null && File.Exists(path) ? path : null;
    }

    private void OnPrivateKeyFileDragOver(object? sender, DragEventArgs e)
    {
        var accepted = sender is TextBox { DataContext: ConnectionEditorViewModel { IsSsh: true } }
            && PrivateKeyDropPath(e.DataTransfer) is not null;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (sender is TextBox box)
            box.Classes.Set("private-key-file-over", accepted);
        e.Handled = true;
    }

    private void OnPrivateKeyFileDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is TextBox box)
            box.Classes.Remove("private-key-file-over");
        e.Handled = true;
    }

    private void OnPrivateKeyFileDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (sender is not TextBox box)
            return;
        box.Classes.Remove("private-key-file-over");
        if (box.DataContext is ConnectionEditorViewModel { IsSsh: true } editor
            && PrivateKeyDropPath(e.DataTransfer) is { } path)
        {
            // Store the original file's path only. Never read, copy or move key material.
            editor.PrivateKeyPath = path;
            e.DragEffects = DragDropEffects.Copy;
        }
    }
}
