# Codex Process Monitor

面向 Windows 11 x64 的 .NET 8 WPF 本地监视器。Codex Process Monitor 以严格只读方式观察 Codex/ChatGPT 桌面进程树、子进程资源、Plugin/MCP/Skill 元数据和两套 `logs_2.sqlite` 元数据源；首版不支持独立手动启动的 Codex CLI。

Codex Process Monitor is a Windows-focused .NET 8 WPF desktop monitor. It observes the Codex/ChatGPT desktop process tree, read-only resource counters, local Plugin/MCP/Skill metadata, and both Codex `logs_2.sqlite` sources. Standalone manually launched Codex CLI processes are outside the first release.

## 功能 / Features

- 读取正在运行的进程及操作系统公开的进程元数据，例如 PID、名称、启动时间和可用的 CPU、内存或 I/O 指标。
- 以本地快照的方式支持排查、观察和测试；无法访问的受保护进程字段会被跳过或按实现返回不可用状态。
- 提供面向 .NET 的基础设施代码，便于上层入口复用，而不要求监视器拥有系统管理权限。
- CI 在 Windows 上验证 restore、build、test，并生成 `win-x64` 自包含发布包和 SHA-256 校验文件。
- WPF 工作台提供总览、进程树、窗口关联、Plugin/MCP/Skill、历史与诊断、设置页面；曲线使用轻量自绘控件。
- 以一次轻量的原生顶级窗口枚举，把可见/前台 Codex 窗口关联到所属 PID；Chromium 子进程会明确标为共享，避免把它们误认成某一条具体对话。
- 默认每 2 秒采样；应用自己的历史数据库位于项目/安装目录下的 `monitor-data\monitor.sqlite`，不会写入 Codex 目录或用户配置目录。

The project:

- reads running processes and the process metadata exposed by Windows, such as PID, name, start time, and available CPU, memory, or I/O counters;
- provides local snapshots for troubleshooting, observation, and tests; protected or inaccessible fields may be omitted or reported as unavailable;
- keeps the reusable monitoring infrastructure separate from any user-facing entry point; and
- produces a self-contained `win-x64` package with a SHA-256 checksum in CI.
- includes a WPF dashboard, process tree, integration inventory, history and redacted diagnostic report actions; and
- writes history only to the monitor-owned `monitor-data` directory beside the project or installed application.

## 监控范围 / Monitoring scope

首版只保留有证据的 Codex 桌面树：`ChatGPT.exe` 根、`codex app-server` 根及其后代；树外进程只有路径明确位于 `.codex\plugins` 的 extension-host 才会纳入。没有 Codex 根时界面显示空树和信息提示，不回退为全机进程列表。进程身份使用 PID 与启动时间组合，避免 PID 复用串线。

The first release retains only evidence-backed Codex desktop roots (`ChatGPT.exe` and `codex app-server`) and their descendants. A tree-external extension host is included only when its path is explicitly under `.codex\plugins`. If no root is present, the UI shows an empty tree rather than scanning every process. PID plus start time is used as the stable identity.

## 只读边界 / Read-only boundary

监视器只读取系统状态。它不会终止、暂停、启动或重启进程，不会修改服务、计划任务、注册表、启动项或文件，不会注入代码，也不会改变网络配置。读取权限不足时，程序应报告受限结果或跳过该字段，而不是通过提权或修改系统来绕过限制。

The monitor is intentionally read-only. It does not terminate, suspend, start, or restart processes; modify services, scheduled tasks, the registry, startup items, or files; inject code; or change network configuration. If Windows denies access, the result should be reported as limited or unavailable rather than bypassed through elevation or system changes.

## 安装与运行 / Install and run

### 使用发布包 / Use a release package

从 GitHub Release 下载 `Codex.ProcessMonitor-<version>-win-x64.zip` 及同名 `.sha256` 文件，先校验压缩包，再解压运行其中的应用程序。该包为 self-contained，不要求目标机器预装 .NET Runtime；它仍然只支持与构建目标相符的 64 位 Windows 环境。

Download `Codex.ProcessMonitor-<version>-win-x64.zip` and its `.sha256` file from a GitHub Release. Verify the archive before extracting and running its application. The archive is self-contained and does not require a separate .NET Runtime, but it targets 64-bit Windows.

在 PowerShell 中校验：

```powershell
Get-FileHash .\Codex.ProcessMonitor-<version>-win-x64.zip -Algorithm SHA256
Get-Content .\Codex.ProcessMonitor-<version>-win-x64.zip.sha256
```

### 从源码运行 / Run from source

开发环境需要 Windows、.NET 8 SDK，以及可运行发布脚本的 PowerShell。根目录 solution 包含 Core、Infrastructure、WPF App 和测试项目：

