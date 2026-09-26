# SSH 连接、认证与传输复用

涉及 `Services/SshConnectionFactory.cs`、`SshHostKey.cs`、`KnownHostsStore.cs`、`SharedSshClient.cs`、`PublicKeyInstaller.cs`、`ConnectionLauncher.cs`、`WslDistroService.cs`。

## 认证：一条路径，两个调用方

交互式终端和非交互式脚本执行器共用 `SshConnectionFactory.Build`，目的是不让两条认证路径产生行为差异。凭据用主密码解密后以编程方式提交，用户平时不用输任何东西。

### 尝试顺序

1. **显式私钥**（带可选口令）。
2. **存储的密码**。很多 sshd 只通过 PAM/keyboard-interactive 暴露密码认证，所以 KI 方法总是挂在后面。
3. **没有密码时**才回退到 ssh-agent / Pageant 身份，再到 `~/.ssh` 下的默认密钥名——照 OpenSSH 客户端的约定。

第 3 条为什么要用"没有密码"来把门：一个确实使用密码的连接如果被额外撒一堆密钥尝试，可能撞上服务器的 `MaxAuthTries`。

**私钥配置错误必须报出来**，即使另一种方法能把连接带起来。默默回退到密码或 agent 密钥，意味着一条坏掉的密钥路径会一直藏着，直到某天它是唯一剩下的凭据，然后在连接时失败且没有任何历史记录。

ssh-agent 查询跑在有超时的工作线程上：agent 缺失或无响应不能拖住整个连接，而且必须在超时内**物化**结果，不能等到后面 `AddRange` 时才真正做 IPC。

### keyboard-interactive 的安全规则

这是整个认证里最需要小心的部分。匹配英文单词 "password" 只是启发式——非英文 locale 的服务器会问"密码"或"Passwort"，提示文本完全取决于 PAM 怎么配。所以：关键词是强线索，**未标注的单个隐藏提示也会用密码回答**。

但有两类提示**永远不用密码回答**：

- **回显的提示**。那是在问用户名或 OTP，把密码送过去等于写进服务器日志。
- **任何读起来像第二因子的东西**。MFA 第二轮通常就是一个隐藏的 "Verification code:"，形状和"未标注密码挑战"一模一样。把密码当验证码提交会认证失败并消耗一次锁定计数。所以二次因子措辞被直接拒绝，并且**一旦本次会话里已经提交过密码，未标注隐藏提示的兜底逻辑就失效**——密码已经发出去了，后面再来的隐藏提示是下一个因子，不是第一个的重试。

二次因子的措辞列表是**刻意具体**的：只写 "code" 会连 "passcode" 一起匹配，把第一轮的密码提示误分类会直接搞坏普通密码登录。

答不上来的提示（OTP、额外 PAM 字段）通过 `PromptUser` 回调交给 GUI 对话框。**必须抛异常而不是返回 null 的 Response**，否则 SSH.NET 会冒出一个无法阅读的 `ArgumentNullException`。

## 主机密钥信任

SSH.NET 没有内置的 host key 校验，默认信任所有主机。`KnownHostsStore` 补上 TOFU（首次见到即信任并保存）+ 后续不匹配检测，等价于 OpenSSH 的 `known_hosts`。

