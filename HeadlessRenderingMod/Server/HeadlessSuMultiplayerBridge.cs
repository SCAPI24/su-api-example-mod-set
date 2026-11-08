using SuAPI;
using System;
using System.Collections.Generic;

namespace HeadlessRenderingMod
{
    /// <summary>
    /// 无头服务器控制台 ↔ SuMultiplayer 的桥（**走 IModEventBus 事件，两边互不引用程序集**）。
    ///
    /// 为什么用事件而不是接口/反射私有成员：
    ///   · 与旧版 HeadlessRenderingMod 连接联机 Mod 的**同一套做法** —— 那时用的是
    ///     `ScMultiplayer.ServerSettings` / `ScMultiplayer.ServerAudit` 事件；
    ///   · 两个 .scmod 可以各自独立部署、独立升级，不存在编译期耦合，
    ///     也不会因为对方把字段改名就静默失效。
    ///
    /// 协议（由 SuMultiplayer 侧实现，见 Plug/SuMultiplayerMod.cs:HandleControlRequest）：
    ///   事件名：`SuMultiplayer.Control`
    ///   请求：  args[0] = Dictionary&lt;string, object&gt;，至少含 "operation"
    ///   应答：  TriggerEvent 返回 object[][]，每项第一元素是结果字典
    ///           （成功含 ok=true + 数据；失败含 ok=false + error 文本）
    ///
    /// 支持的操作：
    ///   · logmode.get / logmode.set(mode)
    ///   · maxplayers.get / maxplayers.set(value)
    ///   · worldsettings.get / worldsettings.set(key, value)
    ///   · hostport.set(value)
    /// </summary>
    internal static class HeadlessSuMultiplayerBridge
    {
        private const string ControlEventName = "SuMultiplayer.Control";

        /// <summary>日志落盘模式（与 SuMultiplayer 侧的同名枚举取值一致）。</summary>
        internal enum LogMode
        {
            Runtime = 0,
            OnlySelf = 1,
            AllPlayers = 2,
        }

        /// <summary>世界联机设置的只读快照。</summary>
        internal readonly struct WorldJoinSettingsSnapshot
        {
            internal WorldJoinSettingsSnapshot(bool autoHost, bool autoApprove,
                bool allowFurnitureDelete, bool allowFurnitureImport, bool allowFurnitureExport,
                int hostPort)
            {
                AutoHost = autoHost;
                AutoApprove = autoApprove;
                AllowFurnitureDelete = allowFurnitureDelete;
                AllowFurnitureImport = allowFurnitureImport;
                AllowFurnitureExport = allowFurnitureExport;
                HostPort = hostPort;
            }

            internal bool AutoHost { get; }
            internal bool AutoApprove { get; }
            internal bool AllowFurnitureDelete { get; }
            internal bool AllowFurnitureImport { get; }
            internal bool AllowFurnitureExport { get; }
            internal int HostPort { get; }
        }

        /// <summary>
        /// 由 HeadlessRenderingMod 在 OnLoad 时注入：事件总线是唯一通信通道。
        /// 未注入时所有调用都抛明确异常（不静默返回假值）。
        /// </summary>
        internal static void Attach(IModEventBus eventBus) => s_eventBus = eventBus;

        private static IModEventBus s_eventBus;

        // ── 日志模式 ──────────────────────────────────────────────────────

