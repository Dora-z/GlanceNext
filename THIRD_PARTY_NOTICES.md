# 第三方组件与许可证

GlanceNext 自有源码采用 [MIT](LICENSE)。第三方组件保留各自版权和许可条款。

| 组件 | 固定版本 | 许可与来源 |
| --- | --- | --- |
| YuNet 人脸检测模型 | `face_detection_yunet_2026may.onnx` | MIT，Copyright (c) 2020 Shiqi Yu；[完整许可证](src/GlanceNext.Desktop/Models/LICENSE.YuNet.txt)、[来源与校验值](src/GlanceNext.Desktop/Models/README.md) |
| OpenCvSharp5 / OpenCvSharp5.runtime.win | `5.0.0.20261003` | NuGet 包标注 Apache-2.0；[项目](https://github.com/shimat/opencvsharp)、[托管包](https://www.nuget.org/packages/OpenCvSharp5/5.0.0.20261003)、[Windows runtime](https://www.nuget.org/packages/OpenCvSharp5.runtime.win/5.0.0.20261003) |
| Microsoft.WindowsAppSDK | `2.5.1` | 分发 SDK 包适用 Microsoft Software License Terms；[NuGet 包](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1)，完整条款见还原后包目录中的 `license.txt` |
| Microsoft.Windows.SDK.BuildTools | `10.0.26100.6584` | Microsoft Windows SDK 许可条款；[NuGet 包](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.6584) |
| .NET SDK | `10.0.401` | .NET 及其组成组件的许可；[.NET 许可证](https://github.com/dotnet/core/blob/main/LICENSE.TXT) |

仓库随源码提供 YuNet 模型及其完整 MIT 许可证。其余依赖在构建时通过官方 SDK 下载与 NuGet 还原取得，传递依赖记录于 `src/GlanceNext.Desktop/packages.lock.json`。

重新分发自包含二进制时，需同时保留相关包的许可证、NOTICE 和原生运行库所需第三方声明，并遵守 Microsoft SDK 的分发条款。GlanceNext 的 MIT 许可证不替代这些条款。
