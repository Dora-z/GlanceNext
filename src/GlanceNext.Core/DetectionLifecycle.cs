namespace GlanceNext.Core;

public enum DetectionRunState { Stopped, Starting, Validating, Running, Locked, Sleeping, Reconnecting, Paused, Faulted, Stopping, Disposed }

/// <summary>Requests run on the caller's dispatcher; device operations are serialized. User intent survives a lock and a transient camera error.</summary>
public sealed class DetectionLifecycle : IAsyncDisposable
{
    private readonly Func<int, CancellationToken, Task> connect;
    private readonly Func<Task> disconnect;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim serial = new(1, 1);
    private CancellationTokenSource operation = new();
    private bool locked, suspended, paused, faulted, disposed;
    private int generation;
    public DetectionLifecycle(Func<int, CancellationToken, Task> connect, Func<Task> disconnect,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    { this.connect = connect; this.disconnect = disconnect; this.delay = delay ?? Task.Delay; }
    public event Action? Changed;
    public DetectionRunState State { get; private set; }
    public bool WantsRunning { get; private set; }
    public bool IsBlocked => locked || suspended;
    public int Generation => generation;
    public int Attempt { get; private set; }
    public Exception? LastError { get; private set; }
    public bool AcceptsFrames(int value) => value == generation && !IsBlocked && WantsRunning && !faulted && !disposed &&
        State is DetectionRunState.Validating or DetectionRunState.Running;

    public Task StartAsync()
    {
        if (disposed || (WantsRunning && !faulted && State is DetectionRunState.Starting or DetectionRunState.Reconnecting or DetectionRunState.Validating or DetectionRunState.Running))
            return Task.CompletedTask;
        WantsRunning = true; paused = faulted = false; LastError = null;
        return RequestAsync(recovery: false);
    }
    public Task StopAsync(bool userPaused = false)
    {
        if (disposed) return Task.CompletedTask;
        WantsRunning = false; paused = userPaused; faulted = false; LastError = null;
        return RequestAsync(recovery: false);
    }
    public Task SetLockedAsync(bool value)
    {
        if (disposed || locked == value) return Task.CompletedTask;
        locked = value;
        if (!IsBlocked) { faulted = false; LastError = null; }
        return RequestAsync(recovery: true);
    }
    public Task SetSuspendedAsync(bool value)
    {
        if (disposed || suspended == value) return Task.CompletedTask;
        suspended = value;
        if (!IsBlocked) { faulted = false; LastError = null; }
        return RequestAsync(recovery: true);
    }
    public Task ReportFaultAsync(int value, Exception error)
    {
        if (!AcceptsFrames(value)) return Task.CompletedTask;
        faulted = true; LastError = error;
        return RequestAsync(recovery: false);
    }
    public void MarkReady(int value)
    {
        if (AcceptsFrames(value) && State == DetectionRunState.Validating) SetState(DetectionRunState.Running);
    }
    private Task RequestAsync(bool recovery)
    {
        generation++;
        operation.Cancel();
        operation.Dispose();
        operation = new();
        SetState(IsBlocked ? (locked ? DetectionRunState.Locked : DetectionRunState.Sleeping) : DetectionRunState.Stopping);
        return ReconcileAsync(generation, operation.Token, recovery);
    }
    private async Task ReconcileAsync(int value, CancellationToken token, bool recovery)
    {
        await serial.WaitAsync();
        try
        {
            if (value != generation) return;
            await disconnect();
            if (value != generation || token.IsCancellationRequested) return;
            if (disposed) { SetState(DetectionRunState.Disposed); return; }
            if (IsBlocked) { SetState(locked ? DetectionRunState.Locked : DetectionRunState.Sleeping); return; }
            if (!WantsRunning) { SetState(paused ? DetectionRunState.Paused : DetectionRunState.Stopped); return; }
            if (faulted) { SetState(DetectionRunState.Faulted); return; }
            var waits = recovery ? new[] { 0d, 0.5, 1, 2, 4 } : new[] { 0d };
            for (var i = 0; i < waits.Length; i++)
            {
                Attempt = i + 1;
                SetState(recovery ? DetectionRunState.Reconnecting : DetectionRunState.Starting);
                try
                {
                    if (waits[i] > 0) await delay(TimeSpan.FromSeconds(waits[i]), token);
                    token.ThrowIfCancellationRequested();
                    await connect(value, token);
                    token.ThrowIfCancellationRequested();
                    LastError = null;
                    SetState(DetectionRunState.Validating);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { await disconnect(); return; }
                catch (Exception error)
                {
                    await disconnect();
                    if (token.IsCancellationRequested || value != generation) return;
                    LastError = error;
                    Changed?.Invoke();
                }
            }
            faulted = true;
            SetState(DetectionRunState.Faulted);
        }
        finally { serial.Release(); }
    }
    private void SetState(DetectionRunState value) { State = value; Changed?.Invoke(); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; WantsRunning = false;
        await RequestAsync(recovery: false);
        operation.Dispose();
        // Do not dispose the semaphore while superseded reconciliation tasks may still be queued.
    }
}
