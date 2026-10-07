namespace GlanceNext.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool PresenceEnabled
    {
        get; set;
    }
    public bool BystanderEnabled
    {
        get; set;
    }
    public bool MaskOnBystander
    {
        get; set;
    }
    public bool AttentionEnabled
    {
        get; set;
    }
    public double AbsenceSeconds { get; set; } = 60;
    public double AttentionSeconds { get; set; } = 3;
    public double BystanderSeconds { get; set; } = 1;
    public double RecoverySeconds { get; set; } = 2;
    public double DimOpacity { get; set; } = 0.55;
    public string Theme { get; set; } = "System";
    public string EmergencyShortcut { get; set; } = "Ctrl+Alt+G";
    public bool StartWithWindows
    {
        get; set;
    }
    public string DeviceId { get; set; } = "";
    public bool CameraDirectionVerified { get; set; }
    public bool CameraLeftIsUserLeft { get; set; }
    public string ModelVersion { get; set; } = "yunet-2026may";
    public bool PresenceVerified
    {
        get; set;
    }
    public bool BystanderVerified
    {
        get; set;
    }
    public bool AttentionVerified
    {
        get; set;
    }
    public CalibrationProfile Calibration { get; set; } = new();

    public void Sanitize()
    {
        AbsenceSeconds = FiniteClamp(AbsenceSeconds, 10, 600, 60);
        AttentionSeconds = FiniteClamp(AttentionSeconds, 1, 30, 3);
        BystanderSeconds = FiniteClamp(BystanderSeconds, 0.5, 10, 1);
        RecoverySeconds = FiniteClamp(RecoverySeconds, 0.5, 10, 2);
        DimOpacity = FiniteClamp(DimOpacity, 0.2, 0.85, 0.55);
        Theme = Theme is "Light" or "Dark" ? Theme : "System";
        EmergencyShortcut = EmergencyShortcut is "Ctrl+Alt+P" or "Ctrl+Shift+F12" ? EmergencyShortcut : "Ctrl+Alt+G";
        Calibration ??= new();
        if (!double.IsFinite(Calibration.ForwardYaw) || !double.IsFinite(Calibration.ForwardPitch))
            Calibration = new();
        Calibration.AwayYawThreshold = FiniteClamp(Calibration.AwayYawThreshold, 12, 60, 24);
        Calibration.AwayPitchThreshold = FiniteClamp(Calibration.AwayPitchThreshold, 12, 60, 22);
        if (ModelVersion != "yunet-2026may")
            ResetValidation();
        ModelVersion = "yunet-2026may";
        PresenceEnabled &= PresenceVerified;
        BystanderEnabled &= BystanderVerified;
        AttentionEnabled &= AttentionVerified && Calibration.IsComplete;
    }

    public void ResetValidation()
    {
        PresenceEnabled = BystanderEnabled = AttentionEnabled = false;
        PresenceVerified = BystanderVerified = AttentionVerified = false;
        Calibration = new();
        CameraDirectionVerified = false;
    }
    private static double FiniteClamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
