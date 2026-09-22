namespace Comms;

public class CommSettings
{
    public int MaxResends { get; set; } = 30;

    public float[] ResendPeriods { get; set; } = new float[2] { 0.1f, 0.15f };

    public float MinimumResendPeriod { get; set; } = 0.08f;

    public float MaximumResendPeriod { get; set; } = 0.15f;

    public float DuplicatePacketsDetectionTime { get; set; } = 20f;

    public float IdleTime { get; set; } = 120f;

    // Source: Comms/Comms/Comm.cs:Comm.GetResendPeriod
    // The base resend period follows the RTT and is only bounded below by MinimumResendPeriod.
    // The exponential backoff multiplies it and is bounded by MaximumBackoffPeriod. Previously
    // the product was clamped by MaximumResendPeriod (0.15 s), which flattened the backoff after
    // three attempts and produced a constant ~150 ms resend storm for up to MaxResends packets.
    public float ResendBackoffFactor { get; set; } = 1.25f;

    public int MaximumResendBackoffSteps { get; set; } = 4;

    public float MaximumBackoffPeriod { get; set; } = 1.0f;

    // Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
    // A partially received message is dropped after this long without progress. It replaces the
    // old derived value (MaxResends * ResendPeriods[last] = 4.5 s), which no longer matches the
    // retransmit schedule once the backoff is allowed to grow.
    public float MessagePartsTimeout { get; set; } = 10f;

    // Source: Comms/Comms/Comm.cs:Comm.RecoverStalledReliableSequence
    // How long a reliable sequenced stream may wait behind a gap before the receiver skips it.
    public float ReliableSequencedStallTimeout { get; set; } = 2f;

    // Source: Comms/Comms/Comm.cs:Comm.IsDatagramAddressChangeAllowed
    // The datagram token lets the receiver repair a peer's datagram address to the source address
    // the packets really arrive from. Allowed by default, including a public IP change, so a peer
    // that moves to a new address keeps its datagram path. Set this to false to accept only a
    // same-IP NAT port change. The repair still requires an exact 32-bit session token match.
    public bool AllowDatagramAddressIpChange { get; set; } = true;

    // Source: Comms/Comms/Comm.cs:Comm.MoveConnection
    // Minimum interval between two accepted address repairs of the same peer. It only rate-limits
    // successful moves, so the first repair of a real NAT change always applies immediately.
    public float MinimumDatagramAddressChangeInterval { get; set; } = 1f;
}
