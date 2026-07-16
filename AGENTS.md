## Rules

- After finishing a feature or fixing a bug
  - Add any interface it need for testing to debug MCP interface.
  - Automatically build and launch the program.
    - **Grok Build only:** its shell runs inside a kill-on-close Job Object. Children of that shell (`Start-Process`, `start ""`, background tasks, `Start-Job`) die when the step ends — even if “detached”. Claude / Codex / other agents are unaffected; normal launch is fine for them.
    - **When running under Grok Build, launch via `Launch.cmd` (or `Run.cmd` after build).** It uses `Win32_Process.Create` (WMI) so `JeekRemoteManager.exe` is created outside Grok’s job. Do not rely on `Start-Process` / `start` from a Grok shell.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree's `bin\JeekRemoteManager.exe`, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP bridge to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.
