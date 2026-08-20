# AI Agent 集成

涉及 `Services/AgentRemoteTools.cs`（含 `AgentCliCatalog`）、`AgentCliLocator.cs`、`AgentCliInstaller.cs`、`AgentCliWorkspace.cs`、`AgentProjectLink.cs`、`AgentMcpConfigCatalog.cs`、`DangerousCommandDetector.cs`、`ViewModels/AgentCliPanelViewModel.cs`、`Views/AgentCliPanelView.axaml.cs`。

## 两种 agent

- **连接内 agent** —— 每个 SSH 终端标签页旁边的侧面板。它拿到的 MCP 适配器被 `--connection` 钉在这个连接上，工具作用于这条 shell。
- **全局 agent** —— 独立标签页。适配器**不钉连接**，所以它能浏览整棵连接树、跨多个连接工作、寻址任意会话。

## 核心设计决定：上下文写进工作区，不写进命令行

每个终端标签页有一个持久的本地工作目录，位于 `%LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces\<树相对路径>`，里面放：

- `AGENTS.md` —— 完整的远程服务器上下文（每次启动前刷新）
- `CLAUDE.md` —— 一行 `@AGENTS.md` 的引用（只有 Claude 需要，别的 agent 本来就读 `AGENTS.md`）
- `AgentMcpConfigCatalog.All` 里列出的每一个项目级 MCP 配置

这样**任何**支持的 agent 或编辑器打开这个目录，都能拿到同一份远程服务器上下文，不需要命令行提示词或参数。而且这些配置启动的是本地 stdio 适配器，**里面没有端口也没有 token，所以跨应用重启永远有效，工作区可以冷启动打开**。

工作区在 `%LOCALAPPDATA%` 下，是机器本地的，**不跟便携/漫游配置根走**——它是生成物。

### 并行标签页隔离

第 1 个会话就用连接路径本身（稳定的默认值）。第 2 个及以后的会话把**叶子文件夹**改名成和标签页标题一样的形式：`vps/bwg (2)`、`vps/bwg (3)`……注意是**兄弟目录，不是子目录**。这样复制出来的标签页之间的 agent 状态互相隔离，同时目录名和用户在标签栏上看到的完全一致。

## MCP 配置目录

`AgentMcpConfigCatalog` 是"哪些项目级配置文件要写"和"每个文件怎么拼同一条 stdio 条目"的唯一真相来源。

**有两个写入方，语义完全不同**，这正是把清单集中在这里的原因：

- `AgentCliWorkspace` 拥有它生成的工作区，**整文件重写**。
- `AgentProjectLink` 写进用户自己的项目，**必须合并**——TOML 用标记块，JSON 逐键合并。

一些各家 agent 的具体差异必须记在这里，否则很容易踩：

- **根键名不同**：Claude / Copilot CLI / Cursor 读 `mcpServers`，VS Code 读 `servers`。VS Code 遇到另一种拼法会**静默忽略整个文件**——不报错，工具就是不出现。
- **Codex 的 `default_tools_approval_mode` 必须写在配置文件里**，不能用 `codex -c` 覆盖。没有 `url`/`command` 的部分 `mcp_servers.*` 补丁会以 "invalid transport" 加载失败。
- **OpenCode** 的条目形状是 `{ type: "local", command: [exe, ...args] }`。
- **Zed** 的 `context_servers` 条目是 `{ command, args }`，没有类型判别字段——变体按形状选择，`type` 根本不在它的 schema 里。
- **Claude** 把项目级 `.mcp.json` 的服务器视为未受信任，直到用户批准。工作区是 JRM 自己生成的且适配器已钉在当前连接上，所以在项目局部设置里**只批准我们这一个服务器**。不这么做的话，新连接会静默地在没有任何 `jrm-remote` 工具的情况下启动 Claude，即使 `--allowedTools` 里写了这些工具名。
- **Cursor CLI** 没有"授予某一个 MCP 服务器的工具"的命令行开关，项目级服务器也不像用户级那样自动批准。所以用它的项目权限文件同时覆盖这两件事，比 `--force` 那种一揽子 shell 权限窄得多。
- **Antigravity** 的 `.agents/mcp_config.json` 没有文档化的按服务器自动批准；它的一揽子自动批准会连本地 shell 和文件写入一起覆盖，比其它 agent 在这里被授予的远程工具宽太多。**所以自动运行模式对 Antigravity 不加任何参数**，由用户在 agent 里自己确认。
- **Pi** 上游刻意没有内置 MCP 客户端。JRM 附带一个小的第一方扩展，读取工作区的 `.mcp.json` 并只暴露那一个服务器的工具。扩展放在 `bin/Data` 下，使它成为运行时的一部分。

## 链接到用户自己的项目

`AgentProjectLink` 把一个连接写进任意本地项目文件夹：项目 `AGENTS.md`（和 `CLAUDE.md`）里的一个带标记块，加上目录里每个配置的一条 MCP 服务器条目。

