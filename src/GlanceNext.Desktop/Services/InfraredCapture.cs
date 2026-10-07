using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GlanceNext.Core;
using OpenCvSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using WinRT;

namespace GlanceNext.Desktop.Services;

public sealed record InfraredDevice(string Id, string Name, string SourceId, MediaFrameSourceGroup Group);
public sealed record PreviewFrame(int Width, int Height, byte[] Bgra, DetectionResult Detection, int Generation = 0);
public sealed record CaptureFault(int Generation, string Error);

public sealed class InfraredCapture : IAsyncDisposable
{
    private MediaCapture? capture;
    private MediaFrameReader? reader;
    private FaceDetectorYN? detector;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object processingLock = new();
    private TimeSpan lastProcessed;
    private bool stopping;
    private long processedFrames;
    private TimeSpan startedAt;
    private int generation;
    public event Action<PreviewFrame>? Frame;
    public event Action<CaptureFault>? Fault;
    public string FormatDescription { get; private set; } = "未启动";
    public bool IsRunning => reader is not null && !stopping;

    public static async Task<IReadOnlyList<InfraredDevice>> EnumerateAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        // Prefer IR-only groups. Never initialize a group that also contains a color source.
        return groups.Where(g => g.SourceInfos.Count > 0 && g.SourceInfos.All(s => s.SourceKind == MediaFrameSourceKind.Infrared))
            .SelectMany(g => g.SourceInfos.Where(s => s.MediaStreamType is MediaStreamType.VideoRecord or MediaStreamType.VideoPreview)
                .Select(s => new InfraredDevice(g.Id, s.DeviceInformation?.Name ?? g.DisplayName, s.Id, g)))
            .GroupBy(d => d.SourceId).Select(g => g.First()).ToArray();
    }

    public async Task StartAsync(InfraredDevice device, int connectionGeneration = 0, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        cancellationToken.ThrowIfCancellationRequested();
        generation = connectionGeneration;
        stopping = false;
        var model = Path.Combine(AppContext.BaseDirectory, "Models", "face_detection_yunet_2026may.onnx");
        if (!File.Exists(model))
            throw new FileNotFoundException("缺少 YuNet 模型，请运行 scripts/setup.ps1。", model);
        using (var modelFile = File.OpenRead(model))
            if (Convert.ToHexString(SHA256.HashData(modelFile)) != "EBAFCE4E3C118D6554634BE5C27AB333B4C047A9A8C3FAF1D7CF93101C22F0F0")
                throw new InvalidOperationException("YuNet 模型校验失败。请恢复原始模型后重新诊断。");
        // The complete OpenCV runtime is required (the slim package omits DNN).
        Environment.SetEnvironmentVariable("OPENCV_FORCE_DNN_ENGINE", "4");
        detector = FaceDetectorYN.Create(model, "", new Size(320, 320), 0.75f, 0.3f);
        capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = device.Group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (!capture.FrameSources.TryGetValue(device.SourceId, out var source) || source.Info.SourceKind != MediaFrameSourceKind.Infrared)
                throw new InvalidOperationException("设备没有可读取的独立红外源。不会调用普通摄像头。");
            var format = source.CurrentFormat;
            FormatDescription = $"{format.VideoFormat.Width} × {format.VideoFormat.Height} · {format.Subtype} · 红外";
            reader = await capture.CreateFrameReaderAsync(source);
            cancellationToken.ThrowIfCancellationRequested();
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            startedAt = clock.Elapsed;
            lastProcessed = TimeSpan.Zero;
            processedFrames = 0;
            var status = await reader.StartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"红外取流失败：{status}。请暂停原版 Glance 或其他相机应用后重试。");
        }
        catch { await StopAsync(); throw; }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (stopping || !Monitor.TryEnter(processingLock))
            return;
        try
        {
            if (sender != reader || stopping) return;
            var frameGeneration = generation;
            var now = clock.Elapsed;
            using var frame = sender.TryAcquireLatestFrame();
            if (frame is null || stopping || now - lastProcessed < TimeSpan.FromMilliseconds(180))
                return;
            if (frame.SourceKind != MediaFrameSourceKind.Infrared)
                throw new InvalidOperationException("收到非红外帧，已停止检测。");
            using var bitmap = frame.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
                throw new InvalidOperationException("红外帧无法转入 CPU 内存。");
            lastProcessed = now;
            using var gray = CopyGray(bitmap);
            Cv2.MeanStdDev(gray, out var mean, out var deviation);
            if (deviation.Val0 < 2 || mean.Val0 < 1 || mean.Val0 > 254)
                throw new InvalidOperationException("红外画面过暗、过亮或没有有效内容。请检查遮挡和红外补光。");
            using var normalized = new Mat();
            Cv2.Normalize(gray, normalized, 0, 255, NormTypes.MinMax, MatType.CV_8UC1.Value);
            using var bgr = new Mat();
            Cv2.CvtColor(normalized, bgr, ColorConversionCodes.GRAY2BGR);
            // Bound inference cost; model expects faces within its documented scale range.
            var scale = Math.Min(1, 640d / bgr.Width);
            using var input = new Mat();
            Cv2.Resize(bgr, input, new Size((int)(bgr.Width * scale), (int)(bgr.Height * scale)));
            detector!.SetInputSize(input.Size());
            using var results = new Mat();
            detector.Detect(input, results);
            var faces = new List<FaceObservation>();
            var faceRows = results.Rows;
            for (var i = 0; i < faceRows; i++)
            {
                var x = results.At<float>(i, 0);
                var y = results.At<float>(i, 1);
                var w = results.At<float>(i, 2);
                var h = results.At<float>(i, 3);
                var points = Enumerable.Range(0, 5).Select(p => new Point2d(results.At<float>(i, 4 + p * 2), results.At<float>(i, 5 + p * 2))).ToArray();
                var (yaw, pitch) = EstimatePose(points, input.Width, input.Height);
                faces.Add(new(x / input.Width, y / input.Height, w / input.Width, h / input.Height, results.At<float>(i, 14), yaw, pitch));
                Cv2.Rectangle(bgr, new Rect((int)(x / scale), (int)(y / scale), Math.Max(1, (int)(w / scale)), Math.Max(1, (int)(h / scale))), new Scalar(150, 230, 80), 2);
            }
            processedFrames++;
            var fps = processedFrames / Math.Max(0.001, (now - startedAt).TotalSeconds);
            var detection = new DetectionResult(now, true, faces, fps, mean.Val0);
            using var bgra = new Mat();
            Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
            var bytes = new byte[bgra.Width * bgra.Height * 4];
            Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
            Frame?.Invoke(new(bgra.Width, bgra.Height, bytes, detection, frameGeneration));
        }
        catch (Exception ex)
        {
            stopping = true;
            Fault?.Invoke(new(generation, ToUserError(ex)));
        }
        finally { Monitor.Exit(processingLock); }
    }

    private static (double Yaw, double Pitch) EstimatePose(Point2d[] points, int width, int height)
    {
        var model = new[] { new Point3d(-32, -30, -25), new Point3d(32, -30, -25), new Point3d(0, 0, 0), new Point3d(-25, 30, -20), new Point3d(25, 30, -20) };
        // YuNet's first eye/mouth corner can appear on either image side; sort anatomically by image X.
        var image = new[] { points[0].X < points[1].X ? points[0] : points[1], points[0].X < points[1].X ? points[1] : points[0], points[2],
            points[3].X < points[4].X ? points[3] : points[4], points[3].X < points[4].X ? points[4] : points[3] };
        using var camera = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
        camera.Set(0, 0, (double)width);
        camera.Set(1, 1, (double)width);
        camera.Set(0, 2, width / 2d);
        camera.Set(1, 2, height / 2d);
        using var dist = Mat.Zeros(4, 1, MatType.CV_64FC1).ToMat();
        using var rotation = new Mat();
        using var translation = new Mat();
        using var objectPoints = Mat.FromArray(model);
        using var imagePoints = Mat.FromArray(image);
        Cv2.SolvePnP(objectPoints, imagePoints, camera, dist, rotation, translation, false, SolvePnPMethod.SQPNP);
        if (rotation.Empty())
            return (double.NaN, double.NaN);
        using var matrix = new Mat();
        Cv2.Rodrigues(rotation, matrix);
        var yaw = Math.Atan2(-matrix.At<double>(2, 0), Math.Sqrt(Math.Pow(matrix.At<double>(0, 0), 2) + Math.Pow(matrix.At<double>(1, 0), 2))) * 180 / Math.PI;
        var pitch = Math.Atan2(matrix.At<double>(2, 1), matrix.At<double>(2, 2)) * 180 / Math.PI;
        return (yaw, pitch);
    }

    private static unsafe Mat CopyGray(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
        var plane = buffer.GetPlaneDescription(0);
        var is16 = bitmap.BitmapPixelFormat == BitmapPixelFormat.Gray16;
        var result = new Mat(bitmap.PixelHeight, bitmap.PixelWidth, is16 ? MatType.CV_16UC1 : MatType.CV_8UC1);
        try
        {
            var stride = plane.Stride;
            for (var y = 0; y < bitmap.PixelHeight; y++)
            {
                var source = data + plane.StartIndex + y * stride;
                var dest = (byte*)result.Ptr(y);
                var pixelBytes = bitmap.BitmapPixelFormat switch
                {
                    BitmapPixelFormat.Gray16 or BitmapPixelFormat.Yuy2 => 2,
                    BitmapPixelFormat.Bgra8 => 4,
                    _ => 1
                };
                var rowStart = (long)plane.StartIndex + (long)y * stride;
                if (rowStart < 0 || rowStart + (long)bitmap.PixelWidth * pixelBytes > capacity)
                    throw new InvalidOperationException("红外帧缓冲区大小异常。");
                switch (bitmap.BitmapPixelFormat)
                {
                    case BitmapPixelFormat.Gray8:
                    case BitmapPixelFormat.Nv12:
                        Buffer.MemoryCopy(source, dest, bitmap.PixelWidth, bitmap.PixelWidth);
                        break;
                    case BitmapPixelFormat.Gray16:
                        Buffer.MemoryCopy(source, dest, bitmap.PixelWidth * 2, bitmap.PixelWidth * 2);
                        break;
                    case BitmapPixelFormat.Yuy2:
                        for (var x = 0; x < bitmap.PixelWidth; x++)
                            dest[x] = source[x * 2];
                        break;
                    case BitmapPixelFormat.Bgra8:
                        for (var x = 0; x < bitmap.PixelWidth; x++)
                            dest[x] = (byte)((source[x * 4] + source[x * 4 + 1] + source[x * 4 + 2]) / 3);
                        break;
                    default:
                        throw new NotSupportedException($"暂不支持红外格式 {bitmap.BitmapPixelFormat}。");
                }
            }
            if (!is16)
                return result;
            // Some devices store 10/12-bit IR in Gray16. Preserve contrast instead of discarding low bits.
            var normalized = new Mat();
            Cv2.Normalize(result, normalized, 0, 255, NormTypes.MinMax, MatType.CV_8UC1.Value);
            result.Dispose();
            return normalized;
        }
        catch { result.Dispose(); throw; }
    }

    public static string ToUserError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "相机访问被拒绝。请在 Windows 设置 → 隐私和安全性 → 相机中允许桌面应用访问。",
        COMException => $"红外设备无法读取（0x{ex.HResult:X8}）。请暂停原版 Glance、关闭其他相机应用后重试；不会切换到普通摄像头。",
        _ => ex.Message
    };

    public async Task StopAsync()
    {
        stopping = true;
        var activeReader = reader;
        reader = null;
        if (activeReader is not null)
        {
            activeReader.FrameArrived -= OnFrameArrived;
            try
            {
                await activeReader.StopAsync();
            }
            catch { }
            try { activeReader.Dispose(); } catch { }
        }
        lock (processingLock)
        {
            var previousDetector = detector;
            var previousCapture = capture;
            detector = null;
            capture = null;
            // Attempt both releases even if a driver/native dispose fails during a session transition.
            try { previousDetector?.Dispose(); } catch { }
            try { previousCapture?.Dispose(); } catch { }
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}

[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
