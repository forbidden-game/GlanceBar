# GlanceBar

适用于 Windows 11 (x64) 的轻量任务栏资源监视器。直接贴合在任务栏上显示实时网速、CPU、GPU 和内存状态，鼠标悬停即可查看占用最高的进程。

## 功能与交互

- **任务栏常驻指标**：实时显示 `↓ 下载速度`、`↑ 上传速度`、`CPU 占用率与温度`、`GPU 占用率`、`内存占用率`。
- **悬停查看详情**：鼠标移至对应指标区域时弹出明细卡片：
  - **网速**：物理网卡总速率，以及按实际 TCP 连接归类的进程树；通过本机代理下载的应用显示在代理进程下方，支持多级转发。
  - **CPU**：总占用率、温度，以及 CPU 占用最高的 Top 5 进程。
  - **GPU**：当前最繁忙图形引擎的负载百分比。
  - **内存**：已用 / 总物理内存，以及工作集（Working Set）最大的 Top 5 进程。
- **自适应外观**：基于纯 Win32 GDI 绘制，支持任务栏透明贴合、系统深浅色主题切换、Per-Monitor V2 高 DPI 缩放，并在全屏游戏或演示模式下自动隐藏。

### 鼠标操作

| 操作 | 作用 |
| :--- | :--- |
| **鼠标悬停** | 显示对应指标的详细数值与 Top 5 进程排行 |
| **左键拖拽** | 在任务栏空白区域移动停靠位置（松开后自动保存至 `HKCU\Software\GlanceBar`） |
| **左键单击** | 打开 Windows 任务管理器（`taskmgr.exe`） |
| **右键单击** | 打开菜单：打开任务管理器、切换开机自启、退出程序 |

## 安装与卸载

### 系统要求

- **操作系统**：Windows 11 x64（Build 22000 及以上）
- **运行权限**：安装过程需要管理员权限，用于注册后台采集服务 `GlanceBarNetwork` 及安装读取 CPU 温度所需的 `PawnIO` 驱动；日常界面以普通用户权限运行。

### 安装

1. 前往 [GitHub Releases](https://github.com/forbidden-game/GlanceBar/releases/latest) 下载最新的 `GlanceBar-<版本>-win-x64-Setup.exe`（可通过同目录下的 `.sha256` 文件校验完整性）。
2. 运行安装程序并按提示完成安装。若首次安装 `PawnIO` 驱动提示需要重启系统，请重启后再使用。

> **说明**：安装器目前未进行 Authenticode 代码签名，Windows SmartScreen 可能会提示“未知发布者”。如果是静默安装或从已提权终端启动安装器，安装后请在目标普通用户会话中直接运行 `GlanceBar.exe`，或执行 `GlanceBar.exe --configure-user` 完成当前用户配置。

### 卸载

在 Windows **设置 → 应用 → 已安装的应用** 中找到 **GlanceBar** 卸载，或运行安装目录下的 `unins000.exe`。

- 卸载程序会自动停止并移除 `GlanceBarNetwork` 服务，清理 `%ProgramData%\GlanceBar` 与程序目录（可能被其他硬件监控工具共用的 `PawnIO` 驱动不会随之卸载）。
- 如需在卸载前清除当前用户的注册表配置（停靠位置与开机自启项），可在普通用户终端中执行：
  ```powershell
  & "$env:ProgramFiles\GlanceBar\GlanceBar.exe" --clear-user-settings
  ```

## 数据口径说明

程序约每 **2 秒**采样并刷新一次，各项指标口径如下：

- **总网速**：汇总当前已连接物理网卡的收发流量（排除虚拟/过滤层重复计数）。采用十进制字节单位（`1 MB/s = 1,000,000 B/s`）。
- **进程连接树**：通过内核 ETW 分别采集非回环流量和应用连接本机服务的流量，再用 Windows IPv4/IPv6 TCP 监听端口与连接所有者确定父子关系，不按进程名猜测。代理行的“对外”速率与应用子行的“本机连接”速率代表同一次传输的不同连接段，不能相加，也不保证与网卡区间总量逐项相等。
- **归属边界**：同一应用的直连与本机代理流量分开；唯一且无混合对外连接的上游可继续嵌套，多上游无法确定时保留独立节点。仅有 TUN 路由或 UDP 信息时不猜测代理父进程；未确认的本机活动仍独立显示，不再直接过滤掉应用。
- **显示限制**：显示前 5 组及每个节点最活跃的前 5 个子项，最多 32 行；省略内容保留“另有 N 项”提示，父节点始终先于子节点。
- **CPU**：使用 `GetSystemTimes` 计算全机非空闲时间占比（未按睿频频率加权，数值可能与任务管理器的 `% Processor Utility` 不同）。温度优先读取 CPU 封装传感器，无对应传感器时取最高核心温度；硬件不支持或服务未运行时显示 `–°C`。
- **GPU**：通过 PDH 计数器 `\GPU Engine(*)\Utilization Percentage` 汇总各物理引擎占用并取最繁忙引擎百分比；无有效数据时显示 `–`。
- **内存**：总占用为系统已用物理内存占比；进程排行为各进程当前工作集大小。

## 从源码构建

### 环境依赖

- Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/) (win-x64)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)（包含命令行编译器 `ISCC.exe`）

### 构建命令

在仓库根目录下运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Release.ps1
```

脚本会自动发布自包含单文件程序、校验并打包固定版本的官方 `PawnIO` 安装器及第三方开源许可声明，最终在 `dist/` 目录生成安装包与 `.sha256` 校验文件。

网络归类的回归测试（IPv4/IPv6、混合直连与代理、多级代理、歧义、循环和条目限制）：

```powershell
dotnet test tests/GlanceBar.Tests/GlanceBar.Tests.csproj -c Release
```

### 架构简述

- **前端界面 (`GlanceBar.exe`)**：普通用户权限运行。采用双层分层窗口（`LWA_COLORKEY` 透明渲染窗口 + `LWA_ALPHA=1` 鼠标输入窗口）挂载于系统任务栏，轮询系统性能计数器并只读读取网络与温度快照。
- **后台服务 (`GlanceBar.exe --network-service`)**：以 `LocalSystem` 权限订阅内核 ETW 网络事件，关联 TCP 端点所有者，并通过 `LibreHardwareMonitor` 读取 CPU 温度；每 2 秒将带父子层级的版本化快照原子更新至 `%ProgramData%\GlanceBar\network-processes.tsv`。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。安装包内附带 `.NET Runtime`、`LibreHardwareMonitor`、`PawnIO` 等第三方依赖的许可证声明及 `PawnIO` 源码包。