- 存在**机器本地**设置文件旁边——主机信任是每台机器自己的决定。
- 首次见到的密钥自动信任并保存；已记住的密钥发生变化时，必须由 `onMismatch` 回调（GUI 对话框）确认才替换。
- `Forget(host, port)` 等价于 `ssh-keygen -R`：下次连接按新主机处理，而不是继续报不匹配。
- **每一条自己拨号的传输都要挂 `SshHostKey.Attach`**，不只是终端。文件浏览器的 `SftpSession` 另开一条连接，漏挂就等于对那条连接关掉了校验，凭据会交给任何冒名应答的主机。后台拨号遇到不匹配直接拒绝，不弹替换对话框——替换是终端连接的事。回归检查是 Debug MCP 的 `sftp_host_key_check`（默认打本机 WSL sshd 测试环境）。
- **记住密钥族，并在协商时优先提供它。** 服务器通常同时持有 ed25519 / ecdsa / rsa 几把密钥，出示哪把取决于客户端的算法排序。排序一变（SSH.NET 升级、服务器删了某个算法），服务器出示的是另一把**合法**的密钥，指纹对不上就会误报"密钥已变更"。所以和 OpenSSH 一样，`SshHostKey.Attach` 在拨号前把该主机记住的密钥族（`rsa-sha2-256/512` 归为 `ssh-rsa`，证书算法归到它认证的那个族）挪到 `HostKeyAlgorithms` 最前面。
- **不匹配的判定仍然与密钥族无关。** 换了一族的密钥绝不静默信任——冒名者自己决定出示什么，按族分别 TOFU 等于给它开了后门。
- 密钥族存成旁边的 `host:port#type` 条目，而不是改写 `host:port` 的值：这个文件是机器本地的，同一台机器上的旧版 Release 也在读它，旧版只查 `host:port`，看到的指纹不变。旧条目在下一次匹配时补记密钥族。
- **损坏的文件先挪走再按空处理**（`known_hosts.json.corrupt-<时间戳>`，并记警告日志）。原先直接按空处理，下一次 `Trust` 会把整个文件覆盖掉，用户信任过的主机全部静默丢失。读文件本身失败（I/O 错误）则抛异常让这次连接失败，而不是拿空表继续——那同样会覆盖真实文件。

## 拨号：SshDialer、跳板机与端口转发

涉及 `Services/SshDialer.cs`、`SshPortForwarding.cs`。

**所有自己拨号的地方都走 `SshDialer.Connect`**：终端的新连接、文件浏览器的 SFTP、公钥安装。它负责组装凭据、按需经过跳板机、挂上主机密钥校验再连接，所以任何一条路径都不会漏掉校验或忽略跳板机。

**跳板机（ProxyJump）。** `Connection.JumpHost` 是另一个已保存 SSH 连接的**树路径**（如 `vps/bastion`），用它自己的凭据和主机密钥。SSH.NET 不能在另一个会话的通道上跑会话，所以做法是：先连跳板机，开一个 `ForwardedPortLocal("127.0.0.1", 0 → 目标 host:port)`，再连本机那个临时端口。要点：

- **主机密钥按真实目标校验**，不是按 127.0.0.1——否则所有经跳板的主机都会共用一条 `127.0.0.1:随机端口` 记录。`Build(connection, dialHost, dialPort)` 只改实际连接的地址，提示框和 known_hosts 用的仍是真实主机名。
- **拨号时才解析跳板机**（窗口注入的 `ResolveConnection`，就是 `ConnectionStore.TryLoadByTreePath`），改了跳板机下次连接就生效，不存解析结果。解析器在 UI 线程上捕获 store，因为它在拨号的工作线程上被调用，那里读窗口的 `DataContext` 会抛跨线程异常。
- 只支持一跳：跳板机自己的 `JumpHost` 不跟随。
- 隧道的生命周期挂在目标连接上：终端里通过 `SharedSshClient.AddOwnedResource`，SFTP 在 `DisposeClient` 里一起释放。

**端口转发。** `Connection.PortForwards` 每行一条：`L 8080 db:5432`（或 ssh -L 的 `L 8080:db:5432`）、`R 9000 localhost:3000`、`D 1080`（SOCKS）。