和生成的工作区不同，**这个文件夹属于用户**：条目是合并进去、也能再摘出来的，绝不整体覆盖。取消链接时要**把项目还原成之前的样子**——块和条目删掉，只因我们才存在的文件或文件夹被删除而不是留下空壳。

几条具体约束：

- 提交进版本库的文件里**不能出现用户名**。所以 MCP 条目启动的是项目旁边的一个可移植 `JeekRemoteManagerMcp.cmd`，它在运行时展开 `%LocalAppData%`。同理，`AGENTS.md` 块里的工作区路径也写成 `%LocalAppData%\...` 而不是展开的 `C:\Users\...`。
- **Release 不带 `--instance`**，这样提交的文件能和用户安装的应用对话；**Debug 带 `--instance`**，这样在该文件夹上打开的 agent 到达的是写配置的那个 worktree，而不是另一个正在跑的实例。
- **TOML 块永远重新追加到文件末尾**：TOML 的表头会捕获它后面的所有键，我们的表绝不能坐在属于项目自己的裸键上方。
- MCP 服务器名**按连接加后缀**（`vps/bwg (2)` → `vps-bwg-2`），这样多个 JRM 标签页可以链接进同一个项目而不撞名。
- **拒绝把产品 MCP 配置写进 JeekRemoteManager 自己的 worktree**（靠根目录的 `JeekRemoteManagerDebugMcp.cmd` 识别）——那些文件夹已经有 Debug MCP 了。

## Agent 定位与安装

`AgentCliLocator` 在 PATH 之外还探测各家官方安装器的默认位置，因为**子安装器无法更新本进程继承的 PATH**。

两个易错点：

- **符号链接/目录联接要解析到真实安装目录**。Codex 独立安装器通过联接暴露 `codex.exe`，而 `codex.exe` 相对自身路径去找 Windows 沙箱助手；经联接启动时那个目录不存在，沙箱命令会报 "program not found"。
- **但 argv[0] 分派型的 shim 不能解析**（例如 mise 的 `codex.exe` → `mise.exe`）：目标二进制按被调用的文件名选择行为，直接启动解析后的路径会丢掉工具身份。
- **桌面协议可用性必须显式检查**。ShellExecute 对任何 URI 都能调用，未注册的 scheme 只会在用户点了之后才失败，所以要先查注册的处理器。
- Cursor CLI 只在 PATH 上按**无歧义的 `cursor-agent`** 名字探测——裸 `agent` 会和别家撞（Grok 在自己的 bin 目录里就有 `agent.exe`）。

探测结果**短暂缓存**：一次探测要走遍 PATH 上所有支持的工具外加注册表，是几百次同步查找，而且原先每次打开面板都在 UI 线程上跑两遍。缓存时间的选择原则是：长到让打开一个 AI 面板期间的多次调用共享一次探测，但**远短于用户跑去下载页或外部控制台安装一趟的时间**——那样装好的 agent 必须在用户回来时就出现，不需要重启应用。探测在锁外进行（几百次文件系统和注册表查找，持锁会把其他调用方全堵在后面）；两个调用方同时探测就都探测，谁的快照胜出都一样新。

`AgentCliInstaller` 跑各家官方的一行安装命令；官方推荐图形安装器的就打开下载页。安装跑在**可见的外部 PowerShell 窗口**里而不是把输出重定向进应用，让用户能直接跟提示、进度和错误互动。

## 危险命令确认

`DangerousCommandDetector` 是**启发式黑名单，不是沙箱**。它标记那些典型形状会大规模删除或不可逆覆盖数据的命令（递归 `rm`、通配符 `rm`、往块设备写裸字节、SQL 批量删除、git 历史/工作树破坏、卷/容器/账户清理），让 UI 先问用户。

模型另外被指示对危险操作使用 `terminal.run-danger` 工具，**两个信号任一触发都会要求确认**。

## 面板生命周期

- 面板打开时自动启动选中的 agent，不需要用户手动点 Start。
- 每次启动/停止请求都会**递增一个 generation token**，被取代的启动会释放自己的进程。切 ComboBox 切得再快，最后只剩下最后一个。
- 切换到一个装不起来的 agent 时，**旧的会被停掉**并报告为"被替换"而不是"用户停止"。留着旧会话会让另一个 agent 的终端顶在这个 agent 的状态文字下面，而且它的运行态会压掉用户真正需要看到的安装提示。
- 重建运行模式选择器时，**先加入要用的模式再移除旧的**：清空集合会让 ComboBox 的选中项变 null，从而以错误的模式启动。
- 桌面/编辑器 surface 打开的是用户自己拥有的长期窗口，不是我们管理的会话：它可能已经在跑（此时只是新开一个窗口，我们启动的进程立刻退出），停止面板也**不会**关掉它。
- CLI 提前退出时，先等 ConPTY 送完最后几行错误，再把纯文本错误显示在终端和状态栏——很多 CLI 在 `DataReceived` 接好之前就死了，所以 `ConPtySession` 的最近输出环形缓冲在这里派上用场。
