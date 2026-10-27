using System;
using System.Collections.Generic;
using System.Linq;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 联机性别：**协议与纯逻辑用的取值**，与引擎 <c>Game.PlayerClass</c> 一一对应。
    /// 放在这里是为了让身份/外观的编解码与同步策略不依赖 Game 程序集（自检可直接编译）。
    /// Source: Survivalcraft/Game/PlayerClass.cs（Male = 0, Female = 1，数值与协议一致）
    /// </summary>
    public enum PlayerSex : byte
    {
        Male = 0,
        Female = 1
    }

    /// <summary>
    /// 一个玩家的**外观输入**，逐字段对应引擎真实用来建模的材料：
    ///   · <see cref="Sex"/>        → <c>PlayerData.PlayerClass</c>（决定 MalePlayer/FemalePlayer 模板）；
    ///   · <see cref="SkinName"/>   → <c>PlayerData.CharacterSkinName</c>（角色皮肤贴图）；
    ///   · <see cref="Clothes"/>    → <c>ComponentClothing</c> 的 4 个槽位（衣服/帽子/…）；
    ///   · <see cref="SkinSha256"/> → 自定义皮肤资源哈希（内置皮肤为空）。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:376-467
    ///         （CapturePlayerRecord 的 Name/PlayerClass/SkinName/SkinSha256/Clothes 字段）
    /// </summary>
    public sealed class PlayerAppearance
    {
        /// <summary>引擎 <c>ClothingSlot</c> 的数量（Head/Torso/Legs/Feet）。</summary>
        public const int ClothingSlotCount = 4;

        public PlayerSex Sex = PlayerSex.Male;
        public string SkinName = string.Empty;
        public byte[] SkinSha256 = Array.Empty<byte>();
        public int[][] Clothes = CreateEmptyClothes();

        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.GetClothes
        public static int[][] CreateEmptyClothes() =>
            new[]
            {
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()
            };

        public static int[][] CloneClothes(int[][] clothes)
        {
            int[][] result = CreateEmptyClothes();
            if (clothes == null) return result;
            for (int i = 0; i < Math.Min(ClothingSlotCount, clothes.Length); i++)
                result[i] = clothes[i] != null ? (int[])clothes[i].Clone() : Array.Empty<int>();
            return result;
        }

        public PlayerAppearance Clone() => new PlayerAppearance
        {
            Sex = Sex,
            SkinName = SkinName ?? string.Empty,
            SkinSha256 = SkinSha256 != null ? (byte[])SkinSha256.Clone() : Array.Empty<byte>(),
            Clothes = CloneClothes(Clothes)
        };

        /// <summary>
        /// 变化判定用的签名：外观任一字段变了就必须重新下发 / 应用到化身。
        /// 只含会改变模型外观的字段（不含名字；名字单独由 profile 签名带上）。
        /// </summary>
        public string Signature
        {
            get
            {
                var parts = new List<string>(ClothingSlotCount + 2)
                {
                    ((int)Sex).ToString(),
                    SkinName ?? string.Empty
                };
                for (int i = 0; i < ClothingSlotCount; i++)
                {
                    int[] slot = Clothes != null && i < Clothes.Length ? Clothes[i] : null;
                    parts.Add(slot == null ? string.Empty : string.Join(",", slot));
                }
                if (SkinSha256 != null && SkinSha256.Length > 0)
                    parts.Add(Convert.ToHexString(SkinSha256));
                return string.Join("|", parts);
            }
        }

        /// <summary>有皮肤名（内置 "$Male3" 或自定义 "Foo.scskin"）。</summary>
        public bool HasSkinName => !string.IsNullOrWhiteSpace(SkinName);

        /// <summary>是不是自定义皮肤（需要资源下发；内置皮肤由引擎自带）。</summary>
        public bool HasCustomSkin => SkinAssetPolicy.IsCustomSkinName(SkinName);

        /// <summary>
        /// 外观是否可用：内置皮肤只需要名字；自定义皮肤还必须有合法的 32 字节 SHA-256
        /// （资源本身可以稍后由客户端上传 / 主机记录补发 —— 缺资源时接收端显示确定性占位并计数）。
        /// </summary>
        public bool HasUsableSkin =>
            HasSkinName && (!HasCustomSkin || SkinAssetPolicy.IsValidHash(SkinSha256));
    }

    /// <summary>
    /// 一个玩家的完整联机资料：显示名 + 外观。握手、资料广播与角色记录都用这一份。
    /// Source: Mod/ScMultiplayer/Message/PlayerProfileMessage.cs:PlayerProfileMessage
    /// </summary>
    public sealed class PlayerProfileSnapshot
    {
        public string Name = string.Empty;
        public PlayerAppearance Appearance = new PlayerAppearance();

        public PlayerProfileSnapshot Clone() => new PlayerProfileSnapshot
        {
            Name = Name ?? string.Empty,
            Appearance = (Appearance ?? new PlayerAppearance()).Clone()
        };

        public string Signature => (Name ?? string.Empty) + "\n" +
            (Appearance?.Signature ?? string.Empty);

        public bool HasName => !string.IsNullOrWhiteSpace(Name);

        public bool HasUsableAppearance =>
            Appearance != null && Appearance.HasUsableSkin;
    }

    /// <summary>
    /// 主机对一次 Hello 的资料裁决（纯值，方便自检直接构造/断言）。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerHealthAndIngress.cs:941-961
    ///         （有记录 → 直接用记录；没有记录且没有完整资料 → SCMP_PROFILE_REQUIRED）
    /// </summary>
    /// <summary>
    /// 主机角色记录里的"重新进入位置"（位置 + 最后睡觉的复活点）。纯值，不依赖 Engine。
    /// 由 HelloAck 的 assigned_position / assigned_spawn_position 携带；客户端在世界加载后
    /// 把本机角色放到 Position，并把 SpawnPosition 设为最后睡觉的复活点。
    /// </summary>
    public readonly struct PlayerPlacement
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public PlayerPlacement(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        public bool IsFinite =>
            !float.IsNaN(X) && !float.IsInfinity(X) &&
            !float.IsNaN(Y) && !float.IsInfinity(Y) &&
            !float.IsNaN(Z) && !float.IsInfinity(Z);

        public override string ToString() =>
            "(" + X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," +
            Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," +
            Z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    public sealed class HelloProfileDecision
    {
        /// <summary>true = 主机没有这个身份的角色记录、Hello 也没带完整资料 → 客户端必须先建角色。</summary>
        public bool ProfileRequired;

        /// <summary>这个身份在主机世界里有角色记录（老玩家）还是本次新建。</summary>
        public bool HasCharacter;

        /// <summary>身份键（PlayerIdentity.RecordKey 的结果）；日志里用短形式。</summary>
        public string RecordKey = string.Empty;

        /// <summary>主机采用的名字（记录名优先）；空 = 不可用。</summary>
        public string Name = string.Empty;

        /// <summary>主机采用的资料（记录或客户端提交的完整资料）；ProfileRequired 时为空。</summary>
        public PlayerProfileSnapshot Profile;

        /// <summary>
        /// 主机角色记录里是否已经存着这个身份的自定义皮肤资源（HelloAck.assigned_skin_asset_present）。
        /// true => 客户端不需要重新上传，主机会在加入时主动补发；
        /// false => 客户端在握手后显式上传（首次加入 / 记录缺资源时的显式重发）。
        /// </summary>
        public bool AssignedSkinAssetPresent;

        /// <summary>true = 主机角色记录里有可用的重新进入位置（HelloAck.assigned_position）。</summary>
        public bool HasPlacement;

        /// <summary>角色记录里的最后离线位置（客户端把本机角色放到这里）。</summary>
        public PlayerPlacement Position;

        /// <summary>角色记录里的最后睡觉复活点（客户端 PlayerData.SpawnPosition 用它）。</summary>
        public PlayerPlacement SpawnPosition;

        /// <summary>明确原因（profile required / 资料无效 / 记录损坏…）。</summary>
        public string Reason;
    }

    /// <summary>
    /// 主机侧的 Hello 资料闸门。由联机角色模块实现（它拥有角色记录与本地玩家），
    /// 地形层的 <c>TerrainSyncHost</c> 只调用它，不直接读 Game 的记录表。
    ///
    /// 语义照旧实现的 SCMP_PROFILE_REQUIRED：
    ///   · 有记录 → 接受，资料取记录（客户端不能改写老角色）；
    ///   · 没记录 + Hello 带完整资料 → 用资料建记录并接受；
    ///   · 没记录 + Hello 没有完整资料 → ProfileRequired=true，**不启动世界传输**。
    /// </summary>
    public interface IHelloProfileGate
    {
        HelloProfileDecision AcceptHello(int clientId, string identity, string name,
            PlayerProfileSnapshot claimedProfile, bool rejoin);

        /// <summary>主机自己的资料（HelloAck.host_profile；名字/性别/皮肤/衣服）。</summary>
        bool TryGetHostProfile(out PlayerProfileSnapshot profile, out string reason);
    }

    /// <summary>
    /// 客户端本机资料存储：角色创建界面完成后把资料放在这里，
    /// 下一次 Hello（重连）带上它；主机回执的 assigned_profile 也会回填到这里，
    /// 让"客户端自己的资料"始终只有一个来源（不做第二份真相）。
    /// </summary>
    public sealed class LocalPlayerProfileStore
    {
        private PlayerProfileSnapshot m_profile;

        /// <summary>最近一次写入的来源（日志用：character-screen / host-record / local-player）。</summary>
        public string Source { get; private set; } = string.Empty;

        /// <summary>每次写入 +1（引用方据此判断"资料变了"）。</summary>
        public long Revision { get; private set; }

        public bool HasProfile => m_profile != null && m_profile.HasName;

        public PlayerProfileSnapshot Profile => m_profile?.Clone();

        public void Set(PlayerProfileSnapshot profile, string source)
        {
            if (profile == null || !profile.HasName) return;
            m_profile = profile.Clone();
            Source = source ?? string.Empty;
            Revision++;
        }

        public void Clear()
        {
            m_profile = null;
            Source = string.Empty;
            Revision++;
        }
    }
}
