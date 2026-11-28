using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MultiConnectionEditorView : UserControl
{
    public MultiConnectionEditorView()
    {
        InitializeComponent();
    }

    private MultiConnectionEditorViewModel? Editor => DataContext as MultiConnectionEditorViewModel;

    private MainWindow? OwnerWindow => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnRunScriptClick(object? sender, RoutedEventArgs e) =>
        OwnerWindow?.HandleRunScriptFromEditor(sender, e);

    private void OnShowPasswordClick(object? sender, RoutedEventArgs e) =>
        OwnerWindow?.HandleShowPasswordClick(sender, e);

    private void OnSshMfaProfileDropDownOpened(object? sender, EventArgs e) =>
        (OwnerWindow?.DataContext as MainWindowViewModel)?.RefreshSshMfaProfileChoices();

    private void OnSshMfaManageClick(object? sender, RoutedEventArgs e) =>
        OwnerWindow?.HandleSshMfaManageClick(sender, e);

    private async void OnLoginCommandsHelpClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } window)
            await window.ShowLoginCommandsHelpAsync();
    }

    private void OnLoginCommandsLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Editor is { IsLoginCommandsModified: true, LoginCommandsWillClear: false } editor)
            editor.LoginCommands = LoginCommandSequence.TrimSurroundingBlankLines(editor.LoginCommands);
    }

    private void OnPrivateKeyFileDragOver(object? sender, DragEventArgs e)
    {
        var accepted = sender is TextBox { DataContext: MultiConnectionEditorViewModel { IsAllSsh: true } }
            && MainWindow.PrivateKeyDropPath(e.DataTransfer) is not null;
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
        if (box.DataContext is MultiConnectionEditorViewModel { IsAllSsh: true } editor
            && MainWindow.PrivateKeyDropPath(e.DataTransfer) is { } path)
        {
            editor.PrivateKeyPath = path;
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnClearShortNameClick(object? sender, RoutedEventArgs e) => Editor?.ClearShortName();
    private void OnRevertShortNameClick(object? sender, RoutedEventArgs e) => Editor?.RevertShortName();

    private void OnClearHostClick(object? sender, RoutedEventArgs e) => Editor?.ClearHost();
    private void OnRevertHostClick(object? sender, RoutedEventArgs e) => Editor?.RevertHost();

    private void OnRevertPortClick(object? sender, RoutedEventArgs e) => Editor?.RevertPort();
    private void OnRevertWslDistroClick(object? sender, RoutedEventArgs e) => Editor?.RevertWslDistro();

    private void OnClearUsernameClick(object? sender, RoutedEventArgs e) => Editor?.ClearUsername();
    private void OnRevertUsernameClick(object? sender, RoutedEventArgs e) => Editor?.RevertUsername();

    private void OnClearPasswordClick(object? sender, RoutedEventArgs e) => Editor?.ClearPassword();
    private void OnRevertPasswordClick(object? sender, RoutedEventArgs e) => Editor?.RevertPassword();

    private void OnRevertSshMfaClick(object? sender, RoutedEventArgs e) => Editor?.RevertSshMfaProfile();
    private void OnRevertTerminalTypeClick(object? sender, RoutedEventArgs e) => Editor?.RevertTerminalType();
    private void OnRevertTerminalEncodingClick(object? sender, RoutedEventArgs e) => Editor?.RevertTerminalEncoding();

    private void OnClearPrivateKeyPathClick(object? sender, RoutedEventArgs e) => Editor?.ClearPrivateKeyPath();
    private void OnRevertPrivateKeyPathClick(object? sender, RoutedEventArgs e) => Editor?.RevertPrivateKeyPath();

    private void OnClearPassphraseClick(object? sender, RoutedEventArgs e) => Editor?.ClearPassphrase();
    private void OnRevertPassphraseClick(object? sender, RoutedEventArgs e) => Editor?.RevertPassphrase();

    private void OnClearLoginCommandsClick(object? sender, RoutedEventArgs e) => Editor?.ClearLoginCommands();
    private void OnRevertLoginCommandsClick(object? sender, RoutedEventArgs e) => Editor?.RevertLoginCommands();

    private void OnClearJumpHostClick(object? sender, RoutedEventArgs e) => Editor?.ClearJumpHost();
    private void OnRevertJumpHostClick(object? sender, RoutedEventArgs e) => Editor?.RevertJumpHost();

    private void OnClearPortForwardsClick(object? sender, RoutedEventArgs e) => Editor?.ClearPortForwards();
    private void OnRevertPortForwardsClick(object? sender, RoutedEventArgs e) => Editor?.RevertPortForwards();

    private void OnClearWslStartDirectoryClick(object? sender, RoutedEventArgs e) => Editor?.ClearWslStartDirectory();
    private void OnRevertWslStartDirectoryClick(object? sender, RoutedEventArgs e) => Editor?.RevertWslStartDirectory();

    private void OnRevertRdpWidthClick(object? sender, RoutedEventArgs e) => Editor?.RevertRdpWidth();
    private void OnRevertRdpHeightClick(object? sender, RoutedEventArgs e) => Editor?.RevertRdpHeight();

    private void OnClearNotesClick(object? sender, RoutedEventArgs e) => Editor?.ClearNotes();
    private void OnRevertNotesClick(object? sender, RoutedEventArgs e) => Editor?.RevertNotes();
}
