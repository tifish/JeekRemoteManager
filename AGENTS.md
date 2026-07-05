## Rules

- After finishing a feature or fixing a bug, automatically build and launch the program for me to test. If the program is already running, kill the process and run it again.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## Debug MCP server

Debug builds host an MCP server at `http://127.0.0.1:8737/mcp` (port overridable via `JRM_MCP_PORT`, loopback only, registered in `.mcp.json` as `jrm-debug`). Use it to inspect and drive the running app while debugging: `describe` for an overview, `get_value`/`set_value`/`invoke` for reflection access to `App`/`Desktop`/`MainWindow`/`MainVm` object paths, plus `list_members`, `visual_tree`, `screenshot`, and `read_logs`. Implementation: `JeekRemoteManager/Services/DebugMcpServer.cs` (compiled out of Release builds).
