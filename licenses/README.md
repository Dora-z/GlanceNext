# 随发布包提供的补充许可文件

这些上游文件用于补充未在 OpenCvSharp NuGet 包中随附的完整许可证。打包脚本将此目录和还原后的 NuGet／.NET runtime 许可声明复制到应用的 `Licenses` 目录。

- `opencvsharp/LICENSE.txt`：OpenCvSharp Apache-2.0；来源为包元数据记录的源码提交 [d8fc8914e09b43fe6e2ec71c1e376aef5188acf1](https://github.com/shimat/opencvsharp/blob/d8fc8914e09b43fe6e2ec71c1e376aef5188acf1/LICENSE)，Copyright 2008–2026 OpenCvSharp contributors。
- `ffmpeg/LICENSE.txt`、`ffmpeg/NOTICE.txt`：OpenCV 5.0.0 为可选 FFmpeg 后端提供的 LGPL-2.1-or-later 许可证及构建／来源说明，原文保留。来源：[license.txt](https://github.com/opencv/opencv/blob/5.0.0/3rdparty/ffmpeg/license.txt)、[readme.txt](https://github.com/opencv/opencv/blob/5.0.0/3rdparty/ffmpeg/readme.txt)。
- 发布包中的 `opencv_videoio_ffmpeg500_64.dll` 来自固定的 `OpenCvSharp5.runtime.win` 包。OpenCV 5.0.0 的下载脚本记录其上游来源为 [opencv_3rdparty / 06dc20cad65dc7fcf784f70c95d46750520889a7](https://github.com/opencv/opencv_3rdparty/tree/06dc20cad65dc7fcf784f70c95d46750520889a7)。GlanceNext 的相机采集通过 Windows MediaCapture 完成。

YuNet 模型的完整 MIT 许可证继续随 `Models/LICENSE.YuNet.txt` 交付。
