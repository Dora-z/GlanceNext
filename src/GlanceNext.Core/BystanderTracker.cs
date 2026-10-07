namespace GlanceNext.Core;

/// <summary>Tracks position, not identity. Ambiguous matches produce a generic alert.</summary>
public sealed class BystanderTracker
{
    private FaceObservation? primary, singleCandidate;
    private TimeSpan? lastFrame, lastSeen, singleSince, directionSince;
    private BystanderDirection direction, candidateDirection;
    private bool? cameraMapping;
    private static double CenterX(FaceObservation face) => face.X + face.Width / 2;
    private static double CenterY(FaceObservation face) => face.Y + face.Height / 2;

    public void Reset()
    {
        primary = singleCandidate = null;
        lastFrame = lastSeen = singleSince = directionSince = null;
        direction = candidateDirection = BystanderDirection.Unknown;
        cameraMapping = null;
    }

    public DetectionResult Observe(DetectionResult result, AppSettings settings)
    {
        if (!result.IsValid) { Reset(); return result with { TrackedPrimaryFace = null, BystanderDirection = BystanderDirection.Unknown }; }
        if (lastFrame is { } previous && (result.Timestamp < previous || result.Timestamp - previous > TimeSpan.FromSeconds(2)))
            Reset();
        bool? mapping = settings.CameraDirectionVerified ? settings.CameraLeftIsUserLeft : null;
        if (mapping != cameraMapping)
        {
            // A corrected mapping must not retain the old side or its debounce timer.
            // Keep the positional anchor so an existing observer cannot become the owner.
            direction = candidateDirection = BystanderDirection.Unknown;
            directionSince = null;
            cameraMapping = mapping;
        }
        lastFrame = result.Timestamp;
        var faces = result.Faces;
        var ownerIndex = -1;
        if (primary is { } anchor)
        {
            var matches = faces.Select((face, index) => (face, index, distance: MatchDistance(anchor, face)))
                .Where(x => double.IsFinite(x.distance)).OrderBy(x => x.distance).ToArray();
            // Never resolve an overlap by choosing the largest face.
            if (matches.Length > 0 && (matches.Length == 1 || matches[1].distance - matches[0].distance > 0.035))
            {
                ownerIndex = matches[0].index;
                primary = faces[ownerIndex];
                lastSeen = result.Timestamp;
            }
            else if (lastSeen is { } seen && result.Timestamp - seen > TimeSpan.FromSeconds(2))
                primary = null;
        }
        if (primary is null && faces.Count == 1)
        {
            if (singleCandidate is null || !double.IsFinite(MatchDistance(singleCandidate.Value, faces[0])))
                singleSince = result.Timestamp;
            singleCandidate = faces[0];
            singleSince ??= result.Timestamp;
            if (result.Timestamp - singleSince.Value >= TimeSpan.FromSeconds(1))
            {
                primary = faces[0];
                lastSeen = result.Timestamp;
                ownerIndex = 0;
            }
        }
        else { singleSince = null; singleCandidate = null; }

        if (faces.Count >= 2)
        {
            var next = BystanderDirection.Unknown;
            if (settings.CameraDirectionVerified && ownerIndex >= 0)
            {
                var left = false; var right = false;
                for (var i = 0; i < faces.Count; i++)
                {
                    if (i == ownerIndex) continue;
                    var delta = CenterX(faces[i]) - CenterX(faces[ownerIndex]);
                    if (!settings.CameraLeftIsUserLeft) delta = -delta;
                    left |= delta < -0.06;
                    right |= delta > 0.06;
                }
                next = left && right ? BystanderDirection.Both : left ? BystanderDirection.Left : right ? BystanderDirection.Right : BystanderDirection.Center;
            }
            if (next != candidateDirection || directionSince is null)
            {
                candidateDirection = next;
                directionSince = result.Timestamp;
            }
            if (result.Timestamp - directionSince.Value >= TimeSpan.FromSeconds(0.5)) direction = next;
        }
        else directionSince = null; // Keep the side during the rule engine's two-second disappearance grace period.
        return result with { TrackedPrimaryFace = ownerIndex >= 0 ? faces[ownerIndex] : null, BystanderDirection = direction };
    }

    private static double MatchDistance(FaceObservation anchor, FaceObservation face)
    {
        if (anchor.Width <= 0 || anchor.Height <= 0 || face.Width <= 0 || face.Height <= 0) return double.PositiveInfinity;
        var dx = Math.Abs(CenterX(anchor) - CenterX(face));
        var dy = Math.Abs(CenterY(anchor) - CenterY(face));
        var widthRatio = face.Width / anchor.Width; var heightRatio = face.Height / anchor.Height;
        if (dx > Math.Max(0.12, anchor.Width * 0.6) || dy > Math.Max(0.12, anchor.Height * 0.6) ||
            widthRatio is < 0.4 or > 2.5 || heightRatio is < 0.4 or > 2.5) return double.PositiveInfinity;
        return Math.Sqrt(dx * dx + dy * dy) + Math.Abs(Math.Log(widthRatio)) * 0.04;
    }
}

public sealed class CameraDirectionCalibration
{
    private double origin;
    private TimeSpan? movedSince, previous;
    private int sign;
    public bool IsCollecting { get; private set; }
    public void Begin(FaceObservation face) { origin = face.X + face.Width / 2; movedSince = previous = null; sign = 0; IsCollecting = true; }
    public void Cancel() { IsCollecting = false; movedSince = previous = null; }
    public bool? Observe(DetectionResult result)
    {
        if (!IsCollecting) return null;
        if (!result.IsValid || result.Faces.Count != 1 || (previous is { } t && (result.Timestamp < t || result.Timestamp - t > TimeSpan.FromSeconds(2))))
        { movedSince = null; previous = result.Timestamp; return null; }
        previous = result.Timestamp;
        var delta = result.Faces[0].X + result.Faces[0].Width / 2 - origin;
        if (Math.Abs(delta) < 0.08) { movedSince = null; return null; }
        if (Math.Sign(delta) != sign) { movedSince = null; sign = Math.Sign(delta); }
        movedSince ??= result.Timestamp;
        if (result.Timestamp - movedSince.Value < TimeSpan.FromSeconds(0.5)) return null;
        IsCollecting = false;
        return delta < 0; // User was explicitly instructed to move toward their own left.
    }
}
