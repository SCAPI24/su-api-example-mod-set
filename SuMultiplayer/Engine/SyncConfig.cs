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
        public bool VerboseLog = true;
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

        // —— 调试镜像（debug-only 主机端分屏镜像；默认关闭）——
        /// <summary>
        /// 调试镜像配置键（**默认关闭**）：空串 / false = 关闭；true = 自动跟随第一个远端角色
        /// （槽位 0）；<c>&lt;clientId&gt;</c> 或 <c>&lt;clientId&gt;#&lt;slot&gt;</c> = 指定目标。
        /// 解析 / 生命周期 / 计数在 <c>Modules/DebugMirrorPorts.cs</c>（纯逻辑），引擎动作在
        /// <c>Engine/DebugMirrorClient.cs</c>；本类只保存原始值，让日志如实显示用户写了什么。
        /// </summary>
        public string DebugMirrorClient = "";

        // —— 调试镜像角色（debug-only 主机本地角色复制；默认关闭）——
        /// <summary>
        /// 调试镜像角色：主机本地角色的 <c>PlayerData.Name</c>（大小写不敏感；产品口径 = "H3"）。
        /// 空 = 关闭（默认）。解析 / 生命周期 / 计数在 <c>Modules/DebugMirrorCharacterPorts.cs</c>，
        /// 引擎动作在 <c>Engine/MirrorCharacterEngine.cs</c>；本类只保存原始值，让日志如实显示用户写了什么。
        /// </summary>
        public string MirrorCharacter = "";
        /// <summary>
        /// 镜像哪个客户端：空 / auto / first / true / on / 0 = 第一个加入的客户端；
        /// <c>&lt;clientId&gt;</c>（&gt; 0）= 指定连接（槽位固定 0）。原始值原样保存，解析在纯逻辑里。
        /// </summary>
        public string MirrorCharacterClient = "";
        /// <summary>
        /// 镜像模式：<c>replace</c>（默认）= 用 H3 的第三个分屏显示客户端角色本身
        /// （H3 的身体从世界表现里摘掉、不复制状态）；<c>copy</c> = 旧行为（把客户端状态逐拍复制到 H3 身上）。
        /// 空 = 默认 replace。原始值原样保存，解析 / 拒绝理由在 <c>Modules/DebugMirrorCharacterPorts.cs</c>。
        /// </summary>
        public string MirrorCharacterMode = "";

        // —— 镜像宿主（调试功能：客户端加入时主机自动创建真实本地角色；**默认开启**）——
        /// <summary>
        /// <c>mirrorcharacterauto</c>（bool，默认 **true**）：主机为每个加入的客户端自动创建一个
        /// **真实的引擎本地角色**（= 第 2/3/4 个分屏，带引擎自带的触控控件 / HUD / 界面镜像 /
        /// 睡眠与受击表现），并把该分屏的相机绑到客户端的第一人称（replace）。
        /// 同屏可用池 = 4 − 主机实际本地角色数（<c>PlayersData.Count</c>，远端化身不占池）；
        /// 池满时明确拒绝（<c>mirrorHostBlocked(max-players)</c>），绝不顶掉已有角色。
        /// 纯逻辑 = <c>Modules/MirrorHostPorts.cs</c>，引擎动作 = <c>Engine/MirrorHostEngine.cs</c>。
        /// </summary>
        public bool MirrorCharacterAuto = true;
        /// <summary>
        /// <c>mirrorhostnameprefix</c>（默认 <c>"Mirror"</c>）：自动角色命名
        /// <c>&lt;前缀&gt;#&lt;clientId&gt;</c>，重名依次追加 <c>-2</c> / <c>-3</c>…。
        /// 空 = 用默认前缀；带控制字符 / 超过 16 字符 = 配置写错（功能保持关闭 + 明确告警）。
        /// </summary>
        public string MirrorHostNamePrefix = MirrorHostSettings.DefaultNamePrefix;
        /// <summary><c>mirrorhostnameprefix</c> 是否在配置文件里显式出现（没有 → 用默认前缀，模板里会写出来）。</summary>
        private bool MirrorHostNamePrefixConfigured;
        /// <summary><c>mirrorhostnameprefix</c> 是否在配置文件里显式出现（没写 → 模板里补出来）。</summary>
        public bool IsMirrorHostNamePrefixConfigured => MirrorHostNamePrefixConfigured;

        // —— 自动客户端分屏（主机为加入的客户端自动开一个第一人称分屏；**默认开启**）——
        /// <summary>
        /// <c>clientview</c>（bool，默认 true）：主机在**有空槽**时为每个加入的客户端自动建一个
        /// camera-only 分屏视图（第一人称，与客户端自己看到的一致），客户端离开 / 离开房间 /
        /// 世界卸载 / 本键关闭时自动拆除。视图不是真实角色：不加入 Project、不存盘、不碰撞、
        /// 不可攻击。上限 = 本机角色 + 联机化身 + 视图 ≤ 4（引擎 PlayerIndex / GameWidget 上限）。
        /// 解析 / 生命周期 / 计数在 <c>Modules/ClientViewPorts.cs</c>，引擎动作在
        /// <c>Engine/ClientViewEngine.cs</c>。
        /// </summary>
        public bool ClientView = true;
        /// <summary>
        /// <c>clientviewclient</c>（可选过滤器）：留空 / auto / all / first = 每个有角色的客户端各一个视图；
        /// <c>&lt;clientId&gt;</c>（&gt; 0）= 只为这个客户端建视图。写错 = 功能保持关闭 + 明确告警
        /// （绝不静默当成"全部客户端"）。原始值原样保存，解析在纯逻辑里。
        /// </summary>
        public string ClientViewClient = "";

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
            VoiceProfiles.Name(VoiceQuality) + " vadHangover=" + VoiceVadHangoverMs + "ms" +
            " debugMirror=" + (string.IsNullOrEmpty(DebugMirrorClient) ? "off" : DebugMirrorClient) +
            " mirrorCharacter=" + (string.IsNullOrEmpty(MirrorCharacter) ? "off" : MirrorCharacter) +
            " mirrorCharacterClient=" +
            (string.IsNullOrEmpty(MirrorCharacterClient) ? "auto" : MirrorCharacterClient) +
            " mirrorCharacterMode=" +
            (string.IsNullOrEmpty(MirrorCharacterMode) ? "replace" : MirrorCharacterMode) +
            " mirrorCharacterAuto=" + (MirrorCharacterAuto ? "on" : "off") +
            " mirrorHostNamePrefix=\"" + MirrorHostNamePrefix + "\"" +
            " clientView=" + (ClientView ? "on" : "off") +
            " clientViewClient=" +
            (string.IsNullOrEmpty(ClientViewClient) ? "all" : ClientViewClient);

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
                    config.WriteTemplate(path);
                    note = "配置不存在，已写入模板：" + path;
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

        private void WriteTemplate(string path)
        {
            try
            {
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  // 这里只有参数：建房/加入都由游戏内操作触发（CR 按钮 / Play 屏房间行）");
                    writer.WriteLine("  \"port\": 26760,");
                    writer.WriteLine("  \"discoveryport\": 26761,");
                    writer.WriteLine("  \"room\": \"SuMP Room\",");
                    writer.WriteLine("  \"radius\": 3,");
                    writer.WriteLine("  \"maxplayers\": 8,");
                    writer.WriteLine("  // 主机交互距离上限（米，5..64）：allowed = min(客户端 reach + 0.5, 本值)");
                    writer.WriteLine("  \"maxinteractionreach\": 16,");
                    writer.WriteLine("  // 近战攻击距离上限（米，5..64）：allowed = min(请求 reach + 0.5, 本值, 主机引擎 reach)；");
                    writer.WriteLine("  // 不写这一项 = 跟随 maxinteractionreach（默认 16）");
                    writer.WriteLine("  \"meleereachmax\": 16,");
                    writer.WriteLine("  // 客户端是否展示乐观命中标记（红色粒子）；伤害数字 / 生命永远等主机结果");
                    writer.WriteLine("  \"meleeprediction\": true,");
                    writer.WriteLine("  // 实时语音：8 kHz；voicequality = high(4-bit) / standard(3-bit) / low(2-bit, 默认) / ultra(1-bit CVSD)");
                    writer.WriteLine("  \"voice\": true,");
                    writer.WriteLine("  \"voicemode\": \"open\",");
                    writer.WriteLine("  \"voicevolume\": 1.0,");
                    writer.WriteLine("  \"voicequality\": \"low\",");
                    writer.WriteLine("  // VAD 挂起毫秒（默认 160）：说完后仍发包的时长，越小空闲包越少");
                    writer.WriteLine("  \"voicevadhangover\": 160,");
                    writer.WriteLine("  // 调试镜像（debug-only，默认关闭）：true = 自动跟随第一个远端角色；");
                    writer.WriteLine("  // <clientId> 或 <clientId>#<slot> = 指定目标；游戏内 MIR 按钮可循环目标");
                    writer.WriteLine("  \"debugmirrorclient\": false,");
                    writer.WriteLine("  // 调试镜像角色（debug-only，默认关闭）：主机本地角色名（例 \"H3\"），空 = 关闭；");
                    writer.WriteLine("  // 默认 replace = 用该角色的第三个分屏显示指定客户端角色本身（该角色的身体从世界里摘掉、");
                    writer.WriteLine("  // 不渲染 / 不可碰撞；不复制背包/生命/动作）；copy = 旧行为（把客户端状态逐拍复制到该角色身上）");
                    writer.WriteLine("  \"mirrorcharacter\": \"\",");
                    writer.WriteLine("  // 镜像哪个客户端：留空 = 第一个加入的客户端；或填 clientId（>0）");
                    writer.WriteLine("  \"mirrorcharacterclient\": \"\",");
                    writer.WriteLine("  // 镜像模式：留空 / replace（默认）= 第三分屏显示客户端角色本身；copy = 复制状态到本机角色身上");
                    writer.WriteLine("  \"mirrorcharactermode\": \"\",");
                    writer.WriteLine("  // 联机调试：客户端加入时主机自动创建真实的本地角色（默认开启）；");
                    writer.WriteLine("  // 同屏可用池 = 4 − 主机实际本地角色数；池满明确拒绝，绝不顶掉已有角色；");
                    writer.WriteLine("  // 离开时只移除本功能自动创建的角色（手动角色不受影响）");
                    writer.WriteLine("  \"mirrorcharacterauto\": true,");
                    writer.WriteLine("  // 自动角色名前缀：<前缀>#<clientId>，重名追加 -2/-3…");
                    writer.WriteLine("  \"mirrorhostnameprefix\": \"Mirror\",");
                    writer.WriteLine("  // 自动客户端分屏（默认开启）：主机有空槽时为每个加入的客户端开一个第一人称分屏；");
                    writer.WriteLine("  // 上限 = 本机角色 + 联机化身 + 视图 ≤ 4，没空槽只记 clientViewNoSlot 不崩溃");
                    writer.WriteLine("  \"clientview\": true,");
                    writer.WriteLine("  // 可选过滤器：留空 / auto / all = 每个客户端；或填 clientId（>0）只给该客户端建视图");
                    writer.WriteLine("  \"clientviewclient\": \"\",");
                    writer.WriteLine("  \"verbose\": true");
                    writer.WriteLine("}");
                }
            }
            catch (Exception e)
            {
                Log.Error("[SuMP] 写配置模板失败：" + e.GetType().Name + " " + e.Message + " @ " + path);
            }
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
                case "debugmirrorclient":
                    // 原始值原样保存：解析 / 拒绝理由由 DebugMirrorController 负责（配错时日志里
                    // 有一条可直接定位的告警，功能保持关闭）；这里不猜目标、也不静默夹紧。
                    config.DebugMirrorClient = value == null ? string.Empty : value.Trim();
                    break;
                case "mirrorcharacter":
                    // 原始值原样保存：空 = 关闭；名字大小写不敏感匹配由 MirrorCharacterController 负责。
                    config.MirrorCharacter = value == null ? string.Empty : value.Trim();
                    break;
                case "mirrorcharacterclient":
                    // 原始值原样保存：auto / first / <clientId> 的解析在纯逻辑里（写错 = 功能关闭 + 明确告警）。
                    config.MirrorCharacterClient = value == null ? string.Empty : value.Trim();
                    break;
                case "mirrorcharactermode":
                    // 原始值原样保存：replace（默认）/ copy 的解析在纯逻辑里（写错 = 功能关闭 + 明确告警）。
                    config.MirrorCharacterMode = value == null ? string.Empty : value.Trim();
                    break;
                case "mirrorcharacterauto":
                case "mirrorhost":
                    // 默认 true：只有显式 false / off / no / 0 才关闭（关闭 = 一个引擎调用都不发生）。
                    config.MirrorCharacterAuto = !string.Equals(value, "false",
                        StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "mirrorhostnameprefix":
                    // 原始值原样保存：空 / 控制字符 / 超长的解析在 MirrorHostSettings（写错 = 功能关闭 + 明确告警）。
                    config.MirrorHostNamePrefix = value == null ? string.Empty : value.Trim();
                    config.MirrorHostNamePrefixConfigured = true;
                    break;
                case "clientview":
                case "clientviewenabled":
                    // 默认 true：只有显式 false / off / no / 0 才关闭（关掉 = 一个引擎调用都不发生）。
                    config.ClientView = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) && value != "0";
                    break;
                case "clientviewclient":
                    // 原始值原样保存：all / auto / <clientId> 的解析在 ClientViewController（写错 = 功能关闭 + 明确告警）。
                    config.ClientViewClient = value == null ? string.Empty : value.Trim();
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
                    writer.WriteLine("  \"debugmirrorclient\": " +
                        (string.IsNullOrEmpty(DebugMirrorClient) ? "false" : "\"" + DebugMirrorClient + "\"") + ",");
                    writer.WriteLine("  \"mirrorcharacter\": " +
                        (string.IsNullOrEmpty(MirrorCharacter) ? "\"\"" : "\"" + MirrorCharacter + "\"") + ",");
                    writer.WriteLine("  \"mirrorcharacterclient\": \"" + MirrorCharacterClient + "\",");
                    writer.WriteLine("  \"mirrorcharactermode\": \"" + MirrorCharacterMode + "\",");
                    writer.WriteLine("  \"mirrorcharacterauto\": " +
                        (MirrorCharacterAuto ? "true" : "false") + ",");
                    writer.WriteLine("  \"mirrorhostnameprefix\": \"" + MirrorHostNamePrefix + "\",");
                    writer.WriteLine("  \"clientview\": " + (ClientView ? "true" : "false") + ",");
                    writer.WriteLine("  \"clientviewclient\": \"" + ClientViewClient + "\",");
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
