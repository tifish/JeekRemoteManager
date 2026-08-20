# 总体架构

## 目标

JeekRemoteManager 是一个 Windows 桌面的远程连接管理器：把 SSH / RDP / WSL 连接组织成文件夹树，SSH 在应用内的终端里跑，并在同一个终端旁边挂上 AI 助手、SFTP 文件浏览、服务器监控三个面板。

架构上有三条贯穿全局的取向：

- **一切围绕"一条终端字节流"展开。** SSH shell channel 和本地 ConPTY 进程被抽象成同一个 `ITerminalChannel`，登录命令、脚本执行、ZMODEM、AI 工具全都建在这个抽象之上，而不是各自再开一条链路。
- **凭据只进不出。** 密码、私钥口令一律以主密码派生的 `jrm1` 信封落盘，任何对外接口（MCP 工具、日志、诊断快照）只回答"有没有"，不回答"是什么"。
- **可被 agent 驱动。** 应用自带两套 MCP 服务端：产品面给用户的 AI 用，调试面给开发用的 agent 用。两套面永不合并。

## 进程与模块

```
JeekRemoteManager.exe            Avalonia 桌面应用（唯一的有状态进程）
├── Models/                      纯数据：Connection、AppSettings、RemoteScript、BastionLoginProfile
├── Services/                    无 UI 依赖的机制层：SSH、ConPTY、ZMODEM、加密、存储、MCP 服务端
├── ViewModels/                  MVVM（CommunityToolkit.Mvvm）
├── Views/                       Avalonia 视图；TerminalView 是最重的一个，承载整条终端管线
└── Controls/                    自定义控件（登录命令编辑器）

JeekRemoteManagerMcp.exe         MCP stdio 适配器（无状态转发器）
JeekTools.NET/                   submodule：McpHost、AutoUpdater、SettingsStorage、ObjectGraph 等通用件
Tests/SmokeTest/                 控制台断言集，直接引用主工程的类
Tools/LegacyPasswordConverter/   历史密码格式迁移工具
```

外部依赖里有三个决定了设计边界：

| 依赖 | 作用 | 带来的约束 |
| --- | --- | --- |
| SSH.NET | SSH 传输、SFTP | 没有公开 API 在已有 `SshClient` 上开 SFTP 子系统通道；`SftpClient` 单通道非线程安全；同步的开通道调用可能永久阻塞 |
| SvcSystems.UI.Terminal + XTerm.NET | 终端渲染 | 不绘制 SGR dim；`TerminalBuffer.Resize` 的光标处理有缺陷，需要外部修补 |
| Avalonia 12 | UI 框架 | UI 线程模型决定了所有面板的调度方式 |

## 启动顺序

`Program.Main` → `App.OnFrameworkInitializationCompleted` → `UnlockThenStartAsync`，顺序本身是有意义的：

1. **单实例互斥 + 激活事件。** 第二个实例只负责唤醒已有窗口然后退出。Debug 构建的互斥体名带 worktree 实例 id，所以并行 worktree 不会互相顶掉。
2. **清掉 `NO_COLOR`。** agent shell 会导出 `NO_COLOR=1`，内嵌的 CLI 继承本进程环境后会失去颜色。必须在任何子进程创建之前清掉。
3. **字体回退链。** 终端默认字体 Cascadia Mono 没有 CJK 字形，显式排出中日韩字体的回退顺序，不能依赖平台的通用回退。
4. **注册固定 MCP 适配器。** 失败只记警告，不阻断 GUI。
5. **`ShutdownMode = OnExplicitShutdown`。** 关主窗口只是隐藏到托盘，退出走托盘菜单。
6. **启动两套 MCP 服务端。** Debug 面在 Release 下是空转（代码照样编译，只是不监听）。
7. **主密码闸门。** 先试 DPAPI 缓存静默解锁；解出来的密钥还必须能真正解开至少一个已存在的 `jrm1` blob，否则视为不可用并重新提问。用户取消就退出进程——没有"跳过加密"这条路。
8. 主窗口构建、托盘菜单构建、后台更新检查。

## 线程模型

有三类线程，边界必须清楚：

- **UI 线程。** 所有 Avalonia 控件、ViewModel 属性、终端 model feed。MCP 工具里凡是碰 UI 状态的，都通过 host 的 `UiInvoker` 投递过去。
- **网络/IO 后台线程。** SSH 握手（可能查询 ssh-agent，会阻塞）、ConPTY 读线程、SFTP 操作队列、监控采样循环。这些线程上的异常必须自己兜住：后台线程抛出会直接带走进程。
- **定时器线程。** 输出帧合并、resize 静默期、ZMODEM 触发串冲刷、面板挂起宽限期。

`ConPtySession` 是这套边界最典型的例子：读线程和等待退出线程整段包在 try 里，`Write` 和 `Dispose` 用两把不同的锁——因为写可能卡在满的输入管道上，而 `Dispose` 绝不能排在一次可能永不返回的写后面。

## 数据落盘的分层

| 位置 | 内容 | 为什么在这里 |
| --- | --- | --- |
| `%LOCALAPPDATA%\JeekRemoteManager\Config` | 机器本地设置、known_hosts、DPAPI 主密码缓存 | 与机器/账户绑定，不该跟着漫游或便携数据走 |
| 活动存储位置的 `Config\` | 漫游偏好、连接树、脚本套件、堡垒机模板 | 可以跟着 AppData / 便携目录 / 自定义目录搬家 |
| `%LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces` | 每个终端标签页的 AI 工作区 | 生成物，机器本地，与便携模式无关 |
| `%LOCALAPPDATA%\JeekRemoteManager\Mcp` | 固定路径的 MCP 适配器 | agent 配置里要写死这个路径，必须永不变化 |
| `%LOCALAPPDATA%\JeekRemoteManager\Update` | 暂存的更新包 | 放系统 temp 会被清理，推迟安装就失效了 |

详见 [存储与设置](storage.md)。

## 各机制文档

见 [README](README.md) 的索引。
