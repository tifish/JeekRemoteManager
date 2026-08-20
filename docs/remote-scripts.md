# 远程脚本与批量执行

涉及 `Models/RemoteScript.cs`、`Services/RemoteScriptStore.cs`、`RemoteScriptLauncher.cs`、`InteractiveShellPayload.cs`、`ViewModels/BatchScriptPanelViewModel.cs`、`TerminalScriptSession.cs`。

## 形态

一个"脚本套件"是一个目录：`params.conf` 声明参数，若干 `.sh` 文件是脚本。参数有名字、类型（string / secret / bool / enum）、默认值、枚举选项。连接可以为套件保存**参数绑定**（`ConnectionScriptBinding`），secret 类型的值用和连接密码一样的 `jrm1` 信封保护。

有两个根：应用内置的套件和用户自己的套件。

## 在交互式 shell 里跑非交互式脚本

这是本机制最微妙的部分。脚本必须在**已经登录好的交互式 shell** 上跑（堡垒机场景下没有别的通道能到达目标），但交互式 shell 会回显命令、打印提示符、还可能启动分页器——这些噪音会混进脚本输出，也会让"退出标记"的检测失效。

`InteractiveShellPayloadRunner.Build` 的解决办法：

- **准备工作放在 `prepareCommand` 里**，它的噪音出现在 BEGIN 标记之前，永远不会被显示。
- **整个收尾部分——状态捕获、hook 加载、恢复 echo、退出标记——和 heredoc 管道共享同一个逻辑行**。这样 shell 读到的是单条命令，脚本自己的输出之后不会再有提示符或回显。
- **禁用交互式分页器**。否则 `git`/`systemctl`/`man` 会打开 `less`，打印 `(END)` 然后永远到不了 EXIT 标记，AI 随后就会去调 `terminal_interrupt`。

`InteractiveShellPayloadMonitor` 负责从字节流里找标记并过滤显示内容：

- 完整输出保存在 **char 缓冲区**而不是 `StringBuilder` 里，因为每次标记扫描要在"新追加的那一段"的 span 上跑。每来一个包就把整个缓冲区物化一次，会让大输出的命令在拷贝和扫描上都退化成 O(n²)。
- 只扫描新追加的区域，**按标记长度重叠**，这样跨包切断的标记仍能找到；找到后就停。记住"最后见到的标记"使增量扫描与全量 `LastIndexOf` 等价。
- 尾部换行**扣住不发**，attach 到下一块有实际内容的数据前面。退出标记的 `printf` 总会注入一个换行，调用方的完成行自己也带换行；不扣住的话，退出状态行前面会出现空行。

## 批量运行

`BatchScriptPanelViewModel` 在编辑器标签页里接管界面，把一个套件同时跑在树上多选的若干连接上。每个目标在**自己的终端标签页**里跑，面板只聚合各服务器的状态。编排逻辑在主 ViewModel 上，面板通过构造时传入的委托访问。

参数行有"统一"复选框：不勾，每台服务器用自己保存的值；勾上，编辑的值应用到全部目标并写回它们的绑定。**统一值在一次运行开始时快照一份**，这样运行途中的编辑不会产生跨目标混合的结果。

批量运行会打开很多终端标签页而用户在看聚合面板，所以有一个"确保终端存在但不切到前台"的变体。
