## Rules

- After finishing a feature or fixing a bug
  - Add any interface it need for testing to debug MCP interface.
  - Automatically build and launch the program.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP bridge to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.
