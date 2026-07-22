# Jeek Remote Manager

[English](README.md) | 简体中文

一个 Windows 平台的远程连接管理工具，集中管理 SSH 和 RDP 连接。

基于 .NET 10 + Avalonia 开发，仅支持 Windows。

## 功能特性

- 以文件夹树的方式管理 SSH 和 RDP 连接
- 内置 SSH 终端，支持多标签页、拖拽排序，以及 ZMODEM（rz/sz）文件传输
- SFTP 远程文件浏览器
- 支持 WSL：可打开本地 WSL 发行版的终端并浏览其文件
- 远程脚本：定义带参数的可复用脚本，在连接上一键执行
- 集成 AI Agent，三种运行模式可选其一：CLI（侧栏 ConPTY，默认）、Windows Terminal、Desktop（仅 Claude/Codex 协议启动）；通过 MCP 操作当前 SSH/WSL 终端
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
