using GlanceNext.Core;

var cases = new List<(string, Func<Task>)>();
FaceObservation Face(double yaw = 0) => new(0.2, 0.2, 0.3, 0.4, 0.95, yaw, 0);
DetectionResult Frame(double t, int count = 1, double yaw = 0, bool valid = true) =>
    new(TimeSpan.FromSeconds(t), valid, Enumerable.Range(0, count).Select(_ => Face(yaw)).ToArray());
AppSettings Settings() => new()
{
    PresenceEnabled = true,
    PresenceVerified = true,
    AbsenceSeconds = 10,
    BystanderEnabled = true,
    BystanderVerified = true,
    AttentionEnabled = true,
    AttentionVerified = true,
    Calibration = new()
    {
        IsComplete = true
    }
};
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Test(string name, Action action) => cases.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => cases.Add((name, action));

Test("Defaults perform no actions", () =>
{
    var e = new ProtectionEngine();
    for (int i = 0; i < 80; i++)
        Equal(ProtectionAction.None, e.Evaluate(Frame(i, 0), new(), true, false).Action);
});
Test("Absence locks once at deadline", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    for (int i = 0; i < 10; i++)
        Equal(ProtectionAction.None, e.Evaluate(Frame(i, 0), s, true, false).Action);
    Equal(ProtectionAction.Lock, e.Evaluate(Frame(10, 0), s, true, false).Action);
    Equal(ProtectionAction.None, e.Evaluate(Frame(11, 0), s, true, false).Action);
});
Test("Return resets absence", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    for (int i = 0; i < 9; i++)
        e.Evaluate(Frame(i, 0), s, true, false);
    e.Evaluate(Frame(9), s, true, false);
    Equal(10d, e.Evaluate(Frame(10, 0), s, true, false).LockCountdownSeconds!.Value);
});
Test("Invalid frame resets all actions and timers", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    for (int i = 0; i < 9; i++)
        e.Evaluate(Frame(i, 0), s, true, false);
    Equal(ProtectionAction.None, e.Evaluate(Frame(9, 0, valid: false), s, true, false).Action);
    Equal(10d, e.Evaluate(Frame(10, 0), s, true, false).LockCountdownSeconds!.Value);
});
Test("Missing frames are never counted as absence", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    e.Evaluate(Frame(0, 0), s, true, false);
    Equal(10d, e.Evaluate(Frame(60, 0), s, true, false).LockCountdownSeconds!.Value);
});
Test("Bystander debounce and recovery", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    Equal(ProtectionAction.None, e.Evaluate(Frame(0, 2), s, true, false).Action);
    Equal(ProtectionAction.Reminder, e.Evaluate(Frame(1, 2), s, true, false).Action);
    Equal(ProtectionAction.Reminder, e.Evaluate(Frame(2), s, true, false).Action);
    Equal(ProtectionAction.Reminder, e.Evaluate(Frame(3), s, true, false).Action);
    Equal(ProtectionAction.None, e.Evaluate(Frame(4), s, true, false).Action);
});
Test("Bystander mask takes priority over dim", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    s.MaskOnBystander = true;
    for (int i = 0; i < 5; i++)
        e.Evaluate(Frame(i, 1, 35), s, true, false);
    e.Evaluate(Frame(5, 2, 35), s, true, false);
    Equal(ProtectionAction.PrivacyMask, e.Evaluate(Frame(6, 2, 35), s, true, false).Action);
});
Test("Lock takes priority over recovering mask", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    s.AbsenceSeconds = 1;
    s.RecoverySeconds = 10;
    s.MaskOnBystander = true;
    e.Evaluate(Frame(0, 2), s, true, false);
    e.Evaluate(Frame(1, 2), s, true, false);
    e.Evaluate(Frame(2, 0), s, true, false);
    Equal(ProtectionAction.Lock, e.Evaluate(Frame(3, 0), s, true, false).Action);
});
Test("Attention debounce and immediate forward recovery", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    for (int i = 0; i < 3; i++)
        Equal(ProtectionAction.None, e.Evaluate(Frame(i, 1, 35), s, true, false).Action);
    Equal(ProtectionAction.Dim, e.Evaluate(Frame(3, 1, 35), s, true, false).Action);
    Equal(ProtectionAction.None, e.Evaluate(Frame(4), s, true, false).Action);
});
Test("Pause, device failure and validation gates suppress actions", () =>
{
    foreach (var condition in new[] { 0, 1, 2 })
    {
        var e = new ProtectionEngine();
        var s = Settings();
        if (condition == 2)
            s.PresenceVerified = s.BystanderVerified = s.AttentionVerified = false;
        for (int i = 0; i < 15; i++)
            Equal(ProtectionAction.None, e.Evaluate(Frame(i, 0), s, condition != 0, condition == 1).Action);
    }
});
Test("Calibration is required for attention", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    s.Calibration.IsComplete = false;
    for (int i = 0; i < 10; i++)
        Equal(ProtectionAction.None, e.Evaluate(Frame(i, 1, 70), s, true, false).Action);
});
Test("Time moving backwards resets timers", () =>
{
    var e = new ProtectionEngine();
    var s = Settings();
    e.Evaluate(Frame(10, 0), s, true, false);
    Equal(10d, e.Evaluate(Frame(5, 0), s, true, false).LockCountdownSeconds!.Value);
});
Test("Corrupt settings are constrained and model change invalidates verification", () =>
{
    var s = Settings();
    s.AbsenceSeconds = double.NaN;
    s.DimOpacity = 9;
    s.ModelVersion = "unknown";
    s.Sanitize();
    Equal(60d, s.AbsenceSeconds);
    Equal(0.85, s.DimOpacity);
    Equal(false, s.PresenceVerified);
    Equal(false, s.AttentionEnabled);
});
Test("Forward recovery during an alert resets attention timing", () =>
{
    var e = new ProtectionEngine(); var s = Settings();
    for (var i = 0; i <= 3; i++) e.Evaluate(Frame(i, 1, 35), s, true, false);
    e.Evaluate(Frame(4, 2, 35), s, true, false);
    Equal(ProtectionAction.Reminder, e.Evaluate(Frame(5, 2, 35), s, true, false).Action);
    e.Evaluate(Frame(6, 2, 0), s, true, false);
    e.Evaluate(Frame(7, 1, 0), s, true, false);
    e.Evaluate(Frame(8, 1, 35), s, true, false);
    Equal(ProtectionAction.None, e.Evaluate(Frame(9, 1, 35), s, true, false).Action);
    Equal(ProtectionAction.Dim, e.Evaluate(Frame(11, 1, 35), s, true, false).Action);
});
Test("Calibration offsets are applied and nonfinite poses cannot dim", () =>
{
    var e = new ProtectionEngine();var s = Settings();s.Calibration.ForwardYaw=20;
    for(var i=0;i<5;i++)Equal(ProtectionAction.None,e.Evaluate(Frame(i,1,20),s,true,false).Action);
    for(var i=5;i<10;i++)Equal(ProtectionAction.None,e.Evaluate(Frame(i,1,double.NaN),s,true,false).Action);
});
Test("Readiness requires thirty uninterrupted valid frames", () =>
{
    var health = new StreamHealth();
    for (var i = 0; i < 29; i++) { health.Observe(Frame(i * 0.2)); Equal(false, health.IsReady); }
    health.Observe(Frame(5.8)); Equal(true, health.IsReady);
    health.Observe(Frame(9)); Equal(false, health.IsReady); Equal(1, health.ConsecutiveFrames);
});
Test("Invalid data and time reversal invalidate readiness", () =>
{
    var health = new StreamHealth();
    for (var i = 0; i < 30; i++) health.Observe(Frame(i * 0.2));
    health.Observe(Frame(6, valid: false)); Equal(0, health.ConsecutiveFrames);
    for (var i = 0; i < 30; i++) health.Observe(Frame(7 + i * 0.2));
    health.Observe(Frame(2)); Equal(false, health.IsReady); Equal(1, health.ConsecutiveFrames);
});
FaceObservation Positioned(double center, double width = 0.18) => new(center - width / 2, 0.25, width, 0.25, 0.95, 0, 0);
DetectionResult Located(double t, params FaceObservation[] faces) => new(TimeSpan.FromSeconds(t), true, faces);
AppSettings DirectionSettings(bool mirror = false) => new() { CameraDirectionVerified = true, CameraLeftIsUserLeft = !mirror };
void Anchor(BystanderTracker tracker, AppSettings settings)
{
    tracker.Observe(Located(0, Positioned(0.5)), settings);
    tracker.Observe(Located(0.5, Positioned(0.5)), settings);
    tracker.Observe(Located(1, Positioned(0.5)), settings);
}
Test("Left and right alerts follow the established user", () =>
{
    foreach (var side in new[] { 0.2, 0.8 })
    {
        var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
        tracker.Observe(Located(1.2, Positioned(side), Positioned(0.5)), settings);
        var result = tracker.Observe(Located(1.8, Positioned(side), Positioned(0.5)), settings);
        Equal(side < 0.5 ? BystanderDirection.Left : BystanderDirection.Right, result.BystanderDirection);
        Equal(Positioned(0.5), result.PrimaryFace!.Value);
    }
});
Test("Mirrored camera mapping reverses image coordinates", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(mirror: true); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.5), Positioned(0.8)), settings);
    Equal(BystanderDirection.Left, tracker.Observe(Located(1.8, Positioned(0.5), Positioned(0.8)), settings).BystanderDirection);
});
Test("Swapping a live mapping clears the old side while retaining the primary", () =>
{
    foreach (var mirror in new[] { false, true })
    {
        var tracker = new BystanderTracker(); var settings = DirectionSettings(mirror); Anchor(tracker, settings);
        tracker.Observe(Located(1.2, Positioned(0.2), Positioned(0.5)), settings);
        Equal(mirror ? BystanderDirection.Right : BystanderDirection.Left,
            tracker.Observe(Located(1.8, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
        settings.CameraLeftIsUserLeft = !settings.CameraLeftIsUserLeft;
        var changed = tracker.Observe(Located(2, Positioned(0.2), Positioned(0.5)), settings);
        Equal(BystanderDirection.Unknown, changed.BystanderDirection);
        Equal(Positioned(0.5), changed.PrimaryFace!.Value);
        Equal(BystanderDirection.Unknown, tracker.Observe(Located(2.4, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
        Equal(mirror ? BystanderDirection.Left : BystanderDirection.Right,
            tracker.Observe(Located(2.6, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
    }
});
Test("Direction verification changes discard a cached side during disappearance", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.2), Positioned(0.5)), settings);
    Equal(BystanderDirection.Left, tracker.Observe(Located(1.8, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
    settings.CameraDirectionVerified = false;
    Equal(BystanderDirection.Unknown, tracker.Observe(Located(2, Positioned(0.5)), settings).BystanderDirection);
    settings.CameraDirectionVerified = true;
    settings.CameraLeftIsUserLeft = false;
    Equal(BystanderDirection.Unknown, tracker.Observe(Located(2.2, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
    Equal(BystanderDirection.Right, tracker.Observe(Located(2.8, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
});
Test("A closer and larger observer does not replace the established user", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.82, 0.4), Positioned(0.5)), settings);
    var result = tracker.Observe(Located(1.8, Positioned(0.82, 0.4), Positioned(0.5)), settings);
    Equal(Positioned(0.5), result.PrimaryFace!.Value);
    Equal(BystanderDirection.Right, result.BystanderDirection);
});
Test("Two sides produce one alert per side", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.2), Positioned(0.5), Positioned(0.8)), settings);
    Equal(BystanderDirection.Both, tracker.Observe(Located(1.8, Positioned(0.2), Positioned(0.5), Positioned(0.8)), settings).BystanderDirection);
});
Test("Startup with multiple people stays generic instead of guessing an owner", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings();
    for (var i = 0; i < 10; i++)
    {
        var result = tracker.Observe(Located(i * 0.2, Positioned(0.2), Positioned(0.8, 0.4)), settings);
        Equal(BystanderDirection.Unknown, result.BystanderDirection);
        Equal<FaceObservation?>(null, result.PrimaryFace);
    }
});
Test("Unverified direction uses a generic alert", () =>
{
    var tracker = new BystanderTracker(); var settings = new AppSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.2), Positioned(0.5)), settings);
    Equal(BystanderDirection.Unknown, tracker.Observe(Located(1.8, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
});
Test("Direction crossing is debounced and disappearance retains the old side", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.2), Positioned(0.5)), settings);
    Equal(BystanderDirection.Left, tracker.Observe(Located(1.8, Positioned(0.2), Positioned(0.5)), settings).BystanderDirection);
    Equal(BystanderDirection.Left, tracker.Observe(Located(2, Positioned(0.5), Positioned(0.8)), settings).BystanderDirection);
    Equal(BystanderDirection.Left, tracker.Observe(Located(2.4, Positioned(0.5), Positioned(0.8)), settings).BystanderDirection);
    Equal(BystanderDirection.Right, tracker.Observe(Located(2.6, Positioned(0.5), Positioned(0.8)), settings).BystanderDirection);
    Equal(BystanderDirection.Right, tracker.Observe(Located(2.8, Positioned(0.5)), settings).BystanderDirection);
});
Test("A briefly missing primary can be reacquired without switching to a distant observer", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.2), Positioned(0.5)), settings);
    tracker.Observe(Located(1.8, Positioned(0.2), Positioned(0.5)), settings);
    tracker.Observe(Located(2, Positioned(0.2)), settings);
    var result = tracker.Observe(Located(2.2, Positioned(0.5), Positioned(0.2)), settings);
    Equal(Positioned(0.5), result.PrimaryFace!.Value);
    Equal(BystanderDirection.Left, result.BystanderDirection);
});
Test("Overlapping observers use central or unknown direction", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings);
    tracker.Observe(Located(1.2, Positioned(0.5), Positioned(0.555)), settings);
    Equal(BystanderDirection.Center, tracker.Observe(Located(1.8, Positioned(0.5), Positioned(0.555)), settings).BystanderDirection);
});
Test("Camera restart drops the positional anchor", () =>
{
    var tracker = new BystanderTracker(); var settings = DirectionSettings(); Anchor(tracker, settings); tracker.Reset();
    tracker.Observe(Located(2, Positioned(0.5), Positioned(0.8)), settings);
    Equal(BystanderDirection.Unknown, tracker.Observe(Located(3, Positioned(0.5), Positioned(0.8)), settings).BystanderDirection);
});
Test("Camera direction calibration requires a steady single-person leftward movement", () =>
{
    var calibration = new CameraDirectionCalibration(); calibration.Begin(Positioned(0.5));
    Equal<bool?>(null, calibration.Observe(Located(0, Positioned(0.48))));
    Equal<bool?>(null, calibration.Observe(Located(0.2, Positioned(0.3))));
    Equal<bool?>(null, calibration.Observe(Located(0.6, Positioned(0.3))));
    Equal<bool?>(true, calibration.Observe(Located(0.8, Positioned(0.3))));
    Equal(false, calibration.IsCollecting);
    calibration.Begin(Positioned(0.5));
    calibration.Observe(Located(1, Positioned(0.7)));
    calibration.Observe(Located(1.5, Positioned(0.7), Positioned(0.4)));
    Equal<bool?>(null, calibration.Observe(Located(1.6, Positioned(0.7))));
    Equal<bool?>(false, calibration.Observe(Located(2.2, Positioned(0.7))));
});
Test("Direction is carried through protection priority and recovery", () =>
{
    var engine = new ProtectionEngine(); var settings = Settings();
    var result = Frame(0, 2) with { BystanderDirection = BystanderDirection.Left };
    engine.Evaluate(result, settings, true, false);
    var decision = engine.Evaluate(result with { Timestamp = TimeSpan.FromSeconds(1) }, settings, true, false);
    Equal(ProtectionAction.Reminder, decision.Action); Equal(BystanderDirection.Left, decision.BystanderDirection);
    settings.MaskOnBystander = true;
    Equal(ProtectionAction.PrivacyMask, engine.Evaluate(result with { Timestamp = TimeSpan.FromSeconds(2) }, settings, true, false).Action);
});

