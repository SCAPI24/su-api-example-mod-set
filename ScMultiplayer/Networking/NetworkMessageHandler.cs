using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class NetworkMessageHandler
    {
        public static void HandleChatMessage(ChatMessage message, int clientID)
        {
            Log.Information($"[Chat] Client{clientID} {message.Sender}: {message.Text}");
            ScMultiplayer.currentInstance.DisplayChatMessage(message, clientID);
        }

        public static void HandleWorldInfoMessage(GameWorldInfoMessage1 message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGameWorldInfoMessage(message);
        }

        public static void HandleModifiedCellsMessage(GameModifiedCellsMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGameModifiedCellsMessage(message, clientID);
        }

        public static void HandlePakWorldMessage(GamePakWorldMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGamePakWorldMessage(message);
        }

        public static void HandlePlayerHealthMessage(GamePlayerHealthMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGamePlayerHealthMessage(message, clientID);
        }
    }
}
