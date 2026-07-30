## Rules

- After finishing a feature or fixing a bug
  - Add any interface it needs for testing to the debug MCP interface.
  - Automatically build and launch the program.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP (`bin\JeekRemoteManagerMcp.exe --surface debug`, which forwards stdio to this worktree's named pipe) to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- When reading code, logs and the Debug MCP are not enough to locate a problem, use a debugger:
  - Use netcoredbg on the Debug build to set breakpoints, step, and inspect variables; feed it a command script via stdin, and drive the program to the breakpoint through the Debug MCP.
  - Use dotnet-dump to analyze hangs and crashes.
  - Only attach to the current worktree's process, run the session with a timeout, and always detach when done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- `JeekTools.NET` is a submodule: commit and push it before the parent commit that moves its pointer.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## MCP

Agents talk to a running instance over a Windows named pipe, never a TCP port. `bin\JeekRemoteManagerMcp.exe` is the stdio adapter they launch; it derives the pipe name from its own folder, so a worktree's copy only ever reaches that worktree's app, and it reconnects on its own when the app restarts.

- **Two surfaces, never merged.** `--surface debug` exposes the object graph, visual tree, and probes, and only listens in Debug builds. `--surface product` exposes connections, sessions, and terminal tools, and ships to users. The debug `invoke` tool can call anything in the process, so it must never be reachable from a user's agent.
- **Register a tool in two places.** Product: handler in `ProductMcpServer`, schema in `ProductMcpContract`. Debug: handler in `DebugMcpServer`, schema in `DebugMcpContract`. A tool missing from the contract is invisible to clients.
- **Passwords are write-only.** No tool returns a password, a passphrase, or an encrypted blob — only `hasPassword`-style booleans. Build responses from an explicit field whitelist, never by serializing a model, so a field added later cannot leak. Secrets the user must type (master password, two-factor) are entered in the GUI and are never tool arguments.
- **Anything that needs the user happens in the GUI.** Activate the window and return a status the agent can poll (`awaiting_user`) rather than blocking a tool call indefinitely.
- Tool work that touches UI state runs on the UI thread through the host's invoker.
