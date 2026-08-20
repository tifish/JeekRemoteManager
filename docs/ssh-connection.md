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
- 文件损坏按空文件处理，用户会被重新询问。

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
