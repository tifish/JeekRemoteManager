# 终端通道与输出管线

涉及 `Services/ITerminalChannel.cs`、`ConPtySession.cs`、`TerminalStreamDecoder.cs`、`TerminalEncoding.cs`、`Utf8ChunkAssembler.cs`、`TerminalSessionOutputBuffer.cs`、`TerminalResizeOutputBuffer.cs`、`TerminalBufferResizeRepair.cs`、`TerminalDimColorFilter.cs`、`Views/TerminalView.axaml.cs`、`Views/AgentCliPanelView.axaml.cs`。

## 目的

让"远程 SSH shell"和"本地进程"在终端控件面前长得一模一样，这样登录命令、脚本 payload、ZMODEM、AI 工具只需要写一遍。

## ITerminalChannel

抽象出四件事：收到字节、出错、关闭、写入 + 改窗口尺寸。三个实现：

- `SshTerminalChannel` —— SSH.NET 的 `ShellStream`
- `LocalConPtyTerminalChannel` —— 本地进程（AI CLI 等）
- `WslTerminalChannel` —— WSL 发行版

**关键属性 `SupportsBinaryTransfers`。** 只有 SSH 是 8 位透明的。ConPTY 会重新合成 VT 输出，二进制协议（ZMODEM）经过它必然被破坏。所以这个属性不是性能开关，是正确性开关：为 false 的通道上必须完全跳过 ZMODEM 检测。

## ConPtySession 的三条硬约束

1. **子进程装进 kill-on-close 的 job object。** 否则 `wsl.exe` 的子进程在会话关闭后还活着。
2. **`Write` 和 `Dispose` 用不同的锁。** `_writeGate` 只串行化写；`_consoleGate` 保护 HPCON。理由：写可能永久卡在满的输入管道上（子进程不再读了），如果 `Dispose` 要等这把锁，关标签页就会挂死。`Dispose` 的做法是直接从底下把流关掉，让那次写自己失败并吞掉异常——调用方是 UI 线程上的按键处理，会话已经没了。
3. **`Resize` 必须持有 `_consoleGate`。** HPCON 是裸句柄，对已被 `Dispose` 关掉的伪控制台调用 resize 是原生的 use-after-free，catch 块救不了。`_disposed` 标志和关闭 HPCON 在同一把锁下发布，所以并发的 resize 要么完整跑在前面，要么看见标志什么都不做。

拆除顺序也是固定的：**先杀进程树让子进程停止写，再关伪控制台**。`ClosePseudoConsole` 会阻塞直到输出管道排空，而读线程一直排到 EOF，所以这个顺序不会挂。

另外 `ConPtySession` 保留了 64 KiB 的最近输出环形缓冲（`RecentOutputBuffer`），用于在 CLI 还没来得及显示就退出时，把最后几行错误捞出来给状态栏。用环形缓冲而不是"列表 + 从头删"，是因为后者要在整个会话生命周期里每来一块数据就整体搬一次窗口。

**子进程的终端类型由 ConPTY 宿主声明。** 启动器可能带着 `TERM=dumb`（例如 agent 的重定向 shell），不能把它原样继承给内嵌交互终端，否则 Codex 会停在 `Continue anyway?`。创建进程时复制继承环境，仅将子进程的 `TERM` 设置为 `xterm-256color`，通过 Unicode 环境块传入，保留 PATH 等其它变量；不能临时修改父进程环境，避免并发启动串扰。Debug MCP 的 `conpty_environment_check` 验证真实子进程收到的值，并检查父进程环境未变。

## SSH 终端的接收路径

`TerminalView.OnShellData` 的分支顺序是有讲究的，从上到下依次是：

