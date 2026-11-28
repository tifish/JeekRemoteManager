# Jeek Remote Manager

[English](README.md) | 简体中文

一个 Windows 平台的远程连接管理工具，集中管理 SSH、RDP 和 VNC 连接。

基于 .NET 10 + Avalonia 开发，仅支持 Windows。

## 功能特性

- 以文件夹树的方式管理 SSH、RDP 和 VNC 连接
- VNC 在 TigerVNC 查看器中打开（首次使用时通过 winget 安装），可经 SSH 隧道连接
- 内置 SSH 终端，支持多标签页、拖拽排序、会话日志记录，以及 ZMODEM（rz/sz）文件传输
- 支持 SSH 跳板机、本地/远程/动态端口转发，以及独立的后台环回端口转发管理
- 支持堡垒机共享登录模板与会话复用，以及在连接面板维护共享 SSH 二次验证配置（支持导入 Google Authenticator 二维码并自动填写 TOTP 验证码）
- 内置 SSH 实时服务器监控面板（CPU、内存、磁盘、网络）
- 集成 AI Agent，支持使用 Antigravity/Claude/Codex/Cursor/GitHub Copilot/Grok/OpenCode/Pi/T3 Code 操作当前 SSH 终端，也可通过全局 AI Agent 管理整个软件并操作多个连接；支持通过桌面应用或 IDE（Antigravity、Claude、Codex、Cursor、GitHub Copilot、T3 Code）直接打开准备好的工作区
- SFTP 远程文件浏览器，支持本地编辑器远程直编与自动回传
- 支持 WSL：可打开本地 WSL 发行版的终端并浏览其文件
- 远程脚本：定义带参数的可复用脚本，在连接上一键执行
- 从 FinalShell、SecureCRT、Xshell 导入连接
- 使用主密钥加密保存密码
- 一键向远程主机安装 SSH 公钥
- 自动更新

## 安装

在 PowerShell 中运行:

```powershell
irm https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1 | iex
```

中国大陆可使用镜像地址:

```powershell
irm https://ghfast.top/https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1 | iex
```

程序会安装到 `%LOCALAPPDATA%\Programs\JeekRemoteManager` 并创建开始菜单快捷方式。不写注册表;卸载时退出程序后删除安装目录和快捷方式即可。
