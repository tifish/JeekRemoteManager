# 堡垒机登录与会话复用

涉及 `Services/LoginCommandSequence.cs`、`LoginMenuSelection.cs`、`LoginMenuPaging.cs`、`BastionLanding.cs`、`BastionSessionPool.cs`、`BastionLoginProfileStore.cs`、`Models/BastionLoginProfile.cs`、`Controls/LoginCommandsTextBox.cs`。

## 问题

企业环境里，SSH 目标常常在一台堡垒机后面：先认证到堡垒机，过 2FA，进一个编号资产菜单，选机器，选账号，落到目标 shell，可能还要 sudo 提权。这一整套是有状态的交互，不是一句 `ssh -J` 能表达的。

而且堡垒机的 SSH 传输本身很贵：每开一条新连接就要重新过一次 2FA。所以这里的两条设计主线是：**把交互脚本化**，以及**把已认证的传输池化复用**。

## 登录命令 DSL

连接的 `LoginCommands` 是逐行文本，每行在远端输出安静下来之后才发送——这样菜单、sudo 提示已经在屏幕上了，答案才被打进去。行内可以是普通命令，也可以是 `#` 指令：

| 指令 | 含义 |
| --- | --- |
| `#input` | 停下来等用户手动输入并按 Enter |
| `#key <键>` | 发一个原始按键（如 `#key Enter`） |
| `#select <名字>` | 按名字而不是编号选择菜单项 |
| `#pagekey <键>` | 配置长菜单的翻页键，对之后所有 `#select` 生效 |
| `#template N` | 展开共享模板片段 1–4 |
| `#reuse-enter` | 复用传输时，进入本目标的命令段起点 |
| `#duplicate` | 到达目标之后的命令段起点 |
| `#reuse-leave` | 复用传输时，离开本目标回到菜单的命令段 |

变量 `{{name}}` / `{{host}}` / `{{port}}` / `{{username}}` 在模板展开之后解析，是一份**显式白名单**——不是通用的字符串插值。

### 分段语义（容易理解错的地方）

`#duplicate` 是**起点标记，不是互斥段**。首次登录和 `#reuse-enter` 在到达目标之后都会继续跑 `#duplicate` 段里的命令；只有"同目标的额外通道"和监控面板才直接从 `#duplicate` 开始。这个语义是为了兼容旧配置里 `#duplicate` 的原始含义。

`HasStructuredReuseWorkflow` 要求 `#reuse-enter` 和 `#reuse-leave` **两者都在**才认为工作流完整。**只有一半的工作流永远不会被池化**——猜"该在哪儿离开"或"该在哪儿进入"可能把用户送到错误的服务器上。

`#template` 展开发生在任何指令解析之前，且模板片段里可以用除 `#template` 以外的所有指令，从而排除递归/循环展开。

### `#select`：为什么不能用编号

堡垒机菜单的编号随资产增删而漂移。今天的 3 号明天可能是别的机器。`#select 机甲-linux构建` 匹配的是屏幕上打出来的菜单文本，然后键入对应的编号。

匹配规则刻意保守：

- 整列精确匹配（IP 或名字）优先。
- 否则接受**唯一的**子串匹配。
- **有歧义或找不到就失败，绝不猜**——菜单漂移过的情况下猜错就是连到别的机器上。

菜单解析只看"最后一段连续的编号行"，因为捕获的输出里可能还留着上一级菜单（先选资产类别再选资产）。只有在最后一段里找不到调用方要的东西时，才回退到扫描全部编号行。菜单是通过 PTY 来的，颜色和光标移动会和文本交织，所以解析前要剥 ANSI；比较时按单空格归一化，列填充是排版不是内容。

`LoginMenuPager` 处理一屏放不下的菜单：匹配当前页，配了 `#pagekey` 就按键翻下一页再匹配。两条终止条件：**一页不再带来新条目就停**（末页重复自己时不会死循环），以及 `MaxPages = 200` 兜底。**页内歧义是一个真实答案**，不能翻过去——翻页会让后面某页的机器盖掉此页已经匹配上的那些。

