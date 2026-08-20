# ZMODEM 文件传输

涉及 `Services/ZmodemTransfer.cs`、`ZmodemTraceLog.cs`、`Views/TerminalView.axaml.cs` 的接管逻辑。

## 目的

远端敲 `sz file` / `rz` 时，直接在当前 SSH shell 里完成文件传输，不另开 SFTP 通道。这对堡垒机/跳板机场景是必要的——那些环境下往往只有这一条已认证的交互式通道能到达目标机器。

## 只在 8 位透明通道上启用

`ITerminalChannel.SupportsBinaryTransfers` 为 false（ConPTY / WSL）时**完全跳过检测**。ConPTY 重新合成 VT 输出，ZMODEM 帧不可能完整到达。这不是优化，是正确性判断。

## 触发串检测：延迟是主要矛盾

`ZmodemTriggerDetector` 坐在每一条 8 位透明通道的接收路径上，所以它必须极便宜。

最长的触发串是 6 字节，理论上一个包的最后 5 字节都可能是某个触发串的开头。**无条件扣住这 5 字节的代价是真实可感的延迟**：回显一次按键就是一个字节，在冲刷定时器（约 80 ms）触发前屏幕上什么都不出现。

所以现在只扣住"确实是某个触发串前缀"的尾巴。所有触发串都以 ZPAD 开头，绝大多数普通输出因此直接穿过。相应地，**只有真的扣住了字节时才武装冲刷定时器**。

两项性能措施：模式数组提升为静态（作为 `params` 传递时每扫描一个字节要分配六个数组），扫描在 ZPAD 字节之间跳跃而不是在每个偏移上试每个模式（一次向量化扫描代替逐字节比较链）。

## 数据通路的交接

检测到触发串后，`TerminalView` 把 `_activeZmodemQueue` 挂上，之后**所有**收到的字节直接进 `ZmodemByteQueue`，不再经过终端显示，同时 `_suppressUserInput = true` 屏蔽用户输入。

`ZmodemByteQueue` 的设计要点：

- **整包入队，从当前包里逐字节取出**。这样一次传输的成本是"每包一次通道操作"而不是"每字节一次"，而且常见的读取同步完成，不分配 Task。
- 逐字节读用 `ValueTask` 而不是 `Task`——因为几乎从不挂起，`Task` 会在每一次读上分配。
- `DrainAvailable()` 把未消费的字节要回来，让终端能显示发送方在协议结束后写的内容。**只能在读取方已停止后调用一次**：`_current` 属于读取方，且调用方必须已经停止 `Append`，此后写入的任何东西都会被丢掉。

## 帧解析的边界

`MaxDataSubpacketBytes = 64 KiB`。协议本身上限是 1024 字节，常见发送方协商到 8192。超过这个数只可能意味着帧终止符丢了，或者对端根本不在说 ZMODEM。**没有上限的话读取方会一直累积直到进程 OOM**，所以给一个宽松的上限然后让这一帧失败。

CRC-16/XMODEM 和 CRC-32 都用启动时构建的查找表。每一个 16 位 ZMODEM 头和子包都要过一遍，把每字节八次移位的代价在启动时付一次即可。

读取子包时用 `Span` 重载而不是 `IReadOnlyList`——后者会装箱枚举器并对子包的每个字节做一次接口调用。

## 诊断

设置 `JRM_ZMODEM_TRACE` 后 `ZmodemTraceLog` 把收发字节写到实例运行时临时目录下的 `ZmodemLogs`。日志写入包在 try 里：**记日志绝不能干扰终端关闭**。

## 相关调试入口

`zmodem_detector_latency_check`（验证普通输出不被扣住）、`zmodem_subpacket_limit_check`。
