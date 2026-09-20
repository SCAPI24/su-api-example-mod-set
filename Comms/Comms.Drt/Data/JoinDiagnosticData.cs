using System.Net;

namespace Comms.Drt;

public enum JoinDiagnosticStage
{
    ConnectReceived,
    Queued,
    ForwardedToHost,
    GameJoinedSend
}

// Source: Comms.Drt/Func/Server/Server.cs:Peer.ConnectRequest
// This is process-local diagnostic data. It is not part of the wire protocol.
public struct JoinDiagnosticData
{
    public JoinDiagnosticStage Stage;

    public int GameID;

    public int ClientID;

    public IPEndPoint Address;

    public string ClientName;

    public int PayloadBytes;

    public int QueueCount;

    public int Tick;

    public int Step;

    public int StateBytes;

    public int TickMessages;

    public string StageName => Stage switch
    {
        JoinDiagnosticStage.ConnectReceived => "connect_received",
        JoinDiagnosticStage.Queued => "queued",
        JoinDiagnosticStage.ForwardedToHost => "forwarded_to_host",
        JoinDiagnosticStage.GameJoinedSend => "game_joined_send",
        _ => "unknown"
    };
}
