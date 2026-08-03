## Rules

- After finishing a feature or fixing a bug
  - Add any interface it needs for testing to the debug MCP interface.
  - Automatically build and launch the program **as Debug** (e.g. `Run.cmd` or `dotnet build` without `-c Release`). Debug MCP only listens in Debug builds.
    - Do **not** use root `Build.cmd` for this loop: it is the **Release** ship script (cleans `bin`, strips PDBs). Use it for packaging/deploy, not agent feature testing.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP (`JeekRemoteManagerDebugMcp.cmd` at the repo root, which starts the fixed per-user `JeekRemoteManagerMcp.exe` with `--surface debug --app <this worktree\bin\JeekRemoteManager.exe>` and forwards stdio to this worktree's named pipe) to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- When reading code, logs and the Debug MCP are not enough to locate a problem, use a debugger:
  - Use netcoredbg on the Debug build to set breakpoints, step, and inspect variables; feed it a command script via stdin, and drive the program to the breakpoint through the Debug MCP.
  - Use dotnet-dump to analyze hangs and crashes.
  - Only attach to the current worktree's process, run the session with a timeout, and always detach when done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- `JeekTools.NET` is a submodule: commit and push it before the parent commit that moves its pointer.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## MCP

Agents talk to a running instance over a Windows named pipe, never a TCP port. There is no loopback HTTP listener, so nothing to allocate or collide over.

- **Stable adapter path.** Agents launch `%LocalAppData%\JeekRemoteManager\Mcp\JeekRemoteManagerMcp.exe` (product configs) or this worktree's root `JeekRemoteManagerDebugMcp.cmd` (Debug only; not under `bin\`), not `bin\JeekRemoteManagerMcp.exe`. The file under `bin\` is only the build output the app copies to the fixed path on startup; a running agent must not lock it, or rebuilds fail. Do not install the fixed adapter during the build step.
- **Debug routing.** `JeekRemoteManagerDebugMcp.cmd` passes `--surface debug --app <this worktree\bin\JeekRemoteManager.exe>`. The adapter derives the worktree instance id from that app directory (or uses HKCU registration) so parallel Debug worktrees never answer for each other. It reconnects on its own when the app restarts.
- **Two surfaces, never merged.** `--surface debug` exposes the object graph, visual tree, and probes, and only listens in Debug builds. `--surface product` exposes connections, sessions, and terminal tools, and ships in Release. The debug `invoke` tool can call anything in the process, so it must never be reachable from a user's agent.
- **Register a tool in two places.** Product: handler in `ProductMcpServer`, schema in `ProductMcpContract`. Debug: handler in `DebugMcpServer`, schema in `DebugMcpContract`. A tool missing from the contract is invisible to clients.
- **Secrets are write-only.** No tool returns a password, a passphrase, or an encrypted blob — only `hasPassword`-style booleans. Build responses from an explicit field whitelist, never by serializing a model, so a field added later cannot leak.
- **Anything that needs the user happens in the GUI.** Secrets the user must type (master password, two-factor) are entered there, never as tool arguments; destructive actions are confirmed there. Activate the window and return a status the agent can poll (`awaiting_user`) rather than blocking a tool call indefinitely.
- Tool work that touches UI state runs on the UI thread through the host's invoker.
