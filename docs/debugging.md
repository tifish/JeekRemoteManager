# 调试设施与测试

涉及 `Services/DebugMcpServer.cs`、`DebugMcpContract.cs`、`DebugInstanceContext.cs`、`Tests/SmokeTest/Program.cs`，以及 `AGENTS.md` 里的迭代流程。

## 为什么要有 Debug MCP

这是个 GUI 应用，绝大多数机制（终端渲染、堡垒机登录、面板生命周期、AI 面板）只有在**真的跑起来**的时候才能验证。Debug MCP 让 agent 能像人一样驱动运行中的实例：读对象图、读可视树、截图、读日志，以及跑一组针对性的**探针工具**。

调试面只在 Debug 构建里监听（见 [MCP 架构](mcp.md)）。

## 三类工具

**1. 通用对象访问**（来自 JeekTools 的 `ObjectGraph`）

`describe`、`get_value`、`set_value`、`invoke`、`list_members`、`visual_tree`、`screenshot`、`read_logs`。根对象是 `App` / `Desktop` / `MainWindow` / `MainVm`，路径里的 `#Name` 段在可视树下按名字找控件。

`invoke` 能调用进程里的任何东西——这正是调试面**绝不能**对用户的 agent 开放的原因。

**2. `*_check` 探针**

每个探针把某一个机制的关键不变量在**真实代码路径**上跑一遍并返回结论。当前覆盖：终端标签标题/焦点/生命周期/连接动作、输出合并与背压、连接树加载与重载顺序、连接文件写入监视、SFTP 重试策略、SSH 认证提示、主机密钥信任、ZMODEM 检测延迟与子包上限、监控挂起、终端字体同步、AI 面板生命周期、文件浏览器会话生命周期、AI CLI 的 Ctrl+C、agent 定位/探测缓存/MCP 配置、登录菜单选择、登录命令流程/补全/变量、堡垒机模板与预设、ConPTY 拆除竞态、堡垒机通道上限、复用落点、连接编辑器切换、产品 MCP、MCP 传输、项目链接、全局 agent、自动更新暂存。

**3. 交互式 probe**

`login_menu_select_probe`（open / status / close，支持 single / paged / switch 三种场景）和 `ai_render_probe`（open / status / hide / close）会开出一个真实但不联网的场景，供多步验证。

## 为测试而暴露的接口是一等公民

代码里有大量 `/// <summary>… exposed for Debug MCP …</summary>` 的属性和方法。这是刻意的设计：**新增功能或修复缺陷后，要把它需要的测试接口加进 Debug MCP**（`AGENTS.md` 的第一条规则）。

这类接口遵循几个模式：

- **返回渲染后的事实**而不是意图：实际字体大小、实际面板列宽、实际可见文本、实际转发的按键字节的十六进制。
- **走真实路由**：`DebugRaiseFunctionKey` 之类的方法触发的是真正的按键处理链，而不是绕过它去调内部方法。
- **提供无网络的夹具**：`SetConnectionWithoutStarting`、指向一个会拒绝连接的本地端口的探针标签页、SSH 类型的假标签页。这样面板的状态机（挂起/恢复、生命周期）可以在完全不碰真实服务器的情况下验证——因为这些状态机本来就与"采样是否成功"无关。

## SmokeTest

`Tests/SmokeTest` 是控制台断言集，**直接引用主工程的类**（不是黑盒测试）。它覆盖那些纯逻辑、不需要 GUI 的部分：`ConnectionStore` 的文件操作、`Utf8ChunkAssembler` 的边界、`TerminalDimColorFilter` 的 SGR 解析、MCP 适配器的文件元数据比较、登录命令解析等。

它在一个隔离的临时根目录下工作，不碰用户的真实数据。

**注意**：`dotnet run` 跑 SmokeTest 会重新构建 `JeekRemoteManager`，所以必须先停掉运行中的应用（见 [构建与发布](build-release.md)）。

## 迭代流程

`AGENTS.md` 里定义的循环：

1. 改完功能/修完缺陷 → 把需要的测试接口加进 Debug MCP。
2. 以 **Debug** 构建并启动（`Run.cmd` 或不带 `-c Release` 的 `dotnet build`）。
3. 若本 worktree 的程序在跑，**只杀可执行路径匹配本 worktree 的那个进程**，其它 worktree 的 Debug 实例保留。
4. 停掉应用后，**一并清掉残留的 `JeekRemoteManagerMcp.exe` 适配器**。每个适配器占一个命名管道实例，攒多了应用会记录 "All pipe instances are busy"，后续 Debug MCP 调用超时。
5. 用本 worktree 的 `JeekRemoteManagerDebugMcp.cmd` 测试，有问题就修，直到通过。

## 读代码和日志都不够的时候

- **netcoredbg** 对 Debug 构建下断点、单步、看变量：用 stdin 喂命令脚本，通过 Debug MCP 把程序驱动到断点。
- **dotnet-dump** 分析挂起和崩溃。
- 两者都**只附加到本 worktree 的进程**，会话要带超时，用完必须 detach。

## 环境变量

- `JRM_AI_CAPTURE_DIR` —— 录制嵌入式 AI CLI 的原始 ConPTY 字节流，外加一个 `.resizes` 侧车文件（每行 `字节偏移:COLSxROWS`），用于离线重放渲染 bug。
- `JRM_ZMODEM_TRACE` —— 开启 ZMODEM 收发字节跟踪日志，写到实例运行时临时目录下的 `ZmodemLogs`。