```
字节到达（后台线程）
 ├─ 登录命令捕获期？ → 同时喂给 LoginMenuOutputCapture（用于 #select 匹配屏幕上的菜单）
 ├─ ZMODEM 传输进行中？ → 全部交给 ZmodemByteQueue，直接返回
 ├─ 脚本 payload 执行中？ → InteractiveShellPayloadMonitor 过滤掉标记行，只把该显示的喂下去
 ├─ 通道不是 8 位透明？ → 跳过 ZMODEM 检测直接显示
 └─ 否则 → ZmodemTriggerDetector 扫描触发串
      ↓
   FeedBytes
 ├─ resize 静默期内？ → 进 TerminalResizeOutputBuffer 暂存
 └─ 否则 → TerminalSessionOutputBuffer.Append(data, generation)，武装帧定时器
      ↓
   DrainTerminalOutputFrame（UI 线程，每帧一次）
      → TerminalStreamDecoder.Decode → model.Feed
```

### 为什么要分这么多层

**`TerminalStreamDecoder`（SSH 侧，输出字符串）。** SSH/ConPTY 的包会把一个多字节字符切开，对单个包直接 `Encoding.UTF8.GetString` 会把不完整序列替换成 U+FFFD——中文变豆腐块。这个解码器保留未完成的尾巴。注意实现细节：**即使当前没有完整字符也必须调用 `GetChars`**，因为 `GetCharCount` 不保留不完整尾部，`GetChars` 才保留。

**终端编码（`TerminalEncoding`）。** SSH 连接可以指定远端 shell 的编码（UTF-8 默认，另有 GB18030 / GBK / Big5 / Shift-JIS / EUC-KR），老服务器和不少国内服务器的 locale 仍是 GBK。转换只发生在**文本边界**，通道本身保持 8 位透明——ZMODEM 要原始字节，所以不能在 `ITerminalChannel` 上套一层转码。边界有这几处，漏一处就会出现"屏幕对、agent 读到乱码"之类的不一致：

- 显示解码器（`TerminalStreamDecoder` 按连接的编码构造）；
- 用户输入：终端控件给出的是 UTF-8，经 `TerminalInputEncoder` 有状态地转成目标编码；应用替用户敲的文本（`WriteToShell(string)`、agent 的 send-keys）直接按目标编码编码；
- `InteractiveShellPayloadMonitor`：按目标编码解码捕获的输出，**交回显示的字节也按同一编码重新编码**，因为它们接着要进显示解码器；
- 登录菜单捕获 `LoginMenuOutputCapture`（`#select` 按名字匹配菜单，中文菜单名必须解对），终端和监控的隐藏 shell 各有一个；
- 应用自己插进字节管线的提示行。

`ApplyTerminalEncoding` 在每次建立连接时把这些一起换掉。WSL 和本地 ConPTY 永远是 UTF-8。回归检查是 Debug MCP 的 `terminal_encoding_check`。

**`Utf8ChunkAssembler`（AI 面板侧，输出字节）。** 同一个问题，但 AI 面板要把字节直接喂给解析器。若走"解码成字符串再编码回字节"，稳定输出流每帧都要付一次完整转码。这个类只扣住不完整的尾部序列（最多 3 字节）。它有一条容易踩的规则：**非法前导字节要放行，不能扣住**。C0/C1（overlong）和 F5–FF 永远不可能开始一个合法序列，扣住它们会让解析器永远看不到这些字节，而且如果它们正好在最后一个包结尾，就被彻底吞掉了。怎么渲染非法字节是解析器的事，不是缓冲区的事。

**`TerminalSessionOutputBuffer`（合并 + 背压）。** 两个目的：
- 全屏 TUI 常把一次重绘拆成好几个包，逐包呈现会露出中间态的光标位置。合并到一帧再呈现。
- 队列有 16 MiB 上限。远端跑 `cat /dev/urandom` 时填充速度远超 UI 排空速度，没有上限就是 OOM；有上限就退化成"丢了一段回滚"，这本来就是真实终端的行为。丢弃**从最老的开始**——用户等着看的是最新的字节。丢弃后会在终端里打一行 `[output truncated: dropped N KiB…]`，而不是留一个静默的空洞。

  实现上有两个细节：一是超限修剪时把幸存的尾巴挪进新的 chunk，让那个曾经涨到高水位的底层数组能被回收，而不是整个会话都保持在峰值；二是 `generation` 参数——重连后旧世代的数据会被直接丢弃，不会串到新会话的屏幕上。