        internal static bool TrySetModuleLogMode(LogMode mode, out string note)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "logmode.set",
                ["mode"] = (int)mode
            });
            note = ReadString(result, "note");
            return ReadBool(result, "ok");
        }

        internal static LogMode ReadModuleLogMode()
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "logmode.get"
            });
            // 模式用**整数**收发，不用枚举名：本程序集经 Obfuscar 混淆，枚举成员名会被改名，
            // 按名字解析会永远失败（真实故障：返回 "Runtime" 却解析不出）。
            // 整数取值由 SuMultiplayer 的 ModuleLogMode 显式钉死（0=Runtime / 1=OnlySelf / 2=AllPlayers）。
            int value = ReadInt(result, "mode");
            switch (value)
            {
                case 0: return LogMode.Runtime;
                case 1: return LogMode.OnlySelf;
                case 2: return LogMode.AllPlayers;
                default:
                    throw new InvalidOperationException(
                        "SuMultiplayer returned an unknown log mode value: " + value + ".");
            }
        }

        // ── 房间容量（MaxPlayers）────────────────────────────────────────

        internal static int ReadMaxPlayers()
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "maxplayers.get"
            });
            return ReadInt(result, "maxPlayers");
        }

        internal static void SetMaxPlayers(int value)
        {
            Call(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operation"] = "maxplayers.set",
                ["value"] = value
            });
        }

        // ── 世界联机设置 ──────────────────────────────────────────────────

        internal static WorldJoinSettingsSnapshot ReadWorldSettings()
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "worldsettings.get"
            });
            return new WorldJoinSettingsSnapshot(
                autoHost: ReadBool(result, "autoHost"),
                autoApprove: ReadBool(result, "autoApprove"),
                allowFurnitureDelete: ReadBool(result, "allowClientFurnitureDelete"),
                allowFurnitureImport: ReadBool(result, "allowFurnitureImport"),
                allowFurnitureExport: ReadBool(result, "allowFurnitureExport"),
                hostPort: ReadInt(result, "hostPort"));
        }

        /// <summary>
        /// 改一个世界联机布尔设置。key 用 SuMultiplayer 侧的枚举名：
        /// AutoHost / AutoApprove / AllowClientFurnitureDelete /
        /// AllowFurnitureImport / AllowFurnitureExport。
        /// </summary>
        internal static string SetWorldBoolean(string key, bool value)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "worldsettings.set",
                ["key"] = key,
                ["value"] = value
            });
            return ReadString(result, "note");
        }

        internal static string SetHostPort(int port)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "hostport.set",
                ["value"] = port
            });
            return ReadString(result, "note");
        }

        // ── 聊天 ──────────────────────────────────────────────────────────

        /// <summary>一条聊天行的只读快照。</summary>
        internal readonly struct ChatLine
        {
            internal ChatLine(long id, string sender, string text, bool self, bool system)
            {
                Id = id;
                Sender = sender;
                Text = text;
                IsSelf = self;
                IsSystem = system;
            }

            internal long Id { get; }
            internal string Sender { get; }
            internal string Text { get; }
            internal bool IsSelf { get; }
            internal bool IsSystem { get; }
        }

        /// <summary>
        /// 读聊天历史。传 <paramref name="since"/> = 上次返回的游标，只取新行；
        /// 传 0 表示全量（首次进聊天室）。
        /// </summary>
        internal static List<ChatLine> ReadChatLines(long since, out long cursor,
            out bool inSession)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "chat.get",
                ["since"] = since
            });

            inSession = ReadBool(result, "inSession");
            cursor = since;
            if (result.TryGetValue("cursor", out object cursorValue) && cursorValue != null)
                long.TryParse(cursorValue.ToString(), out cursor);

            var lines = new List<ChatLine>();
            if (!result.TryGetValue("lines", out object linesValue) ||
                !(linesValue is System.Collections.IEnumerable enumerable))
            {
                return lines;
            }

            foreach (object item in enumerable)
            {
                if (!(item is Dictionary<string, object> line)) continue;
                long.TryParse(ReadStringOrEmpty(line, "id"), out long id);
                lines.Add(new ChatLine(
                    id,
                    ReadStringOrEmpty(line, "sender"),
                    ReadStringOrEmpty(line, "text"),
                    ReadBoolOrDefault(line, "self"),
                    ReadBoolOrDefault(line, "system")));
            }
            return lines;
        }

        /// <summary>发送一条聊天。失败抛出带可读原因（空/超长/限速/不在会话）。</summary>
        internal static void SendChat(string text)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "chat.send",
                ["text"] = text
            });
            // ok=false 时由 ThrowForFailure 抛出 error 文本。
            ReadBool(result, "ok");
        }

        private static bool ReadBoolOrDefault(Dictionary<string, object> dictionary, string name)
        {
            if (!dictionary.TryGetValue(name, out object value) || value == null) return false;
            if (value is bool flag) return flag;
            return bool.TryParse(value.ToString(), out bool parsed) && parsed;
        }

        // ── 房间（Host Room / 状态）──────────────────────────────────────

        /// <summary>房间状态快照（累计字节数，速率由调用方按两次采样差值算）。</summary>
        internal readonly struct RoomStatus
        {
            internal RoomStatus(bool hosting, int clients, long bytesSent, long bytesReceived,
                string roomName)
            {
                Hosting = hosting;
                Clients = clients;
                BytesSent = bytesSent;
                BytesReceived = bytesReceived;
                RoomName = roomName;
            }

            internal bool Hosting { get; }
            internal int Clients { get; }
            internal long BytesSent { get; }
            internal long BytesReceived { get; }
            internal string RoomName { get; }
        }

        /// <summary>
        /// 把**当前正在玩的世界**开成房间（等价于游戏内按 CR 按钮）。
        /// 返回 (是否本次新建, 说明文字)；已在开房时 already=true 且不算失败。
        /// </summary>
        internal static bool TryHostRoom(out bool already, out string note)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "room.host"
            });
            already = ReadBoolOrDefault(result, "already");
            note = ReadStringOrEmpty(result, "note");
            return ReadBool(result, "ok");
        }

        internal static RoomStatus ReadRoomStatus()
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "room.status"
            });
            return new RoomStatus(
                hosting: ReadBoolOrDefault(result, "hosting"),
                clients: ReadInt(result, "clients"),
                bytesSent: ReadLong(result, "bytesSent"),
                bytesReceived: ReadLong(result, "bytesReceived"),
                roomName: ReadStringOrEmpty(result, "roomName"));
        }

        internal static void CloseRoom()
        {
            Call(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operation"] = "room.close"
            });
        }

        private static long ReadLong(Dictionary<string, object> dictionary, string name)
        {
            if (dictionary.TryGetValue(name, out object value) && value != null)
            {
                if (value is long number) return number;
                if (value is int intNumber) return intNumber;
                if (long.TryParse(value.ToString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long parsed))
                {
                    return parsed;
                }
            }
            ThrowForFailure(dictionary);
            return 0;
        }

        // ── 主机角色幽灵身体 ──────────────────────────────────────────────

        /// <summary>
        /// 主机自己的角色是否处于幽灵状态（不可见 / 不可攻击 / 睡眠）。
        /// 只针对主机本机角色；客户端角色不受影响。
        /// </summary>
        internal static bool ReadHostGhost()
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "ghost.get"
            });
            return ReadBool(result, "ghost");
        }

        /// <summary>
        /// 进入 / 退出幽灵状态。幽灵化同时把主机角色置为睡眠
        /// —— 否则客户端全部睡觉时世界不会加速（引擎要求至少一人真的在睡）。
        /// </summary>
        internal static bool SetHostGhost(bool ghost)
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "ghost.set",
                ["ghost"] = ghost
            });
            return ReadBool(result, "ok");
        }

        // ── 联机角色（主机端 SuMultiplayerPlayers.xml 记录）──────────────────

        /// <summary>
        /// 一条联机角色记录（主机端角色文件里的一行）。
        /// 字段与 `SuMultiplayerPlayers.xml` 对齐；这里只是只读快照，不含背包明细。
        /// </summary>
        internal readonly struct NetworkPlayerInfo
        {
            internal NetworkPlayerInfo(string recordKey, string name, string playerClass,
                string skinName, bool online, int clientId, double level, double health,
                double air, double food, double stamina, double sleep, double temperature,
                double wetness, double positionX, double positionY, double positionZ)
            {
                RecordKey = recordKey;
                Name = name;
                PlayerClass = playerClass;
                SkinName = skinName;
                Online = online;
                ClientId = clientId;
                Level = level;
                Health = health;
                Air = air;
                Food = food;
                Stamina = stamina;
                Sleep = sleep;
                Temperature = temperature;
                Wetness = wetness;
                PositionX = positionX;
                PositionY = positionY;
                PositionZ = positionZ;
            }

            internal string RecordKey { get; }
            internal string Name { get; }
            internal string PlayerClass { get; }
            internal string SkinName { get; }
            internal bool Online { get; }
            internal int ClientId { get; }
            internal double Level { get; }
            internal double Health { get; }
            internal double Air { get; }
            internal double Food { get; }
            internal double Stamina { get; }
            internal double Sleep { get; }
            internal double Temperature { get; }
            internal double Wetness { get; }
            internal double PositionX { get; }
            internal double PositionY { get; }
            internal double PositionZ { get; }
        }

        /// <summary>列出主机端全部联机角色（含离线角色）。</summary>
        internal static List<NetworkPlayerInfo> ListNetworkPlayers()
        {
            Dictionary<string, object> result = Call(new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["operation"] = "players.list"
            });

            var players = new List<NetworkPlayerInfo>();
            if (!result.TryGetValue("players", out object playersValue) ||
                !(playersValue is System.Collections.IEnumerable enumerable))
            {
                return players;
            }
            foreach (object item in enumerable)
            {
                if (!(item is Dictionary<string, object> player)) continue;
                players.Add(new NetworkPlayerInfo(
                    recordKey: ReadStringOrEmpty(player, "recordKey"),
                    name: ReadStringOrEmpty(player, "name"),
                    playerClass: ReadStringOrEmpty(player, "playerClass"),
                    skinName: ReadStringOrEmpty(player, "skinName"),
                    online: ReadBoolOrDefault(player, "online"),
                    clientId: ReadIntOrDefault(player, "clientId"),
                    level: ReadDoubleOrDefault(player, "level"),
                    health: ReadDoubleOrDefault(player, "health"),
                    air: ReadDoubleOrDefault(player, "air"),
                    food: ReadDoubleOrDefault(player, "food"),
                    stamina: ReadDoubleOrDefault(player, "stamina"),
                    sleep: ReadDoubleOrDefault(player, "sleep"),
                    temperature: ReadDoubleOrDefault(player, "temperature"),
                    wetness: ReadDoubleOrDefault(player, "wetness"),
                    positionX: ReadDoubleOrDefault(player, "positionX"),
                    positionY: ReadDoubleOrDefault(player, "positionY"),
                    positionZ: ReadDoubleOrDefault(player, "positionZ")));
            }
            return players;
        }

        /// <summary>改联机角色的名字 / 皮肤 / 各项数值（只提交出现的字段）。</summary>
        internal static void UpdateNetworkPlayer(string recordKey,
            Dictionary<string, object> changes)
        {
            var request = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operation"] = "players.update",
                ["recordKey"] = recordKey
            };
            foreach (KeyValuePair<string, object> pair in changes)
                request[pair.Key] = pair.Value;
            Call(request);
        }

        /// <summary>删除一条联机角色记录。</summary>
        internal static void DeleteNetworkPlayer(string recordKey)
        {
            Call(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operation"] = "players.delete",
                ["recordKey"] = recordKey
            });
        }

        private static int ReadIntOrDefault(Dictionary<string, object> dictionary, string name)
        {
            if (!dictionary.TryGetValue(name, out object value) || value == null) return 0;
            if (value is int number) return number;
            return int.TryParse(value.ToString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

        private static double ReadDoubleOrDefault(Dictionary<string, object> dictionary,
            string name)
        {
            if (!dictionary.TryGetValue(name, out object value) || value == null) return 0.0;
            try
            {
                return Convert.ToDouble(value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0.0;
            }
        }

        // ── 事件调用与解析 ────────────────────────────────────────────────

        /// <summary>
        /// 发一条控制请求并取回结果字典。
        /// 失败（无监听者 / ok=false / 超时）一律抛 **明确异常**，不返回假数据。
        /// </summary>
        private static Dictionary<string, object> Call(Dictionary<string, object> request)
        {
            IModEventBus bus = s_eventBus;
            if (bus == null)
            {
                throw new InvalidOperationException(
                    "Multiplayer control is unavailable: the event bus is not attached yet.");
            }

            object[][] results = bus.TriggerEvent(ControlEventName, new object[] { request });
            if (results == null || results.Length == 0)
            {
                throw new InvalidOperationException(
                    "SuMultiplayer is not loaded. Put [SuAPI]SuMultiplayer-*.scmod in the "
                    + "server's Mods/ directory to control multiplayer settings.");
            }

            for (int i = 0; i < results.Length; i++)
            {
                object[] entry = results[i];
                if (entry == null || entry.Length == 0) continue;
                if (entry[0] is Dictionary<string, object> dictionary)
                    return dictionary;
            }

            throw new InvalidOperationException(
                "SuMultiplayer returned no usable response for \""
                + ReadStringOrEmpty(request, "operation") + "\".");
        }

        private static string ReadStringOrEmpty(Dictionary<string, object> dictionary, string name) =>
            dictionary.TryGetValue(name, out object value) ? value?.ToString() ?? string.Empty
                : string.Empty;

        private static bool ReadBool(Dictionary<string, object> dictionary, string name)
        {
            if (dictionary.TryGetValue(name, out object value) && value != null)
            {
                if (value is bool flag) return flag;
                if (bool.TryParse(value.ToString(), out bool parsed)) return parsed;
            }
            ThrowForFailure(dictionary);
            return false;
        }

        private static int ReadInt(Dictionary<string, object> dictionary, string name)
        {
            if (dictionary.TryGetValue(name, out object value) && value != null)
            {
                if (value is int number) return number;
                if (int.TryParse(value.ToString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                {
                    return parsed;
                }
            }
            ThrowForFailure(dictionary);
            return 0;
        }

        private static string ReadString(Dictionary<string, object> dictionary, string name)
        {
            if (dictionary.TryGetValue(name, out object value) && value != null)
                return value.ToString();
            ThrowForFailure(dictionary);
            return string.Empty;
        }

        /// <summary>ok=false 时把 error 文本抛出来；否则说明字段缺失（是协议问题，也要报）。</summary>
        private static void ThrowForFailure(Dictionary<string, object> dictionary)
        {
            if (dictionary.TryGetValue("ok", out object ok) && ok is bool applied && applied)
                return;

            string error = dictionary.TryGetValue("error", out object errorValue)
                ? errorValue?.ToString()
                : null;
            throw new InvalidOperationException(string.IsNullOrEmpty(error)
                ? "SuMultiplayer rejected the control request."
                : error);
        }
    }
}
