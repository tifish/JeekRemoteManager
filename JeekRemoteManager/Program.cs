using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace JeekRemoteManager;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private const string SingleInstanceMutexName = "JeekRemoteManager.App.SingleInstance";
    private const string ActivationEventName = "JeekRemoteManager.App.Activate";

    internal static event Action? ActivationRequested;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return;
        }

        StartActivationListener();
        SetCurrentProcessExplicitAppUserModelID("JeekRemoteManager.App");

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _activationEvent?.Dispose();
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private static void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _ = Task.Run(() =>
        {
            var activationEvent = _activationEvent;
            if (activationEvent is null)
                return;

            try
            {
                while (activationEvent.WaitOne())
                    ActivationRequested?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                // Normal shutdown.
            }
        });
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch
        {
            // Older/racing instances may not have the event yet; use the legacy
            // visible-window activation as a fallback.
            ActivateExistingInstance();
            return;
        }

        ActivateExistingInstance();
    }

    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id)
                continue;

            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
                continue;

            if (IsIconic(handle))
                ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
            break;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
