using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Comms.Drt;

internal class DesyncDetector
{
    private ServerGame ServerGame;

    private int LastHashStep;

    private Dictionary<int, ushort> Hashes = new();

    private double DesyncDetectedTime;

    private DesyncData DesyncData;

    public int? DesyncDetectedStep => DesyncData?.Step;

    public DesyncDetector(ServerGame serverGame)
    {
        ServerGame = serverGame;
    }

    public void Run()
    {
        if (DesyncData != null && ServerGame.DesyncDetectionMode == DesyncDetectionMode.Locate && DesyncDetectedTime != 0.0)
        {
            bool num = Comm.GetTime() - DesyncDetectedTime > (double)ServerGame.Server.Settings.DesyncDetectionStatesTimeout;
            bool flag = DesyncData.States.Count >= DesyncData.ClientsCount && DesyncData.PriorStates.Count >= DesyncData.ClientsCount;
            if (num || flag)
            {
                DesyncDetectedTime = 0.0;
                ServerGame.Server.InvokeDesync(DesyncData);
            }
        }
    }

    public void HandleHashes(int firstHashStep, ushort[] hashes, ServerClient serverClient)
    {
        if (ServerGame.DesyncDetectionMode == DesyncDetectionMode.None || DesyncData != null)
        {
            return;
        }
        for (int i = 0; i < hashes.Length; i++)
        {
            if (Hashes.TryGetValue(i + firstHashStep, out var num))
            {
                if (hashes[i] != num)
                {
                    DesyncData = new DesyncData
                    {
                        GameID = ServerGame.GameID,
                        Step = firstHashStep + i,
                        ClientsCount = ServerGame.Clients.Count
                    };
                    DesyncDetectedTime = Comm.GetTime();
                    ServerGame.Server.InvokeWarning($"Desync detected at step {DesyncData.Step} when comparing hashes received from client \"{serverClient.ClientName}\" at {serverClient.PeerData.Address}");
                    if (ServerGame.DesyncDetectionMode == DesyncDetectionMode.Locate)
                    {
                        ServerGame.SendDataMessageToAllClients(new ServerDesyncStateRequestMessage
                        {
                            Step = DesyncData.Step - 1
                        });
                        ServerGame.SendDataMessageToAllClients(new ServerDesyncStateRequestMessage
                        {
                            Step = DesyncData.Step
                        });
                    }
                    return;
                }
            }
            else
            {
                Hashes.Add(i + firstHashStep, hashes[i]);
                LastHashStep = Math.Max(LastHashStep, i + firstHashStep);
            }
        }
        int num2 = ServerGame.DesyncDetectionPeriod + 20;
        List<int> list = new();
        foreach (int key in Hashes.Keys)
        {
            if (LastHashStep - key > num2)
            {
                list.Add(key);
            }
        }
        foreach (int item in list)
        {
            Hashes.Remove(item);
        }
    }

    public void HandleDesyncState(int step, byte[] stateBytes, bool isDeflated, ServerClient serverClient)
    {
        if (ServerGame.DesyncDetectionMode == DesyncDetectionMode.Locate && DesyncData != null)
        {
            if (step == DesyncData.Step - 1)
            {
                DesyncData.PriorStates[serverClient.ClientID] = ProcessState(stateBytes, isDeflated);
            }
            else if (step == DesyncData.Step)
            {
                DesyncData.States[serverClient.ClientID] = ProcessState(stateBytes, isDeflated);
            }
        }
    }

    private byte[] ProcessState(byte[] bytes, bool isDeflated)
    {
        if (isDeflated)
        {
            using (DeflateStream deflateStream = new(new MemoryStream(bytes), CompressionMode.Decompress))
            {
                using MemoryStream memoryStream = new();
                deflateStream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }
        return bytes;
    }
}
