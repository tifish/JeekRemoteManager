# MCP 架构（双面 + 命名管道）

涉及 `Services/ProductMcpServer.cs`、`ProductMcpContract.cs`、`DebugMcpServer.cs`、`DebugMcpContract.cs`、`McpPipeNames.cs`、`McpAdapterRegistry.cs`、`McpAdapterRegistration.cs`、`DebugInstanceContext.cs`、`Tools/JeekRemoteManagerMcp/Program.cs`，以及 JeekTools 的 `McpHost.cs`、`McpPipeServer.cs`、`ObjectGraph.cs`。

## 两个面，永不合并

| | 产品面 (`--surface product`) | 调试面 (`--surface debug`) |
| --- | --- | --- |
| 内容 | 连接、会话、终端工具 | 对象图、可视树、探针 |
| 发布 | 随 Release 出货 | 只在 Debug 构建里监听 |
| 管道 | `JeekRemoteManager.Mcp` | `JeekRemoteManager.Mcp.Debug` |
| 注册位置 | `ProductMcpServer` + `ProductMcpContract` | `DebugMcpServer` + `DebugMcpContract` |

**为什么必须分开**：调试面的 `invoke` 工具能调用进程里的任何东西。它绝不能出现在用户的 agent 能到达的地方。两套工具注册表分属两个端点，不共享。

调试面的代码在所有配置下都编译（行为一致），只是 `ListeningEnabled` 这个运行时开关在 Release 下为 false——用运行时门而不是把整个文件包在 `#if DEBUG` 里。

**注册工具要改两个地方**：处理器在 Server 里，schema 在 Contract 里。**Contract 里漏掉的工具对客户端不可见。**

## 传输：命名管道，不是回环 HTTP

原来是回环 HTTP，现在全部改成 Windows 命名管道。理由是一串连锁的：

- **没有端口要分配**，所以管道名永远稳定，可以直接写死在用户的项目配置里。HTTP 端口每次启动都可能不同，配置就会过期。
- **没有防火墙提示**。
- **访问控制来自管道 ACL 而不是 URL 里的 token**：管道实例只对运行应用的账户（加 SYSTEM）可读写，其他用户是被内核拒绝的，不是被一个我们还得存起来的秘密拒绝的。
- **双工流**给服务端主动推送的通知留了空间，POST/response 的 HTTP 监听器做不到这个。

分帧是**每行一条 JSON-RPC 消息**——和 stdio 适配器另一端说的是同一种分帧，所以适配器转发字节时不需要重新分帧。

每个接受的客户端占一个管道实例，也就是它的 MCP 会话。**这带来一条运维约束**：残留的适配器进程会各自占住一个管道实例，攒多了应用会开始记录 "All pipe instances are busy"，后续调用超时。所以停掉应用后要一并清掉残留的 `JeekRemoteManagerMcp.exe`。

## 实例 id 与并行 worktree

Release 构建用裸管道名。Debug 构建在名字后面附加一个**从可执行文件目录哈希出来的 12 位十六进制实例 id**（`McpPipeNames.InstanceId`）。适配器和应用在同一个文件夹里，所以适配器不需要被告知该找哪个实例，自己就能算出同样的值。

`McpPipeNames` 这个文件被**同时编译进应用和适配器**（适配器链接了它），两端因此不可能漂移。

`DebugInstanceContext` 把这套身份扩展到别的地方：单实例互斥体名、激活事件名、AppUserModelId、运行时临时目录、窗口标题装饰。所以并行的 Debug worktree 各跑各的，互不顶掉，也不互相应答。

## 固定路径的适配器

agent 启动的是 `%LocalAppData%\JeekRemoteManager\Mcp\JeekRemoteManagerMcp.exe`（产品配置），或者仓库根的 `JeekRemoteManagerDebugMcp.cmd`（仅 Debug，不在 `bin\` 下）。

**`bin\` 下的那个文件只是构建产物**，应用在启动时把它安装到固定路径。**agent 绝不能直接启动它**——运行中的 agent 会锁住这个文件，导致重新构建失败。所以构建步骤里也不安装固定适配器。

`McpAdapterRegistry` 管理固定位置和每实例注册表（HKCU）：

- Release 用稳定的 `release` 键；每个 Debug worktree 用它路径派生的 id。
- **Release 拥有适配器更新权**；Debug 构建只在没有 Release 安装被注册时才能更新它。但 `McpAdapterRegistration.RegisterCurrentInstance` 会在旁边发布的副本与固定副本不同时总是刷新——agent 启动的是固定路径，Debug worktree 必须能推送路由修复。
- 目标文件被锁时保留旧文件。这是安全的：**适配器是协议无关的转发器**，旧的一样能转发。
- 判断"已安装的适配器是否最新"**只看文件大小和 UTC 修改时间，刻意不读内容**。

## 适配器的行为

`Tools/JeekRemoteManagerMcp/Program.cs` 是个无状态的 stdio↔管道转发器。几条关键规则：

- **只有真正的工具调用才值得启动 GUI。** MCP 客户端在会话开始时就打开 stdio 服务器，每次会话都弹个窗口是很无礼的。
- 应用不可达时**保持会话可用**而不是让握手失败：客户端保持连接，只有真正的工具调用才报告为什么什么都没发生。
- **断管道时重试一次**，这样应用重启不会结束 agent 的会话。
- 转发时**跳过服务端主动发来的通知**，避免把它们误当成本次请求的回复（管道是双工的）。
- `--connection` 参数把适配器**钉在**某个连接上，链接到项目里的配置就不必每次调用都写连接路径。显式参数总是优先。
- **显式路由过的或固定的适配器绝不回退到 Release**：如果一个 Debug worktree 离线了，转而连上用户已安装的实例是危险的。
- 只有产品面会按需启动应用；Debug worktree 由已经开着应用的开发者驱动。

## 产品面的两条铁律

`ProductMcpContract` 的注释里写死了这两条，实现必须一致：

1. **密码只写不读。** 没有任何工具返回密码、口令或 `jrm1` 加密 blob，只返回 `hasPassword` 这类布尔值。响应**按显式字段白名单逐个组装，从不序列化模型**——以后往模型上加字段不会意外泄漏。
2. **任何需要用户的事情都发生在 GUI 里。** 用户必须输入的秘密（主密码、二次因子）在 GUI 输入，绝不作为工具参数；破坏性动作在 GUI 确认。工具的做法是激活窗口并返回一个 agent 可以轮询的状态（如 `awaiting_user`），而不是无限期地阻塞一次工具调用。

另外：**碰 UI 状态的工具工作都通过 host 的 `UiInvoker` 跑在 UI 线程上**。

产品面上的会话用**和 agent 工作区一样的树相对路径**寻址（`vps/bwg`、`vps/bwg (2)`），这样 id 对人是有意义的，也不需要维护一张注册表。

## 对象图（仅调试面）

JeekTools 的 `ObjectGraph` 是基于反射的对象路径引擎：`Root.Member[0]["key"].#Name` 这样的路径可以穿过属性、字段（含非公开）、列表下标、字典键，以及一个可选的"具名子元素"钩子（在 UI 元素下按 `#Name` 找控件）。支持读、写、调用，能转换 JSON 参数（包括 `{"$path": ...}` 这种活对象引用）。

产品面故意把根解析器设成"永远抛异常"，并且 `ProductMcpContract` 从不宣告标准的 get/set/invoke 工具。
