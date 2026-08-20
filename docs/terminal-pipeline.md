# 终端通道与输出管线

涉及 `Services/ITerminalChannel.cs`、`ConPtySession.cs`、`Utf8StreamDecoder.cs`、`Utf8ChunkAssembler.cs`、`TerminalSessionOutputBuffer.cs`、`TerminalResizeOutputBuffer.cs`、`TerminalBufferResizeRepair.cs`、`TerminalDimColorFilter.cs`、`Views/TerminalView.axaml.cs`、`Views/AgentCliPanelView.axaml.cs`。

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
      → Utf8StreamDecoder.Decode → model.Feed
```

### 为什么要分这么多层

**`Utf8StreamDecoder`（SSH 侧，输出字符串）。** SSH/ConPTY 的包会把一个多字节字符切开，对单个包直接 `Encoding.UTF8.GetString` 会把不完整序列替换成 U+FFFD——中文变豆腐块。这个解码器保留未完成的尾巴。注意实现细节：**即使当前没有完整字符也必须调用 `GetChars`**，因为 `GetCharCount` 不保留不完整尾部，`GetChars` 才保留。

**`Utf8ChunkAssembler`（AI 面板侧，输出字节）。** 同一个问题，但 AI 面板要把字节直接喂给解析器。若走"解码成字符串再编码回字节"，稳定输出流每帧都要付一次完整转码。这个类只扣住不完整的尾部序列（最多 3 字节）。它有一条容易踩的规则：**非法前导字节要放行，不能扣住**。C0/C1（overlong）和 F5–FF 永远不可能开始一个合法序列，扣住它们会让解析器永远看不到这些字节，而且如果它们正好在最后一个包结尾，就被彻底吞掉了。怎么渲染非法字节是解析器的事，不是缓冲区的事。

**`TerminalSessionOutputBuffer`（合并 + 背压）。** 两个目的：
- 全屏 TUI 常把一次重绘拆成好几个包，逐包呈现会露出中间态的光标位置。合并到一帧再呈现。
- 队列有 16 MiB 上限。远端跑 `cat /dev/urandom` 时填充速度远超 UI 排空速度，没有上限就是 OOM；有上限就退化成"丢了一段回滚"，这本来就是真实终端的行为。丢弃**从最老的开始**——用户等着看的是最新的字节。丢弃后会在终端里打一行 `[output truncated: dropped N KiB…]`，而不是留一个静默的空洞。

  实现上有两个细节：一是超限修剪时把幸存的尾巴挪进新的 chunk，让那个曾经涨到高水位的底层数组能被回收，而不是整个会话都保持在峰值；二是 `generation` 参数——重连后旧世代的数据会被直接丢弃，不会串到新会话的屏幕上。

**`TerminalResizeOutputBuffer`。** readline 重绘提示符的方式是"回车 + 替换文本"，这两段可能分包到达。分别渲染会让光标可见地跳到行首再跳回来。所以 resize 之后开一个短静默期把它们合成一次呈现，同时设一个绝对截止时间，防止持续输出的命令被一直扣住。

**`TerminalBufferResizeRepair`。** 绕开 XTerm.NET `TerminalBuffer.Resize` 的两个光标 bug：视口变矮时它只夹紧相对行，光标会落在旧屏幕文本上；视口变高（最大化）时它同时缩小 `YBase` 却不推进相对行，光标落到还有内容的历史上，随后 shell 重绘提示符就把那段文本覆盖了。修复策略：优先把光标恢复到 resize 前的**绝对行**（`YBase + Y`），该行还在视口内就直接用；在视口下方就滚到底行；在视口上方则退回 resize 前的相对行。

**`TerminalDimColorFilter`（AI 面板侧）。** SvcSystems.UI.Terminal 会解析 SGR dim(2) 但不绘制。这个过滤器把 dim 改写成显式的柔灰前景色。**最容易写错的地方**：`38;2;r;g;b` / `48;2;r;g;b` 真彩色序列里的那个 `2` 是颜色模式而不是 dim，误判会毁掉调色板并让用户输入变暗。所以 38/48/58 必须走单独的 `CopyExtendedColor` 分支。选柔灰而不是 bright-black(90)，是因为 90 在深色主题下几乎全黑。

## AI 面板的 ConPTY 渲染

AI 面板另有两条独立的约束：

- **resize 必须防抖到静默。** conhost 对每一次 resize 都回一整屏按那个尺寸排版的重绘，这些字节到达时本地 model 可能已经换到更新的尺寸了——行会重新折行，每次重绘都把一屏陈旧内容滚进回滚区，表现为 Codex 流式输出时的"断层/split screen"。所以拖动过程中的中间尺寸**绝不能**发给 ConPTY，只有静默后的最终尺寸能发（唯一的例外是 `AttachLiveSession` 里的首次定尺）。
- **粘性跟随（`_followOutput`）。** 光判断 `IsAtBottom` 不够：`TerminalControlModel.Send` 每次都会 `EnsureCaretIsVisible`（鼠标追踪的移动/按下也会触发），某些 VT 更新还会在流中途把 `YDisp` 拽到 `YBase`。所以用一个粘性标志，只有用户自己回到底部才重新挂上跟随。

面板还有一条生命周期约束：`TabControl` 会卸载非活动标签，**不能在 `Unloaded` 里拆 ConPTY 接线**——ViewModel 里的会话还活着，应该在 `Loaded` 时重新挂接。

## 相关调试入口

`terminal_output_coalescing_check`、`terminal_output_backpressure_check`、`conpty_teardown_race_check`、`ai_render_probe`、`terminal_font_sync_check`。设 `JRM_AI_CAPTURE_DIR` 可以录下嵌入式 CLI 的原始 ConPTY 字节流和 resize 侧车文件，用于离线重放渲染 bug。
