# Pasteup

> Windows 剪贴板历史 + 截图 / 贴图工具（C# / .NET 10 / WPF，开发中）。

Pasteup 常驻系统托盘：`Alt+V` 唤起剪贴板面板（历史、搜索、收藏、置顶、预览、粘贴），
`Alt+S` 框选截图并生成可置顶的贴图窗口。设计上强调**键盘优先、低打扰、本机隐私、便携部署**：
不联网、不装系统服务，数据只保存在本机，发布形态为解压即用的自包含单目录便携版。

## 功能特性

- **剪贴板历史**：文本、图片、文件列表自动入库；默认容量 1000 条 / 保留 90 天（可设置）；置顶与收藏不参与过期淘汰；右键菜单支持粘贴 / 复制 / 收藏 / 置顶 / 删除。
- **键盘优先的面板**：`Alt+V` 唤起或收起；`↑↓` 选择、`Enter` 粘贴、`Esc` 隐藏、`Space` 预览；搜索框输入即过滤；虚拟化列表支撑大历史量。
- **智能落位**：面板优先贴近当前文本光标（Windows 原生 caret / UI Automation / MSAA 多级回退），取不到时跟随鼠标；适配多显示器与高 DPI 缩放。
- **截图与贴图**：`Alt+S` 框选截图，生成置顶贴图窗口；多张贴图可同时存在，支持滚轮缩放、`Ctrl+滚轮` 调透明度、`F9` 点击穿透；托盘右键「贴图列表」可集中查看已打开的贴图（缩略图 + 尺寸），点击激活置前、一键全部关闭。
- **来源应用图标**：历史条目按来源进程显示真实应用图标。
- **双主题**：浅色「框中纸」/ 深色「数据终端」两套设计令牌，可跟随系统深浅色。
- **本机隐私**：不联网；历史 / 设置 / 缓存保存在 `%LOCALAPPDATA%\Pasteup`；尊重 Windows 与密码管理器写入的「不记录 / 不上云」剪贴板标记，被标记的内容不会入库。
- **轻量驻留**：托盘常驻、单实例（重复启动只唤起已有实例）；空闲时自动修剪工作集（实测可回落到 ~10MB 级别）。
- **便携发布**：自包含 .NET 运行时，无安装器、无需管理员权限；解压后双击 `Pasteup.exe` 即用。

## 系统要求

- Windows 11 或 Windows 10（建议 22H2 及以上），64 位（x64）。已在 Windows 11 实机验证；Windows 10 与 ARM64 未实测。
- 运行发布版**无需预装 .NET**；从源码构建需要 **.NET 10 SDK**（开发验证版本 10.0.400）。
- 界面目前为中文。

## 快速开始

### 使用便携版

1. 解压 `Pasteup-<版本>-win-x64-portable.zip` 到任意文件夹（保留文件夹内全部文件，不能只拷 `Pasteup.exe`）。
2. 双击 `Pasteup.exe`。程序无主窗口，启动后驻留通知区域（托盘，时钟旁，可能折叠在 `^` 里）。
3. `Alt+V` 唤起面板，`Alt+S` 截图；托盘右键 → 贴图列表 / 设置 / 退出。

首次运行若遇 Windows SmartScreen（程序未签名），点「更多信息」→「仍要运行」。
数据目录为 `%LOCALAPPDATA%\Pasteup`；升级前先在托盘退出再覆盖程序目录，历史记录不受影响。

### 从源码构建运行

```powershell
# 环境：Windows + .NET 10 SDK
dotnet build                                              # 构建 Debug（日常开发默认）
dotnet test tests/Pasteup.Tests                           # 运行单元测试
src\Pasteup.App\bin\Debug\net10.0-windows\Pasteup.exe     # 直接运行
```

> 仓库约定：日常只构建 Debug；Release / 发布构建只在打包或明确需要时执行（开发约定见本地 `AGENTS.md`，不随仓库分发）。

### 打包便携版

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-portable.ps1
```

输出（`publish/` 已被 git 忽略）：

- `publish/pasteup/`：最新便携版目录（应用 + `使用说明.txt` + `版本信息.txt`）
- `publish/Pasteup-<版本>-win-x64-portable.zip`：可直接分发的 zip（根目录为版本化文件夹）
- `publish/Pasteup-<版本>-win-x64-portable.zip.sha256`：SHA256 校验文件

打包规则、产物约定与验收清单见本地开发文档 `docs/rules/发布与打包.md`（`docs/` 不随仓库分发）。

## 项目结构

```
Pasteup.sln
├─ src/Pasteup.Core/     剪贴板捕获、历史索引与持久化、全局热键（无 UI 依赖）
├─ src/Pasteup.Themes/   设计令牌与浅 / 深主题 ResourceDictionary
├─ src/Pasteup.App/      WPF 宿主：面板、设置、贴图、截图覆盖层（MVVM + DI）
├─ tests/Pasteup.Tests/  单元测试
├─ scripts/              开发与发布脚本
├─ packaging/            分发包内文件模板（使用说明）
├─ docs/                本地开发文档（任务记录 / 报错 / 规则 / 原型 / 截图，不随仓库分发）
└─ publish/              本地发布产物（git 忽略）
```

## 文档

开发过程中的任务记录、报错分析、设计规则、交互原型与截图保存在本地 `docs/` 目录，**不随仓库分发**（已在 `.gitignore` 中忽略）。

- 本地 `AGENTS.md`：技术栈、文档规则、构建约定与开发记录总表（不随仓库分发）
- `docs/tasks/`：每个任务的落实记录（问题、解决、踩坑）
- `docs/errors/`：报错信息、原因分析与解决方案
- `docs/rules/`：长期有效的编码 / 设计 / 发布规则
- `docs/prototypes/`：可直接用浏览器打开的交互原型

## 隐私与数据

- 剪贴板历史以明文 JSON / PNG 保存在本机 `%LOCALAPPDATA%\Pasteup`（`history.json` + 增量 `history.journal`、`cache/`、`logs/`），不会上传网络；密码等敏感内容请使用密码管理器。
- 程序尊重系统 / 密码管理器的剪贴板排除标记（不记录、不上云），被标记内容不会入库。
- 不联网、不安装系统服务；「设置 → 通用 → 开机自启」可按需开启（默认关闭，只写当前用户启动项，可随时关闭）。
- 完全卸载：托盘右键退出 → 删除程序文件夹 → 删除 `%LOCALAPPDATA%\Pasteup`。

## 已知限制 / 开发中

- 未做数字签名：首次运行可能触发 SmartScreen 或杀软误报。
- 仅 Windows x64 实测；Windows 10、ARM64、多用户切换等场景未系统验证。
- 界面仅中文。
- 功能与文档仍在持续迭代，内部接口 / 存储格式可能调整。

## 许可证

本仓库暂未添加 `LICENSE` 文件，默认保留所有权利；如需开源或再分发，请补充许可证。
第三方依赖（WPF-UI、CommunityToolkit.Mvvm、H.NotifyIcon.Wpf、Microsoft.Extensions.DependencyInjection 等）遵循各自 NuGet 包许可。

## 贡献

欢迎提 issue / PR。动手前请先阅读本地 `AGENTS.md` 的文档与构建约定（不随仓库分发）：
本地开发记录放 `docs/tasks/`、报错分析放 `docs/errors/`、长期规则放 `docs/rules/`（`docs/` 不随仓库分发）；日常构建只构建 Debug。