```powershell
dotnet restore .\Codex.ProcessMonitor.sln
dotnet build .\Codex.ProcessMonitor.sln --configuration Release --no-restore
dotnet test .\Codex.ProcessMonitor.sln --configuration Release --no-build
```

To run the WPF application from source:

```powershell
dotnet run --project .\src\Codex.ProcessMonitor.App\Codex.ProcessMonitor.App.csproj
```

The application starts as a normal user (`asInvoker`); it does not register startup entries or request administrator privileges.

## 数据隐私 / Data privacy

- 默认情况下，监视数据留在本机；项目不因读取快照而自动上传遥测或向远端发送进程数据。
- 进程名、PID、路径、命令行、用户、资源指标和导出文件可能属于敏感信息。分享日志、截图或发布包前，请先审查并脱敏。
- 受保护进程和跨用户进程可能无法完整读取；不要为了获取字段而以管理员身份运行，除非你的组织明确要求并理解其影响。
- 两个 Codex 日志库（`%USERPROFILE%\.codex\logs_2.sqlite` 与 `%USERPROFILE%\.codex\sqlite\logs_2.sqlite`）分别以只读连接追踪；仅允许 `id`、时间、等级、target、module_path、file、line、thread_id、process_uuid、estimated_bytes 等元数据列，绝不读取或保存 `feedback_log_body`。
- 窗口关联只读取顶级窗口的 PID、可见/最小化/前台状态和尺寸；不读取窗口标题、辅助功能树、对话标题或对话内容，也不会把窗口关联写入历史库或诊断报告。
- 本地历史批处理保留进程明细 7 天、5 分钟汇总 30 天；命令行和完整路径不会写入应用历史或导出 CSV。若需要指定存储位置，可设置 `CODEX_PROCESS_MONITOR_DATA_DIR`；否则数据保留在项目/安装目录的 `monitor-data` 下。
- 本仓库忽略 `.codex`、诊断快照、数据库、日志和构建产物；这些内容不应提交到版本库。

By default, observations remain local; the project does not upload telemetry or process data merely because a snapshot is read. Process names, PIDs, paths, command lines, users, counters, and exported files may be sensitive, so review and redact them before sharing. Protected or cross-user processes may not be fully readable. Do not run elevated solely to obtain a field unless that is an explicit, understood operational requirement.

## 开发与构建 / Development and build

建议使用与项目目标框架匹配的 .NET 8 SDK。当前项目的本地验证顺序如下：

```powershell
dotnet restore .\src\Codex.ProcessMonitor.App\Codex.ProcessMonitor.App.csproj
dotnet build .\src\Codex.ProcessMonitor.App\Codex.ProcessMonitor.App.csproj --configuration Release --no-restore
dotnet test .\tests\Core.Tests\Codex.ProcessMonitor.Core.Tests.csproj --configuration Release
dotnet test .\tests\Infrastructure.Tests\Infrastructure.Tests.csproj --configuration Release
```

生成本地发布包（如果有多个应用项目，请用 `-Project` 指定可执行项目）：

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1 `
  -Project .\src\<Application>\<Application>.csproj
```

脚本面向 `win-x64`、`Release` 和 self-contained 发布，明确设置 `PublishTrimmed=false`，以避免反射或运行时发现机制因裁剪而缺失。输出位于 `artifacts`，包括 ZIP 和相邻的 `.sha256` 文件。也可以用 `-Runtime`、`-Configuration`、`-OutputRoot` 或 `-Version` 覆盖默认值。

The publish script targets `win-x64`, `Release`, and self-contained deployment, and explicitly sets `PublishTrimmed=false` so reflection or runtime discovery is not broken by trimming. It writes the ZIP and adjacent `.sha256` file below `artifacts` by default. Use `-Runtime`, `-Configuration`, `-OutputRoot`, or `-Version` to override defaults.

## CI 与发布 / CI and release

`.github/workflows/ci.yml` 在 `windows-latest` 上执行 restore、build、test 和 publish，并将发布文件上传为 Actions artifact：

- Pull request、分支 push 和 `workflow_dispatch` 只构建并上传 artifact；手动运行不会创建 GitHub Release。
- 推送匹配 `v*` 的 tag（例如 `v1.0.0`）时，workflow 额外创建 GitHub Release，附带 self-contained `win-x64` ZIP 与 SHA-256 文件。
- Release 资产的校验文件使用 `<SHA256>  <filename>` 格式，可用 `Get-FileHash` 或其他 SHA-256 工具复核。

The workflow runs restore, build, test, and publish on `windows-latest`. Pull requests, branch pushes, and manual runs upload an artifact only. A push of a `v*` tag (for example `v1.0.0`) additionally creates a GitHub Release containing the self-contained `win-x64` ZIP and its SHA-256 file. A manual run never creates a Release.

## 许可证 / License

本项目使用 MIT License，详见 [LICENSE](LICENSE)。

This project is available under the MIT License; see [LICENSE](LICENSE).
