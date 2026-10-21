using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Modules
{
    /// <summary>免疫字段要写入的值类型（纯值；引擎侧按它装箱成对应 CLR 类型）。</summary>
    public enum MirrorHostImmuneValueKind
    {
        Bool,
        Float,
        Int,
        Null
    }

    /// <summary>
    /// 镜像宿主"免伤 / 免病 / 体征归位"要写入的字段清单（**纯逻辑，不依赖 Game**）。
    ///
    /// 产品口径（2026-10-21）：第三分屏的宿主是**调试视图宿主**（引擎本地角色
    /// <c>Mirror#&lt;clientId&gt;</c>），不是玩家自己的角色。它一旦被冻 / 饿 / 打死，
    /// <c>PlayerData</c> 会停在 <c>PlayerDead</c>（该状态只在 <c>GameWidget.Input.Any</c> 时复活，
    /// 而自动宿主的 <c>InputDevice = None</c> ⇒ **永远卡死**，第三分屏黑屏）。
    /// 所以本功能每拍把它的生命 / 疾病 / 体征写回"安全值"：
    ///   · <c>ComponentHealth</c>：血量固定 1 + <c>IsInvulnerable</c> + 清死因 / 死亡时间；
    ///   · <c>ComponentFlu</c> / <c>ComponentSickness</c> / <c>ComponentOnFire</c>：清持续时间 / 因子
    ///     （避免"一复活就带冻流感 / 着火"）；
    ///   · <c>ComponentVitalStats</c>：温度回舒适值（12 / 目标 12）、wetness=0、food/stamina/sleep=1。
    ///
    /// 只对**自动镜像宿主**生效：谁能被写由 <see cref="MirrorHostImmunityTarget"/> 判定
    /// （必须在宿主账本里 + 名字是 <c>&lt;前缀&gt;#&lt;clientId&gt;</c> + 不是联机化身），
    /// 手动角色（h1/h2/H3）与客户端化身**一个字段都不写**。
    ///
    /// 字段名来源（逐个核对过引擎源码）：
    ///   · <c>ComponentHealth</c>：<c>Health</c> / <c>IsInvulnerable</c> / <c>HealthChange</c> /
    ///     <c>CauseOfDeath</c> / <c>DeathTime</c>（均为 <c>{ get; private set; }</c> 属性；
    ///     Source: Survivalcraft/Game/ComponentHealth.cs:36-58）；
    ///   · <c>ComponentFlu</c>：<c>m_fluOnset</c> / <c>m_fluDuration</c> / <c>m_coughDuration</c> /
    ///     <c>m_sneezeDuration</c> / <c>m_blackoutDuration</c> / <c>m_blackoutFactor</c>
    ///     （Source: ComponentFlu.cs:23-33）；
    ///   · <c>ComponentSickness</c>：<c>m_sicknessDuration</c> / <c>m_greenoutDuration</c> /
    ///     <c>m_greenoutFactor</c>（Source: ComponentSickness.cs:21-25）；
    ///   · <c>ComponentOnFire</c>：<c>m_fireDuration</c> / <c>m_fireTouchCount</c>
    ///     （Source: ComponentOnFire.cs:23-29）；
    ///   · <c>ComponentVitalStats</c>：<c>Food</c> / <c>Stamina</c> / <c>Sleep</c> / <c>Temperature</c> /
    ///     <c>Wetness</c>（private setter 会 Saturate / Clamp，见 VitalStats.cs:72-130）+
    ///     <c>m_targetTemperature</c>（Source: ComponentVitalStats.cs:62）。
    ///
    /// 本类型只描述"写什么"，不做任何引擎调用；写入由
    /// <c>Engine/MirrorHostImmunityEngine.cs</c> 走 SuAPI <c>IModParentField</c> 完成。
    /// </summary>
    public static class MirrorHostImmunityFields
    {
        // 组件名（引擎类名）：引擎侧按它把"组件名 → 组件实例"显式映射，未知名字 = 明确失败。
        public const string OwnerHealth = "ComponentHealth";
        public const string OwnerFlu = "ComponentFlu";
        public const string OwnerSickness = "ComponentSickness";
        public const string OwnerOnFire = "ComponentOnFire";
        public const string OwnerVitalStats = "ComponentVitalStats";

        /// <summary>血量目标：>0 才不会让 <c>PlayerData</c> 的 Playing 状态记成死亡。</summary>
        public const float HealthTarget = 1f;

        /// <summary>舒适体温（引擎在非生存模式下也用 12f：Source: ComponentVitalStats.cs:536）。</summary>
        public const float TemperatureTarget = 12f;

        /// <summary>目标体温（温度条档位 / 引擎降温趋势的目标值）。</summary>
        public const float TargetTemperatureTarget = 12f;

        /// <summary>food / stamina / sleep 目标：满值。</summary>
        public const float VitalTarget = 1f;

        /// <summary>wetness 目标：干爽（湿衣服会放大冻伤）。</summary>
        public const float WetnessTarget = 0f;

        /// <summary>
        /// 免疫要写入的**完整字段清单**（顺序即日志顺序，测试按它逐条核对）。
        /// </summary>
        public static readonly MirrorHostImmuneField[] Fields =
        {
            // ---- ComponentHealth：不死 + 无敌 + 清死因 / 死亡时间
            Float(OwnerHealth, "Health", HealthTarget,
                "血量固定 1：>0 才不会被 PlayerData 判成死亡（Playing 状态只在 Health<=0 时进 PlayerDead）"),
            BoolValue(OwnerHealth, "IsInvulnerable", true,
                "ComponentHealth.Injure 直接返回：冻死 / 饿死 / 摔死 / 岩浆 / 近战全部无效"),
            Float(OwnerHealth, "HealthChange", 0f,
                "清掉上一拍的掉血反馈量（HUD 红闪由 MirrorViewFeedback 单独镜像）"),
            Clear(OwnerHealth, "CauseOfDeath",
                "清死因：PlayerDead 进入时读它显示死亡文本"),
            Clear(OwnerHealth, "DeathTime",
                "清死亡时间：PlayerDead 的复活判定读它"),

            // ---- ComponentFlu：清流感（持续时间 / 潜伏 / 咳 / 喷嚏 / 黑屏）
            Float(OwnerFlu, "m_fluOnset", 0f, "清流感潜伏计时（低温累计超过 120s 就会 StartFlu）"),
            Float(OwnerFlu, "m_fluDuration", 0f, "清流感持续时间（HasFlu 的唯一事实来源）"),
            Float(OwnerFlu, "m_coughDuration", 0f, "清咳嗽时长"),
            Float(OwnerFlu, "m_sneezeDuration", 0f, "清喷嚏时长"),
            Float(OwnerFlu, "m_blackoutDuration", 0f, "清流感黑屏时长"),
            Float(OwnerFlu, "m_blackoutFactor", 0f, "清流感黑屏系数"),

            // ---- ComponentSickness：清恶心 / 呕吐
            Float(OwnerSickness, "m_sicknessDuration", 0f, "清疾病持续时间（IsSick 的唯一事实来源）"),
            Float(OwnerSickness, "m_greenoutDuration", 0f, "清绿屏时长"),
            Float(OwnerSickness, "m_greenoutFactor", 0f, "清绿屏系数"),

            // ---- ComponentOnFire：清着火（触发计数也清，避免站在火里 5 拍又被点燃）
            Float(OwnerOnFire, "m_fireDuration", 0f, "清着火持续时间（IsOnFire 的唯一事实来源）"),
            IntValue(OwnerOnFire, "m_fireTouchCount", 0, "清「碰到火」的累计拍数（>=5 会 SetOnFire）"),

            // ---- ComponentVitalStats：体征归位（防饿死 / 冻死 / 过热死）
            Float(OwnerVitalStats, "Food", VitalTarget, "食物满值：饿死路径的充要条件之一"),
            Float(OwnerVitalStats, "Stamina", VitalTarget, "体力满值"),
            Float(OwnerVitalStats, "Sleep", VitalTarget, "睡眠满值（困倦黑屏 / 睡眠不足伤害）"),
            Float(OwnerVitalStats, "Temperature", TemperatureTarget,
                "体温回 12：>6 不会感冒，<=0 会「冻死」（Source: ComponentVitalStats.cs:537-548）"),
            Float(OwnerVitalStats, "Wetness", WetnessTarget, "干爽：湿衣服把保温系数降到 5%，冻伤更快"),
            Float(OwnerVitalStats, "m_targetTemperature", TargetTemperatureTarget,
                "目标体温回 12：引擎每拍按环境重算，写它让温度条档位与体温一致")
        };

        /// <summary>按 <see cref="MirrorHostImmuneField.Key"/> 找一条（找不到 = false）。</summary>
        public static bool TryFind(string key, out MirrorHostImmuneField field)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (!string.Equals(Fields[i].Key, key, StringComparison.Ordinal)) continue;
                field = Fields[i];
                return true;
            }
            field = default(MirrorHostImmuneField);
            return false;
        }

        /// <summary>按组件统计字段数（日志用：<c>ComponentHealth=5 ComponentFlu=6 …</c>）。</summary>
        public static string DescribeCounts()
        {
            var names = new List<string>();
            var counts = new List<int>();
            for (int i = 0; i < Fields.Length; i++)
            {
                int index = names.IndexOf(Fields[i].Component);
                if (index < 0)
                {
                    names.Add(Fields[i].Component);
                    counts.Add(1);
                    continue;
                }
                counts[index] = counts[index] + 1;
            }
            var text = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(names[i]).Append('=').Append(counts[i]);
            }
            return text.ToString();
        }

        /// <summary>整份清单的一行文本（每个字段 <c>组件.成员=值</c>；报告 / 日志用）。</summary>
        public static string Describe()
        {
            var text = new StringBuilder();
            text.Append("fields=").Append(Fields.Length).Append('(').Append(DescribeCounts()).Append(") [");
            for (int i = 0; i < Fields.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(Fields[i].Describe());
            }
            return text.Append(']').ToString();
        }

        private static MirrorHostImmuneField Float(string component, string member, float value,
            string role) =>
            new MirrorHostImmuneField(component, member, MirrorHostImmuneValueKind.Float, false, value,
                0, role);

        private static MirrorHostImmuneField BoolValue(string component, string member, bool value,
            string role) =>
            new MirrorHostImmuneField(component, member, MirrorHostImmuneValueKind.Bool, value, 0f,
                0, role);

        private static MirrorHostImmuneField IntValue(string component, string member, int value,
            string role) =>
            new MirrorHostImmuneField(component, member, MirrorHostImmuneValueKind.Int, false, 0f,
                value, role);

        private static MirrorHostImmuneField Clear(string component, string member, string role) =>
            new MirrorHostImmuneField(component, member, MirrorHostImmuneValueKind.Null, false, 0f, 0,
                role);
    }

    /// <summary>
    /// 一条"要写进去的字段"（纯值）：组件名 / 成员名 / 值类型 / 值 / 为什么写。
    /// 成员既可以是 public 属性（例如 <c>ComponentHealth.Health</c> 的 private setter），
    /// 也可以是 private 字段（例如 <c>ComponentFlu.m_fluDuration</c>）—— 引擎侧统一走
    /// SuAPI <c>IModParentField.ModifyParentField</c>（先找字段、再找属性）。
    /// </summary>
    public readonly struct MirrorHostImmuneField
    {
        private readonly bool m_boolValue;
        private readonly float m_floatValue;
        private readonly int m_intValue;

        public MirrorHostImmuneField(string component, string member, MirrorHostImmuneValueKind kind,
            bool boolValue, float floatValue, int intValue, string role)
        {
            Component = component ?? string.Empty;
            Member = member ?? string.Empty;
            Kind = kind;
            m_boolValue = boolValue;
            m_floatValue = floatValue;
            m_intValue = intValue;
            Role = role ?? string.Empty;
        }

        /// <summary>引擎组件类名（<c>ComponentHealth</c> / <c>ComponentFlu</c> …）。</summary>
        public string Component { get; }

        /// <summary>组件上的成员名（属性名或私有字段名）。</summary>
        public string Member { get; }

        public MirrorHostImmuneValueKind Kind { get; }

        /// <summary>为什么写（报告 / 日志里逐条说明，不用猜）。</summary>
        public string Role { get; }

        /// <summary>清单键（<c>组件.成员</c>；测试与日志都按它定位）。</summary>
        public string Key => Component + "." + Member;

        public bool BoolValue => m_boolValue;

        public float FloatValue => m_floatValue;

        public int IntValue => m_intValue;

        /// <summary>要写进去的值（装箱成对应 CLR 类型；<see cref="MirrorHostImmuneValueKind.Null"/> = null）。</summary>
        public object Value
        {
            get
            {
                switch (Kind)
                {
                    case MirrorHostImmuneValueKind.Bool:
                        return m_boolValue;
                    case MirrorHostImmuneValueKind.Float:
                        return m_floatValue;
                    case MirrorHostImmuneValueKind.Int:
                        return m_intValue;
                    case MirrorHostImmuneValueKind.Null:
                        return null;
                    default:
                        // 未知值类型 = 清单写错了：明确失败，不猜一个默认值写进引擎。
                        throw new InvalidOperationException("未知的免疫字段值类型：" + Kind + "（" + Key + "）");
                }
            }
        }

        public string Describe()
        {
            string value;
            switch (Kind)
            {
                case MirrorHostImmuneValueKind.Bool:
                    value = m_boolValue ? "true" : "false";
                    break;
                case MirrorHostImmuneValueKind.Float:
                    value = m_floatValue.ToString("0.###", CultureInfo.InvariantCulture);
                    break;
                case MirrorHostImmuneValueKind.Int:
                    value = m_intValue.ToString(CultureInfo.InvariantCulture);
                    break;
                case MirrorHostImmuneValueKind.Null:
                    value = "null";
                    break;
                default:
                    value = "?";
                    break;
            }
            return Key + "=" + value;
        }
    }

    /// <summary>
    /// 自动镜像宿主"卡死在死亡状态"的判定（纯逻辑，三条条件**任一命中**即卡死）：
    ///   · <c>ComponentHealth.Health &lt;= 0</c>（引擎 Playing 状态会把这种宿主推进 PlayerDead）；
    ///   · 分屏相机是 <c>DeathCamera</c> 且**不是**"客户端角色死亡"的预期表现
    ///     （PlayerDead 进入时切它，Source: PlayerData.cs:275-277）；
    ///   · <c>PlayerData</c> 状态机当前状态 = <c>PlayerDead</c>
    ///     （该状态只在 <c>GameWidget.Input.Any</c> 时自己复活，而自动宿主 <c>InputDevice=None</c>
    ///     ⇒ 不修就永远卡住，Source: PlayerData.cs:300-321）。
    ///
    /// **为什么相机那条要排除"预期表现"**：replace 模式下客户端角色死亡时，
    /// <c>MirrorCharacterEngine.ValidateBound</c> 会**主动**把第三分屏切到 <c>DeathCamera</c>
    /// （环绕客户端尸体，Source: Engine/MirrorCharacterEngine.cs:729-776），客户端复活时再切回
    /// <c>FppCamera</c>；客户端的死亡屏同样走原生链路（Source: Engine/PlayerVitalsReplica.cs:540、:863）。
    /// 若把这种相机当成宿主卡死，客户端**每次死亡**都会触发"解绑 + RemoveEntity"复活，
    /// 第三屏在客户端死亡期间反复重建 —— 那正是要修掉的抖动。宿主自己真死时，
    /// 另外两条（health≤0 / PlayerData=PlayerDead）仍然命中。
    /// </summary>
    public static class MirrorHostStuck
    {
        public const string ReasonHealth = "health<=0";
        public const string ReasonDeathCamera = "camera=DeathCamera";
        public const string ReasonPlayerDead = "PlayerData=PlayerDead";

        /// <summary>
        /// 这台 <c>DeathCamera</c> 是否是"客户端角色死亡"的预期表现（= 绑定在这个宿主上 + 客户端
        /// 化身的 <c>ComponentHealth.Health &lt;= 0</c>）。true = 不按宿主卡死处理。
        /// </summary>
        public static bool IsClientDeathPresentation(bool boundToThisHost, bool clientAvatarDead) =>
            boundToThisHost && clientAvatarDead;

        /// <summary>
        /// 三条条件任一命中即卡死；<paramref name="detail"/> 列出命中哪几条（可直接定位）。
        /// 这个重载不区分"相机是不是客户端死亡的预期表现"（等价于第 4 个参数传 false）——
        /// 引擎侧用带过滤的重载，纯逻辑测试两个都覆盖。
        /// </summary>
        public static bool IsStuck(bool healthAtOrBelowZero, bool deathCamera, bool playerDataDead,
            out string detail) =>
            IsStuck(healthAtOrBelowZero, deathCamera, false, playerDataDead, out detail);

        /// <summary>带"客户端死亡表现"过滤的卡死判定（引擎侧用这个重载）。</summary>
        public static bool IsStuck(bool healthAtOrBelowZero, bool deathCamera,
            bool clientDeathPresentation, bool playerDataDead, out string detail)
        {
            detail = null;
            bool stuckCamera = deathCamera && !clientDeathPresentation;
            if (!healthAtOrBelowZero && !stuckCamera && !playerDataDead) return false;
            var text = new StringBuilder("卡死条件命中：");
            bool first = true;
            if (healthAtOrBelowZero)
            {
                text.Append(ReasonHealth);
                first = false;
            }
            if (stuckCamera)
            {
                if (!first) text.Append(" + ");
                text.Append(ReasonDeathCamera);
                first = false;
            }
            if (playerDataDead)
            {
                if (!first) text.Append(" + ");
                text.Append(ReasonPlayerDead);
            }
            detail = text.ToString();
            return true;
        }
    }

    /// <summary>
    /// "谁可以被免疫 / 卡死修复"的判定（纯逻辑）：**只允许自动镜像宿主**。
    /// 三道闸门缺一不可：
    ///   · 必须在宿主账本里（<c>m_sessionOwned</c>，本功能创建 / 收编的）；
    ///   · 名字必须是本功能的自动命名 <c>&lt;前缀&gt;#&lt;clientId&gt;</c>，**或者**它在世界存档里带着
    ///     本功能的自动宿主标记（<c>SuSubsystemPlayers.MarkAutoHost</c>，两种证据任一成立即可；
    ///     手动角色 h1/h2/H3 两种都不满足 ⇒ 一个字段都不写）；
    ///   · 绝不能是联机化身（客户端角色，它的生命由客户端自己权威）。
    /// </summary>
    public static class MirrorHostImmunityTarget
    {
        /// <summary>是否可以维护（false = <paramref name="reason"/> 写清是哪道闸门拒的）。</summary>
        public static bool IsEligible(bool sessionOwned, bool autoName, bool isNetworkAvatar,
            out string reason) =>
            IsEligible(sessionOwned, autoName, autoHostFlag: false, isNetworkAvatar, out reason);

        /// <summary>
        /// 带"自动宿主标记"的判别（引擎侧用这个重载）：<paramref name="autoHostFlag"/> = 世界存档里
        /// 本功能的 <c>MarkAutoHost</c> 标记（自动创建 / 收编时写入）。
        /// 名字前缀与存档标记是**两种证据**：显式名字（<c>mirrorcharacter</c>）收编的 auto 宿主
        /// 名字可能不满足 <c>&lt;前缀&gt;#&lt;N&gt;</c>，但标记仍在 ⇒ 也算自动宿主。
        /// </summary>
        public static bool IsEligible(bool sessionOwned, bool autoName, bool autoHostFlag,
            bool isNetworkAvatar, out string reason)
        {
            reason = null;
            if (isNetworkAvatar)
            {
                reason = "目标是联机化身（客户端角色）→ 绝不写它的生命 / 体征";
                return false;
            }
            if (!sessionOwned)
            {
                reason = "目标不在镜像宿主账本里（手动角色 h1/h2/H3 一律不动）";
                return false;
            }
            if (!autoName && !autoHostFlag)
            {
                reason = "目标既不是自动命名（<前缀>#<clientId>），也没有自动宿主标记" +
                    "（MarkAutoHost）→ 按手动角色处理，不动";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 一拍"自动宿主维护"的增量结果（纯值）：引擎侧逐宿主累加，控制器按拍汇总进
    /// <c>MirrorHostCounters</c>（状态行键 <c>mirrorHostImmune</c> / <c>mirrorHostRevived</c> /
    /// <c>mirrorHostStuckDead</c> / <c>mirrorHostImmunityFailures</c> /
    /// <c>mirrorHostReviveFailures</c>）。
    /// 口径：
    ///   · <see cref="Immune"/> = 本拍成功写入全部免疫字段的**宿主数**（不是字段数）；
    ///   · <see cref="StuckDetected"/> = 本拍命中卡死条件的宿主数；
    ///   · <see cref="Revived"/> / <see cref="ReviveFailures"/> = 触发引擎复活（RemoveEntity）成功 / 失败；
    ///   · <see cref="AwaitingSpawn"/> = 账本里的宿主还没出生（ComponentPlayer == null，等引擎状态机）。
    /// </summary>
    public struct MirrorHostMaintenance
    {
        public int Immune;
        public int ImmunityFailures;
        public int StuckDetected;
        public int Revived;
        public int ReviveFailures;
        public int AwaitingSpawn;

        public void CountImmune() => Immune++;

        public void CountImmunityFailure() => ImmunityFailures++;

        public void CountStuck() => StuckDetected++;

        public void CountRevived() => Revived++;

        public void CountReviveFailure() => ReviveFailures++;

        public void CountAwaitingSpawn() => AwaitingSpawn++;

        /// <summary>日志片段（状态行之外的单行摘要）。</summary>
        public readonly string Describe() =>
            "immune=" + Immune + " immunityFailures=" + ImmunityFailures +
            " stuck=" + StuckDetected + " revived=" + Revived +
            " reviveFailures=" + ReviveFailures + " awaitingSpawn=" + AwaitingSpawn;
    }
}
