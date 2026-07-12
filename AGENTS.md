## Rules

- After finishing a feature or fixing a bug
  - Add any interface it need for testing to debug MCP interface.
  - Automatically build and launch the program. If the program is already running, kill the process and run it again.
  - Use Debug MCP to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.
