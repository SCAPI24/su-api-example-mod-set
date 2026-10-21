using System;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 第三分屏（H3 的 <c>GameWidget</c>）**客户端受伤 / 体征 / 死亡反馈的纯映射**：
    /// 不引用 <c>Game</c>，自检（P:\tmp\tsverify）直接编译本文件。
    ///
    /// 为什么需要它（引擎事实，逐条已核对源码）：
    ///   · 掉血反馈只在 <c>ComponentHealth.Update</c> 里产生，并且写的是**该 ComponentHealth 所属玩家自己**的
    ///     <c>ComponentGui</c> / <c>ComponentScreenOverlays</c>：
    ///       <c>HealthChange = Health - m_lastHealth</c>（<c>ComponentHealth.cs:251</c>）；
    ///       <c>m_redScreenFactor += -4f * HealthChange</c>（<c>:256</c>）；
    ///       <c>HealthBarWidget.Flash(MathUtils.Clamp((int)((0f - HealthChange) * 30f), 0, 10))</c>（<c>:257</c>）；
    ///       <c>RedoutFactor = MathUtils.Max(RedoutFactor, m_redScreenFactor)</c>（<c>:261</c>）；
    ///       <c>HealthBarWidget.Value = Health</c>（<c>:265</c>）。
    ///     replace 模式把第三分屏的相机绑到客户端的 <c>ComponentPlayer</c>，但 HUD 仍属于视图所有者 H3，
    ///     而 H3 的 <c>ComponentHealth.Update</c> 在绑定期间被摘掉（<c>MirrorCharacterEngine.TryBind</c>）——
    ///     所以客户端掉血不会让第三分屏闪红、血条也不会动。本类把这条链在视图所有者身上**重放一遍**，
    ///     只产生"要写哪些显示值"的决定，实际写控件的活由 <see cref="MirrorViewFeedback"/> 做。
    ///   · <c>m_redScreenFactor</c> 每拍先按 <c>MathUtils.Pow(0.2f, dt)</c> 衰减、≤0.01 归零（<c>ComponentHealth.cs:141-147</c>）；
    ///     红屏 <c>RedoutFactor</c> / 黑屏 <c>BlackoutFactor</c> / 冰冻 <c>IceFactor</c> 都被
    ///     <c>ComponentScreenOverlays.Update</c> 每拍清 0（<c>:60-77</c>，UpdateOrder.Reset），所以只要还有值就必须每拍重写。
    ///   · 食物条 = <c>ComponentVitalStats.UpdateFood</c> 每拍写 <c>FoodBarWidget.Value = Food</c>（<c>:345</c>）；
    ///     温度条**不是进度条**：XML 里 <c>Value=1</c>，变的只有 <c>BarSubtexture</c>（<c>:589-616</c>，
    ///     档位阈值 22/18/14/10/6/2 全部严格大于）；冰冻覆盖 <c>IceFactor = Saturate(1 - Temperature/6)</c>（<c>:579</c>）；
    ///     等级文字 = <c>ComponentLevel.Update</c> 的 <c>"Level " + MathUtils.Floor(PlayerData.Level)</c>（<c>:335</c>，边沿触发）。
    ///   · 死亡表现 = 黑屏 + <c>ComponentGui.DisplayLargeMessage</c>（<c>PlayerData.cs:275-300</c>：
    ///     大标题 "YOU HAVE DIED"、小字 "Cause of death: …"、30 秒 / 1.5 秒延迟）。
    ///
    /// 明确的"不做"：不写 <c>ComponentHealth</c> / <c>ComponentVitalStats</c> / <c>PlayerData</c> 的任何字段
    /// （没有 Injure / 没有 setter / 没有背包复制），不切相机（第三分屏的 ActiveCamera 必须留在客户端第一人称，
    /// 否则 <c>MirrorCharacterEngine.ValidateBound</c> 会把相机重新对回客户端）。
    ///
    /// Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update:139-265
    /// Source: Survivalcraft/Game/ComponentScreenOverlays.cs:ComponentScreenOverlays.Update:60-77
    /// Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateFood:345 / UpdateTemperature:579,589-616
    /// Source: Survivalcraft/Game/ComponentLevel.cs:ComponentLevel.Update:335
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead:275-300
    /// Source: Pak/Widgets/GameWidget.xml:51-54（HealthBar / TemperatureBar / LevelLabel / FoodBar）
    /// </summary>
    public static class MirrorViewFeedbackMath
    {
        /// <summary>红屏因子衰减到该值以下即归零（引擎 <c>ComponentHealth.Update:141-147</c>）。</summary>
        public const float RedoutCutoff = 0.01f;

        /// <summary>每 1.0 点掉血增加的红屏因子（引擎 <c>-4f * HealthChange</c>，而 <c>HealthChange = -drop</c>）。</summary>
        public const float RedFactorPerHealth = 4f;

        /// <summary>血条闪烁强度系数（引擎 <c>(0f - HealthChange) * 30f</c>）。</summary>
        public const float FlashStrengthPerHealth = 30f;

        /// <summary>血条闪烁上限（引擎 <c>MathUtils.Clamp(..., 0, 10)</c>）。</summary>
        public const int MaxHealthBarFlash = 10;

        /// <summary>HUD 显示值去重阈值：同值不重复写控件（"diff-based"）。</summary>
        public const float HudEpsilon = 0.0001f;

        /// <summary>
        /// 覆盖层"画不画"的截断（引擎 <c>ComponentScreenOverlays.Draw</c> 对
        /// Blackout / Greenout / Redout 都是 <c>&gt; 0.01f</c> 才画：<c>:89-118</c>）。
        /// 冰覆盖不在此列（<c>IceFactor &gt; 0</c> 就画）。
        /// </summary>
        public const float OverlayCutoff = 0.01f;

        /// <summary>非有限值（NaN / ±Inf）—— 一律明确失败，绝不写进引擎控件。</summary>
        public static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// 一次掉血要加的红屏因子：引擎 <c>m_redScreenFactor += -4f * HealthChange</c>，
        /// 其中 <c>HealthChange = Health - m_lastHealth = -drop</c>，所以等价于 <c>4f * drop</c>。
        /// 掉血 &lt;= 0 / 非有限值一律 0（加血不加红屏）。
        /// </summary>
        public static float RedFactorIncrement(float healthDrop)
        {
            if (!(healthDrop > 0f) || !IsFinite(healthDrop)) return 0f;
            return RedFactorPerHealth * healthDrop;
        }

        /// <summary>
        /// 血条闪烁次数：引擎 <c>MathUtils.Clamp((int)((0f - HealthChange) * 30f), 0, 10)</c>
        /// = <c>Clamp((int)(drop * 30f), 0, 10)</c>（float→int 截断，然后夹到 0..10）。
        /// </summary>
        public static int HealthBarFlashCount(float healthDrop)
        {
            if (!(healthDrop > 0f) || !IsFinite(healthDrop)) return 0;
            if (healthDrop * FlashStrengthPerHealth >= MaxHealthBarFlash) return MaxHealthBarFlash;
            int count = (int)(healthDrop * FlashStrengthPerHealth);
            if (count < 0) return 0;
            return count > MaxHealthBarFlash ? MaxHealthBarFlash : count;
        }

        /// <summary>
        /// 红屏因子每拍衰减：引擎 <c>if (m_redScreenFactor &gt; 0.01f) m_redScreenFactor *= Pow(0.2f, dt); else 0f;</c>
        /// （<c>ComponentHealth.cs:141-147</c>）。非有限值按 0 处理（调用方已保证输入有限）。
        /// </summary>
        public static float DecayRedFactor(float factor, float dt)
        {
            if (!IsFinite(factor) || !IsFinite(dt)) return 0f;
            if (!(factor > RedoutCutoff)) return 0f;
            return factor * MathUtils.Pow(0.2f, dt);
        }

        /// <summary>两个 HUD 显示值是否视为同一个（不重复写控件）。</summary>
        public static bool Near(float left, float right) =>
            Math.Abs(left - right) <= HudEpsilon;

        /// <summary>等级文字口径：引擎 <c>"Level " + MathUtils.Floor(PlayerData.Level)</c>（<c>ComponentLevel.cs:335</c>）。</summary>
        public static int LevelOf(float level) => (int)MathUtils.Floor(level);

        /// <summary>等级文字（与引擎完全同一个字符串）。</summary>
        public static string LevelText(int level) => "Level " + level;
    }

    /// <summary>
    /// 第三分屏冰覆盖的**数据源标记**（实机问题 1，状态行 <c>viewIceSource</c> 直接输出这些稳定键）。
    ///
    /// 唯一合法来源 = <see cref="Authority"/>：主机权威冻结进度（vitals.proto 字段 22，
    /// 与客户端收到的完全同一个 float）。旧实现按 <c>vitals.Temperature</c> 自己算
    /// <c>Saturate(1 - T/6)</c>，与客户端不是同一条数据 / 时序（客户端走 95 + 插值），
    /// 于是"客户端先冻、第三屏晚很多、屏幕占比不一样"。
    /// </summary>
    public static class MirrorViewIceSource
    {
        /// <summary>主机权威 freeze_progress（95 字段 22）—— 唯一正常来源。</summary>
        public const string Authority = "authority";

        /// <summary>角色死亡：与客户端一致（<c>PlayerVitalsReplica.ApplyLocalHud</c> 的 <c>state.Freeze.Reset()</c>）立刻归零。</summary>
        public const string AuthorityDead = "authority-dead";

        /// <summary>主机还没抓到冻结进度：明确跳过并计数，不得用温度 / 0 顶替。</summary>
        public const string Missing = "authority-missing";

        /// <summary>未接入 / 未驱动（状态行初始值）。</summary>
        public const string None = "none";
    }

    /// <summary>
    /// 组合根 → 第三分屏的**主机权威冻结采样**（只读值类型；<c>PlayerVitalsReplica.TryGetAuthorityFreeze</c>
    /// 的结果）。<see cref="Available"/> 为 false 时 <see cref="Reason"/> 写清为什么拿不到。
    /// </summary>
    public readonly struct MirrorViewFreezeSample
    {
        public MirrorViewFreezeSample(bool available, float progress, uint stage, string reason)
        {
            Available = available;
            Progress = progress;
            Stage = stage;
            Reason = reason;
        }

        /// <summary>这一拍是否拿到了主机权威冻结进度。</summary>
        public bool Available { get; }

        /// <summary>主机权威 freeze_progress（0..1，95 字段 22 的原值）。</summary>
        public float Progress { get; }

        /// <summary>主机权威 freeze_stage（95 字段 23）。</summary>
        public uint Stage { get; }

        /// <summary>不可用原因（稳定键 + 明细；可用时为 null）。</summary>
        public string Reason { get; }

        public string Describe() =>
            Available
                ? "progress=" + Progress.ToString("F4") + " stage=" + Stage
                : "unavailable(" + (Reason ?? "no-reason") + ")";
    }

    /// <summary>
    /// 一帧的冰覆盖决定（实机问题 1）：<see cref="Value"/> 是**每帧逼近**主机权威
    /// <see cref="Progress"/> 之后的插值（与客户端同一个 <see cref="PlayerPresentationInterpolator"/>：
    /// 时间常数 0.2 s / 限速 3 每秒 / 0.002 内直接贴合），绝不阶跃写入。
    /// </summary>
    public readonly struct MirrorViewIceStep
    {
        public MirrorViewIceStep(bool advanced, bool write, float progress, uint stage, float value,
            bool snapped, string source, string error)
        {
            Advanced = advanced;
            Write = write;
            Progress = progress;
            Stage = stage;
            Value = value;
            Snapped = snapped;
            Source = source;
            Error = error;
        }

        /// <summary>本拍推进了插值器（有权威目标；缺失 / 失败时 false）。</summary>
        public bool Advanced { get; }

        /// <summary>本拍要写 <c>IceFactor</c>（引擎每拍清 0，所以只要 &gt; 0 就必须每拍写）。</summary>
        public bool Write { get; }

        /// <summary>主机权威 freeze_progress（目标值）。</summary>
        public float Progress { get; }

        /// <summary>主机权威 freeze_stage。</summary>
        public uint Stage { get; }

        /// <summary>本拍的插值结果（要写进 <c>IceFactor</c> 的值）。</summary>
        public float Value { get; }

        /// <summary>本拍是"直接贴合"（首次播种 / 差分 ≤ <see cref="PlayerPresentationInterpolator.SnapEpsilon"/> / 死亡归零）。</summary>
        public bool Snapped { get; }

        /// <summary>数据源（<see cref="MirrorViewIceSource"/> 的稳定键）。</summary>
        public string Source { get; }

        /// <summary>非空 = 本拍冰覆盖不可用（明确失败原因；绝不静默跳过）。</summary>
        public string Error { get; }

        public string Describe() =>
            "ice{src=" + (Source ?? "none") + " advanced=" + (Advanced ? "1" : "0") +
            " progress=" + Progress.ToString("F4") + " stage=" + Stage +
            " value=" + Value.ToString("F4") + " write=" + (Write ? "1" : "0") +
            " snap=" + (Snapped ? "1" : "0") + " error=" + (Error ?? "none") + "}";
    }

    /// <summary>
    /// 第三分屏病程 / 火焰表现的**纯状态机**（实机问题 2 的"呈现哪一份"部分）：
    /// 输入主机侧客户端化身的**权威表现因子**，输出这一拍要写哪些覆盖层与计数语义。
    ///
    /// 为什么读主机侧化身的因子（而不是重放 109 事件；选型理由见
    /// <see cref="MirrorViewFeedback"/> 的类型注释）：
    ///   · <c>ComponentFlu.m_blackoutFactor</c> / <c>ComponentSickness.m_greenoutFactor</c> 是**主机
    ///     自己在模拟**的表现值：引擎已按 <c>0.5/s</c> 渐入到 0.95 / <c>0.5/s</c> 渐出算好
    ///     （<c>ComponentFlu.cs:145-159</c>、<c>ComponentSickness.cs:112-131</c>），与客户端
    ///     <c>PlayerVitalsReplica.AdvanceLocalEffectPresentation</c> 的曲线逐项相同；
    ///   · 但这两个因子只写在**客户端化身自己的** <c>ComponentScreenOverlays</c> 上，而
    ///     <c>ComponentScreenOverlays.Draw</c> 只在 <c>m_componentPlayer.GameWidget == camera.GameWidget</c>
    ///     时画（<c>:70-73</c>）—— 化身的 GameWidget 已被摘掉（<c>NetworkAvatarSpawner</c>），
    ///     所以第三分屏（H3 的 GameWidget）什么都看不到。
    /// 于是：把这两个权威因子每拍镜像到 H3 的覆盖层。**不重复**播声音 / 粒子：主机模拟已经在化身身上
    /// 放了（<c>ComponentFlu.Cough/Sneeze</c>、<c>PukeParticleSystem</c>、<c>OnFireParticleSystem</c>），
    /// 而第三分屏的相机本身就是 <c>SubsystemAudio</c> 的监听点之一（<c>SubsystemAudio.Update:178-182</c>
    /// 收集**每个** GameWidget 的 <c>ActiveCamera.ViewPosition</c>），重播会双份。
    /// </summary>
    public readonly struct MirrorViewEffectSample
    {
        public MirrorViewEffectSample(int entityToken, bool hasFlu, float blackoutFactor,
            bool coughing, bool hasSickness, float greenoutFactor, bool puking, bool hasFire,
            bool onFire)
        {
            EntityToken = entityToken;
            HasFlu = hasFlu;
            BlackoutFactor = blackoutFactor;
            Coughing = coughing;
            HasSickness = hasSickness;
            GreenoutFactor = greenoutFactor;
            Puking = puking;
            HasFire = hasFire;
            OnFire = onFire;
        }

        /// <summary>源化身实体标识（复活会换实体 → 上一段表现作废）。</summary>
        public int EntityToken { get; }

        /// <summary>客户端化身这一拍有没有 <c>ComponentFlu</c>。</summary>
        public bool HasFlu { get; }

        /// <summary><c>ComponentFlu.m_blackoutFactor</c>（主机权威黑屏因子，0..0.95）。</summary>
        public float BlackoutFactor { get; }

        /// <summary><c>ComponentFlu.IsCoughing</c>（咳嗽 / 喷嚏动作进行中；音效由主机引擎播放）。</summary>
        public bool Coughing { get; }

        /// <summary>客户端化身这一拍有没有 <c>ComponentSickness</c>。</summary>
        public bool HasSickness { get; }

        /// <summary><c>ComponentSickness.m_greenoutFactor</c>（主机权威绿屏因子，0..0.95）。</summary>
        public float GreenoutFactor { get; }

        /// <summary><c>ComponentSickness.IsPuking</c>（呕吐粒子在主机世界里活着）。</summary>
        public bool Puking { get; }

        /// <summary>客户端化身这一拍有没有 <c>ComponentOnFire</c>。</summary>
        public bool HasFire { get; }

        /// <summary><c>ComponentOnFire.IsOnFire</c>（着火粒子在主机世界里活着）。</summary>
        public bool OnFire { get; }

        public string Describe() =>
            "flu=" + (HasFlu ? BlackoutFactor.ToString("F3") : "-") +
            " cough=" + (Coughing ? "1" : "0") +
            " sick=" + (HasSickness ? GreenoutFactor.ToString("F3") : "-") +
            " puke=" + (Puking ? "1" : "0") +
            " fire=" + (HasFire ? (OnFire ? "1" : "0") : "-") +
            " token=" + EntityToken;
    }

    /// <summary>一帧的病程 / 火焰呈现决定（只有 <c>Write*</c> 为 true 的项才写覆盖层）。</summary>
    public readonly struct MirrorViewEffectPlan
    {
        public MirrorViewEffectPlan(bool writeBlackout, float blackout, bool blackoutOnset,
            bool writeGreenout, float greenout, bool greenoutOnset, bool fireOn, bool fireOnset,
            bool coughing, bool puking, bool duplicate, bool stale, string failure)
        {
            WriteBlackout = writeBlackout;
            Blackout = blackout;
            BlackoutOnset = blackoutOnset;
            WriteGreenout = writeGreenout;
            Greenout = greenout;
            GreenoutOnset = greenoutOnset;
            FireOn = fireOn;
            FireOnset = fireOnset;
            Coughing = coughing;
            Puking = puking;
            Duplicate = duplicate;
            Stale = stale;
            Failure = failure;
        }

        /// <summary>本拍把黑屏因子写进 H3（引擎每拍清 0，只要 &gt; 0.01 就必须每拍写）。</summary>
        public bool WriteBlackout { get; }
        public float Blackout { get; }

        /// <summary>一段黑屏表现的**起始**（计数 <c>viewFluEffectPresented</c>）。</summary>
        public bool BlackoutOnset { get; }

        /// <summary>本拍把绿屏因子写进 H3。</summary>
        public bool WriteGreenout { get; }
        public float Greenout { get; }

        /// <summary>一段绿屏表现的起始。</summary>
        public bool GreenoutOnset { get; }

        /// <summary>着火中（主机世界里的着火粒子由第三分屏的相机看到）。</summary>
        public bool FireOn { get; }

        /// <summary>着火起始（计数 <c>viewFluEffectPresented</c>）。</summary>
        public bool FireOnset { get; }

        /// <summary>咳嗽动作进行中（主机引擎已播放音效）。</summary>
        public bool Coughing { get; }

        /// <summary>呕吐粒子活着（主机引擎已生成，第三分屏的相机看得到）。</summary>
        public bool Puking { get; }

        /// <summary>本拍仍在同一段表现里（不是新的起始 ⇒ **不重复计一次呈现**）。</summary>
        public bool Duplicate { get; }

        /// <summary>源化身换代（复活）→ 上一段表现作废。</summary>
        public bool Stale { get; }

        /// <summary>非空 = 本拍表现不可用（非有限因子等），绝不写进覆盖层。</summary>
        public string Failure { get; }

        public string Describe() =>
            "effect{blackout=" + (WriteBlackout ? Blackout.ToString("F4") : "-") +
            (BlackoutOnset ? "(onset)" : "") +
            " greenout=" + (WriteGreenout ? Greenout.ToString("F4") : "-") +
            (GreenoutOnset ? "(onset)" : "") +
            " fire=" + (FireOn ? "1" : "0") + (FireOnset ? "(onset)" : "") +
            " cough=" + (Coughing ? "1" : "0") + " puke=" + (Puking ? "1" : "0") +
            " dup=" + (Duplicate ? "1" : "0") + " stale=" + (Stale ? "1" : "0") +
            " failure=" + (Failure ?? "none") + "}";
    }

    /// <summary>
    /// 病程 / 火焰表现的逐帧状态机（纯逻辑、无引擎引用、无静态可变状态）。
    ///
    /// 计数语义（进第三分屏 10 秒状态行）：
    ///   · <c>viewFluEffectPresented</c> = 一段表现的**起始**次数（黑屏 / 绿屏 / 着火各算一次；
    ///     绑定瞬间已经在吐 / 已经在烧也算一次 —— 那一段确实被呈现出来了）；
    ///   · <c>viewFluEffectDuplicate</c> = 同一段表现内的后续帧（<see cref="MirrorViewEffectPlan.Duplicate"/>）：
    ///     证明引擎每拍清 0 时我们只重复写因子，**不会**把同一段咳 / 吐算成两次；
    ///   · <c>viewFluEffectStale</c> = 源化身换代（复活换实体）丢弃上一段表现的次数；
    ///   · <c>viewFluEffectRejected</c> = 因子非有限 / 读不到 / 写覆盖层抛异常。
    /// </summary>
    public sealed class MirrorViewEffectWatch
    {
        private bool m_seeded;
        private int m_token;
        private bool m_blackoutActive;
        private bool m_greenoutActive;
        private bool m_fireActive;

        /// <summary>是否已经观测过第一帧（未观测时一切按"起始"处理）。</summary>
        public bool Seeded => m_seeded;

        /// <summary>解绑 / 换化身：下一次推进重新播种（不把上一段表现带过来）。</summary>
        public void Reset()
        {
            m_seeded = false;
            m_token = 0;
            m_blackoutActive = false;
            m_greenoutActive = false;
            m_fireActive = false;
        }

        /// <summary>推进一帧；非法输入返回 <see cref="MirrorViewEffectPlan.Failure"/> 非空的计划。</summary>
        public MirrorViewEffectPlan Advance(in MirrorViewEffectSample sample)
        {
            if (!MirrorViewFeedbackMath.IsFinite(sample.BlackoutFactor) ||
                !MirrorViewFeedbackMath.IsFinite(sample.GreenoutFactor))
                return new MirrorViewEffectPlan(false, 0f, false, false, 0f, false, false, false,
                    false, false, false, false, "view-effect-not-finite");
            bool stale = m_seeded && sample.EntityToken != m_token;
            if (stale)
            {
                // 化身换代（复活换实体）：上一段的"进行中"状态作废，新实体从起始重新算。
                m_blackoutActive = false;
                m_greenoutActive = false;
                m_fireActive = false;
            }
            m_token = sample.EntityToken;
            bool seed = !m_seeded;
            m_seeded = true;
            bool blackout = sample.HasFlu &&
                sample.BlackoutFactor > MirrorViewFeedbackMath.OverlayCutoff;
            bool greenout = sample.HasSickness &&
                sample.GreenoutFactor > MirrorViewFeedbackMath.OverlayCutoff;
            bool fire = sample.HasFire && sample.OnFire;
            bool blackoutOnset = blackout && !m_blackoutActive;
            bool greenoutOnset = greenout && !m_greenoutActive;
            bool fireOnset = fire && !m_fireActive;
            // 同一段表现内的后续帧：不再计一次"呈现"（去重）。
            bool duplicate = !seed && ((blackout && !blackoutOnset) || (greenout && !greenoutOnset));
            m_blackoutActive = blackout;
            m_greenoutActive = greenout;
            m_fireActive = fire;
            return new MirrorViewEffectPlan(blackout, sample.BlackoutFactor, blackoutOnset,
                greenout, sample.GreenoutFactor, greenoutOnset, fire, fireOnset, sample.Coughing,
                sample.Puking, duplicate, stale, null);
        }

        public string Describe() =>
            "seeded=" + (m_seeded ? "1" : "0") + " token=" + m_token +
            " blackout=" + (m_blackoutActive ? "1" : "0") +
            " greenout=" + (m_greenoutActive ? "1" : "0") +
            " fire=" + (m_fireActive ? "1" : "0");
    }

    /// <summary>
    /// 第三分屏的死亡文字（与引擎同一套大消息 / 时长 / 延迟；小字只写死因与本端状态，
    /// **不**写 "Tap to respawn" —— 主机点屏幕不能替客户端复活，那句话会是假的）。
    ///
    /// 生命周期（2026-10-21 产品修正，实机问题 5）：**与引擎同款** ——
    ///   · 死亡边沿下发**一次**（"YOU HAVE DIED" + 死因，30 秒 / 1.5 秒延迟）；
    ///   · 到期由引擎自己丢弃（<c>ComponentGui.cs:385-388</c>），本类**不再续期**；
    ///   · 复活边沿显式清掉（<see cref="MirrorViewFeedback.ApplyDeathText"/> 的 ClearOverlay 分支）。
    /// 旧实现"到期前静默续期、一直显示到复活"导致客户端死亡后第三屏文字永不消失（实测死亡 9 分钟
    /// 仍挂着），与单机 / 客户端自己的死亡文字行为不一致 —— 已删除续期。
    ///
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead:275-300
    /// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplayLargeMessage:178-187
    /// Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.UpdateWidgets:373-390（显示 / 淡入 / 过期）
    /// Source: SuMultiplayer/Net/PlayerRespawnNet.cs:PlayerRespawnRules.MessageDurationSeconds/MessageDelaySeconds
    /// </summary>
    public static class MirrorViewDeathText
    {
        /// <summary>引擎死亡大标题（<c>PlayerData.cs:275-300</c>，与 <c>PlayerRespawnRules.DeathLargeText</c> 同一个串）。</summary>
        public const string LargeText = "YOU HAVE DIED";

        /// <summary>大消息持续时间（引擎 30 秒）。</summary>
        public const float DurationSeconds = 30f;

        /// <summary>大消息延迟（引擎 1.5 秒）。</summary>
        public const float DelaySeconds = 1.5f;

        /// <summary>
        /// 一次下发的到期时刻 = <c>callTime + delay + DurationSeconds</c>
        /// （引擎在 <c>Time.RealTime &gt; StartTime + Duration</c> 时丢弃消息，Source: ComponentGui.cs:385-388）。
        /// </summary>
        public static double ExpiryAt(double callTime, float delay) =>
            callTime + delay + DurationSeconds;

        /// <summary>
        /// 小字：<c>"Cause of death: &lt;cause&gt;\n\n&lt;client&gt; is dead (waiting for respawn)"</c>。
        /// 死因为空 = "Unknown"（引擎 <c>PlayerData.cs:279-283</c> 同口径）。
        /// </summary>
        public static string BuildSmallText(string cause, string clientName)
        {
            string causeText = string.IsNullOrEmpty(cause) ? "Unknown" : cause;
            string who = string.IsNullOrEmpty(clientName) ? "The client" : clientName;
            return "Cause of death: " + causeText + "\n\n" + who +
                " is dead (waiting for respawn)";
        }
    }

    /// <summary>
    /// 第三分屏客户端反馈镜像的计数器（实例持有，无静态状态）。字段名与需求逐项对应：
    /// <c>viewDamageFlash</c> / <c>viewHealthBarFlash</c> / <c>viewHudNumbers</c> /
    /// <c>viewDeathOverlay</c> / <c>viewFeedbackFailures</c> / <c>viewFeedbackSkipped</c>（+ 原因）。
    /// 额外细项（便于实机排查）：<c>viewIceWrites</c>、<c>viewRespawnClears</c>、
    /// <c>viewDeathTextWrite</c>（死亡大消息下发次数）、<c>viewDeathTextShown/Cleared/Expired</c>
    /// （死亡边沿 / 复活清除 / 引擎同款 30 秒到期）、<c>viewDeathTextClearFailures</c>、
    /// <c>viewDeathTextMissing</c>
    /// （大消息控件没能移出 HUD 根 = 文字会被 HUD 显隐一起藏掉）、<c>viewDeathTextFailures</c>（写入抛异常）。
    /// 计数**每拍如实累加**，日志按 10 秒限流（见 <see cref="MirrorViewFeedback"/>）。
    /// </summary>
    public sealed class MirrorViewFeedbackCounters
    {
        /// <summary>掉血 → 第三分屏红屏的次数（红屏因子真的超过截断、会被画出来的那些拍）。</summary>
        public long ViewDamageFlash { get; private set; }

        /// <summary>掉血 → <c>HealthBarWidget.Flash(...)</c> 的次数（闪烁次数 &gt; 0 的那些拍）。</summary>
        public long ViewHealthBarFlash { get; private set; }

        /// <summary>HUD 数值 / 档位 / 等级文字的写入次数（diff-based：只在值变了才写）。</summary>
        public long ViewHudNumbers { get; private set; }

        /// <summary>死亡黑屏 + 死亡大消息的展示次数（每次死亡边界 1 次）。</summary>
        public long ViewDeathOverlay { get; private set; }

        /// <summary>写入失败次数（控件缺失 / 引擎字段读不到 / 写控件抛异常）。</summary>
        public long ViewFeedbackFailures { get; private set; }

        /// <summary>本拍跳过镜像的次数（不可观测 / 未接入 / 来源组件缺失）。</summary>
        public long ViewFeedbackSkipped { get; private set; }

        /// <summary>最近一次跳过原因（稳定键，日志 / 状态行直接用）。</summary>
        public string LastSkipReason { get; private set; }

        /// <summary>最近一次失败原因（稳定键 + 明细）。</summary>
        public string LastFailureReason { get; private set; }

        /// <summary>冰冻覆盖 <c>IceFactor</c> 的写入次数（引擎每拍清 0，所以有值就必须每拍写）。</summary>
        public long ViewIceWrites { get; private set; }

        /// <summary>复活清屏次数（清黑屏 / 清死亡大消息 / 红屏因子归零）。</summary>
        public long ViewRespawnClears { get; private set; }

        /// <summary>死亡大消息的**下发次数**（死亡边沿 1 次 + 之后每次到期续期 1 次）。</summary>
        public long ViewDeathTextWrite { get; private set; }

        /// <summary>
        /// 死亡大消息**画不出来**的次数：绑定期没能把 <c>Gui/LargeMessage</c> 移到 <c>GameWidget</c> 根下
        /// （引擎 XML 改版 / 视图控件缺失）。这种状态下文字会随 HUD 显隐一起被藏掉，必须能在日志里看见。
        /// </summary>
        public long ViewDeathTextMissing { get; private set; }

        /// <summary>死亡大消息写入抛异常的次数。</summary>
        public long ViewDeathTextFailures { get; private set; }

        /// <summary>死亡边沿下发死亡文字的次数（每次死亡 1 次；不再续期）。</summary>
        public long ViewDeathTextShown { get; private set; }

        /// <summary>复活时清掉死亡文字的次数（发过 ClearOverlay 且当时文字在屏上 / 认为在屏上）。</summary>
        public long ViewDeathTextCleared { get; private set; }

        /// <summary>与引擎同款 30 秒到期、文字被引擎自己丢掉的次数（不续期）。</summary>
        public long ViewDeathTextExpired { get; private set; }

        /// <summary>复活清文字失败（写控件抛异常）的次数。</summary>
        public long ViewDeathTextClearFailures { get; private set; }

        /// <summary>最近一次死亡大消息的问题（稳定键 + 明细；没出过问题时为 null）。</summary>
        public string LastDeathTextProblem { get; private set; }

        /// <summary>被驱动的帧数（分母；配置关闭 / 未绑定时不涨）。</summary>
        public long ViewFrames { get; private set; }

        // ---------------------------------------------------------------- 冰覆盖数据源（实机问题 1）
        /// <summary>冰覆盖当前数据源（<see cref="MirrorViewIceSource"/> 的稳定键）；未驱动 = <c>none</c>。</summary>
        public string ViewIceSource { get; private set; } = MirrorViewIceSource.None;

        /// <summary>冰覆盖当前插值值（状态行 <c>viewIceValue</c>；不是累计计数，是**当前值**）。</summary>
        public float ViewIceValue { get; private set; }

        /// <summary>冰覆盖插值推进的帧数（有权威目标才涨）。</summary>
        public long ViewIceFrames { get; private set; }

        /// <summary>冰覆盖"直接贴合"次数（首次播种 / 差分 ≤ 0.002 / 死亡归零）。</summary>
        public long ViewIceSnap { get; private set; }

        /// <summary>权威冻结进度这一拍不可观测的次数（明确跳过，不按温度顶替）。</summary>
        public long ViewIceMissing { get; private set; }

        /// <summary>冰覆盖失败次数（越界 / 非有限 / 写覆盖层抛异常）。</summary>
        public long ViewIceFailures { get; private set; }

        // ---------------------------------------------------------------- 病程 / 火焰表现（实机问题 2）
        /// <summary>第三屏**呈现出来的表现段数**（黑屏 / 绿屏 / 着火各算一次起始）。</summary>
        public long ViewFluEffectPresented { get; private set; }

        /// <summary>呈现帧数（黑屏 / 绿屏 / 着火任一在屏上；引擎每拍清 0，所以必须每拍重写）。</summary>
        public long ViewFluEffectFrames { get; private set; }

        /// <summary>表现不可用 / 写入失败的次数（非有限因子 / 字段读不到 / 写覆盖层抛异常）。</summary>
        public long ViewFluEffectRejected { get; private set; }

        /// <summary>同一段表现内的后续帧（**去重证据**：不把一段咳嗽 / 呕吐算成两次）。</summary>
        public long ViewFluEffectDuplicate { get; private set; }

        /// <summary>源化身换代（复活换实体）丢弃上一段表现的次数。</summary>
        public long ViewFluEffectStale { get; private set; }

        /// <summary>黑屏因子写入帧数。</summary>
        public long ViewFluBlackoutFrames { get; private set; }

        /// <summary>绿屏因子写入帧数。</summary>
        public long ViewFluGreenoutFrames { get; private set; }

        /// <summary>咳嗽 / 喷嚏动作进行中的帧数（音效由主机引擎播放，第三屏的相机是监听点）。</summary>
        public long ViewFluCoughFrames { get; private set; }

        /// <summary>呕吐粒子活着的帧数（主机世界里已有 <c>PukeParticleSystem</c>，第三屏的相机看得到）。</summary>
        public long ViewFluPukeFrames { get; private set; }

        /// <summary>着火帧数（主机世界里已有 <c>OnFireParticleSystem</c>）。</summary>
        public long ViewFireFrames { get; private set; }

        /// <summary>表现数据源（稳定键；正常 = <c>authority-host-avatar</c>）。</summary>
        public string ViewFluEffectSource { get; private set; } = "none";

        /// <summary>最近一次表现问题（稳定键 + 明细；没出过问题 = null）。</summary>
        public string LastFluEffectProblem { get; private set; }

        public void NoteFrame()
        {
            ViewFrames++;
        }

        public void NoteDamageFlash()
        {
            ViewDamageFlash++;
        }

        public void NoteHealthBarFlash()
        {
            ViewHealthBarFlash++;
        }

        public void NoteHudNumber()
        {
            ViewHudNumbers++;
        }

        public void NoteDeathOverlay()
        {
            ViewDeathOverlay++;
        }

        public void NoteIceWrite()
        {
            ViewIceWrites++;
        }

        public void NoteRespawnClear()
        {
            ViewRespawnClears++;
        }

        public void NoteDeathTextWrite()
        {
            ViewDeathTextWrite++;
        }

        public void NoteDeathTextMissing(string reason)
        {
            ViewDeathTextMissing++;
            LastDeathTextProblem = reason;
        }

        public void NoteDeathTextFailures(string reason)
        {
            ViewDeathTextFailures++;
            LastDeathTextProblem = reason;
        }

        public void NoteDeathTextShown()
        {
            ViewDeathTextShown++;
        }

        public void NoteDeathTextCleared()
        {
            ViewDeathTextCleared++;
        }

        public void NoteDeathTextExpired()
        {
            ViewDeathTextExpired++;
        }

        public void NoteDeathTextClearFailures(string reason)
        {
            ViewDeathTextClearFailures++;
            LastDeathTextProblem = reason;
        }

        public void NoteFailure(string reason)
        {
            ViewFeedbackFailures++;
            LastFailureReason = reason;
        }

        // ---------------------------------------------------------------- 冰覆盖（实机问题 1）

        /// <summary>本拍冰覆盖的数据源（每拍更新；状态行 <c>viewIceSource</c>）。</summary>
        public void NoteIceSource(string source, float value)
        {
            ViewIceSource = source ?? MirrorViewIceSource.None;
            ViewIceValue = value;
        }

        /// <summary>插值推进了一拍（有权威目标）。</summary>
        public void NoteIceFrame()
        {
            ViewIceFrames++;
        }

        /// <summary>本拍是"直接贴合"（播种 / 差分 ≤ 0.002 / 死亡归零）。</summary>
        public void NoteIceSnap()
        {
            ViewIceSnap++;
        }

        /// <summary>权威冻结进度这一拍不可观测（明确跳过，不按温度顶替）。</summary>
        public void NoteIceMissing(string reason)
        {
            ViewIceMissing++;
            LastFailureReason = reason;
        }

        /// <summary>冰覆盖失败（越界 / 非有限 / 写覆盖层抛异常）。</summary>
        public void NoteIceFailure(string reason)
        {
            ViewIceFailures++;
            LastFailureReason = reason;
        }

        // ---------------------------------------------------------------- 病程 / 火焰（实机问题 2）

        /// <summary>一段表现被呈现（黑屏 / 绿屏 / 着火起始各一次）。</summary>
        public void NoteFluPresented()
        {
            ViewFluEffectPresented++;
        }

        /// <summary>本拍有覆盖层 / 着火被呈现。</summary>
        public void NoteFluFrame()
        {
            ViewFluEffectFrames++;
        }

        /// <summary>表现不可用 / 写入失败。</summary>
        public void NoteFluRejected(string reason)
        {
            ViewFluEffectRejected++;
            LastFluEffectProblem = reason;
        }

        /// <summary>同一段表现内的后续帧（去重，不重复计一次呈现）。</summary>
        public void NoteFluDuplicate()
        {
            ViewFluEffectDuplicate++;
        }

        /// <summary>源化身换代，上一段表现作废。</summary>
        public void NoteFluStale()
        {
            ViewFluEffectStale++;
        }

        public void NoteFluBlackoutFrame()
        {
            ViewFluBlackoutFrames++;
        }

        public void NoteFluGreenoutFrame()
        {
            ViewFluGreenoutFrames++;
        }

        public void NoteFluCoughFrame()
        {
            ViewFluCoughFrames++;
        }

        public void NoteFluPukeFrame()
        {
            ViewFluPukeFrames++;
        }

        public void NoteFireFrame()
        {
            ViewFireFrames++;
        }

        /// <summary>表现数据源（每拍更新；状态行 <c>viewFluEffectSource</c>）。</summary>
        public void NoteFluSource(string source)
        {
            ViewFluEffectSource = source ?? "none";
        }

        public void NoteSkipped(string reason)
        {
            ViewFeedbackSkipped++;
            LastSkipReason = reason;
        }

        /// <summary>可审计的一行（10 秒日志 / 状态行）。</summary>
        public string Describe() =>
            "viewDamageFlash=" + ViewDamageFlash +
            " viewHealthBarFlash=" + ViewHealthBarFlash +
            " viewHudNumbers=" + ViewHudNumbers +
            " viewDeathOverlay=" + ViewDeathOverlay +
            " viewFeedbackFailures=" + ViewFeedbackFailures +
            " viewFeedbackSkipped=" + ViewFeedbackSkipped +
            " viewSkippedReason=" + (LastSkipReason ?? "none") +
            " viewFailureReason=" + (LastFailureReason ?? "none") +
            " viewIceWrites=" + ViewIceWrites +
            " viewRespawnClears=" + ViewRespawnClears +
            " viewDeathTextWrite=" + ViewDeathTextWrite +
            " viewDeathTextMissing=" + ViewDeathTextMissing +
            " viewDeathTextFailures=" + ViewDeathTextFailures +
            " viewDeathTextShown=" + ViewDeathTextShown +
            " viewDeathTextCleared=" + ViewDeathTextCleared +
            " viewDeathTextExpired=" + ViewDeathTextExpired +
            " viewDeathTextClearFailures=" + ViewDeathTextClearFailures +
            " viewDeathTextProblem=" + (LastDeathTextProblem ?? "none") +
            " viewIceSource=" + ViewIceSource +
            " viewIceValue=" + ViewIceValue.ToString("F4") +
            " viewIceApplied=" + ViewIceWrites +
            " viewIceFrames=" + ViewIceFrames +
            " viewIceSnap=" + ViewIceSnap +
            " viewIceMissing=" + ViewIceMissing +
            " viewIceFailures=" + ViewIceFailures +
            " viewFluEffectSource=" + ViewFluEffectSource +
            " viewFluEffectPresented=" + ViewFluEffectPresented +
            " viewFluEffectFrames=" + ViewFluEffectFrames +
            " viewFluEffectRejected=" + ViewFluEffectRejected +
            " viewFluEffectDuplicate=" + ViewFluEffectDuplicate +
            " viewFluEffectStale=" + ViewFluEffectStale +
            " viewFluBlackoutFrames=" + ViewFluBlackoutFrames +
            " viewFluGreenoutFrames=" + ViewFluGreenoutFrames +
            " viewFluCoughFrames=" + ViewFluCoughFrames +
            " viewFluPukeFrames=" + ViewFluPukeFrames +
            " viewFireFrames=" + ViewFireFrames +
            " viewFluEffectProblem=" + (LastFluEffectProblem ?? "none") +
            " viewFrames=" + ViewFrames;
    }

    /// <summary>
    /// 一帧"客户端化身"的可显示状态（只读值类型）。<see cref="HasHealth"/> / <see cref="HasVitals"/>
    /// 为 false 表示该组件这一拍不可观测 —— 调用方必须跳过并写清原因，绝不用默认值顶替。
    ///
    /// <see cref="HasFreeze"/> / <see cref="FreezeProgress"/> / <see cref="FreezeStage"/>（实机问题 1）：
    /// 冰覆盖的**唯一**数据源 = 主机权威冻结进度（vitals.proto 字段 22/23，与客户端收到的是同一个数）。
    /// <see cref="Temperature"/> **不再**参与冰覆盖计算 —— 两端各按温度推导会让冻结时刻 / 屏幕占比
    /// 不一致（客户端走 95 的 freeze_progress 且带插值，第三屏若按温度即时推导就会错开）。
    /// </summary>
    public readonly struct MirrorViewSourceSample
    {
        public MirrorViewSourceSample(bool hasHealth, float health, string causeOfDeath,
            bool hasVitals, float food, float temperature, float targetTemperature, float level,
            bool hasFreeze, float freezeProgress, uint freezeStage)
        {
            HasHealth = hasHealth;
            Health = health;
            CauseOfDeath = causeOfDeath;
            HasVitals = hasVitals;
            Food = food;
            Temperature = temperature;
            TargetTemperature = targetTemperature;
            Level = level;
            HasFreeze = hasFreeze;
            FreezeProgress = freezeProgress;
            FreezeStage = freezeStage;
        }

        /// <summary>客户端化身的 <c>ComponentHealth</c> 这一拍是否存在。</summary>
        public bool HasHealth { get; }

        /// <summary><c>ComponentHealth.Health</c>（0..1）。</summary>
        public float Health { get; }

        /// <summary><c>ComponentHealth.CauseOfDeath</c>（死亡屏小字用；可空）。</summary>
        public string CauseOfDeath { get; }

        /// <summary>客户端化身的 <c>ComponentVitalStats</c> 这一拍是否存在。</summary>
        public bool HasVitals { get; }

        /// <summary><c>ComponentVitalStats.Food</c>（0..1）。</summary>
        public float Food { get; }

        /// <summary><c>ComponentVitalStats.Temperature</c>（0..24）。</summary>
        public float Temperature { get; }

        /// <summary><c>ComponentVitalStats.m_targetTemperature</c>（温度条档位；0..24）。</summary>
        public float TargetTemperature { get; }

        /// <summary><c>PlayerData.Level</c>（等级文字；只读）。</summary>
        public float Level { get; }

        /// <summary>
        /// 主机权威冻结进度这一拍是否可读（= <c>PlayerVitalsReplica.TryGetAuthorityFreeze</c> 的结果）。
        /// false = 主机还没抓到（还没进世界 / 化身未就绪 / 抓取失败）：冰覆盖必须**明确跳过**并计数，
        /// 不得按温度自己算、也不得拿 0 顶替（0 是"不冷"这个合法值）。
        /// </summary>
        public bool HasFreeze { get; }

        /// <summary>
        /// 主机权威冻结进度（vitals.proto 字段 22 = <c>PlayerFreezePolicy.TryCapture</c> 的结果，
        /// 与 95 上报给客户端的是同一个 float）。冰覆盖的唯一目标值。
        /// </summary>
        public float FreezeProgress { get; }

        /// <summary>主机权威冻结阶段（vitals.proto 字段 23，0 none / 1 chilly / 2 cold / 3 hypo / 4 frozen）。</summary>
        public uint FreezeStage { get; }

        public string Describe() =>
            "health=" + Health.ToString("F4") + " cause=" + (CauseOfDeath ?? "null") +
            " food=" + Food.ToString("F4") + " temperature=" + Temperature.ToString("F4") +
            " targetTemperature=" + TargetTemperature.ToString("F4") +
            " level=" + Level.ToString("F2") +
            " freeze=" + (HasFreeze ? FreezeProgress.ToString("F4") : "unavailable") +
            "/" + FreezeStage;
    }

    /// <summary>一帧的掉血反馈决定（红屏因子 / 血条闪烁）。</summary>
    public readonly struct MirrorViewDamageStep
    {
        public MirrorViewDamageStep(bool damageApplied, bool writeRedout, float redoutFactor,
            int flashCount)
        {
            DamageApplied = damageApplied;
            WriteRedout = writeRedout;
            RedoutFactor = redoutFactor;
            FlashCount = flashCount;
        }

        /// <summary>本拍观测到掉血（= <c>HealthChange &lt; 0</c>）。</summary>
        public bool DamageApplied { get; }

        /// <summary>本拍要写 <c>RedoutFactor</c>（红屏因子仍高于引擎截断）。</summary>
        public bool WriteRedout { get; }

        /// <summary>要写的红屏因子（= 衰减 + 本拍增量的结果）。</summary>
        public float RedoutFactor { get; }

        /// <summary>要传给 <c>HealthBarWidget.Flash</c> 的次数（0 = 不闪）。</summary>
        public int FlashCount { get; }
    }

    /// <summary>一帧的 HUD 显示值决定（只有 <c>Write*</c> 为 true 的项才写控件）。</summary>
    public readonly struct MirrorViewHudWrites
    {
        public MirrorViewHudWrites(bool writeHealthBar, float healthBarValue, bool writeFoodBar,
            float foodBarValue, bool writeTemperatureTexture, int temperatureTextureIndex,
            string temperatureTextureName, bool writeIce, float iceFactor, bool writeLevelLabel,
            int level)
        {
            WriteHealthBar = writeHealthBar;
            HealthBarValue = healthBarValue;
            WriteFoodBar = writeFoodBar;
            FoodBarValue = foodBarValue;
            WriteTemperatureTexture = writeTemperatureTexture;
            TemperatureTextureIndex = temperatureTextureIndex;
            TemperatureTextureName = temperatureTextureName;
            WriteIce = writeIce;
            IceFactor = iceFactor;
            WriteLevelLabel = writeLevelLabel;
            Level = level;
        }

        public bool WriteHealthBar { get; }
        public float HealthBarValue { get; }
        public bool WriteFoodBar { get; }
        public float FoodBarValue { get; }
        public bool WriteTemperatureTexture { get; }
        public int TemperatureTextureIndex { get; }
        public string TemperatureTextureName { get; }
        /// <summary>引擎每拍把 <c>IceFactor</c> 清 0，所以只要 &gt; 0 就每拍写。</summary>
        public bool WriteIce { get; }
        public float IceFactor { get; }
        public bool WriteLevelLabel { get; }
        public int Level { get; }
    }

    /// <summary>一帧的死亡 / 复活边沿决定。</summary>
    public readonly struct MirrorViewDeathStep
    {
        public MirrorViewDeathStep(bool showOverlay, bool keepBlackout, bool clearOverlay,
            string cause, string smallText)
        {
            ShowOverlay = showOverlay;
            KeepBlackout = keepBlackout;
            ClearOverlay = clearOverlay;
            Cause = cause;
            SmallText = smallText;
        }

        /// <summary>本拍刚进入死亡（显示一次死亡大消息）。</summary>
        public bool ShowOverlay { get; }

        /// <summary>死亡期间每拍写 <c>BlackoutFactor = 1</c>（引擎每拍清 0）。</summary>
        public bool KeepBlackout { get; }

        /// <summary>本拍刚复活（清死亡大消息 / 红屏因子归零 / 停止黑屏）。</summary>
        public bool ClearOverlay { get; }

        /// <summary>死因（<c>ComponentHealth.CauseOfDeath</c>；仅 <see cref="ShowOverlay"/> 时有值）。</summary>
        public string Cause { get; }

        /// <summary>死亡大消息小字（仅 <see cref="ShowOverlay"/> 时有值）。</summary>
        public string SmallText { get; }
    }

    /// <summary>
    /// 一帧的完整镜像计划。<see cref="SkipReason"/> / <see cref="FailureReason"/> 非空时其余字段无意义
    /// （调用方只计数 + 限流日志，不写任何控件）。
    /// </summary>
    public readonly struct MirrorViewFramePlan
    {
        public MirrorViewFramePlan(string skipReason, string failureReason, string hudSkipReason,
            MirrorViewDamageStep damage, MirrorViewHudWrites hud, MirrorViewDeathStep death,
            MirrorViewIceStep ice)
        {
            SkipReason = skipReason;
            FailureReason = failureReason;
            HudSkipReason = hudSkipReason;
            Damage = damage;
            Hud = hud;
            Death = death;
            Ice = ice;
        }

        /// <summary>整帧跳过原因（不可观测 / 未接入）。</summary>
        public string SkipReason { get; }

        /// <summary>整帧失败原因（输入非法 —— 保留上一次有效显示）。</summary>
        public string FailureReason { get; }

        /// <summary>只跳过 HUD 的原因（体征不可观测；红屏 / 死亡反馈照常）。</summary>
        public string HudSkipReason { get; }

        public MirrorViewDamageStep Damage { get; }
        public MirrorViewHudWrites Hud { get; }
        public MirrorViewDeathStep Death { get; }

        /// <summary>
        /// 冰覆盖这一拍的来源 / 目标 / 插值结果（实机问题 1）：数据源只有主机权威
        /// <c>freeze_progress</c>，每帧用与客户端同一个插值器逼近。
        /// </summary>
        public MirrorViewIceStep Ice { get; }

        public bool IsSkipped => SkipReason != null;

        public bool IsFailed => FailureReason != null;

        public static MirrorViewFramePlan Skipped(string reason) =>
            new MirrorViewFramePlan(reason, null, null, default, default, default, default);

        public static MirrorViewFramePlan Failed(string reason) =>
            new MirrorViewFramePlan(null, reason, null, default, default, default, default);

        public string Describe() =>
            "skip=" + (SkipReason ?? "none") + " failure=" + (FailureReason ?? "none") +
            " hudSkip=" + (HudSkipReason ?? "none") +
            " damage{applied=" + (Damage.DamageApplied ? "1" : "0") +
            " redout=" + (Damage.WriteRedout ? "1" : "0") + ":" +
            Damage.RedoutFactor.ToString("F4") +
            " flash=" + Damage.FlashCount + "}" +
            " hud{health=" + (Hud.WriteHealthBar ? Hud.HealthBarValue.ToString("F4") : "-") +
            " food=" + (Hud.WriteFoodBar ? Hud.FoodBarValue.ToString("F4") : "-") +
            " tempTex=" + (Hud.WriteTemperatureTexture
                ? Hud.TemperatureTextureIndex.ToString()
                : "-") +
            " ice=" + (Hud.WriteIce ? Hud.IceFactor.ToString("F4") : "-") +
            " level=" + (Hud.WriteLevelLabel ? Hud.Level.ToString() : "-") + "}" +
            " death{show=" + (Death.ShowOverlay ? "1" : "0") +
            " keep=" + (Death.KeepBlackout ? "1" : "0") +
            " clear=" + (Death.ClearOverlay ? "1" : "0") + "}" +
            " " + Ice.Describe();
    }

    /// <summary>
    /// 第三分屏反馈的**逐帧差分状态机**（纯逻辑，无引擎引用）：
    /// 输入一帧的客户端化身可显示值，输出"这一帧要写哪些显示值与红屏 / 黑屏 / 死亡大消息"。
    ///
    /// 差分口径：
    ///   · 掉血 = 本拍观测到的 <c>Health</c> 相对上一拍下降（与引擎 <c>HealthChange = Health - m_lastHealth</c>
    ///     同一个量，只是跨帧保存）；
    ///   · 红屏因子 = 上一拍因子按 <c>Pow(0.2, dt)</c> 衰减 + 本拍 <c>4f * drop</c>，与引擎同序；
    ///   · 血条 / 食物条 / 温度档位 / 等级文字：值没变就不写（引擎里它们每拍被写，是因为引擎自己的
    ///     Update 在跑；绑定期 Update 被摘掉，值本来就不变，diff-based 等价且不会每拍刷控件）；
    ///   · 死亡边沿：首次观测到 <c>Health &lt;= 0</c> 才显示一次大消息；复活边沿清消息 + 因子归零；
    ///   · 首次观测（绑定后的第一拍）只写当前值，**不**因为"从 0 变到 X"而闪红 / 报死亡（除非本来就已经死了）。
    ///
    /// 本类不做 IO、不持引擎对象、无静态可变状态；一个绑定实例一个 <see cref="Reset"/> 生命周期。
    /// </summary>
    public sealed class MirrorViewFeedbackDiff
    {
        private bool m_seeded;
        private bool m_hudSeeded;
        private float m_lastHealth;
        private bool m_dead;
        private bool m_deathOverlayShown;
        private float m_redFactor;
        private float m_lastHealthBarValue;
        private float m_lastFoodBarValue;
        private int m_lastTemperatureTextureIndex = int.MinValue;
        private float m_lastIceFactor;
        private float m_iceTarget;
        private string m_iceSource = MirrorViewIceSource.None;
        private uint m_iceStage;
        private int m_lastLevel = int.MinValue;

        /// <summary>
        /// 冰覆盖的连续插值（实机问题 1）：**与客户端完全同一个类 / 同一组参数**
        /// （<see cref="PlayerPresentationInterpolator"/>：时间常数 0.2 s、限速 3 每秒、
        /// 0.002 内直接贴合）。客户端在 <c>PlayerVitalsReplica.ApplyLocalHud</c> 里用的就是它，
        /// 第三屏必须用同一条曲线，否则冻结 / 退去的时刻会错开。
        /// </summary>
        private readonly PlayerPresentationInterpolator m_ice = new PlayerPresentationInterpolator();

        /// <summary>是否已观测过第一帧（未观测时不产生任何掉血 / 死亡边沿）。</summary>
        public bool Seeded => m_seeded;

        /// <summary>HUD 显示值是否已播种（播种那拍写全部当前值）。</summary>
        public bool HudSeeded => m_hudSeeded;

        /// <summary>上一拍是否处于死亡状态。</summary>
        public bool Dead => m_dead;

        /// <summary>死亡大消息是否已经显示过（复活时清）。</summary>
        public bool DeathOverlayShown => m_deathOverlayShown;

        /// <summary>当前红屏因子（引擎 <c>m_redScreenFactor</c> 的镜像）。</summary>
        public float RedFactor => m_redFactor;

        /// <summary>最后写入的血条值（诊断用）。</summary>
        public float LastHealthBarValue => m_lastHealthBarValue;

        /// <summary>最后写入的食物条值（诊断用）。</summary>
        public float LastFoodBarValue => m_lastFoodBarValue;

        /// <summary>最后写入的温度贴图档位（诊断用；<c>int.MinValue</c> = 还没写过）。</summary>
        public int LastTemperatureTextureIndex => m_lastTemperatureTextureIndex;

        /// <summary>最后观测到的冰冻因子 = 插值器当前值（诊断用）。</summary>
        public float LastIceFactor => m_lastIceFactor;

        /// <summary>冰覆盖当前数据源（<see cref="MirrorViewIceSource"/> 的稳定键）。</summary>
        public string IceSource => m_iceSource;

        /// <summary>冰覆盖当前目标（主机权威 freeze_progress；不可观测时是上一次的值）。</summary>
        public float IceTarget => m_iceTarget;

        /// <summary>冰覆盖当前阶段（vitals.proto 字段 23；只用于状态行 / 排查）。</summary>
        public uint IceStage => m_iceStage;

        /// <summary>最后写入的等级（诊断用；<c>int.MinValue</c> = 还没写过）。</summary>
        public int LastLevel => m_lastLevel;

        /// <summary>解绑 / 重新绑定：清空全部差分状态（下一次 <see cref="Advance"/> 重新播种）。</summary>
        public void Reset()
        {
            m_seeded = false;
            m_hudSeeded = false;
            m_lastHealth = 0f;
            m_dead = false;
            m_deathOverlayShown = false;
            m_redFactor = 0f;
            m_lastHealthBarValue = 0f;
            m_lastFoodBarValue = 0f;
            m_lastTemperatureTextureIndex = int.MinValue;
            m_lastIceFactor = 0f;
            // 冰覆盖：下一次推进重新播种（不从上一次绑定 / 上一次会话的值淡入）。
            m_ice.Reset();
            m_iceTarget = 0f;
            m_iceSource = MirrorViewIceSource.None;
            m_iceStage = 0u;
            m_lastLevel = int.MinValue;
        }

        /// <summary>
        /// 推进一帧并给出计划。<paramref name="dt"/> 是这一拍的时长（秒，引擎 <c>Time.FrameDuration</c>）；
        /// <paramref name="clientName"/> 只用于死亡大消息的小字。
        /// </summary>
        public MirrorViewFramePlan Advance(in MirrorViewSourceSample sample, float dt,
            string clientName)
        {
            if (!sample.HasHealth) return MirrorViewFramePlan.Skipped("source-health-missing");
            if (!MirrorViewFeedbackMath.IsFinite(sample.Health))
                return MirrorViewFramePlan.Failed("source-health-not-finite");
            if (!MirrorViewFeedbackMath.IsFinite(dt) || dt < 0f) dt = 0f;

            float drop = 0f;
            if (m_seeded && sample.Health < m_lastHealth) drop = m_lastHealth - sample.Health;
            m_seeded = true;
            m_lastHealth = sample.Health;
            bool damageApplied = drop > 0f;

            bool dead = sample.Health <= 0f;
            bool showDeath = dead && !m_deathOverlayShown;
            // 产品口径（2026-10-20）：镜像分屏死亡时**不再压黑屏**，改为让 MirrorCharacterEngine
            // 切到引擎的 DeathCamera（环绕尸体视角）+ 保留 "YOU HAVE DIED" 大消息。
            // 之前写 BlackoutFactor=1 会导致第四分屏全黑，看不到死亡相机。
            bool keepBlackout = false;
            bool clearDeath = !dead && m_dead;
            if (showDeath) m_deathOverlayShown = true;
            if (clearDeath)
            {
                // 复活：死亡表现立刻消失（黑屏因子每拍由引擎清 0，这里只负责不再写），红屏因子归零。
                m_deathOverlayShown = false;
                m_redFactor = 0f;
            }
            m_dead = dead;

            m_redFactor = MirrorViewFeedbackMath.DecayRedFactor(m_redFactor, dt);
            if (damageApplied) m_redFactor += MirrorViewFeedbackMath.RedFactorIncrement(drop);
            bool writeRedout = m_redFactor > MirrorViewFeedbackMath.RedoutCutoff;
            int flashCount = damageApplied
                ? MirrorViewFeedbackMath.HealthBarFlashCount(drop)
                : 0;

            var damage = new MirrorViewDamageStep(damageApplied, writeRedout, m_redFactor,
                flashCount);
            var death = new MirrorViewDeathStep(showDeath, keepBlackout, clearDeath,
                showDeath ? sample.CauseOfDeath : null,
                showDeath
                    ? MirrorViewDeathText.BuildSmallText(sample.CauseOfDeath, clientName)
                    : null);

            string hudSkipReason = null;
            bool writeHealth = false, writeFood = false, writeTemperature = false,
                writeIce = false, writeLevel = false;
            float healthBarValue = 0f, foodBarValue = 0f, iceFactor = 0f;
            int temperatureIndex = 0, level = 0;
            string temperatureName = null;
            var ice = new MirrorViewIceStep(false, false, 0f, 0u, 0f, false,
                MirrorViewIceSource.None, null);
            if (!sample.HasVitals)
            {
                // 体征不可观测：红屏 / 死亡反馈照常，只跳过 HUD 数值（明确计数，不拿默认值顶替）。
                hudSkipReason = "source-vitals-missing";
            }
            else
            {
                // 冰覆盖的数据源校验（先做）：权威冻结值非法时失败原因必须指向 freeze 本身，
                // 不能被下面"体征 HUD 输入校验"的通用失败键盖住。
                if (!dead && sample.HasFreeze &&
                    (!MirrorViewFeedbackMath.IsFinite(sample.FreezeProgress) ||
                     sample.FreezeProgress < 0f || sample.FreezeProgress > 1f))
                    return MirrorViewFramePlan.Failed("source-freeze-invalid");
                // 体征 HUD 输入校验（血条 / 食物条 / 温度档位由 PlayerVitalsPresentation 统一折算）。
                // 注意：它的 IceFactor 字段**不再使用** —— 冰覆盖只由 AdvanceIce 的插值器给出
                // （实机问题 1：第三屏不得再按温度自己算 Saturate(1 - T/6)）。
                if (!PlayerVitalsPresentation.TryCreateWithFreeze(dead ? 0f : sample.Health,
                        sample.Food, sample.Temperature, sample.TargetTemperature,
                        dead ? 0f : (sample.HasFreeze ? sample.FreezeProgress : 0f), dead,
                        out PlayerVitalsHudValues values))
                    return MirrorViewFramePlan.Failed("source-vitals-not-finite");
                if (!MirrorViewFeedbackMath.IsFinite(sample.Level))
                    return MirrorViewFramePlan.Failed("source-level-not-finite");
                ice = AdvanceIce(in sample, dead, dt);
                if (ice.Error != null) return MirrorViewFramePlan.Failed(ice.Error);
                bool seed = !m_hudSeeded;
                int levelNow = MirrorViewFeedbackMath.LevelOf(sample.Level);
                if (seed ||
                    !MirrorViewFeedbackMath.Near(values.HealthBarValue, m_lastHealthBarValue))
                {
                    writeHealth = true;
                    healthBarValue = values.HealthBarValue;
                    m_lastHealthBarValue = values.HealthBarValue;
                }
                if (seed ||
                    !MirrorViewFeedbackMath.Near(values.FoodBarValue, m_lastFoodBarValue))
                {
                    writeFood = true;
                    foodBarValue = values.FoodBarValue;
                    m_lastFoodBarValue = values.FoodBarValue;
                }
                if (seed || values.TemperatureTextureIndex != m_lastTemperatureTextureIndex)
                {
                    writeTemperature = true;
                    temperatureIndex = values.TemperatureTextureIndex;
                    temperatureName = values.TemperatureTextureName;
                    m_lastTemperatureTextureIndex = temperatureIndex;
                }
                // 引擎每拍清 0：只要还有冰覆盖就必须每拍写；为 0 时不写（引擎已经是 0）。
                if (ice.Write)
                {
                    writeIce = true;
                    iceFactor = ice.Value;
                }
                if (seed || levelNow != m_lastLevel)
                {
                    writeLevel = true;
                    level = levelNow;
                    m_lastLevel = levelNow;
                }
                m_hudSeeded = true;
            }

            var hud = new MirrorViewHudWrites(writeHealth, healthBarValue, writeFood,
                foodBarValue, writeTemperature, temperatureIndex, temperatureName, writeIce,
                iceFactor, writeLevel, level);
            return new MirrorViewFramePlan(null, null, hudSkipReason, damage, hud, death, ice);
        }

        /// <summary>
        /// 冰覆盖一帧的推进（实机问题 1）：目标 = **主机权威** <c>freeze_progress</c>
        /// （vitals.proto 字段 22，与客户端收到的是同一个 float）；用与客户端**同一个**
        /// <see cref="PlayerPresentationInterpolator"/>（0.2 s / 3 每秒 / 0.002 贴合）每帧逼近，
        /// 绝不阶跃写入。
        ///   · 死亡：与客户端一致（<c>PlayerVitalsReplica.ApplyLocalHud</c> 的
        ///     <c>state.Freeze.Reset()</c>）立刻归零，不做淡出；
        ///   · 权威值这一拍不可观测：<see cref="MirrorViewIceSource.Missing"/>，**不写**覆盖层、
        ///     也**不**按温度自己算（明确计数，绝不静默顶替）；
        ///   · 非有限 / 越界：<see cref="MirrorViewIceStep.Error"/> 非空（整帧失败，保留上一次显示）。
        /// </summary>
        private MirrorViewIceStep AdvanceIce(in MirrorViewSourceSample sample, bool dead, float dt)
        {
            if (dead)
            {
                m_ice.Reset();
                m_iceSource = MirrorViewIceSource.AuthorityDead;
                m_iceStage = 0u;
                m_iceTarget = 0f;
                m_lastIceFactor = 0f;
                return new MirrorViewIceStep(false, false, 0f, 0u, 0f, true,
                    MirrorViewIceSource.AuthorityDead, null);
            }
            if (!sample.HasFreeze)
            {
                m_iceSource = MirrorViewIceSource.Missing;
                return new MirrorViewIceStep(false, false, 0f, 0u, m_ice.Value, false,
                    MirrorViewIceSource.Missing, null);
            }
            if (!MirrorViewFeedbackMath.IsFinite(sample.FreezeProgress) ||
                sample.FreezeProgress < 0f || sample.FreezeProgress > 1f)
                return new MirrorViewIceStep(false, false, sample.FreezeProgress,
                    sample.FreezeStage, m_ice.Value, false, MirrorViewIceSource.Missing,
                    "source-freeze-invalid");
            bool seeded = !m_ice.Primed;
            float before = m_ice.Value;
            if (!m_ice.TryAdvance(sample.FreezeProgress, dt, out float value, out string error))
                return new MirrorViewIceStep(false, false, sample.FreezeProgress,
                    sample.FreezeStage, before, false, MirrorViewIceSource.Authority, error);
            bool snapped = seeded ||
                Math.Abs(sample.FreezeProgress - before) <=
                    PlayerPresentationInterpolator.SnapEpsilon;
            m_iceSource = MirrorViewIceSource.Authority;
            m_iceStage = sample.FreezeStage;
            m_iceTarget = sample.FreezeProgress;
            m_lastIceFactor = value;
            return new MirrorViewIceStep(true,
                value > MirrorViewFeedbackMath.HudEpsilon, sample.FreezeProgress,
                sample.FreezeStage, value, snapped, MirrorViewIceSource.Authority, null);
        }

        /// <summary>可审计的一行（10 秒日志 / 状态行）。</summary>
        public string Describe() =>
            "seeded=" + (m_seeded ? "1" : "0") + " hud=" + (m_hudSeeded ? "1" : "0") +
            " dead=" + (m_dead ? "1" : "0") +
            " deathOverlay=" + (m_deathOverlayShown ? "1" : "0") +
            " red=" + m_redFactor.ToString("F4") +
            " healthBar=" + m_lastHealthBarValue.ToString("F4") +
            " foodBar=" + m_lastFoodBarValue.ToString("F4") +
            " tempTex=" + (m_lastTemperatureTextureIndex == int.MinValue
                ? "-"
                : m_lastTemperatureTextureIndex.ToString()) +
            " ice=" + m_lastIceFactor.ToString("F4") +
            " iceSource=" + m_iceSource + " iceStage=" + m_iceStage +
            " level=" + (m_lastLevel == int.MinValue ? "-" : m_lastLevel.ToString());
    }
}