AsyncTest("Unlock retries a transient failure and starts in validating state", async () =>
{
    var connects = 0; var waits = new List<double>();
    await using var life = new DetectionLifecycle((_, _) => { connects++; if (connects == 2) throw new InvalidOperationException("device not ready"); return Task.CompletedTask; },
        () => Task.CompletedTask, (duration, _) => { waits.Add(duration.TotalSeconds); return Task.CompletedTask; });
    await life.StartAsync(); var first = life.Generation; life.MarkReady(first);
    await life.SetLockedAsync(true); Equal(false, life.AcceptsFrames(first)); Equal(true, life.WantsRunning);
    await life.SetLockedAsync(false);
    Equal(3, connects); Equal(0.5, waits.Single()); Equal(DetectionRunState.Validating, life.State);
    Equal(false, life.AcceptsFrames(first)); life.MarkReady(life.Generation); Equal(DetectionRunState.Running, life.State);
});
AsyncTest("Recovery retry exhaustion reports the concrete failure", async () =>
{
    var connects = 0; var waits = new List<double>();
    await using var life = new DetectionLifecycle((_, _) => { if (++connects > 1) throw new InvalidOperationException("IR busy"); return Task.CompletedTask; },
        () => Task.CompletedTask, (duration, _) => { waits.Add(duration.TotalSeconds); return Task.CompletedTask; });
    await life.StartAsync(); await life.SetLockedAsync(true); await life.SetLockedAsync(false);
    Equal(6, connects); Equal("0.5,1,2,4", string.Join(',', waits)); Equal(DetectionRunState.Faulted, life.State);
    Equal("IR busy", life.LastError!.Message); Equal(false, life.AcceptsFrames(life.Generation));
});
AsyncTest("Duplicate lock notifications keep the resume intent", async () =>
{
    var connects = 0;
    await using var life = new DetectionLifecycle((_, _) => { connects++; return Task.CompletedTask; }, () => Task.CompletedTask);
    await life.StartAsync(); await life.SetLockedAsync(true); var generation = life.Generation;
    await life.SetLockedAsync(true); Equal(generation, life.Generation);
    await life.SetLockedAsync(false); await life.SetLockedAsync(false); Equal(2, connects);
});
AsyncTest("Lock and sleep must both clear before reconnecting", async () =>
{
    var connects = 0;
    await using var life = new DetectionLifecycle((_, _) => { connects++; return Task.CompletedTask; }, () => Task.CompletedTask);
    await life.StartAsync(); await life.SetLockedAsync(true); await life.SetSuspendedAsync(true);
    await life.SetLockedAsync(false); Equal(1, connects); Equal(DetectionRunState.Sleeping, life.State);
    await life.SetSuspendedAsync(false); Equal(2, connects); Equal(DetectionRunState.Validating, life.State);
});
AsyncTest("Manual pause cancels a pending retry without reconnecting", async () =>
{
    var connects = 0; var delayed = new TaskCompletionSource();
    await using var life = new DetectionLifecycle((_, _) => { if (++connects > 1) throw new InvalidOperationException("warming up"); return Task.CompletedTask; },
        () => Task.CompletedTask, async (_, token) => { delayed.SetResult(); await Task.Delay(Timeout.Infinite, token); });
    await life.StartAsync(); await life.SetLockedAsync(true); var recovery = life.SetLockedAsync(false);
    await delayed.Task.WaitAsync(TimeSpan.FromSeconds(2)); await life.StopAsync(userPaused: true); await recovery;
    Equal(2, connects); Equal(DetectionRunState.Paused, life.State); Equal(false, life.WantsRunning);
    await life.SetLockedAsync(true); await life.SetLockedAsync(false); Equal(2, connects);
});
AsyncTest("Late connection completion cannot revive a manually stopped detector", async () =>
{
    var connecting = new TaskCompletionSource(); var released = new TaskCompletionSource(); var stopped = 0;
    await using var life = new DetectionLifecycle(async (_, _) => { connecting.SetResult(); await released.Task; }, () => { stopped++; return Task.CompletedTask; });
    var start = life.StartAsync(); await connecting.Task;
    var stop = life.StopAsync(userPaused: true); released.SetResult();
    await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(2));
    Equal(DetectionRunState.Paused, life.State); Equal(false, life.AcceptsFrames(life.Generation));
    if (stopped < 2) throw new Exception("Canceled connection was not cleaned up");
});
AsyncTest("Old frames and faults are ignored after reconnect", async () =>
{
    await using var life = new DetectionLifecycle((_, _) => Task.CompletedTask, () => Task.CompletedTask);
    await life.StartAsync(); var old = life.Generation;
    await life.SetLockedAsync(true); await life.SetLockedAsync(false);
    life.MarkReady(old); Equal(DetectionRunState.Validating, life.State);
    await life.ReportFaultAsync(old, new Exception("late fault")); Equal(DetectionRunState.Validating, life.State);
    life.MarkReady(life.Generation); Equal(DetectionRunState.Running, life.State);
});
AsyncTest("A stream fault immediately before a lock does not lose automatic recovery", async () =>
{
    var connects = 0;
    await using var life = new DetectionLifecycle((_, _) => { connects++; return Task.CompletedTask; }, () => Task.CompletedTask);
    await life.StartAsync(); await life.ReportFaultAsync(life.Generation, new Exception("Windows Hello took the device"));
    Equal(DetectionRunState.Faulted, life.State); Equal(true, life.WantsRunning);
    await life.SetLockedAsync(true); await life.SetLockedAsync(false); Equal(2, connects);
});
AsyncTest("Exit cancels recovery and rejects stale callbacks", async () =>
{
    var connects = 0; var delayed = new TaskCompletionSource();
    var life = new DetectionLifecycle((_, _) => { if (++connects > 1) throw new Exception("busy"); return Task.CompletedTask; },
        () => Task.CompletedTask, async (_, token) => { delayed.SetResult(); await Task.Delay(Timeout.Infinite, token); });
    await life.StartAsync(); await life.SetLockedAsync(true); var recovery = life.SetLockedAsync(false);
    await delayed.Task; await life.DisposeAsync(); await recovery;
    Equal(DetectionRunState.Disposed, life.State); Equal(false, life.AcceptsFrames(life.Generation));
    await life.StartAsync(); Equal(2, connects);
});

var failed = 0;
foreach (var (name, test) in cases) { try { await test().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine($"PASS {name}"); } catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); } }
Console.WriteLine($"{cases.Count - failed}/{cases.Count} passed");
return failed == 0 ? 0 : 1;
