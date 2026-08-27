using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Views;

/// <summary>
/// Closes the Windows IME while a password box holds focus, and puts the previous open status
/// back once focus moves on. Passwords are typed as literal characters, so a Chinese/Japanese
/// IME left open swallows the first keystrokes into a composition window.
/// <para>
/// Only the IME's open status is flipped, the IME is not disabled: a user who really wants a
/// non-ASCII password can still switch it back on inside the box.
/// </para>
/// </summary>
internal static class PasswordImeGuard
{
    private const int WmImeControl = 0x0283;
    private const int ImcGetOpenStatus = 0x0005;
    private const int ImcSetOpenStatus = 0x0006;

    private static bool _installed;
    private static TextBox? _guarded;
    private static WindowBase? _guardedWindow;

    /// <summary>
    /// IME open status captured before the guard closed it, kept until it is handed back.
    /// Null once nothing is owed.
    /// </summary>
    private static bool? _savedOpen;

    /// <summary>
    /// Registers one class handler for the whole application, so every password box is covered,
    /// including the ones dialogs build at runtime.
    /// </summary>
    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        // The class handler runs for every element the event bubbles through, so act only on
        // the one that actually took focus.
        InputElement.GotFocusEvent.AddClassHandler<InputElement>((element, e) =>
        {
            if (ReferenceEquals(element, e.Source))
                OnGotFocus(element);
        });
    }

    internal static bool IsPasswordBox(TextBox box) => box.PasswordChar != default;

    /// <summary>Password box the guard currently holds the IME closed for; exposed for Debug MCP.</summary>
    internal static TextBox? GuardedBox => _guarded;

    private static void OnGotFocus(InputElement element)
    {
        if (element is TextBox box && IsPasswordBox(box))
            Guard(box);
        else
            Release(element);
    }

    private static void Guard(TextBox box)
    {
        if (ReferenceEquals(_guarded, box))
            return;

        if (_guarded is { } previous)
            previous.DetachedFromVisualTree -= OnGuardedDetached;
        _guarded = box;
        _guardedWindow = TopLevel.GetTopLevel(box) as WindowBase;
        box.DetachedFromVisualTree += OnGuardedDetached;

        var ime = ResolveImeWindow(box);
        if (ime == IntPtr.Zero)
            return;

        _savedOpen ??= GetOpenStatus(ime);
        SetOpenStatus(ime, false);
    }

    private static void OnGuardedDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // A dialog can be closed while its password box still holds focus, and nothing else
        // would tell the guard that the box is gone.
        if (ReferenceEquals(_guarded, sender))
            Release(null);
    }

    private static void Release(InputElement? focused)
    {
        if (_guarded is { } box)
        {
            box.DetachedFromVisualTree -= OnGuardedDetached;
            _guarded = null;
        }

        if (_savedOpen != true)
        {
            _savedOpen = null;
            _guardedWindow = null;
            return;
        }

        // Reopening the IME only takes on a control that owns an input context; on a button or
        // a checkbox the request is dropped. Stay owing the IME until focus reaches something
        // that can type.
        if (focused is not null && !TakesTextInput(focused))
            return;

        // Windows attaches the input context to the newly focused control after the focus event,
        // and an open status set before that is lost, so hand the IME back once focus settled.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_guarded is not null || _savedOpen != true)
                    return;

                var ime = ResolveImeWindow(focused);
                if (ime == IntPtr.Zero)
                    return;

                SetOpenStatus(ime, true);
                _savedOpen = null;
                _guardedWindow = null;
            },
            DispatcherPriority.Background);
    }

    private static bool TakesTextInput(InputElement element) =>
        InputMethod.GetIsInputMethodEnabled(element)
        && element is TextBox { IsReadOnly: false } or TerminalControl;

    /// <summary>
    /// The default IME window of this thread, resolved from the window that is in play. A closed
    /// dialog no longer has a handle, so fall back to the main window, which outlives them all.
    /// </summary>
    private static IntPtr ResolveImeWindow(Visual? near)
    {
        var hwnd = GetHandle(TopLevel.GetTopLevel(near));
        if (hwnd == IntPtr.Zero)
            hwnd = GetHandle(_guardedWindow);
        if (hwnd == IntPtr.Zero
            && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            hwnd = GetHandle(desktop.MainWindow);
        }

        return hwnd == IntPtr.Zero ? IntPtr.Zero : ImmGetDefaultIMEWnd(hwnd);
    }

    private static IntPtr GetHandle(TopLevel? window)
    {
        if (window is null)
            return IntPtr.Zero;

        try
        {
            return window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch (Exception)
        {
            // The window is being torn down; there is nothing left to talk to.
            return IntPtr.Zero;
        }
    }

    internal static IntPtr ImeWindowOf(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? IntPtr.Zero : ImmGetDefaultIMEWnd(hwnd);

    internal static bool GetOpenStatus(IntPtr imeWindow) =>
        SendMessage(imeWindow, WmImeControl, ImcGetOpenStatus, IntPtr.Zero) != IntPtr.Zero;

    internal static void SetOpenStatus(IntPtr imeWindow, bool open) =>
        SendMessage(imeWindow, WmImeControl, ImcSetOpenStatus, open ? 1 : 0);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