- **监听默认只绑 127.0.0.1**，包括远端转发在服务器上的那一端；要对外暴露必须显式写绑定地址。不用 `localhost`：SSH.NET 会自己解析，可能拿到 `::1`，服务器上的 IPv4 客户端就连不上。
- **转发属于这个标签页拨出的传输**，作为 owned resource 挂在 `SharedSshClient` 上：复制出来的标签页共享它们，最后一个持有者释放传输时一起停掉。借用的堡垒机池传输不会再启动一遍。
- **一条失败不影响其它条，也不影响会话**：端口被占用、服务器禁止远端转发，都只在终端里打一行黄色提示。格式错误在编辑器里就提示；通过产品 MCP 写入时直接报错。

回归检查是 Debug MCP 的 `ssh_jump_forward_check`（经跳板执行命令，L/D/R 三种转发各真实传一次数据，释放后端口关闭）。

## 传输复用：SharedSshClient

SSH 在一条已认证连接上多路复用多个 session channel。所以"复制标签页"应该在同一个传输上开一条新的 shell 通道，而不是重新拨号 + 重新认证（对堡垒机来说还意味着重新过一遍 2FA）。

`SharedSshClient` 是引用计数包装：最后一个持有者 `Release` 时才断开并释放。

### 通道容量学习

堡垒机常有"每连接最多 N 条通道"的限制，而 SSH.NET 的同步开通道调用在撞到限制时会**无限期等待**。所以 `CreateShellStreamAsync` 带硬超时，并且超时后完成的通道会被立即释放而不是泄漏。

`ShellChannelCapacityTracker` 记录学到的上限，但对两种信号的处理不同：

- **服务器明确拒绝** → 直接记下这个上限。
- **开通道超时** → 这只是推断，不是答案。只有当这个传输**已经成功开过通道**时，超时才意味着"没有通道了"。一条通道都没开过时，更可能是链路慢或半死，此时记下 0 上限会永久性地把一条健康的传输判死刑。

`IsShellChannelExhausted`（上限为 0）表示服务器连第一条通道都拒了，这种传输放进池子只是浪费槽位。

## 公钥安装

`PublicKeyInstaller` 是 `ssh-copy-id` 的等价物，走一条新开的交互式 SSH shell，**幂等**：已存在的密钥被识别出来而不是重复追加。选哪把公钥：优先连接自己私钥的 `.pub` 同名文件，否则 `~/.ssh` 下第一个找到的默认密钥。

`Build`（可能通过 IPC 查 ssh-agent）和 `Connect` 都跑在后台线程——这两个调用会阻塞，绝不能在 UI 线程上跑。

## RDP 与 WSL

**RDP** 不走应用内终端，通过系统的 `mstsc.exe` 启动。要点：

- 生成的 `.rdp` 文件里的密码用 DPAPI 加密成 mstsc 期望的确切格式（见 [凭据保护](secrets.md)），并在 mstsc 读取后**尽快删除**，不让加密 blob 留在磁盘上。
- mstsc 用 `.rdp` 文件名当窗口标题，所以文件按连接名命名，并放进独立子目录避免多个连接同时启动时撞名。
- 文件写 UTF-16 LE——Windows 自己就是这么写的。

**WSL** 通过 `wsl.exe` 在 ConPTY 里开发行版 shell。两个坑：

- `WslDistroService` 优先直接读 Lxss 注册表键（快、不用起进程、而且知道哪个是默认发行版），只有键存在但没读出东西时才回退到 `wsl.exe --list --quiet`。键不存在意味着从没注册过发行版，起进程也没意义。
- 从无控制台的窗口应用调用 `wsl.exe` 时，**必须重定向全部三个标准流**。stdin/stderr 没有有效句柄时 `wsl.exe` 每次调用会卡约 60 秒。而且要异步排空两个管道，否则 `WaitForExit` 的超时形同虚设，满的 stderr 管道还能把子进程卡死。重定向后 `wsl.exe` 输出的是 UTF-16LE。
`SharedSshClient` 的堡垒机会话池引用只用于连接仍被终端或监控使用的期间。最后一个外部引用释放后，池会移除该条目并断开传输，避免下一次连接复用无人使用的旧路由。
