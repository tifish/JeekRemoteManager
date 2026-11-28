# Jeek Remote Manager

English | [简体中文](README.zh-CN.md)

A remote connection manager for Windows that organizes SSH, RDP, and VNC connections in one place.

Built with .NET 10 and Avalonia. Windows only.

## Features

- Manage SSH, RDP, and VNC connections in a folder tree
- VNC opens in the TigerVNC viewer (installed through winget on first use), optionally through an SSH tunnel
- Built-in SSH terminal with tabs, drag-to-reorder, session logging, and ZMODEM (rz/sz) file transfer
- SSH jump hosts, local/remote/dynamic port forwarding, and managed background loopback port forwards
- Bastion login templates with automatic session reuse and shared SSH MFA profiles (Google Authenticator QR import and automatic TOTP entry)
- Built-in live server monitor panel (CPU, memory, disk, network) over SSH
- Integrated AI agents: use Antigravity, Claude, Codex, Cursor, GitHub Copilot, Grok, OpenCode, Pi, or T3 Code to operate the current SSH terminal, or use the global AI Agent to manage the application and work across multiple connections; desktop apps and IDEs (Antigravity, Claude, Codex, Cursor, GitHub Copilot, T3 Code) open the prepared workspace directly
- SFTP file browser with remote editing for remote hosts
- WSL support: open terminals and browse files in local WSL distros
- Remote scripts: define reusable scripts with parameters and run them on connections
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