`LoginKeySequence` 把 `Ctrl-F` 这样的键名解析成终端实际发送的字节（0x06）。支持反斜杠转义（如 `n\r`），覆盖命名键之外的情况。光标/翻页键按终端的**普通模式**（非应用模式）编码。

## 会话池

`BastionSessionPool` 是进程内的已认证传输池。

- **自动按端点、用户、凭据身份分组**，不持久化任何"堡垒机分组"设置——用户不需要配置这件事。
- 池键是**哈希**的，诊断快照里只出现路由名和端点标签，不出现凭据。
- 每个条目记录"最后已知的逻辑目标"（`BastionRoute`）。这一点很重要：**新开的通道通常落在堡垒机菜单上，而不是在那个目标里面**。

### 借用与路由切换

`TryAcquireAsync` 返回一个 lease，它持有一次客户端引用并**串行化路由切换**（`RouteGate`）。切换逻辑在 `BastionLanding.SelectReusePhases`：

- 切换到不同目标：先把旧目标的 `#reuse-leave` **跑完**，再跑新目标的 `#reuse-enter`。
- 同目标的额外通道：只从 `#duplicate` 开始。

`BastionLanding.Classify` 判断新开的 shell 当前落在哪里：

| 落点 | 含义 |
| --- | --- |
| `Menu` | 编号资产/账号菜单，堡垒机 SSH 认证后的典型状态 |
| `Prompt` | 2FA 或密码提示。**在这里发菜单命令会烧掉验证码** |
| `Target` | 已经在目标 shell 里。只有此时 `#reuse-leave` 才有意义 |

lease 的四种收尾方式对应四种不同的后果：

- `CompleteAndTakeClient()` —— 通道建立成功，引用移交给终端/监控。
- `KeepAndTakeClient()` —— 用户手动接管终端。保留已认证传输，但**把当前路由标记为未知**，这样下一个借用者不会在堡垒机菜单上傻发 `#reuse-leave`。
- `AbandonAndTakeClient()` —— 保留引用给手动接管，但把池条目摘掉，因为传输本身已不可用。
- `Abandon()` —— 路由不确定，释放引用。

监控面板在这里有个专门的判断：**通道创建撞上已观测的传输上限时，路由其实没被改变**，此时应保留池条目和它学到的上限，让下一个终端能立刻跳过这条传输；而不是丢弃已认证传输、逼下一个目标重走一遍 2FA。

## 共享模板

同一台堡垒机后面往往有几十个连接，登录命令几乎一样。`BastionLoginProfile` 是**固定四个**片段（id 永久为 1–4），按自动探测出的端点/凭据身份关联。

- 模板文件里只有命令和一个安全的端点标签（如 `user@host:22`），**密码和加密 blob 永不进入这个文件**。
- 模板 id 是不透明的，从会话池用的同一个端点/凭据身份派生。
- 外部编辑把文件搞坏时，profile 保持不可用状态而不是把连接文件毁掉。

`BastionLoginTemplatePreset` 提供一键起点，对应最常见的交互流程：输 2FA → 输 0 看全部资产 → 选当前连接和账号 → 到达后 sudo 提权 → 切换复用传输时返回菜单。

## 编辑体验

`LoginCommandsTextBox` 在行首（允许前导空白）输入 `#` 时弹出指令自动补全。带参数的指令补全后自动加一个空格。有两个状态标志值得注意：重写文本/光标时要抑制嵌套的变更处理，否则会为刚插入的 token 再次弹出补全；方向键/Home/End 移动光标时不能打开列表。

## 相关调试入口

`login_menu_select_check`、`login_menu_select_probe`（single/paged/switch 三种场景）、`login_command_flow_check`、`login_command_completion_check`、`login_command_variable_check`、`bastion_login_template_check`、`bastion_template_preset_check`、`bastion_channel_limit_check`、`bastion_reuse_landing_check`。
