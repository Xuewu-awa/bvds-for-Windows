# bvds-for-windows — B站视频下载器 (Windows)

Android 版 [bvds](../bvds) 的 **C# 全量重写版**。WPF 原生界面，业务逻辑全部用 C# 实现（含前端）。

## 技术栈

| 层 | 技术 |
|---|---|
| **语言/框架** | C# / .NET 10 WPF (MVVM) |
| **HTTP** | HttpClient + 手动重定向链 + Cookie 持久化 |
| **JSON** | System.Text.Json |
| **二维码** | QRCoder |
| **合轨** | 捆绑 ffmpeg 便携版（`-c copy` 不转码） |
| **构建** | dotnet publish（单文件 / 自包含） |

> 为什么合轨用 ffmpeg 而不是系统 API：Windows 没有类似 Android `MediaMuxer` 的简单 MP4 封装 API（Media Foundation 的 MP4 Sink 对 B站 DASH 分离流支持极差且 COM 管线复杂），故捆绑 ffmpeg 便携版，秒级完成无转码合轨。

## 架构

```
┌─── WPF UI (MVVM) ─────────────────┐      ┌─── C# 服务层 (BvdsForWindows.Core) ──┐
│ HomeView / TasksView /            │      │                                      │
│ LoginView / SettingsView          │      │ BvdsService (组合根)                 │
│ 四个页面 + 分P多选 + C盘提醒       │      │  ├─ VideoParser    (三级回退解析)    │
│ 绑定 ViewModel，不碰业务          │      │  ├─ LoginManager   (二维码登录轮询)   │
│                                   │─────→│  ├─ TaskScheduler  (Channel 并发调度) │
│ 命令 → Service                    │      │  ├─ DownloadEngine (流式下载+断点续传)│
│ 事件 → ViewModel → 绑定刷新       │←─────│  ├─ MediaMerger   (ffmpeg -c copy)  │
│                                   │      │  └─ ApiClient     (Cookie 持久化)    │
└───────────────────────────────────┘      └──────────────────────────────────────┘
```

## 项目结构

```
bvds-for-windows/
├── BvdsForWindows.slnx
├── src/
│   ├── BvdsForWindows.Core/            # 业务层（纯 .NET 类库，可独立测试）
│   │   ├── BvdsService.cs              # 组合根（模块组装 / 任务执行协调）
│   │   ├── VideoParser.cs              # 解析：__playinfo__ → 播放API → PGC 三级回退
│   │   ├── LoginManager.cs             # 二维码登录（轮询 / 过期重生成）
│   │   ├── TaskScheduler.cs            # Channel 并发调度（1-64 可配置 / 重试3次退避）
│   │   ├── DownloadEngine.cs           # 流式下载 + Range 断点续传 + 进度回调
│   │   ├── MediaMerger.cs              # ffmpeg -c copy 合轨
│   │   ├── ApiClient.cs                # UA/Referer/Cookie/手动跟跳/gzip
│   │   ├── CookieStore.cs              # Cookie 池（JSON 持久化）
│   │   ├── ConfigStore.cs              # 配置（%AppData%/bvds/config.json）
│   │   └── QrCodeGenerator.cs          # QRCoder 封装
│   └── BvdsForWindows/                 # WPF 应用（MVVM）
│       ├── App.xaml / MainWindow.xaml  # 浅色主题 + 底部信息条 + 页面动画
│       ├── ViewModels/                 # Home / Tasks / Login / Settings
│       └── Views/                      # 四个页面 + 分P多选窗 + C盘提醒窗
├── tests/
│   ├── BvdsSmokeTest/                  # 集成冒烟测试（联网验证解析/下载链路）
│   └── Probe/                          # 临时诊断探针
└── tools/ffmpeg/                       # 捆绑的 ffmpeg 便携版（不入库）
```

## 功能

- 二维码扫码登录（Cookie 持久化，重启保持登录，登录状态显示于底部信息条）
- 短链自动跳转（b23.tv），批量粘贴多个链接
- 清晰度：4K / 1080P / 720P / 480P / 360P（360P+ 需登录）
- 三种下载模式：仅视频 / 仅音频 (MP3) / 视频+音频合轨（ffmpeg）
- 分P / 番剧整季识别，自动弹窗多选（全选 / 取消全选）
- 并发下载数可配置（1-64，默认 5，超过 15 弹窗确认）
- 失败指数退避重试（500ms→1s→2s），重试自动断点续传
- 下载目录可配置，C盘下载提醒（可勾选"以后不再提醒"）
- 主题切换（青色 / 琥珀，配色参考设计稿）
- 页面切换 / 弹窗 / Toast 过渡动画
- 内置调试日志（设置页查看 / 导出）

## 构建与发布

```bash
dotnet build BvdsForWindows.slnx                          # Debug 开发构建

# 绿色版（单文件自包含，目标机无需装 .NET，~62MB + ffmpeg）
dotnet publish src/BvdsForWindows/BvdsForWindows.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

# 依赖版（单文件，需 .NET 10 Desktop Runtime，~0.5MB + ffmpeg）
dotnet publish src/BvdsForWindows/BvdsForWindows.csproj -c Release \
  --self-contained false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

发布目录包含 `BvdsForWindows.exe` + `tools/ffmpeg/ffmpeg.exe`（合轨组件，刻意保留外部避免拖慢单文件启动）。ffmpeg 查找顺序：程序目录 `tools/ffmpeg/ffmpeg.exe` → 仓库根 → PATH。

## 测试

```bash
dotnet run --project tests/BvdsSmokeTest   # 需要联网，验证解析/登录/断点下载链路
```

## 配置

`%AppData%\bvds\config.json`（所有版本共用）：

| 键 | 说明 | 默认 |
|---|---|---|
| `cookies` | 登录 Cookie（SESSDATA 等） | - |
| `download_dir` | 下载目录 | `~/Downloads/bvds` |
| `quality` | 清晰度 | `2` (1080P) |
| `theme` | 主题 teal/amber | `teal` |
| `max_concurrent` | 并发下载数 1-64 | `5` |
| `no_c_drive_warning` | 是否不再提醒 C 盘 | - |

## 变更记录

### v1.1 — Windows 首发
- Android 版 (Kotlin) 全量移植为 C# / WPF
- 适配 B站新版页面（番剧整季走 `pgc/view/web/season` API）
- 单文件发布，绿色版 / 依赖版双版本

## 声明

仅供学习交流，请勿用于违法用途。

## 许可

MIT