**`TerminalResizeOutputBuffer`。** readline 重绘提示符的方式是"回车 + 替换文本"，这两段可能分包到达。分别渲染会让光标可见地跳到行首再跳回来。所以 resize 之后开一个短静默期把它们合成一次呈现，同时设一个绝对截止时间，防止持续输出的命令被一直扣住。

**旁路写入必须先排空这条队列。** `FeedLine` 直接写 `model.Feed`，绕过上面整条管线——`[script exit N]`、`[copy public key exit N]` 这类应用自己生成的行都走它。麻烦在于脚本的退出标记**总是**和脚本最后几行输出同包到达：那些字节刚进 `TerminalSessionOutputBuffer` 等 16 ms 帧定时器，而等在 `WaitForExitAsync` 上的调用方已经被唤醒，完成行就插到了它本该总结的那段输出**前面**。所以 `FeedCompletionLineAndRefreshPromptAsync` 在写之前先 `FlushResizeOutputBuffer()` + `DrainTerminalOutputFrame()`。

光排空还不够。`InteractiveShellPayloadMonitor.Append` 原本在**返回显示字节之前**就完成了 exit 的 TaskCompletionSource，等待方可能在 `OnShellData` 还没来得及把这一包交给 `FeedBytes` 时就跑完了。所以要渲染输出的调用方得打开 `DeferExitCompletion`，由 `OnShellData` 在 `FeedBytes` 之后显式调用 `ReleasePendingExit()`；只收集输出、不渲染的调用方（`PublicKeyInstaller`、`ServerMonitorSession`）保持默认关闭即可。回归检查是 Debug MCP 的 `script_completion_order_check`。

**`TerminalBufferResizeRepair`。** 绕开 XTerm.NET `TerminalBuffer.Resize` 的两个光标 bug：视口变矮时它只夹紧相对行，光标会落在旧屏幕文本上；视口变高（最大化）时它同时缩小 `YBase` 却不推进相对行，光标落到还有内容的历史上，随后 shell 重绘提示符就把那段文本覆盖了。修复策略：优先把光标恢复到 resize 前的**绝对行**（`YBase + Y`），该行还在视口内就直接用；在视口下方就滚到底行；在视口上方则退回 resize 前的相对行。

**`TerminalDimColorFilter`（AI 面板侧）。** SvcSystems.UI.Terminal 会解析 SGR dim(2) 但不绘制。这个过滤器把 dim 改写成显式的柔灰前景色。**最容易写错的地方**：`38;2;r;g;b` / `48;2;r;g;b` 真彩色序列里的那个 `2` 是颜色模式而不是 dim，误判会毁掉调色板并让用户输入变暗。所以 38/48/58 必须走单独的 `CopyExtendedColor` 分支。选柔灰而不是 bright-black(90)，是因为 90 在深色主题下几乎全黑。

## 终端外观：字体、配色、回滚行数

涉及 `Services/TerminalAppearance.cs`。三项都是漫游设置，在设置对话框的"终端"卡片里改。

- **配色是应用级资源。** SvcSystems.UI.Terminal 渲染时按 `SvcSystems.UI.TerminalColorN`、`TerminalCaretBrush`、`TerminalSelectionBrush` 这些键查资源，所以配色写进 `Application.Resources` 就能同时作用于所有终端（主 shell 和 AI 面板）。`App.axaml` 不再定义这套调色板，默认配色和其它配色一样由代码在启动时写入，只有一份来源。
- **切换配色必须清控件的渲染缓存。** 控件把每段文字连同解析好的画刷一起缓存，只有字体变化才清缓存；不清的话背景会变、已缓存的文字还是旧颜色。控件没有公开接口，`RefreshRendering` 通过反射调用 `ClearFormattedTextCache` 并重绘内部 surface。库升级若改了这两个成员名，`CanClearRenderCache` 会变成 false，Debug MCP 的 `terminal_appearance_check` 会报出来。
- **只提供深色配色。** 控件没有独立的默认前景/背景：背景用调色板 0 号，默认文字用 15 号。浅色配色只能把 ANSI "黑色"设成浅色，会让所有用黑色输出的程序看不见字。
- **回滚行数只对新标签页生效。** XTerm.NET 在创建缓冲区时按 `TerminalOptions.Scrollback` 定长，之后改不了；所以 `TerminalView` 通过构造参数接收它。字体和配色立即作用于已打开的终端。

