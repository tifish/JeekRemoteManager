# Jeek Remote Manager

English | [简体中文](README.zh-CN.md)

A remote connection manager for Windows that organizes SSH and RDP connections in one place.

Built with .NET 10 and Avalonia. Windows only.

## Features

- Manage SSH and RDP connections in a folder tree
- Built-in SSH terminal with tabs, drag-to-reorder, and ZMODEM (rz/sz) file transfer
- SFTP file browser for remote hosts
- WSL support: open terminals and browse files in local WSL distros
- Remote scripts: define reusable scripts with parameters and run them on connections
- Integrated AI agents with three launch modes: CLI (side-panel ConPTY, default), Windows Terminal, or Desktop (Claude/Codex protocol handlers); remote SSH/WSL actions via product MCP
- Import connections from FinalShell, SecureCRT, and Xshell
- Passwords encrypted with a master key
- One-click public key installation to remote hosts
- Auto update

## Installation

Run in PowerShell:

```powershell
irm https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1 | iex
```

If GitHub is hard to reach (e.g. in mainland China), use the mirror:

```powershell
irm https://ghfast.top/https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1 | iex
```

The app is installed to `%LOCALAPPDATA%\Programs\JeekRemoteManager` with a Start Menu shortcut. No registry entries are written; to uninstall, quit the app and delete the install directory and the shortcut.
