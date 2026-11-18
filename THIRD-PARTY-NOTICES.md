# 第三方组件

本项目自身以 MIT 许可发布（见 `LICENSE`）。运行时依赖以下第三方组件，各自遵循其原始许可。

| 组件 | 版本 | 许可 | 用途 | 来源 |
|---|---|---|---|---|
| ScreenRecorderLib | 7.0.1 | MIT | 屏幕捕获（WGC / Desktop Duplication）与 Media Foundation 编码 | https://github.com/sskodje/ScreenRecorderLib |

ScreenRecorderLib 以二进制形式随发布包分发（`ScreenRecorderLib.dll`），其许可证文本随 NuGet 包提供。

以下组件由 Windows 提供，不属于本项目分发内容：

- Media Foundation（`mfplat.dll`、`mfreadwrite.dll`）——系统组件
- Windows Graphics Capture / Desktop Duplication API —— 系统组件

应用图标由本仓库的 `tools/make-icon.py` 生成，无外部素材。
