# bvds-for-windows — B站视频下载器 (Windows)

Android 版 [bvds](../bvds) 的 **C# / WPF 全量重写版**。业务逻辑逐模块从 Kotlin 翻译，UI 用 WPF 按 Android 版深色主题重新实现。

## 技术栈

| 层 | 技术 |
|---|---|
| **语言/框架** | C# / .NET 10 WPF (MVVM) |
| **HTTP** | HttpClient + 手动重定向链 + Cookie 持久化 |
| **JSON** | System.Text.Json |
| **二维码** | QRCoder |
| **合轨** | 捆绑 ffmpeg 便携版（`-c copy` 不转码） |

> 为什么合轨用 ffmpeg 而不是系统 API：Windows 没有类似 Android `MediaMuxer` 的简单 MP4 封装 API（Media Foundation 的 MP4 Sink 对 B站 DASH 分离流支持极差且 COM 管线复杂），故捆绑 ffmpeg 便携版，秒级完成无转码合轨。

## 项目结构

```
bvds-for-windows/
├── BvdsForWindows.sln
├── src/BvdsForWindows/
│   ├── App.xaml / MainWindow.xaml      # 深色主题 + 左侧导航壳
│   ├── Core/                           # 业务层（对应 Android Kotlin 模块）
│   │   ├── BvdsService.cs              # 组合根（对应 BvdsEngine）
│   │   ├── VideoParser.cs              # 解析：__playinfo__ → 播放API → PGC 三级回退
│   │   ├── LoginManager.cs             # 二维码登录（轮询 + 过期重生成）
│   │   ├── TaskScheduler.cs            # Channel + SemaphoreSlim 并发调度（3任务/重试3次）
│   │   ├── DownloadEngine.cs           # 流式下载 + Range 断点续传 + 进度回调
│   │   ├── MediaMerger.cs              # ffmpeg -c copy 合轨
│   │   ├── ApiClient.cs                # UA/Referer/Cookie/手动跟跳
│   │   ├── CookieStore.cs              # Cookie 池（JSON 持久化）
│   │   ├── ConfigStore.cs              # 配置（%AppData%/bvds/config.json）
│   │   └── QrCodeGenerator.cs          # QRCoder 封装
│   ├── ViewModels/                     # MVVM（Home/Tasks/Login/Settings）
│   └── Views/                          # 四个页面 + 分P选择弹窗
├── tests/BvdsSmokeTest/                # 核心逻辑集成冒烟测试（无 GUI）
└── tools/ffmpeg/                       # 捆绑的 ffmpeg 便携版
```

## 功能（与 Android 版对齐）

- 二维码扫码登录，Cookie 持久化（重启保持登录）
- 短链自动跳转（b23.tv），批量链接输入
- 清晰度：4K / 1080P / 720P / 480P / 360P（360P+ 需登录）
- 三种模式：仅视频 / 仅音频 (MP3) / 视频+音频合轨（ffmpeg）
- 分P / 合集（番剧整季）识别 + 多选弹窗
- 并发 3 任务，失败指数退避重试（500ms→1s→2s），重试断点续传
- 下载目录可配置，完成后可直接打开文件夹
- 内置调试日志（设置页查看 / 导出）

## 构建与运行

```bash
dotnet build src/BvdsForWindows/BvdsForWindows.csproj
dotnet run --project src/BvdsForWindows
```

ffmpeg 查找顺序：`tools/ffmpeg/ffmpeg.exe`（或解压子目录 `tools/ffmpeg/ffmpeg-*/bin/ffmpeg.exe`）→ 程序输出目录 `tools/ffmpeg/ffmpeg.exe` → PATH。未找到时合轨模式不可用（仅视频/仅音频不受影响）。

仓库已捆绑 ffmpeg 6.1.1 (essentials) 便携版于 `tools/ffmpeg/ffmpeg.exe`，构建时自动复制到输出目录，无需手动安装。

## 测试

```bash
dotnet run --project tests/BvdsSmokeTest   # 需要联网，验证解析/登录/断点下载链路
```

## 声明

仅供学习交流，请勿用于违法用途。
