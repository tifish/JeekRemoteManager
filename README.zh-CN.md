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
- 集成终端的 AI Agent 聊天（Claude、Codex、OpenAI 兼容 API）
- 从 FinalShell 导入连接
- 使用主密钥加密保存密码
- 一键向远程主机安装 SSH 公钥
- 自动更新
