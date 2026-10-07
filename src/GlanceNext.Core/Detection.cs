namespace GlanceNext.Core;

public readonly record struct FaceObservation(double X, double Y, double Width, double Height,
    double Confidence, double Yaw, double Pitch);

public sealed record DetectionResult(TimeSpan Timestamp, bool IsValid, IReadOnlyList<FaceObservation> Faces,
    double FrameRate = 0, double MeanIntensity = 0, string? Error = null,
    FaceObservation? TrackedPrimaryFace = null, BystanderDirection BystanderDirection = BystanderDirection.Unknown)
{
    public FaceObservation? PrimaryFace => TrackedPrimaryFace ?? (Faces.Count == 1 ? Faces[0] : null);
}

public enum BystanderDirection { Unknown, Left, Right, Both, Center }

public enum ProtectionAction
{
    None, Reminder, Dim, PrivacyMask, Lock
}
public sealed record ProtectionDecision(ProtectionAction Action, double? LockCountdownSeconds = null,
    string Status = "待机", BystanderDirection BystanderDirection = BystanderDirection.Unknown);

public sealed class CalibrationProfile
{
    public bool IsComplete
    {
        get; set;
    }
    public double ForwardYaw
    {
        get; set;
    }
    public double ForwardPitch
    {
        get; set;
    }
    public double AwayYawThreshold { get; set; } = 24;
    public double AwayPitchThreshold { get; set; } = 22;
    public bool IsLookingAway(FaceObservation face) => IsComplete &&
        (Math.Abs(face.Yaw - ForwardYaw) >= AwayYawThreshold ||
         Math.Abs(face.Pitch - ForwardPitch) >= AwayPitchThreshold);
}
