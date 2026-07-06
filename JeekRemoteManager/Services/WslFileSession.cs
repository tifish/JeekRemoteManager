using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// File browser backend for WSL connections: plain System.IO against the
/// distribution's \\wsl.localhost UNC share, with the browser's Unix-style paths
/// translated at the boundary. The 9P share exposes no Unix permissions, so
/// <see cref="SupportsPermissions"/> is false and chmod is unavailable.
/// Operations are serialized like <see cref="SftpSession"/> so a slow transfer
/// cannot interleave with a listing on the same session.
/// </summary>
public sealed class WslFileSession : IFileSystemSession
{
    private readonly string _distro;
    private readonly string _user;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WslOps? _ops;
    private volatile bool _disposed;

    public WslFileSession(string distro, string user)
    {
        _distro = distro.Trim();
        _user = user.Trim();
    }

    public string? HomePath { get; private set; }

    public bool SupportsPermissions => false;

    public async Task<T> RunAsync<T>(Func<IFileSystemOps, T> operation, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => operation(EnsureReady()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WslOps EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ops is { } ready)
            return ready;

        if (_distro.Length == 0)
            throw new InvalidOperationException("No WSL distribution is configured.");

        HomePath ??= QueryHomePath();
        _ops = new WslOps(WslDistroService.UncRoot(_distro), HomePath);
        return _ops;
    }

    /// <summary>Asks the distribution for the connection user's $HOME. Also serves as
    /// the "connect" step: a missing distribution fails here with wsl.exe's message.</summary>
    private string QueryHomePath()
    {
        var psi = new ProcessStartInfo(WslDistroService.WslExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--distribution");
        psi.ArgumentList.Add(_distro);
        if (_user.Length > 0)
        {
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add(_user);
        }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("printf %s \"$HOME\"");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start wsl.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException("WSL did not respond while resolving the home directory.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"wsl.exe exited with code {process.ExitCode}."
                : error.Trim());
        }

        var home = output.Trim();
        return home.StartsWith('/') ? home : "/";
    }

    private sealed class WslOps : IFileSystemOps
    {
        private readonly string _uncRoot;

        public WslOps(string uncRoot, string homePath)
        {
            _uncRoot = uncRoot;
            WorkingDirectory = homePath;
        }

        public string WorkingDirectory { get; }

        /// <summary>"/home/user" → "\\wsl.localhost\Distro\home\user".</summary>
        private string ToUnc(string unixPath)
        {
            var normalized = unixPath.Replace('/', '\\');
            if (!normalized.StartsWith('\\'))
                normalized = "\\" + normalized;
            var result = _uncRoot + normalized.TrimEnd('\\');
            // "/" maps to the share root, which needs its trailing separator back.
            return result == _uncRoot ? _uncRoot + "\\" : result;
        }

        public IEnumerable<FileSystemEntry> ListDirectory(string path)
        {
            var directory = new DirectoryInfo(ToUnc(path));
            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                var isSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                yield return new FileSystemEntry(
                    info.Name,
                    CombineUnix(path, info.Name),
                    isDirectory,
                    isSymlink,
                    info is FileInfo file ? file.Length : 0,
                    info.LastWriteTime,
                    Permissions: "");
            }
        }

        private static string CombineUnix(string directory, string name) =>
            directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

        public void CreateDirectory(string path) => Directory.CreateDirectory(ToUnc(path));

        public void RenameFile(string oldPath, string newPath)
        {
            var source = ToUnc(oldPath);
            var target = ToUnc(newPath);
            if (Directory.Exists(source))
                Directory.Move(source, target);
            else
                File.Move(source, target);
        }

        public void DeleteFile(string path)
        {
            var unc = ToUnc(path);
            // A symlink to a directory surfaces as a directory over UNC; deleting
            // it non-recursively removes the link without touching its target.
            if (Directory.Exists(unc))
                Directory.Delete(unc, recursive: false);
            else
                File.Delete(unc);
        }

        public void DeleteDirectory(string path) => Directory.Delete(ToUnc(path), recursive: false);

        public bool Exists(string path)
        {
            var unc = ToUnc(path);
            return File.Exists(unc) || Directory.Exists(unc);
        }

        public void ChangePermissions(string path, short mode) =>
            throw new NotSupportedException("Permissions are not available over \\\\wsl.localhost.");

        public void UploadFile(Stream source, string remotePath, Action<ulong> progress)
        {
            using var destination = File.Create(ToUnc(remotePath));
            CopyWithProgress(source, destination, progress);
        }

        public void DownloadFile(string remotePath, Stream destination, Action<ulong> progress)
        {
            using var source = File.OpenRead(ToUnc(remotePath));
            CopyWithProgress(source, destination, progress);
        }

        private static void CopyWithProgress(Stream source, Stream destination, Action<ulong> progress)
        {
            var buffer = new byte[128 * 1024];
            ulong total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, read);
                total += (ulong)read;
                progress(total);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
