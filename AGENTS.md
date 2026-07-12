## Rules

- After finishing a feature or fixing a bug, automatically build and launch the program and use Debug MCP server to test. If the program is already running, kill the process and run it again.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## Debug MCP server

When debugging or testing the running app, use the debug MCP server (`jrm-debug` in `.mcp.json`) as the primary method — prefer it over synthetic input, temporary harness code, or guessing from logs alone.

Debug builds host it at `http://127.0.0.1:8737/mcp` (port overridable via `JRM_MCP_PORT`, loopback only). Typical workflow after launching the app:

- `describe` — overview of windows, object-path roots, and the log file.
- `get_value` / `set_value` / `invoke` — reflection access to `App`/`Desktop`/`MainWindow`/`MainVm` object paths to read state, change it, and execute commands or methods on the UI thread (e.g. `invoke MainVm.OpenSettingsCommand`, `get_value MainVm.Nodes[0].Name`).
- `list_members` — discover what an object exposes.
- `visual_tree` / `screenshot` — verify what the UI actually shows.
- `read_logs` — tail the current log file with an optional filter.

Implementation: `JeekRemoteManager/Services/DebugMcpServer.cs` (compiled into all builds; the listener only starts in Debug builds).
