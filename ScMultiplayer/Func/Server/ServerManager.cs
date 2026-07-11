using System;
using System.Collections.Generic;
using System.Linq;
using Engine;

namespace ScMultiplayer
{
    public static class ServerBanManager
    {
        private static HashSet<string> bannedIPs = new HashSet<string>();

        public static bool IsBanned(string address)
        {
            return bannedIPs.Contains(address);
        }

        public static void Ban(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            bannedIPs.Add(address);
            Log.Information($"[Ban] Banned: {address}");
        }

        public static void Unban(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            bannedIPs.Remove(address);
            Log.Information($"[Ban] Unbanned: {address}");
        }

        public static IEnumerable<string> GetBannedList()
        {
            return bannedIPs;
        }

        /// <summary>
        /// 检查 IP 是否被封禁（供连接时调用）
        /// </summary>
        public static bool CheckConnectionAllowed(string address)
        {
            return !bannedIPs.Contains(address);
        }
    }

    public static class ServerCmdManager
    {
        public static string ExecuteCommand(string cmd, int callerClientId)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            if (!cmd.StartsWith("/")) return null;

            var parts = cmd.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return GetHelp();

            string command = parts[0].ToLower();
            string[] args = parts.Length > 1 ? parts.Skip(1).ToArray() : Array.Empty<string>();

            switch (command)
            {
                case "help":
                    return GetHelp();
                case "list":
                case "players":
                    return ListPlayers();
                case "kick":
                    return KickPlayer(args);
                case "ban":
                    return BanPlayer(args);
                case "unban":
                    return UnbanPlayer(args);
                case "bans":
                    return ListBans();
                default:
                    return $"Unknown command: /{command}\n{GetHelp()}";
            }
        }

        private static string GetHelp()
        {
            return "/help - Show this help\n" +
                   "/list - List online players\n" +
                   "/kick <name> [reason] - Kick a player\n" +
                   "/ban <name> [reason] - Ban a player\n" +
                   "/unban <ip> - Unban an IP\n" +
                   "/bans - List banned IPs";
        }

        private static string ListPlayers()
        {
            if (!ScMultiplayer.IsHost)
                return "Only the host can run this command.";

            var subsystemPlayers = Game.GameManager.Project?.FindSubsystem<Game.SubsystemPlayers>(false);
            if (subsystemPlayers == null) return "No game running.";

            var lines = new System.Text.StringBuilder("Online players:");
            foreach (var pd in subsystemPlayers.PlayersData)
            {
                int clientId = ScMultiplayer.playerMappingManager.GetClientId(pd.PlayerIndex);
                string hostMarker = clientId == 0 ? " [HOST]" : "";
                lines.AppendLine($"  [{pd.PlayerIndex}] {pd.Name}{hostMarker}");
            }
            return lines.Length == 14 ? "No players online." : lines.ToString();
        }

        private static string KickPlayer(string[] args)
        {
            if (!ScMultiplayer.IsHost)
                return "Only the host can kick players.";

            if (args.Length == 0)
                return "Usage: /kick <name> [reason]";

            string name = args[0];
            string reason = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "Kicked by host";

            if (ScMultiplayer.KickByPlayerName(name, reason))
                return $"Kicked: {name}";
            else
                return $"Player not found: {name}";
        }

        private static string BanPlayer(string[] args)
        {
            if (!ScMultiplayer.IsHost)
                return "Only the host can ban players.";

            if (args.Length == 0)
                return "Usage: /ban <name> [reason]";

            string name = args[0];
            string reason = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "Banned by host";

            if (ScMultiplayer.BanByPlayerName(name, reason))
                return $"Banned: {name}";
            else
                return $"Player not found: {name}";
        }

        private static string UnbanPlayer(string[] args)
        {
            if (!ScMultiplayer.IsHost)
                return "Only the host can unban players.";

            if (args.Length == 0)
                return "Usage: /unban <ip>";

            string ip = args[0];
            if (!ServerBanManager.GetBannedList().Contains(ip))
                return $"IP not banned: {ip}";

            ServerBanManager.Unban(ip);
            return $"Unbanned: {ip}";
        }

        private static string ListBans()
        {
            if (!ScMultiplayer.IsHost)
                return "Only the host can view bans.";

            var bans = ServerBanManager.GetBannedList();
            if (!ServerBanManager.GetBannedList().Any())
                return "No banned IPs.";

            var lines = new System.Text.StringBuilder("Banned IPs:");
            foreach (var ip in bans)
                lines.AppendLine($"  {ip}");
            return lines.ToString();
        }
    }
}
