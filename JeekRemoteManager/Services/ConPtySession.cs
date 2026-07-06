using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace JeekRemoteManager.Services;

/// <summary>
/// A local process attached to a Windows pseudo console (ConPTY). The read side
/// delivers VT-sequence bytes exactly like an SSH shell channel, so the same
/// terminal control consumes both. The child runs inside a kill-on-close job
/// object, so closing the session tears down the whole process tree (wsl.exe
/// keeps children alive otherwise).
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _job;
    private readonly FileStream _writer;
    private readonly object _writeGate = new();
    private volatile bool _disposed;
    private int _exitRaised;

    /// <summary>Raised on a background thread for every chunk the child writes.</summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>Raised once, on a background thread, when the child exits.</summary>
    public event Action<int>? Exited;

    private ConPtySession(
        IntPtr pseudoConsole,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead,
        SafeFileHandle process,
        SafeFileHandle job)
    {
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = process;
        _job = job;
        _writer = new FileStream(_inputWrite, FileAccess.Write);
    }

    /// <summary>Starts <paramref name="exePath"/> under a new pseudo console.</summary>
    public static ConPtySession Start(string exePath, IReadOnlyList<string> arguments, int cols, int rows)
    {
        CreatePipePair(out var inputRead, out var inputWrite);
        SafeFileHandle? outputRead = null, outputWrite = null;
        var pseudoConsole = IntPtr.Zero;
        SafeFileHandle? process = null, job = null;
        try
        {
            CreatePipePair(out outputRead, out outputWrite);

            var size = new Coord { X = (short)Math.Max(20, cols), Y = (short)Math.Max(5, rows) };
            Check(CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole), "CreatePseudoConsole");

            // The pseudo console holds its own references to these two ends.
            inputRead.Dispose();
            outputWrite.Dispose();
            inputRead = null!;
            outputWrite = null;

            process = SpawnAttachedProcess(pseudoConsole, BuildCommandLine(exePath, arguments));
            job = CreateKillOnCloseJob();
            if (!AssignProcessToJobObject(job, process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");

            var session = new ConPtySession(pseudoConsole, inputWrite, outputRead, process, job);
            session.StartReadLoop();
            session.StartExitWatch();
            return session;
        }
        catch
        {
            if (process is not null && !process.IsInvalid)
                TerminateProcess(process, 1);
            if (pseudoConsole != IntPtr.Zero)
                ClosePseudoConsole(pseudoConsole);
            inputRead?.Dispose();
            inputWrite.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            process?.Dispose();
            job?.Dispose();
            throw;
        }
    }

    public void Write(byte[] data)
    {
        if (_disposed || data.Length == 0)
            return;
        lock (_writeGate)
        {
            _writer.Write(data, 0, data.Length);
            _writer.Flush();
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed)
            return;
        var size = new Coord { X = (short)Math.Max(20, cols), Y = (short)Math.Max(5, rows) };
        ResizePseudoConsole(_pseudoConsole, size);
    }

    private void StartReadLoop()
    {
        var thread = new Thread(() =>
        {
            // The whole body is guarded: an exception on a background thread would
            // take down the process, and every failure here just means "stream over".
            try
            {
                using var reader = new FileStream(_outputRead, FileAccess.Read);
                var buffer = new byte[8192];
                while (true)
                {
                    var read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    DataReceived?.Invoke(buffer.AsSpan(0, read).ToArray());
                }
            }
            catch
            {
                // Pipe broken on teardown — exit is reported by the wait thread.
            }
        })
        {
            IsBackground = true,
            Name = "ConPtyRead",
        };
        thread.Start();
    }

    private void StartExitWatch()
    {
        var thread = new Thread(() =>
        {
            // Guarded like the read loop: Dispose() closes _process while this
            // thread may sit between WaitForSingleObject and GetExitCodeProcess,
            // and touching the closed SafeHandle throws ObjectDisposedException.
            try
            {
                WaitForSingleObject(_process, Infinite);
                if (!GetExitCodeProcess(_process, out var code))
                    code = unchecked((uint)-1);
                if (!_disposed && Interlocked.Exchange(ref _exitRaised, 1) == 0)
                    Exited?.Invoke(unchecked((int)code));
            }
            catch
            {
                // Torn down mid-wait; nobody is listening for the exit anymore.
            }
        })
        {
            IsBackground = true,
            Name = "ConPtyWait",
        };
        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Exchange(ref _exitRaised, 1);

        // Kill the tree first so the child stops writing, then close the pseudo
        // console. ClosePseudoConsole blocks until the output pipe is drained —
        // the read thread keeps draining until it sees EOF, so this cannot hang.
        try { TerminateJobObject(_job, 0); } catch { /* already dead */ }
        try { ClosePseudoConsole(_pseudoConsole); } catch { /* ignore */ }
        try { _writer.Dispose(); } catch { /* ignore */ }
        _process.Dispose();
        _job.Dispose();
        // _outputRead is owned (and disposed) by the read thread's FileStream.
    }

    // ---- Process creation plumbing ----

    private static string BuildCommandLine(string exePath, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { exePath }.Concat(arguments).Select(QuoteArgument));

    /// <summary>Standard Windows command-line quoting (backslashes double before quotes).</summary>
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(c => c is not (' ' or '\t' or '"' or '\\')))
            return argument;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                backslashes = 0;
                sb.Append('"');
                continue;
            }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static SafeFileHandle SpawnAttachedProcess(IntPtr pseudoConsole, string commandLine)
    {
        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");

            try
            {
                if (!UpdateProcThreadAttribute(
                        attributeList, 0, ProcThreadAttributePseudoConsole,
                        pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
                }

                var startupInfo = new StartupInfoEx();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                startupInfo.lpAttributeList = attributeList;

                if (!CreateProcessW(
                        null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false,
                        ExtendedStartupInfoPresent, IntPtr.Zero, null,
                        ref startupInfo, out var processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed.");
                }

                CloseHandle(processInfo.hThread);
                return new SafeFileHandle(processInfo.hProcess, ownsHandle: true);
            }
            finally
            {
                DeleteProcThreadAttributeList(attributeList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ptr, (uint)length))
            {
                job.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
    }

    private static void Check(int hresult, string what)
    {
        if (hresult != 0)
            throw new Win32Exception(hresult, $"{what} failed (0x{hresult:X8}).");
    }

    // ---- P/Invoke ----

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x20016;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, SafeFileHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle hProcess, out uint lpExitCode);
}
