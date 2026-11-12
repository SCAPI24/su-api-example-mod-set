using System;
using System.Threading;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;

using var server = new TcpTerrainServer(0);
server.Start();
using var client = new TcpTerrainClient("127.0.0.1", server.Port);
using var kickReceived = new ManualResetEventSlim();
client.FrameReceived += frame => { if (frame.Type == MessageType.Kicked) kickReceived.Set(); };
if (!client.Connect()) throw new Exception("Client failed to connect");
if (!SpinWait.SpinUntil(() => server.ListPeers().Length > 0, 3000))
    throw new Exception("Server did not accept client");
int id = server.ListPeers()[0].Id;
if (!server.DisconnectClient(id, out string reason)) throw new Exception(reason);
if (!kickReceived.Wait(3000)) throw new Exception("Client did not receive Kicked frame");
if (!SpinWait.SpinUntil(() => !client.IsAlive, 3000))
    throw new Exception("Client connection remained open");
Console.WriteLine("PASS: Kicked frame received before TCP disconnect");
