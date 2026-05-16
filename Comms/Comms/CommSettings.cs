namespace Comms;

public class CommSettings
{
    public int MaxResends { get; set; } = 30;

    public float[] ResendPeriods { get; set; } = new float[2] { 0.5f, 1f };

    public float DuplicatePacketsDetectionTime { get; set; } = 20f;

    public float IdleTime { get; set; } = 120f;
}
