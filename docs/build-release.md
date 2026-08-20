# 构建、发布与自动更新

涉及 `Build.cmd`、`Run.cmd`、`Launch.ps1`、`install.ps1`、`.github/workflows/BuildAndRelease.yml`、`JeekRemoteManager.csproj`、`Services/AutoUpdateService.cs`、JeekTools 的 `AutoUpdater.cs`。

## 版本号 = 提交数

CI 用 `git rev-list --count HEAD` 算出提交数，作为程序集主版本号烤进 Release 构建（`/p:Version=<count>`），同时写进随发布上传的 `version.txt`。开发构建永远是 `0.0.0.0`，并且低于阈值的本地版本被视为开发构建**永不更新**。

## 两个构建脚本，用途不同

| | 用途 | 行为 |
| --- | --- | --- |
| `Build.cmd` | **Release 出货脚本** | 清理 `bin` 里的陈旧产物（避免 NetBeauty / 依赖变动留下孤儿 DLL），Release 构建，剥掉 PDB |
| `Run.cmd` | **开发/agent 迭代循环** | 默认 Debug 构建然后启动 |

**给 agent 做功能测试时不要用 `Build.cmd`**——Debug MCP 只在 Debug 构建里监听。

`Run.cmd` 只杀"由本 worktree 构建出来的那个进程"（比较可执行文件完整路径），其它 worktree 的 Debug 实例**故意保留**，用于并排验证。

**任何触及本工程的构建之前都必须先停掉应用**，包括 `dotnet run` 跑 `Tests\SmokeTest`——那会重新构建 `JeekRemoteManager`，而运行中的应用占着 `bin`，NetBeauty 后处理步骤会以 MSB3073 失败。出现这个错误时代码本身没有问题。

`Launch.ps1` 通过 WMI 的 `Win32_Process.Create` 启动，**目的是跳出 Grok Build 的 kill-on-close job object**（只有 Grok 有这个问题，但这条路径对所有 agent 都能用）。

## MCP 适配器随构建发布

`JeekRemoteManager.csproj` 里有两个 target（`AfterTargets="Build"` 和 `AfterTargets="Publish"`），把 `Tools/JeekRemoteManagerMcp` 单文件发布到同一个输出目录，**保证任何打包都不会漏掉它**（CI 里还有一条显式检查：`bin\JeekRemoteManagerMcp.exe` 不存在就让构建失败）。

覆盖之前先把已存在的 exe **改名**（Windows 允许重命名正在运行的映像），这样残留的适配器进程不会让主工程构建失败。改名后的 `.old` 文件尽力删除。

再次强调：这个副本只是构建产物，**应用启动时才把它安装到固定的每用户路径**，构建步骤里不安装。

## NetBeauty

`nulastudio.NetBeauty` 把依赖 DLL 收进 `Libs` 子目录，让根目录干净。它是**构建时**就跑（`BeautyOnPublishOnly=False`），这也是"应用在跑就不能构建"的直接原因。

## 发布流程（CI）

push 到 `main` 时：算提交数 → Release 构建 → 校验适配器存在 → 删 PDB → 打包 `bin\*` 成 zip → 删掉旧的 `latest_release` 发布和标签 → 重建 → 上传 zip 和 `version.txt`。

用固定标签 `latest_release` 而不是版本号标签，是为了让下载地址永远稳定，安装脚本和自动更新都能写死。

## 自动更新

`AutoUpdater`（JeekTools）的流程是**先下载、暂存、校验，最后才交给外部 PowerShell 脚本换文件并重启**。这样失败的下载永远不会让用户失去一个能跑的应用。

几个设计点：

- **Debug 实例永不自更新。**
- 下载放在 `%LOCALAPPDATA%\<App>\Update` 而**不是系统 temp**：推迟安装的包必须能挺过重启和临时目录清理。并行的调试实例要指向各自隔离的根，避免抢文件。
- 暂存目录里保留一份和发布资产同名的本地 `version.txt`。下次检查**只有在这个文件与当前远端版本匹配时**才复用已暂存的包。
- 校验方式是"暂存包里必须有应用可执行文件"。
- 下载走 `GitHubMirrors`：没有成功记录时先测速排序镜像；某个镜像完整下完 zip 之后，后续优先用它。停滞或速度长期低于下限的镜像会被放弃。**版本检查只对 `version.txt` 赛跑取版本号，不参与选 zip 镜像。**

## 安装

`install.ps1` 装到 `%LOCALAPPDATA%\Programs\JeekRemoteManager` 并建开始菜单快捷方式，**不写任何注册表项**。卸载 = 退出应用 + 删目录 + 删快捷方式。

## 子模块

`JeekTools.NET` 是 submodule。**先提交并推送它，再提交父仓库里移动指针的那次提交。**

## 其它约定

- Git 一律 rebase + fast-forward，不用 merge。
- 提交信息用英文，一两句话说明目的，不展开实现细节。
- 运行时文件不从源目录拷贝，直接放在 `bin` 目录下并纳入版本控制。