## 终端内查找

Ctrl+Shift+F 打开查找栏（普通的 Ctrl+F 属于远端：readline 的前进一字符、vim 翻页）。搜索用控件自带的 `Search`/`SelectNext`/`SelectPrevious`，覆盖整个缓冲区（含回滚），不区分大小写，命中会被选中并滚动到可见区域。Enter / Shift+Enter（或 F3）前后跳，Esc 关闭。

有一个细节：控件在缓冲区一有变化（新输出）就丢弃命中列表，此后 `SelectNext` 返回 -1。查找栏把这种情况当作"重新搜索"，并从用户原来的位置继续，而不是跳回第一个命中——否则在持续输出的会话里按 Enter 永远停在第一个。回归检查是 Debug MCP 的 `terminal_find_check`。

## 会话日志

涉及 `Services/TerminalSessionLog.cs`。标签页右键菜单可随时开始/停止记录；连接上勾选"记录会话日志"（`Connection.AutoLogSession`）则每次打开都自动记录。文件在 `%LocalAppData%\JeekRemoteManager\SessionLogs`，按"连接名-开始时间"命名。

- **记录的是解码后的文本，不是原始字节。** 挂在 `DrainTerminalOutputFrame` 解码之后，所以已经按会话编码转成了 Unicode，GBK 服务器的日志也是正常的 UTF-8 文本。
- **去掉转义序列。** `AnsiTextStripper` 是有状态的，跨包切开的 CSI/OSC 也能去干净；`\r\n` 变 `\n`，单独的 `\r`（进度条重绘）丢掉，免得一个进度条刷出几百行。应用自己插进终端的提示行（`FeedLine`）不进日志。
- **UI 线程只追加到缓冲流**，每秒一次的定时器在后台刷盘，停止或关标签页时收尾并写结束标记。
- **放在机器本地，不跟随便携数据。** 日志可能包含服务器打印的任何东西。没有自动清理，由用户自己管理。

回归检查是 Debug MCP 的 `terminal_session_log_check`。

## AI 面板的 ConPTY 渲染

AI 面板另有两条独立的约束：

- **resize 必须防抖到静默。** conhost 对每一次 resize 都回一整屏按那个尺寸排版的重绘，这些字节到达时本地 model 可能已经换到更新的尺寸了——行会重新折行，每次重绘都把一屏陈旧内容滚进回滚区，表现为 Codex 流式输出时的"断层/split screen"。所以拖动过程中的中间尺寸**绝不能**发给 ConPTY，只有静默后的最终尺寸能发（唯一的例外是 `AttachLiveSession` 里的首次定尺）。
- **粘性跟随（`_followOutput`）。** 光判断 `IsAtBottom` 不够：`TerminalControlModel.Send` 每次都会 `EnsureCaretIsVisible`（鼠标追踪的移动/按下也会触发），某些 VT 更新还会在流中途把 `YDisp` 拽到 `YBase`。所以用一个粘性标志，只有用户自己回到底部才重新挂上跟随。

面板还有一条生命周期约束：`TabControl` 会卸载非活动标签，**不能在 `Unloaded` 里拆 ConPTY 接线**——ViewModel 里的会话还活着，应该在 `Loaded` 时重新挂接。

## 相关调试入口

`terminal_output_coalescing_check`、`terminal_output_backpressure_check`、`conpty_teardown_race_check`、`ai_render_probe`、`terminal_font_sync_check`。设 `JRM_AI_CAPTURE_DIR` 可以录下嵌入式 CLI 的原始 ConPTY 字节流和 resize 侧车文件，用于离线重放渲染 bug。
