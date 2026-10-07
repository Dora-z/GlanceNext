namespace GlanceNext.Core;

/// <summary>Readiness requires uninterrupted, valid frames, rather than a cumulative frame count.</summary>
public sealed class StreamHealth
{
    private TimeSpan? previous;
    public int ConsecutiveFrames { get; private set; }
    public bool IsReady => ConsecutiveFrames >= 30;

    public void Observe(DetectionResult result)
    {
        if (!result.IsValid)
        {
            Reset();
            return;
        }
        if (previous is { } last && (result.Timestamp < last || result.Timestamp - last > TimeSpan.FromSeconds(2)))
            Reset();
        previous = result.Timestamp;
        ConsecutiveFrames = Math.Min(30, ConsecutiveFrames + 1);
    }

    public void Reset()
    {
        previous = null;
        ConsecutiveFrames = 0;
    }
}
