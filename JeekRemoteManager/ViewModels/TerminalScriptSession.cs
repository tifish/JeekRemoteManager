using System;
using System.Threading;
using System.Threading.Tasks;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

public sealed class TerminalScriptSession
{
    private readonly Func<CancellationToken, Task> _waitUntilConnectedAsync;
    private readonly Func<RemoteScriptSuite, RemoteScriptFile, ConnectionScriptBinding, CancellationToken, Task<RemoteScriptExecutionResult>> _runScriptAsync;
    private readonly Action _activate;
    private readonly Action _showScriptPanel;
    private readonly Action _hideScriptPanel;
    private readonly Func<bool> _isScriptRunning;

    public TerminalScriptSession(
        Connection connection,
        string? sourcePath,
        Func<CancellationToken, Task> waitUntilConnectedAsync,
        Func<RemoteScriptSuite, RemoteScriptFile, ConnectionScriptBinding, CancellationToken, Task<RemoteScriptExecutionResult>> runScriptAsync,
        Action activate,
        Action showScriptPanel,
        Action hideScriptPanel,
        Func<bool>? isScriptRunning = null)
    {
        Connection = connection;
        SourcePath = sourcePath;
        _waitUntilConnectedAsync = waitUntilConnectedAsync;
        _runScriptAsync = runScriptAsync;
        _activate = activate;
        _showScriptPanel = showScriptPanel;
        _hideScriptPanel = hideScriptPanel;
        _isScriptRunning = isScriptRunning ?? (() => false);
    }

    public Connection Connection { get; }

    public string? SourcePath { get; }

    // Session objects are created fresh each time the script chooser opens, so the
    // live flag is delegated back to the underlying terminal view.
    public bool IsScriptRunning => _isScriptRunning();

    public Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default) =>
        _waitUntilConnectedAsync(cancellationToken);

    public Task<RemoteScriptExecutionResult> RunScriptAsync(
        RemoteScriptSuite suite,
        RemoteScriptFile scriptFile,
        ConnectionScriptBinding binding,
        CancellationToken cancellationToken = default) =>
        _runScriptAsync(suite, scriptFile, binding, cancellationToken);

    public void Activate() => _activate();

    public void ShowScriptPanel() => _showScriptPanel();

    public void HideScriptPanel() => _hideScriptPanel();
}
