namespace Comms;

public class CommSettings
{
    public int MaxResends { get; set; } = 30;

    public float[] ResendPeriods { get; set; } = new float[2] { 0.1f, 0.15f };

    public float MinimumResendPeriod { get; set; } = 0.08f;

    public float MaximumResendPeriod { get; set; } = 0.15f;

    public float DuplicatePacketsDetectionTime { get; set; } = 20f;

    public float IdleTime { get; set; } = 120f;
}
