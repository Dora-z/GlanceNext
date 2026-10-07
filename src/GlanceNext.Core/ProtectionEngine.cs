namespace GlanceNext.Core;

/// <summary>All time comes from a monotonic clock. Camera errors never mean absence.</summary>
public sealed class ProtectionEngine
{
    private TimeSpan? lastFrame, absenceSince, bystanderSince, clearSince, awaySince;
    private bool bystanderActive, lockIssued;
    public void Reset()
    {
        lastFrame = absenceSince = bystanderSince = clearSince = awaySince = null;
        bystanderActive = lockIssued = false;
    }

    public ProtectionDecision Evaluate(DetectionResult result, AppSettings settings, bool ready, bool paused)
    {
        if (!ready || paused || !result.IsValid)
        {
            Reset();
            return new(ProtectionAction.None, Status: paused ? "已暂停" : "等待有效红外数据");
        }
        // A discontinuity clears every timer and latch; the next frame starts a new observation.
        if (lastFrame is { } previous && (result.Timestamp < previous || result.Timestamp - previous > TimeSpan.FromSeconds(2)))
            Reset();
        lastFrame = result.Timestamp;
        double? countdown = null;
        if (settings.PresenceEnabled && settings.PresenceVerified && result.Faces.Count == 0)
        {
            absenceSince ??= result.Timestamp;
            countdown = Math.Max(0, settings.AbsenceSeconds - (result.Timestamp - absenceSince.Value).TotalSeconds);
            if (countdown == 0 && !lockIssued)
            {
                lockIssued = true;
                return new(ProtectionAction.Lock, 0, "离席超时，正在锁屏");
            }
        }
        else
        {
            absenceSince = null;
            lockIssued = false;
        }

        if (settings.BystanderEnabled && settings.BystanderVerified)
        {
            if (result.Faces.Count >= 2)
            {
                clearSince = null;
                bystanderSince ??= result.Timestamp;
                if ((result.Timestamp - bystanderSince.Value).TotalSeconds >= settings.BystanderSeconds)
                    bystanderActive = true;
            }
            else
            {
                bystanderSince = null;
                clearSince ??= result.Timestamp;
                if ((result.Timestamp - clearSince.Value).TotalSeconds >= settings.RecoverySeconds)
                    bystanderActive = false;
            }
        }
        else
        {
            bystanderSince = clearSince = null;
            bystanderActive = false;
        }
        var shouldDim = false;
        if (settings.AttentionEnabled && settings.AttentionVerified &&
            result.PrimaryFace is { } face && settings.Calibration.IsLookingAway(face))
        {
            awaySince ??= result.Timestamp;
            shouldDim = (result.Timestamp - awaySince.Value).TotalSeconds >= settings.AttentionSeconds;
        }
        else
            awaySince = null;
        // Keep attention timing current while a higher-priority alert is visible.
        if (bystanderActive)
            return new(settings.MaskOnBystander ? ProtectionAction.PrivacyMask : ProtectionAction.Reminder,
                countdown, "检测到旁观者", result.BystanderDirection);
        if (shouldDim)
            return new(ProtectionAction.Dim, countdown, "未注视屏幕，已调暗");
        return new(ProtectionAction.None, countdown, countdown.HasValue ? "离席倒计时" : "本地检测中");
    }
}
