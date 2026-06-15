# Jeek Remote Manager

## 需求说明

- 这是一个管理远程连接的工具，支持 SSH 和 RDP 的配置管理。
- SSH 直接调用系统的 ssh.exe，RDP 调用系统的 mstsc.exe。
- 使用 .NET 10 + Avalonia，仅支持 Windows 平台。
- 使用 NetBeauty 优化。
- 使用 R2R 优化。
- 根目录提供构建和运行脚本。
- 支持按目录管理 SSH 和 RDP 连接。
- 每个连接保存成单独的文件。
- 密码加密保存。
- 连接和目录都支持复制、剪切、粘贴。
- 支持右键菜单操作。
- 可以设置连接保存在程序当前目录还是User目录。

## 构建与运行

需要 .NET 10 SDK（Windows）。根目录提供了三个 `.cmd` 脚本，可直接双击运行：

```bat
build.cmd                 :: 调试构建（输出到 .\bin）
build.cmd Release         :: 指定配置

run.cmd                   :: 构建并启动

publish.cmd               :: 发布优化版（R2R + NetBeauty）到 .\publish
publish.cmd win-x64 self  :: 自包含发布（目标机无需安装 .NET 运行时）
publish.cmd win-arm64     :: 指定运行时
```

也可直接使用 dotnet 命令：

```powershell
dotnet build
dotnet run --project JeekRemoteManager
```

### 优化说明

- **R2R（ReadyToRun）**：在 `publish.ps1` 中通过 `-p:PublishReadyToRun=true -r win-x64`
  启用，对 IL 进行提前编译以加快启动。仅在发布时应用，日常 `build`/`run` 不受影响。
  默认框架依赖发布（体积小，需目标机安装 .NET 桌面运行时）；`-SelfContained` 可生成自包含包。
  因 Avalonia 不完全兼容裁剪，已显式关闭 Trimming。
- **NetBeauty**：通过 `nulastudio.NetBeauty` 包集成，发布时自动把运行时/依赖 DLL
  移动到 `libs` 子目录，使输出目录整洁（顶层仅保留 exe 等少量文件）。配置为
  `BeautyOnPublishOnly`，因此只在发布时运行。

## 实现说明

- **存储位置**：连接按目录树保存，每个连接是一个独立的 `.json` 文件，文件名即连接名。
  可在「Settings」中选择存储位置：
  - 用户目录（默认）：`%APPDATA%\JeekRemoteManager\Connections`
  - 程序目录（便携）：exe 同级的 `Connections` 文件夹
  切换时若新位置为空且旧位置有数据，会询问是否复制过去（原数据保留，不自动删除）；
  若目标目录不可写（如装在 `Program Files` 下却选程序目录），会拒绝切换并提示。
  设置保存在 `%APPDATA%\JeekRemoteManager\settings.json`（始终可写，保证选择能持久化）。
  点击工具栏「Open data folder」可直接打开当前数据目录。
- **密码加密**：使用 Windows DPAPI（`CurrentUser` 作用域）加密后以 Base64 保存，
  磁盘上不存在明文密码；只有同一 Windows 账户、同一台机器才能解密。
- **SSH**：调用系统 `ssh.exe`，在新的控制台窗口中打开。支持私钥（`-i`）、
  自定义端口（`-p`）与额外参数（额外参数为高级用法，会原样传给 ssh，请只用于自己的连接）。
  由于 `ssh.exe` 无法通过命令行传入密码，连接时会把密码复制到剪贴板，便于在提示符处粘贴，
  并在 30 秒后自动清除（仅当剪贴板内容仍是该密码时）。
- **RDP**：生成临时 `.rdp` 文件（密码以 mstsc 所需的 DPAPI 十六进制格式写入），
  调用系统 `mstsc.exe` 打开，连接后短暂延时自动删除该临时文件。
- **复制 / 剪切 / 粘贴**：连接和目录均支持。复制为副本（自动重命名避免冲突），
  剪切为移动；目录为递归操作，并阻止把目录粘贴进自身子树。
- **右键菜单 / 快捷键**：树上右键提供 连接、新建、重命名、复制/剪切/粘贴、删除。
  快捷键（焦点在树上时生效）：`Enter` 连接、`Ctrl+C/X/V` 复制/剪切/粘贴、
  `F2` 重命名、`Delete` 删除；双击连接即连接。
- **界面**：左侧为目录/连接树，右侧为连接编辑器。

## 项目结构

```
JeekRemoteManager/
  Models/        Connection、ConnectionType、AppSettings（StorageLocation）
  Services/      PasswordProtector（DPAPI）、ConnectionStore（按文件存储 + 复制/移动）、
                 ConnectionLauncher（ssh/mstsc）、SettingsService（设置与存储位置解析）
  ViewModels/    MainWindowViewModel、TreeNodeViewModel、ConnectionEditorViewModel
  Views/         MainWindow（树 + 编辑器 + 右键菜单 + 对话框）
build.cmd / run.cmd / publish.cmd   根目录构建/运行/发布脚本（可双击）
tests/SmokeTest/ 存储、加密、复制/移动、设置逻辑的冒烟测试（dotnet run --project tests/SmokeTest）
```
