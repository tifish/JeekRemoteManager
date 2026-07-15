using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using JeekRemoteManager.Services;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager;

internal static class Program
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(Program));
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    internal static event Action? ActivationRequested;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(true, DebugInstanceContext.SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return;
        }

        StartActivationListener();
        SetCurrentProcessExplicitAppUserModelID(DebugInstanceContext.AppUserModelId);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log.ZLogCritical(ex, $"Unhandled exception (terminating={e.IsTerminating})");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.ZLogError(e.Exception, $"Unobserved task exception");
            e.SetObserved();
        };

        Log.ZLogInformation($"JeekRemoteManager starting (build {AutoUpdateService.GetLocalCommitCount()})");

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.ZLogCritical(ex, $"Fatal error running the application");
            throw;
        }
        finally
        {
            Log.ZLogInformation($"JeekRemoteManager exiting");
            _activationEvent?.Dispose();
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private static void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DebugInstanceContext.ActivationEventName);
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
            using var activationEvent = EventWaitHandle.OpenExisting(DebugInstanceContext.ActivationEventName);
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

            try
            {
                if (!DebugInstanceContext.IsCurrentExecutable(process.MainModule?.FileName))
                    continue;
            }
            catch
            {
                continue;
            }

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
            // Cascadia Mono (terminal default) has no CJK glyphs; prefer system CJK faces
            // before the platform's generic fallback when matching missing codepoints.
            .With(new FontManagerOptions
            {
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei") },
                    new FontFallback { FontFamily = new FontFamily("NSimSun") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans SC") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
                    new FontFallback { FontFamily = new FontFamily("Source Han Sans SC") },
                    new FontFallback { FontFamily = new FontFamily("PingFang SC") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                    new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
                ],
            })
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
