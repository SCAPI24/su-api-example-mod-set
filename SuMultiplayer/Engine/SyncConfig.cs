using System;
using System.Globalization;
using System.IO;
using System.Text;
using Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Voice;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 联机参数配置。两端各一份，放在游戏数据根目录：
    /// <c>data:/Mods/SuMultiplayer.json</c>（Windows: &lt;运行根&gt;\Mods\；
    /// Android: /sdcard/Download/Survivalcraft/Mods/）。
    ///
    /// **这里只有"参数"，没有"模式"**：旧实现把 host/client 写进配置，于是启动即联网，
    /// 那不是用户要的入口。新实现的入口只有两个，都由玩家操作触发 ——
    ///   · 游戏内 CR 按钮 → 房子建在**正在玩的那个世界**上；
    ///   · Play 屏点房间行 → 连到那个房间。
    /// 因此本文件不再有 mode/role 字段（写了也不认）。
    ///
    /// 刻意不用 System.Text.Json：mod 要同时跑在 Windows 与 Android，
    /// 一个扁平配置用 40 行解析足够，少一个依赖少一个坑。
    ///
    /// 演示页标记：CFG01。
    /// </summary>
    public sealed class SyncConfig
    {
        public int Port = 26760;
        public int Radius = 3;
        public bool VerboseLog = false;
        /// <summary>主机侧是否把收到的客户端位置当作兴趣中心（实机 MVP 里两端各跑一个世界，故允许）。</summary>
        public bool TrustClientCenter = true;

        // —— 房间（会话）相关 ——
        /// <summary>房间名（主机应答发现探测时广播的名字）。</summary>
        public string RoomName = "SuMP Room";
        /// <summary>LAN 发现端口（UDP 探测/应答）。</summary>
        public int DiscoveryPort = 26761;
        public int MaxPlayers = 8;

        /// <summary>
        /// 主机侧交互距离**上限**（米）：客户端声明的 reach 无论多大，
        /// allowed = min(reach + 容差, MaxInteractionReach)。默认 16，允许 5..64。
        /// 见 <see cref="InteractionReachGate"/>（all in one place）。
        /// </summary>
        public float MaxInteractionReach = InteractionReachGate.DefaultServerMaxMeters;

        // —— 近战攻击（melee.proto / Modules/MeleeSyncModule.cs）——
        /// <summary>
        /// <c>meleereachmax</c>（米，5..64，默认 = <see cref="MaxInteractionReach"/>）：
        /// 主机对近战攻击的距离上限。裁决 = min(请求 reach + 0.5 m 容差, 本值, 主机引擎 reach)；
        /// 与交互距离同一套语义（<see cref="InteractionReachGate"/> 的容差 / 合法范围），
        /// 但**没有**单独的 <c>meleereachmax</c> 配置时直接跟随交互上限，不另立一个默认值。
        /// Source: Survivalcraft/Game/ComponentMiner.cs:378（reach = 创造 CreativeReach / 生存 5）
        /// </summary>
        public float MeleeMaxReach = InteractionReachGate.DefaultServerMaxMeters;

        /// <summary>
        /// <c>meleeprediction</c>（bool，默认 true）：客户端是否展示乐观的**命中标记**
        /// （红色命中粒子）。挥手 / 挥击声与"主机结果到达前不显示伤害数字"不受它影响：
        /// 客户端永远不本地 roll 命中、不本地扣血；关掉只是少一个预测标记。
        /// </summary>
        public bool MeleePrediction = true;

        /// <summary><c>meleereachmax</c> 是否在配置里显式出现（没有 → 跟随交互上限）。</summary>
        private bool m_meleeReachConfigured;

        // —— 实时语音（voice.proto / Modules/VoiceChatModule.cs）——
        /// <summary>本端是否启用语音（关 = 不采集 / 不发送 / 不播放）。</summary>
        public bool VoiceEnabled = true;
        /// <summary>
        /// true = HUD 的 TALK 按钮点按说话（**不绑定键盘键**）；false = 开麦 + VAD。
        /// 可选 PTT 模式保留，但 V 键不参与（产品要求，2026-10-19）。
        /// </summary>
        public bool VoicePushToTalk = false;
        /// <summary>远端语音的本地播放增益（0..1）。</summary>
        public float VoiceVolume = 1f;
        /// <summary>
        /// 语音编码档位（high / standard / low / ultra）；默认 low（2-bit ADPCM）。
        /// 见 <see cref="VoiceProfiles"/>：每包 163 / 123 / 83 / 43 字节。
        /// </summary>
        public VoiceQuality VoiceQuality = VoiceProfiles.Default;
        /// <summary>VAD 挂起毫秒（默认 160；0 = 说完立刻停发，上限 2000）。</summary>
        public int VoiceVadHangoverMs = VoiceFormat.VadHangoverDefaultMs;


        public const string FileName = "SuMultiplayer.json";

        public static string SettingsPath => Storage.CombinePaths("data:/Mods", FileName);

        public string Describe() =>
            "port=" + Port + " radius=" + Radius + " room=\"" + RoomName + "\" discovery=" +
            DiscoveryPort + " maxplayers=" + MaxPlayers + " maxReach=" +
            MaxInteractionReach.ToString("0.##", CultureInfo.InvariantCulture) + "m meleeReach=" +
            MeleeMaxReach.ToString("0.##", CultureInfo.InvariantCulture) + "m meleePrediction=" +
            MeleePrediction + " verbose=" +
            VerboseLog + " voice=" + VoiceEnabled + " ptt=" + VoicePushToTalk + " voiceVol=" +
            VoiceVolume.ToString("0.##", CultureInfo.InvariantCulture) + " voiceQuality=" +
            VoiceProfiles.Name(VoiceQuality) + " vadHangover=" + VoiceVadHangoverMs + "ms";

        /// <summary>
        /// 读配置；不存在就写一份模板。**读失败不静默兜底**：日志里给出可直接定位的原因，
        /// 然后按默认参数继续（默认参数不含任何"自动联网"开关，所以不会假装联机成功）。
        /// </summary>
        public static SyncConfig Load(out string note)
        {
            var config = new SyncConfig();
            string path = SettingsPath;
            try
            {
                if (!Storage.FileExists(path))
                {
                    // 用户口径（2026-10-21）：不额外创建配置文件。没有文件 = 全部用默认参数；
                    // 需要改端口/房间名/语音等再手动创建 Mods/SuMultiplayer.json（调试镜像键不放这里，
                    // 它们在 <世界目录>/SuMultiplayerDebug.json）。
                    note = "没有配置文件，使用默认参数（不自动生成）：" + path;
                    return config;
                }
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    string text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                    ApplyFlatJson(config, text);
                }
                if (!config.m_meleeReachConfigured)
                {
                    // 没有专门的近战上限 → 跟随交互上限（同一个数值语义，不另立默认）。
                    config.MeleeMaxReach = config.MaxInteractionReach;
                }
                note = "配置已读取：" + path;
            }
            catch (Exception e)
            {
                note = "配置读取失败（用默认参数继续）：" + e.GetType().Name + " " + e.Message + " @ " + path;
                Log.Error("[SuMP] " + note);
            }
            return config;
        }

        /// <summary>扁平 JSON 解析：只认 "key": value（字符串/数字/布尔），支持 // 注释。</summary>
        private static void ApplyFlatJson(SyncConfig config, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int index = 0;
            while (index < text.Length)
            {
                int keyStart = text.IndexOf('"', index);
                if (keyStart < 0) break;
                int keyEnd = text.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1);
                int colon = text.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;
                int valueStart = colon + 1;
                while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart])) valueStart++;
                if (valueStart >= text.Length) break;
                int valueEnd = valueStart;
                string value;
                if (text[valueStart] == '"')
                {
                    valueEnd = text.IndexOf('"', valueStart + 1);
                    if (valueEnd < 0) break;
                    value = text.Substring(valueStart + 1, valueEnd - valueStart - 1);
                    valueEnd++;
                }
                else
                {
                    while (valueEnd < text.Length && text[valueEnd] != ',' && text[valueEnd] != '}' &&
                           text[valueEnd] != '\n' && text[valueEnd] != '\r')
                        valueEnd++;
                    value = text.Substring(valueStart, valueEnd - valueStart).Trim();
                }
                Apply(config, key, value);
                index = valueEnd;
            }
        }

        private static void Apply(SyncConfig config, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "mode":
                case "role":
                    // 旧配置里的模式字段：不再作为入口，明确警告一次，避免"改了配置却没变化"。
                    Log.Warning("[SuMP] 配置里的 \"" + key + "\" 已不再生效：建房走游戏内 CR 按钮，" +
                                "加入走 Play 屏房间行");
                    break;
                case "port":
                    if (int.TryParse(value, out int port) && port > 0 && port < 65536) config.Port = port;
                    break;
                case "radius":
                    if (int.TryParse(value, out int radius)) config.Radius = Math.Max(1, Math.Min(32, radius));
                    break;
                case "verbose":
                case "log":
                    config.VerboseLog = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "room":
                case "roomname":
                    if (!string.IsNullOrWhiteSpace(value)) config.RoomName = value.Trim();
                    break;
                case "discoveryport":
                case "discoverport":
                    if (int.TryParse(value, out int discoveryPort) && discoveryPort > 0 && discoveryPort < 65536)
                        config.DiscoveryPort = discoveryPort;
                    break;
                case "maxplayers":
                    if (int.TryParse(value, out int maxPlayers)) config.MaxPlayers = Math.Max(1, maxPlayers);
                    break;
                case "maxinteractionreach":
                case "reachcap":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out float reachCap))
                    {
                        float clamped = Math.Max(InteractionReachGate.MinServerMaxMeters,
                            Math.Min(InteractionReachGate.MaxServerMaxMeters, reachCap));
                        if (Math.Abs(clamped - reachCap) > 0.0001f)
                        {
                            Log.Warning("[SuMP] 配置 " + key + "=" + value + " 超出允许范围 " +
                                InteractionReachGate.MinServerMaxMeters + ".." +
                                InteractionReachGate.MaxServerMaxMeters + " m → 已夹到 " +
                                clamped.ToString("0.##", CultureInfo.InvariantCulture) + " m");
                        }
                        config.MaxInteractionReach = clamped;
                    }
                    else
                    {
                        Log.Warning("[SuMP] 配置 " + key + "=" + value + " 不是数字 → 使用默认 " +
                            InteractionReachGate.DefaultServerMaxMeters + " m");
                    }
                    break;
                case "meleereachmax":
                case "meleemaxreach":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out float meleeCap))
                    {
                        float clampedMelee = Math.Max(InteractionReachGate.MinServerMaxMeters,
                            Math.Min(InteractionReachGate.MaxServerMaxMeters, meleeCap));
                        if (Math.Abs(clampedMelee - meleeCap) > 0.0001f)
                        {
                            Log.Warning("[SuMP] 配置 " + key + "=" + value + " 超出允许范围 " +
                                InteractionReachGate.MinServerMaxMeters + ".." +
                                InteractionReachGate.MaxServerMaxMeters + " m → 已夹到 " +
                                clampedMelee.ToString("0.##", CultureInfo.InvariantCulture) + " m");
                        }
                        config.MeleeMaxReach = clampedMelee;
                        config.m_meleeReachConfigured = true;
                    }
                    else
                    {
                        Log.Warning("[SuMP] 配置 " + key + "=" + value + " 不是数字 → 跟随交互上限 " +
                            InteractionReachGate.DefaultServerMaxMeters + " m");
                    }
                    break;
                case "meleeprediction":
                case "meleepredict":
                    config.MeleePrediction = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "trustclientcenter":
                    config.TrustClientCenter = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "voice":
                case "voiceenabled":
                    config.VoiceEnabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "voicemode":
                case "voicetalk":
                    if (string.Equals(value, "ptt", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "pushtotalk", StringComparison.OrdinalIgnoreCase))
                        config.VoicePushToTalk = true;
                    else if (string.Equals(value, "open", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(value, "vad", StringComparison.OrdinalIgnoreCase))
                        config.VoicePushToTalk = false;
                    else
                        Log.Warning("[SuMP] 配置 " + key + "=" + value +
                            " 不是 open / ptt → 保持默认（open）");
                    break;
                case "pushtotalk":
                case "voicepushtotalk":
                    config.VoicePushToTalk = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "voicevolume":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out float voiceVolume))
                        config.VoiceVolume = Math.Max(0f, Math.Min(1f, voiceVolume));
                    break;
                case "voicequality":
                case "voiceprofile":
                    if (VoiceProfiles.TryParse(value, out VoiceQuality quality))
                        config.VoiceQuality = quality;
                    else
                        Log.Warning("[SuMP] 配置 " + key + "=" + value +
                            " 不是 high / standard / low / ultra → 保持默认 " +
                            VoiceProfiles.Name(VoiceProfiles.Default));
                    break;
                case "voicevadhangover":
                case "vadhangover":
                    if (int.TryParse(value, out int vadHangoverMs))
                    {
                        int clamped = Math.Max(0,
                            Math.Min(VoiceFormat.VadHangoverMaxMs, vadHangoverMs));
                        if (clamped != vadHangoverMs)
                            Log.Warning("[SuMP] 配置 " + key + "=" + value + " 超出 0.." +
                                VoiceFormat.VadHangoverMaxMs + " ms → 已夹到 " + clamped + " ms");
                        config.VoiceVadHangoverMs = clamped;
                    }
                    else
                    {
                        Log.Warning("[SuMP] 配置 " + key + "=" + value + " 不是毫秒数字 → 保持默认 " +
                            VoiceFormat.VadHangoverDefaultMs + " ms");
                    }
                    break;
            }
        }

        /// <summary>把当前参数写回去（测试脚本用得到）。</summary>
        public void Save()
        {
            try
            {
                using (Stream stream = Storage.OpenFile(SettingsPath, OpenFileMode.Create))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  \"port\": " + Port + ",");
                    writer.WriteLine("  \"discoveryport\": " + DiscoveryPort + ",");
                    writer.WriteLine("  \"room\": \"" + RoomName + "\",");
                    writer.WriteLine("  \"radius\": " + Radius + ",");
                    writer.WriteLine("  \"maxplayers\": " + MaxPlayers + ",");
                    writer.WriteLine("  \"maxinteractionreach\": " +
                        MaxInteractionReach.ToString("0.##", CultureInfo.InvariantCulture) + ",");
                    writer.WriteLine("  \"meleereachmax\": " +
                        MeleeMaxReach.ToString("0.##", CultureInfo.InvariantCulture) + ",");
                    writer.WriteLine("  \"meleeprediction\": " + (MeleePrediction ? "true" : "false") + ",");
                    writer.WriteLine("  \"voice\": " + (VoiceEnabled ? "true" : "false") + ",");
                    writer.WriteLine("  \"voicemode\": \"" + (VoicePushToTalk ? "ptt" : "open") + "\",");
                    writer.WriteLine("  \"voicevolume\": " +
                        VoiceVolume.ToString("0.##", CultureInfo.InvariantCulture) + ",");
                    writer.WriteLine("  \"voicequality\": \"" +
                        VoiceProfiles.Name(VoiceQuality) + "\",");
                    writer.WriteLine("  \"voicevadhangover\": " + VoiceVadHangoverMs + ",");
                    writer.WriteLine("  \"verbose\": " + (VerboseLog ? "true" : "false"));
                    writer.WriteLine("}");
                }
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 写配置失败：" + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
