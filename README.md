# BF66 Codex Display

把梵沐 BF66（Android 13，720 × 1280）变成一块由 Windows 控制的横/竖屏信息屏。支持 USB 与同一 Wi-Fi 传输，内置自定义画面、GPT Usage 和可触屏互动的 Codex 桌宠三种模式。

> 这是非官方个人项目，与 OpenAI、Codex 或梵沐没有隶属或背书关系。Codex 等名称和标识的权利归其各自权利人所有。

## 横屏效果

| 自定义画面 | GPT Usage | Codex 桌宠 |
| --- | --- | --- |
| ![自定义画面](docs/screenshots/效果图1.png) | ![GPT Usage](docs/screenshots/效果图2.png) | ![Codex 桌宠](docs/screenshots/效果图3.png) |
| 文字、时钟、颜色与图片 | 本地用量与周额度 | 工作状态与触屏互动 |

三张图片均来自 BF66 设备的实际横屏截图，不是电脑端模拟图。

## 主要能力

- 自定义画面：标题、正文、时钟、字体大小、前景/背景色，以及 JPG、PNG、WebP、GIF、BMP 图片。
- GPT Usage：每 5 秒从电脑本地 Codex 会话记录计算 Token 汇总，并显示周额度信息。
- Codex 桌宠：轻触、连续轻触、长按和小范围拖动均有动画反馈；长时间无新会话会休息。
- 横竖屏：在控制台中切换，Android 显示端即时旋转并重新排版。
- 双通道：优先 USB，断开数据线后可在同一 Wi-Fi 内自动寻找已配对控制端。
- 静音与本地优先：桌宠不播放声音；显示数据只在电脑与已配对设备之间传输。

## 工作方式

```text
Windows 控制台
  ├─ 读取本地 Codex 用量摘要
  ├─ 提供显示页面与状态接口（端口 8787）
  └─ 生成随机配对密钥
        │
        ├─ USB：ADB reverse → 127.0.0.1:8787
        └─ Wi-Fi：同一局域网 → 电脑地址:8787
                         │
                    BF66 Android 显示端
```

USB 首次连接时，控制端将随机配对密钥传给 BF66。Wi-Fi 模式沿用该密钥，未配对设备无法读取显示内容。

## 使用前准备

- Windows 10/11。
- Android 设备已开启开发者选项和 USB 调试。
- Android SDK Platform Tools；将 `platform-tools` 放到控制台程序旁的 `tools/platform-tools` 目录。
- 从源码构建时需要 .NET 8 SDK；Android 端需要 Android Studio 或兼容的 Gradle/Android SDK 环境。

完整操作见 [使用指南](docs/使用指南.md)，数据边界见 [隐私与安全](docs/隐私与安全.md)。

## 从源码构建

Windows 控制端：

```powershell
dotnet publish pc/BF66Host/BF66Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Android 显示端：使用 Android Studio 打开 `android` 目录，等待 Gradle 同步后构建或安装 `app` 模块。项目使用 Android Gradle Plugin 8.9.2、compileSdk 35，最低支持 Android 6.0（API 23）。

## 目录

```text
android/            BF66 Android 显示端
pc/BF66Host/        Windows 控制端
docs/               使用、隐私说明与效果图
```

## 隐私说明

仓库不包含真实配对密钥、Codex 会话、设备序列号、用户名、绝对本机路径、调试日志或个人配置。`bf66-connection.key` 只会在首次运行时于用户电脑生成，并已加入 `.gitignore`。GPT Usage 读取仅发生在本机，显示端只接收计算后的统计摘要，不接收对话正文。
