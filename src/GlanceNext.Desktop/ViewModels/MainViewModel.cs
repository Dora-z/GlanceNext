using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GlanceNext.Core;
using GlanceNext.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace GlanceNext.Desktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly LocalStore store;
    private readonly WindowsHost host;
    private readonly DispatcherQueue dispatcher;
    private readonly InfraredCapture camera = new();
    private readonly ProtectionEngine engine = new();
    private readonly StreamHealth streamHealth = new();
    private readonly BystanderTracker bystanders = new();
    private readonly CameraDirectionCalibration directionCalibration = new();
    private readonly DispatcherQueueTimer watchdog;
    private readonly DetectionLifecycle lifecycle;
    private readonly Stopwatch reception = Stopwatch.StartNew();
    private readonly List<FaceObservation> calibrationSamples = new();
    private bool paused, running, busy, enumerating, stable, sessionBlocked, disposed;
    private int healthyFrames, lastFaceCount, calibrationStep;
    private double lastReceiveSeconds;
    private bool seenEmpty, seenOne, seenTwo, seenAway, frameQueued;
    private ProtectionAction lastAction;
    private InfraredDevice? selectedDevice;
    private DetectionResult? lastDetection;
    private string statusTitle = "等待红外诊断", statusDetail = "先验证设备，再开启适合你的保护功能。", deviceFormat = "仅使用红外摄像头", fpsText = "—", faceCountText = "—", poseText = "—", countdownText = "检测暂停 · 不计时", validationText = "尚未验证", calibrationInstruction = "请坐在屏幕前，点击「记录正视」，保持自然姿态 3 秒。";
    private WriteableBitmap? preview;
    private string directionInstruction = "坐在正前方，点击「确认左右方向」，然后向你自己的左侧平移头部与上半身，保持一秒。无需转头。";
    public AppSettings Settings
    {
        get;
    }
    public bool DiagnosticOnly { get; } = Environment.GetCommandLineArgs().Any(x => x is "--smoke" or "--recovery-check" or "--ui-check");
    public int TotalDiagnosticFrames
    {
        get; private set;
    }
    public int MaximumObservedFaces
    {
        get; private set;
    }
    public ObservableCollection<InfraredDevice> Devices { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ThemeChanged;
    public MainViewModel(LocalStore store, WindowsHost host, DispatcherQueue dispatcher)
    {
        this.store = store;
        this.host = host;
        this.dispatcher = dispatcher;
        Settings = store.Load();
        lifecycle = new(ConnectCameraAsync, DisconnectCameraAsync);
        lifecycle.Changed += LifecycleChanged;
        try
        {
            host.RegisterShortcut(Settings.EmergencyShortcut);
        }
        catch (Exception ex) { statusDetail = ex.Message; store.Log("hotkey", ex.Message); }
        camera.Frame += OnCameraFrame;
        camera.Fault += fault => dispatcher.TryEnqueue(() => _ = FailAsync(fault.Error, fault.Generation));
        host.SessionLocked += locked => dispatcher.TryEnqueue(() => _ = lifecycle.SetLockedAsync(locked));
        host.PowerSuspended += suspended => dispatcher.TryEnqueue(() => _ = lifecycle.SetSuspendedAsync(suspended));
        watchdog = dispatcher.CreateTimer();
        watchdog.Interval = TimeSpan.FromMilliseconds(500);
        watchdog.Tick += (_, _) => { if (running && reception.Elapsed.TotalSeconds - lastReceiveSeconds > 2) _ = FailAsync("红外画面已中断。保护动作已停止，请重试连接。", lifecycle.Generation); };
    }

    public InfraredDevice? SelectedDevice
    {
        get => selectedDevice; set
        {
            if (running || IsBusy)
                return;
            Set(ref selectedDevice, value);
        }
    }
    public string StatusTitle
    {
        get => statusTitle; private set => Set(ref statusTitle, value);
    }
    public string StatusDetail
    {
        get => statusDetail; private set => Set(ref statusDetail, value);
    }
    public string DeviceFormat
    {
        get => deviceFormat; private set => Set(ref deviceFormat, value);
    }
    public string FpsText
    {
        get => fpsText; private set => Set(ref fpsText, value);
    }
    public string FaceCountText
    {
        get => faceCountText; private set => Set(ref faceCountText, value);
    }
    public string PoseText
    {
        get => poseText; private set => Set(ref poseText, value);
    }
    public string CountdownText
    {
        get => countdownText; private set => Set(ref countdownText, value);
    }
    public string ValidationText
    {
        get => validationText; private set => Set(ref validationText, value);
    }
    public string CalibrationInstruction
    {
        get => calibrationInstruction; private set => Set(ref calibrationInstruction, value);
    }
    public WriteableBitmap? Preview
    {
        get => preview; private set => Set(ref preview, value);
    }
    public bool IsBusy => busy || enumerating;
    public bool CanSelectDevice => !running && !IsBusy;
    public bool IsRunning => running;
    public bool IsPaused => paused;
    public bool IsStable => stable;
    public bool CanStart => !running && !IsBusy && (SelectedDevice is not null || Settings.DeviceId.Length > 0) && !sessionBlocked;
    public bool CanStop => lifecycle.WantsRunning && !sessionBlocked;
    public bool CanCalibrate => stable && lastFaceCount == 1 && calibrationStep is 0 or 2;
    public bool CanVerifyPresence => stable && seenEmpty && seenOne;
    public bool CanVerifyBystander => stable && seenTwo;
    public bool CanVerifyAttention => stable && Settings.Calibration.IsComplete && seenAway;
    public bool CanEnablePresence => Settings.PresenceEnabled || (stable && Settings.PresenceVerified && host.Shortcut.Length > 0);
    public bool CanEnableBystander => Settings.BystanderEnabled || (stable && Settings.BystanderVerified && host.Shortcut.Length > 0);
    public bool CanEnableAttention => Settings.AttentionEnabled || (stable && Settings.AttentionVerified && Settings.Calibration.IsComplete && host.Shortcut.Length > 0);
    public bool CanToggleDetection => !disposed && !sessionBlocked && (lifecycle.WantsRunning || (!IsBusy && (SelectedDevice is not null || Settings.DeviceId.Length > 0)));
    public string PauseLabel => lifecycle.State == DetectionRunState.Faulted ? "重试连接" : lifecycle.WantsRunning ? "暂停检测" : paused ? "恢复检测" : "开始检测";
    public string StartButtonText => lifecycle.State == DetectionRunState.Faulted ? "重试连接" : "开始检测";
    public string CalibrationButtonText => calibrationStep == 2 ? "记录转头" : "记录正视";
    public string EnabledSummary => $"{EnabledCount} / 3 项功能已开启";
    private int EnabledCount => new[] { Settings.PresenceEnabled, Settings.BystanderEnabled, Settings.AttentionEnabled }.Count(x => x);
    private bool AutomaticActionsReady => stable && running && !sessionBlocked && !DiagnosticOnly && !directionCalibration.IsCollecting && host.Shortcut.Length > 0;
    public string ProtectionSummary => stable && running && !sessionBlocked ? DiagnosticOnly ? "诊断模式 · 自动动作关闭" :
        directionCalibration.IsCollecting ? "方向校准中 · 自动保护暂停" : host.Shortcut.Length == 0 ? "快捷键不可用 · 自动保护暂停" :
        EnabledCount == 0 ? "检测运行中 · 功能尚未开启" : $"正在保护 {EnabledCount} 项" : lifecycle.State switch
    { DetectionRunState.Locked => "锁屏期间暂停保护", DetectionRunState.Sleeping => "休眠期间暂停保护", DetectionRunState.Reconnecting => "正在重连 · 保护待恢复", DetectionRunState.Faulted => "设备故障 · 保护已暂停", DetectionRunState.Paused => "已手动暂停保护", DetectionRunState.Stopped => "检测已停止 · 未执行保护", _ => "等待有效数据后恢复保护" };
    public string ReadinessSummary => stable ? "红外数据稳定" : lifecycle.State switch
    { DetectionRunState.Reconnecting => "正在重新连接", DetectionRunState.Starting => "正在连接红外设备", DetectionRunState.Locked => "会话已锁定", DetectionRunState.Sleeping => "设备休眠中", DetectionRunState.Validating => "正在验证红外数据", DetectionRunState.Faulted => "红外连接失败", DetectionRunState.Paused => "检测已暂停", DetectionRunState.Stopped => "摄像头已释放", _ => "设备尚未就绪" };
    public string DirectionInstruction { get => directionInstruction; private set => Set(ref directionInstruction, value); }
    public string DirectionSummary => Settings.CameraDirectionVerified ?
        $"左右方向已确认 · 画面左侧对应你的{(Settings.CameraLeftIsUserLeft ? "左" : "右")}侧" : "尚未确认左右 · 暂时使用顶部居中提醒";
    public bool CanCalibrateDirection => stable && lastFaceCount == 1;
    public bool CanSwapDirection => !disposed && !sessionBlocked && !IsBusy && Settings.CameraDirectionVerified && !directionCalibration.IsCollecting;
    public string CalibrationSummary => Settings.Calibration.IsComplete ? $"已校准 · 转头阈值 {Settings.Calibration.AwayYawThreshold:F0}°" : "尚未校准";
    public string LocalDataPath => store.DirectoryPath;
    public bool PresenceEnabled
    {
        get => Settings.PresenceEnabled; set
        {
            if (value == Settings.PresenceEnabled) return;
            Settings.PresenceEnabled = value && CanEnablePresence;
            ChangedSetting();
        }
    }
    public bool BystanderEnabled
    {
        get => Settings.BystanderEnabled; set
        {
            if (value == Settings.BystanderEnabled) return;
            Settings.BystanderEnabled = value && CanEnableBystander;
            ChangedSetting();
        }
    }
    public bool AttentionEnabled
    {
        get => Settings.AttentionEnabled; set
        {
            if (value == Settings.AttentionEnabled) return;
            Settings.AttentionEnabled = value && CanEnableAttention;
            ChangedSetting();
        }
    }
    public bool MaskOnBystander
    {
        get => Settings.MaskOnBystander; set
        {
            Settings.MaskOnBystander = value;
            ChangedSetting();
        }
    }
    public bool PresenceVerified
    {
        get => Settings.PresenceVerified; set
        {
            Settings.PresenceVerified = value && CanVerifyPresence;
            if (!value)
                Settings.PresenceEnabled = false;
            ChangedSetting();
        }
    }
    public bool BystanderVerified
    {
        get => Settings.BystanderVerified; set
        {
            Settings.BystanderVerified = value && CanVerifyBystander;
            if (!value)
                Settings.BystanderEnabled = false;
            ChangedSetting();
        }
    }
    public bool AttentionVerified
    {
        get => Settings.AttentionVerified; set
        {
            Settings.AttentionVerified = value && CanVerifyAttention;
            if (!value)
                Settings.AttentionEnabled = false;
            ChangedSetting();
        }
    }
    public double AbsenceSeconds
    {
        get => Settings.AbsenceSeconds; set
        {
            Settings.AbsenceSeconds = value;
            Settings.Sanitize();
            ChangedSetting();
        }
    }
    public double AttentionSeconds
    {
        get => Settings.AttentionSeconds; set
        {
            Settings.AttentionSeconds = value;
            Settings.Sanitize();
            ChangedSetting();
        }
    }
    public double DimOpacity
    {
        get => Settings.DimOpacity; set
        {
            Settings.DimOpacity = value;
            Settings.Sanitize();
            host.ClearOverlay();
            ChangedSetting();
        }
    }
    public string Theme
    {
        get => Settings.Theme; set
        {
            Settings.Theme = value;
            ChangedSetting();
            ThemeChanged?.Invoke();
        }
    }
    public string EmergencyShortcut
    {
        get => Settings.EmergencyShortcut; set
        {
            try
            {
                host.RegisterShortcut(value);
                Settings.EmergencyShortcut = value;
                ChangedSetting();
            }
            catch (Exception ex)
            {
                host.ClearOverlay();
                engine.Reset();
                StatusDetail = ex.Message;
                OnChanged();
                RefreshComputed();
            }
        }
    }
    public bool StartWithWindows
    {
        get => Settings.StartWithWindows; set
        {
            try
            {
                WindowsHost.SetStartup(value);
                Settings.StartWithWindows = value;
                ChangedSetting();
            }
            catch (Exception ex) { StatusDetail = "开机启动设置失败：" + ex.Message; OnChanged(); }
        }
    }

    public async Task RefreshDevicesAsync()
    {
        if (running || IsBusy || disposed)
            return;
        enumerating = true;
        var enumerationGeneration = lifecycle.Generation;
        RefreshComputed();
        try
        {
            var devices = await InfraredCapture.EnumerateAsync();
            if (disposed || enumerationGeneration != lifecycle.Generation) return;
            Devices.Clear();
            foreach (var device in devices)
                Devices.Add(device);
            // Set the field while busy; the public setter protects user changes during capture.
            selectedDevice = Settings.DeviceId.Length > 0 ? devices.FirstOrDefault(d => d.Id == Settings.DeviceId) : devices.FirstOrDefault();
            OnChanged(nameof(SelectedDevice));
            if (selectedDevice is null)
            {
                StatusTitle = "没有找到独立红外源";
                StatusDetail = "请检查红外摄像头连接和驱动。GlanceNext 不会调用普通摄像头。";
            }
            else
            {
                StatusTitle = "红外设备已找到";
                StatusDetail = "点击开始诊断，查看画面并完成实机验证。";
            }
        }
        catch (Exception ex) { StatusTitle = "设备枚举失败"; StatusDetail = InfraredCapture.ToUserError(ex); store.Log("enumerate-error", ex.GetType().Name); }
        finally { enumerating = false; RefreshComputed(); }
    }

    public async Task StartAsync()
    {
        if (disposed || sessionBlocked || IsBusy) return;
        if (SelectedDevice is not null && Settings.DeviceId != SelectedDevice.Id)
        {
            Settings.ResetValidation();
            Settings.DeviceId = SelectedDevice.Id;
            Save();
        }
        await lifecycle.StartAsync();
    }
    private async Task ConnectCameraAsync(int generation, CancellationToken token)
    {
        var devices = await InfraredCapture.EnumerateAsync();
        token.ThrowIfCancellationRequested();
        Devices.Clear();
        foreach (var device in devices) Devices.Add(device);
        selectedDevice = Settings.DeviceId.Length > 0 ? devices.FirstOrDefault(d => d.Id == Settings.DeviceId) : devices.FirstOrDefault();
        OnChanged(nameof(SelectedDevice));
        if (selectedDevice is null) throw new InvalidOperationException("原来的独立红外设备尚未就绪。请检查驱动或相机占用，再重试连接；不会调用普通摄像头。");
        if (Settings.DeviceId.Length == 0) { Settings.DeviceId = selectedDevice.Id; Save(); }
        await camera.StartAsync(selectedDevice, generation, token);
        token.ThrowIfCancellationRequested();
        running = true;
        DeviceFormat = camera.FormatDescription;
        lastReceiveSeconds = reception.Elapsed.TotalSeconds;
        watchdog.Start();
        store.Log("capture-start", $"generation {generation} · {DeviceFormat}");
    }
    private async Task DisconnectCameraAsync()
    {
        running = stable = false;
        watchdog.Stop();
        host.ClearOverlay();
        ResetLiveState();
        Preview = null;
        await camera.StopAsync();
        store.Log("capture-stop", "camera released");
    }
    public Task StopAsync() => lifecycle.StopAsync();
    private async Task FailAsync(string error, int generation)
    {
        if (!lifecycle.AcceptsFrames(generation)) return;
        store.Log("capture-fault", error);
        await lifecycle.ReportFaultAsync(generation, new InvalidOperationException(error));
    }
    public Task TogglePauseAsync() => sessionBlocked ? Task.CompletedTask : lifecycle.State == DetectionRunState.Faulted ? StartAsync() : lifecycle.WantsRunning ? lifecycle.StopAsync(userPaused: true) : StartAsync();
    public async Task EmergencyAsync()
    {
        host.ClearOverlay();
        engine.Reset();
        await lifecycle.StopAsync(userPaused: true);
        StatusTitle = "已紧急解除保护";
        StatusDetail = "检测已暂停。需要时可从托盘恢复。";
        RefreshComputed();
    }
    private void LifecycleChanged()
    {
        sessionBlocked = lifecycle.IsBlocked;
        paused = lifecycle.State == DetectionRunState.Paused;
        busy = lifecycle.State is DetectionRunState.Starting or DetectionRunState.Reconnecting or DetectionRunState.Stopping;
        if (lifecycle.State is not (DetectionRunState.Validating or DetectionRunState.Running))
        {
            running = stable = false;
            watchdog.Stop();
            host.ClearOverlay();
            engine.Reset();
            CountdownText = "检测暂停 · 不计时";
        }
        StatusTitle = lifecycle.State switch
        {
            DetectionRunState.Starting => "正在连接红外摄像头",
            DetectionRunState.Reconnecting => $"正在恢复保护 · 连接尝试 {lifecycle.Attempt} / 5",
            DetectionRunState.Validating => "正在验证红外数据",
            DetectionRunState.Running => "本地检测中",
            DetectionRunState.Locked => "锁屏期间暂停检测",
            DetectionRunState.Sleeping => "休眠期间暂停检测",
            DetectionRunState.Paused => "已手动暂停",
            DetectionRunState.Faulted => "红外连接失败 · 保护已暂停",
            _ => "检测已停止"
        };
        StatusDetail = lifecycle.LastError is { } error ? InfraredCapture.ToUserError(error) : lifecycle.State switch
        {
            DetectionRunState.Locked or DetectionRunState.Sleeping => lifecycle.WantsRunning ? "摄像头已释放，返回并登录 Windows 后将恢复此前运行的检测。" : "摄像头已释放。此前已停止或暂停，登录后不会自动开始检测。",
            DetectionRunState.Validating => "重新积累 30 帧有效画面后恢复保护，离席计时重新开始。",
            DetectionRunState.Reconnecting => "等待红外设备恢复，功能开关和校准保持不变。",
            _ => "画面只在本机内存中处理，不保存、不上传。"
        };
        store.Log("lifecycle", $"{lifecycle.State} · generation {lifecycle.Generation} · attempt {lifecycle.Attempt}" + (lifecycle.LastError is { } ex ? $" · {ex.GetType().Name}: {ex.Message}" : ""));
        RefreshComputed();
    }

    // Diagnostic harness uses the same lifecycle without locking Windows or enabling actions.
    public Task DiagnosticSessionAsync(bool locked) => DiagnosticOnly ? lifecycle.SetLockedAsync(locked) : Task.CompletedTask;

    private void OnCameraFrame(PreviewFrame frame)
    {
        // Bound UI work to one queued frame; don't retain camera frames behind a busy dispatcher.
        lock (camera)
        {
            if (frameQueued || disposed)
                return;
            frameQueued = true;
        }
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (running && lifecycle.AcceptsFrames(frame.Generation))
                    ProcessFrame(frame);
            }
            catch (Exception ex) { _ = FailAsync("检测处理失败：" + ex.Message, frame.Generation); }
            finally { lock (camera) frameQueued = false; }
        }))
        {
            lock (camera)
                frameQueued = false;
        }
    }

    private void ProcessFrame(PreviewFrame frame)
    {
        frame = frame with { Detection = bystanders.Observe(frame.Detection, Settings) };
        lastReceiveSeconds = reception.Elapsed.TotalSeconds;
        lastDetection = frame.Detection;
        lastFaceCount = frame.Detection.Faces.Count;
        streamHealth.Observe(frame.Detection);
        healthyFrames = streamHealth.ConsecutiveFrames;
        stable = streamHealth.IsReady;
        if (stable) lifecycle.MarkReady(frame.Generation);
        TotalDiagnosticFrames++;
        MaximumObservedFaces = Math.Max(MaximumObservedFaces, lastFaceCount);
        seenEmpty |= lastFaceCount == 0;
        seenOne |= lastFaceCount == 1;
        seenTwo |= lastFaceCount >= 2;
        if (directionCalibration.Observe(frame.Detection) is { } leftIsLeft)
        {
            Settings.CameraLeftIsUserLeft = leftIsLeft;
            Settings.CameraDirectionVerified = true;
            bystanders.Reset();
            DirectionInstruction = "左右方向已确认。请让旁观者分别从你左右两侧进入，检查提醒是否出现在对应侧。";
            Save();
        }
        if (frame.Detection.PrimaryFace is { } face)
        {
            PoseText = double.IsFinite(face.Yaw) && double.IsFinite(face.Pitch) ? $"水平 {face.Yaw:+0;-0;0}° · 俯仰 {face.Pitch:+0;-0;0}°" : "朝向暂不可用";
            seenAway |= Settings.Calibration.IsLookingAway(face);
            CollectCalibration(face, lastFaceCount);
        }
        else
            PoseText = "没有检测到人脸";
        FpsText = $"{frame.Detection.FrameRate:F1} fps";
        FaceCountText = $"{lastFaceCount} 人";
        ValidationText = stable ? "取流稳定 · 请完成下方功能验证" : $"有效画面 {healthyFrames} / 30";
        if (Preview is null || Preview.PixelWidth != frame.Width || Preview.PixelHeight != frame.Height)
            Preview = new WriteableBitmap(frame.Width, frame.Height);
        using (var stream = Preview.PixelBuffer.AsStream())
        {
            stream.Position = 0;
            stream.Write(frame.Bgra);
        }
        Preview.Invalidate();
        var decision = engine.Evaluate(frame.Detection, Settings, AutomaticActionsReady, paused);
        CountdownText = !stable ? "等待数据稳定 · 不计时" : !AutomaticActionsReady ? "自动动作暂停 · 不计时" : decision.LockCountdownSeconds is { } seconds ? $"{seconds:F0} 秒后锁屏" : Settings.PresenceEnabled ? "在座 · 无倒计时" : "功能未开启";
        StatusTitle = directionCalibration.IsCollecting ? "正在确认左右方向" : stable ? (DiagnosticOnly ? "红外诊断运行中 · 自动动作关闭" : decision.Status) : "正在验证红外数据";
        StatusDetail = directionCalibration.IsCollecting ? "请向你自己的左侧平移头部与上半身，保持一秒。校准期间暂停自动动作。" : stable ? "检测在本机运行 · 仅红外 · 不保存画面" : "连续有效画面达到 30 帧后，可进行功能验证。";
        if (host.Shortcut.Length == 0)
            StatusDetail = "紧急快捷键不可用。请在设置中选择其他组合后开启保护。";
        if (decision.Action != lastAction)
        {
            store.Log("protection", decision.Action.ToString());
            lastAction = decision.Action;
        }
        host.Apply(decision.Action, Settings.DimOpacity, decision.BystanderDirection);
        RefreshComputed();
    }

    public void BeginCalibration()
    {
        if (!CanCalibrate)
            return;
        host.ClearOverlay();
        engine.Reset();
        Settings.AttentionEnabled = false;
        Settings.AttentionVerified = false;
        calibrationSamples.Clear();
        if (calibrationStep == 2)
        {
            calibrationStep = 3;
            CalibrationInstruction = "请保持转头姿态 3 秒，继续采集红外关键点。";
        }
        else
        {
            Settings.Calibration = new();
            calibrationStep = 1;
            CalibrationInstruction = "请自然看向屏幕，保持 3 秒，正在记录正视姿态。";
        }
        Save();
        RefreshComputed();
    }
    public void BeginDirectionCalibration()
    {
        if (!CanCalibrateDirection || lastDetection?.Faces.Count != 1) return;
        host.ClearOverlay();
        engine.Reset();
        Settings.CameraDirectionVerified = false;
        bystanders.Reset();
        directionCalibration.Begin(lastDetection.Faces[0]);
        DirectionInstruction = "中心位置已记录。请向你自己的左侧平移头部和上半身，保持一秒；不要只转头。";
        Save();
        RefreshComputed();
    }
    public void SwapCameraDirection()
    {
        if (!CanSwapDirection) return;
        Settings.CameraLeftIsUserLeft = !Settings.CameraLeftIsUserLeft;
        DirectionInstruction = "已交换提醒左右。请让旁观者分别从你实际的左、右两侧进入，确认卡片位置；如仍反向，可再次交换。";
        ChangedSetting();
    }
    private void CollectCalibration(FaceObservation face, int count)
    {
        if (calibrationStep is not (1 or 3))
            return;
        if (count != 1 || !double.IsFinite(face.Yaw) || !double.IsFinite(face.Pitch))
        {
            calibrationSamples.Clear();
            return;
        }
        calibrationSamples.Add(face);
        if (calibrationSamples.Count < 15)
            return;
        var yaw = Median(calibrationSamples.Select(f => f.Yaw));
        var pitch = Median(calibrationSamples.Select(f => f.Pitch));
        var jitter = calibrationSamples.Max(f => f.Yaw) - calibrationSamples.Min(f => f.Yaw);
        calibrationSamples.Clear();
        if (jitter > 15)
        {
            CalibrationInstruction = "姿态变化较大，请保持稳定；正在重新采集。";
            return;
        }
        if (calibrationStep == 1)
        {
            Settings.Calibration.ForwardYaw = yaw;
            Settings.Calibration.ForwardPitch = pitch;
            calibrationStep = 2;
            CalibrationInstruction = "正视已记录。请明显向左或向右转头，再点击「记录转头」，保持 3 秒。";
        }
        else
        {
            var delta = Math.Abs(yaw - Settings.Calibration.ForwardYaw);
            if (delta < 20)
            {
                calibrationStep = 2;
                CalibrationInstruction = "转头差异不足 20°，请加大幅度后重新记录。";
            }
            else
            {
                Settings.Calibration.AwayYawThreshold = Math.Clamp(delta * 0.6, 12, 60);
                Settings.Calibration.IsComplete = true;
                calibrationStep = 0;
                seenAway = false;
                CalibrationInstruction = "校准完成。再测试转头与返回正视，确认识别正确后勾选注意力验证。";
            }
        }
        Save();
        RefreshComputed();
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
    private void ResetLiveState()
    {
        streamHealth.Reset();
        bystanders.Reset();
        directionCalibration.Cancel();
        DirectionInstruction = "坐在正前方，点击「确认左右方向」，然后向你自己的左侧平移头部与上半身，保持一秒。无需转头。";
        healthyFrames = 0;
        stable = false;
        seenEmpty = seenOne = seenTwo = seenAway = false;
        lastAction = ProtectionAction.None;
        lastDetection = null;
        lastFaceCount = 0;
        calibrationSamples.Clear();
        calibrationStep = 0;
        engine.Reset();
        FpsText = FaceCountText = PoseText = "—";
        CountdownText = "检测暂停 · 不计时";
        ValidationText = "尚未验证";
    }
    private void ChangedSetting([CallerMemberName] string? name = null)
    {
        engine.Reset();
        host.ClearOverlay();
        lastAction = ProtectionAction.None;
        Save();
        OnChanged(name);
        RefreshComputed();
    }
    private void Save()
    {
        try
        {
            store.Save(Settings);
        }
        catch (Exception ex) { StatusDetail = "设置保存失败：" + ex.Message; store.Log("settings-save", ex.GetType().Name); }
    }
    private void RefreshComputed()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(CanSelectDevice), nameof(IsRunning), nameof(IsPaused), nameof(IsStable), nameof(CanStart), nameof(CanStop), nameof(CanCalibrate), nameof(CanVerifyPresence), nameof(CanVerifyBystander), nameof(CanVerifyAttention), nameof(CanEnablePresence), nameof(CanEnableBystander), nameof(CanEnableAttention), nameof(CanToggleDetection), nameof(PauseLabel), nameof(CalibrationButtonText), nameof(EnabledSummary), nameof(ReadinessSummary), nameof(CalibrationSummary), nameof(PresenceEnabled), nameof(BystanderEnabled), nameof(AttentionEnabled), nameof(PresenceVerified), nameof(BystanderVerified), nameof(AttentionVerified), nameof(ProtectionSummary), nameof(StartButtonText), nameof(DirectionSummary), nameof(CanCalibrateDirection), nameof(CanSwapDirection) })
            OnChanged(property);
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnChanged(name);
    }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await lifecycle.DisposeAsync();
        watchdog.Stop();
        await camera.DisposeAsync();
        host.Dispose();
    }
}
