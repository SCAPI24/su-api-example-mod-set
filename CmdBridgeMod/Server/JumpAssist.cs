using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CmdBridgeMod
{
    /// <summary>
    /// 空格/跳跃审计 + 可选「跳跃缓冲」。
    ///
    /// 背景（用户实测 + 两轮采样）：Windows 上偶尔「按空格没起跳」。两轮实测证明
    /// **引擎键盘层每次都收到了空格**（29/29、34/34），所以丢的不是输入投递，而是游戏内部的跳跃裁决：
    ///   · `ComponentInput.cs:181` 跳跃读的是**边沿**（`IsKeyDownOnce(Key.Space)`）；
    ///   · `ComponentInput.cs:76-81` 0.3 秒内第二次按空格会被当成「切换创造飞行」而不是跳跃；
    ///   · `ComponentInput.cs:91-117` 未就绪 / 睡着 / 非操控相机 / 窗口不活跃会把 Jump 清零；
    ///   · `ComponentLocomotion.cs:527` 起跳要求**当帧** `StandingOnValue.HasValue || ImmersionFactor > 0.5`，
    ///     而 `JumpOrder` 每帧清零（`:344-349`）——**没有任何输入缓冲**，当帧不满足就永久丢掉。
    ///
    /// 本类做两件事，**分别落在原版流程的两个阶段上**：
    ///   1. **审计（只读，帧首 `Tick()`）**：逐帧统计「引擎看到几次空格边沿 / 游戏产生了几次跳跃指令 /
    ///      有没有真的离地 / 几次被拒绝」，并记下每次边沿当帧的接地、焦点、相机、睡眠、速度。
    ///   2. **输入阶段注入（代码里默认开，`jump.buffer state=off` 只关本次运行，`PumpInputStage()`）**：
    ///      对**所有边沿型按键**（空格跳跃、Shift 蹲、E 背包、C 衣服、V 相机、Q 丢弃、数字键…）生效，
    ///      不只是跳跃。
    ///      由 order **-11** 的 `IUpdateable`（`CursorSoftGuard`，紧贴 `ComponentInput` 之前）调用。
    ///
    /// 为什么注入点必须是这个阶段（源码链）：
    ///   · 原版把输入分两步：**消息到达时记录**（`Keyboard.ProcessKeyDown` 置 `m_keysDownOnceArray`），
    ///     然后**到输入帧一次性写进 `PlayerInput`**（`ComponentInput.Update`，`ComponentInput.UpdateOrder`
    ///     = `UpdateOrder.Input` = -10）；
    ///   · 所以任何"替换/插入"都必须和原版输入同一时刻 —— 帧首（早于 order -100）或帧末都离开了这个阶段；
    ///   · 旧实现直接写 `ComponentLocomotion.JumpOrder`，**绕过了整条管线**（`ComponentInput.cs:76-87` 的
    ///     0.3 秒双按 = 切创造飞行、`:91-117` 的就绪/睡眠/相机门槛），只是被 `ComponentPlayer.cs:143`
    ///     的 `MathUtils.Max(Jump?1:0, JumpOrder)` 侥幸保住，属于巧合而非设计。
    ///
    /// 现在的做法：在 -11 如果"事件记录到一次空格、而引擎的边沿却不存在"（`Keyboard.cs:171-176` 的
    /// `if (!m_keysDownArray[key])` 守卫会因"引擎认为还按着"而不置边沿；`Keyboard.Clear()` 也会把已置的边沿抹掉），
    /// 且玩家此刻满足原版起跳前提时，**恢复引擎边沿**（`m_keysDownOnceArray[Space] = true`）——
    /// 紧随其后的 `ComponentInput`(-10) 会把它当成真实按下读进 `PlayerInput`，双按规则与全部门槛照原版走。
    /// </summary>
    internal sealed class JumpAssist
    {
        /// <summary>边沿被「拒绝」的判定窗口：这么久内游戏还没产生跳跃指令，就认定这一下没跳成。</summary>
        private const double BufferSeconds = 0.15;

        /// <summary>补偿后再等这么久才允许下一次补偿：与游戏 0.3 秒的双按规则保持安全距离。</summary>
        private const double CooldownSeconds = 0.4;

        /// <summary>离地观测窗口：边沿之后这么久内出现「不接地且向上速度」就算真的跳起来了。</summary>
        private const double RiseWatchSeconds = 0.35;

        private bool m_enabled;

        // ---- 输入阶段注入（order -11，紧贴 `ComponentInput`(-10) 之前）----
        // 直接拿引擎自己的 downOnce 数组：恢复边沿 = 让原版输入层"看见"这次按下，
        // 而不是绕过它去写 ComponentLocomotion.JumpOrder。
        private bool[] m_keysDownOnce;
        private string m_arrayError = string.Empty;
        private long m_inputStageCalls;
        private long m_edgesRestored;
        private string m_inputStageError = string.Empty;

        // ---- 通用按键边沿恢复（**不只空格**）----
        // 边沿型输入都可能被同一类机制吞掉：蹲 = `ComponentInput.cs:187` 的 `IsKeyDownOnce(Key.Shift)`，
        // 还有 E/C/V/Q/G/T/L/K/J/P/H/R/F/数字键（`:187-241`）与创造飞行的双击规则。
        // 每个键各自持有一个"事件已记录、引擎边沿却缺失"的挂起窗口。
        private bool[] m_pendingKeys;
        private double[] m_pendingKeyDeadline;

        /// <summary>
        /// 每个键"自上次抬起后是否允许再恢复一次"。防的是：若引擎在按住时仍送来重复 KeyDown
        /// （本机实测 OpenTK 不送重复，但不依赖这一点），恢复出来的边沿会被当成一次次新的按下。
        /// </summary>
        private bool[] m_restoreArmed;
        private long m_edgesRestoredSpace;
        private long m_edgesRestoredOther;
        private long m_expiredPresses;
        private long m_restoreSkippedByGate;
        private string m_lastRestoredKey = string.Empty;

        // ---- 引擎事件级计数（与帧时序无关）：`Keyboard.KeyDown` 每次真实按下都会触发。
        // 为什么必须有它：帧首读 `IsKeyDownOnce` 只能看到"帧首之前已置位"的边沿；若按键事件落在
        // **本帧输入采样之后**，它会被 `Keyboard.AfterFrame` 的帧末清零抹掉 —— 帧首观察者和游戏输入层
        // 都看不到，这正是"有时按空格没跳"的一条真实通路。
        private long m_keyDownEvents;

        // ---- 每次按键**事件**发生瞬间的玩家状态（这是判定"空中按空格"还是"接地却没跳"的唯一可靠依据）----
        private long m_eventsGrounded;
        private long m_eventsAirborne;
        private long m_eventSnapshotErrors;
        private string m_eventSnapshotError = string.Empty;
        private bool m_lastEventStanding;
        private bool m_lastEventActive;
        private bool m_lastEventSleeping;
        private bool m_lastEventReady;
        private bool m_lastEventCameraOk;
        private bool m_lastEventAlive;
        private float m_lastEventVelocityY;
        private float m_lastEventJumpOrder;
        private float m_lastEventLastJumpOrder;
        private int m_lastEventFrame = -1;

        /// <summary>挂起窗口到期时，缓冲究竟被哪个门槛挡住（"airborne"/"cooldown"/"camera"…）。</summary>
        private string m_pendingBlockReason = string.Empty;
        private string m_rejectedReason = string.Empty;

        // ---- 游戏侧真值（与本类有没有观察到边沿**无关**）----
        private long m_orderFrames;   // 上一帧产生过跳跃指令的帧数（`LastJumpOrder > 0`）
        private long m_jumpStarts;    // "站立/不上升" → "离地且向上"的跳变次数（真的跳起来了）
        private bool m_wasAirborneUp;

        // ---- 审计累计量（单调递增，供高频轮询取增量，天然免疫"标志只活一帧"）----
        private long m_frames;
        private long m_edges;
        private long m_orders;
        private long m_rises;
        private long m_rejected;

        // ---- 当前挂起的边沿 ----
        private bool m_pending;
        private double m_pendingDeadline;
        private double m_cooldownUntil;
        private bool m_riseWatchArmed;
        private double m_riseWatchUntil;

        // ---- 最近一次边沿的快照（供诊断）----
        private int m_lastEdgeFrame = -1;
        private bool m_lastEdgeStanding;
        private bool m_lastEdgeActive;
        private bool m_lastEdgeSleeping;
        private bool m_lastEdgeCameraOk;
        private bool m_lastEdgeReady;
        private bool m_lastEdgeAlive;
        private float m_lastEdgeVelocityY;
        private float m_lastEdgeJumpOrder;
        private float m_lastEdgeLastJumpOrder;
        private string m_lastNote = string.Empty;

        /// <param name="enabled">
        /// 初始开关，**代码里默认 true**（用户决定恒久打开，不再落 `CmdBridge.json`）。
        /// 关着时本类只做审计与计数，一次都不会恢复引擎边沿。
        /// </param>
        public JumpAssist(bool enabled = true)
        {
            m_enabled = enabled;
            m_lastNote = enabled ? "jump buffer ON" : "jump buffer OFF";
            int keyCount = Enum.GetValues(typeof(Key)).Length;
            m_pendingKeys = new bool[keyCount];
            m_pendingKeyDeadline = new double[keyCount];
            m_restoreArmed = new bool[keyCount];
            for (int i = 0; i < keyCount; i++)
                m_restoreArmed[i] = true;
            // 事件级计数：任何真实按下都会到这里，与帧首时序无关。
            // Source: Engine/Engine/Input/Keyboard.cs:Keyboard.KeyDown
            Keyboard.KeyDown += OnEngineKeyDown;
            // 抬起用来重新武装：一次"按下-抬起"周期最多恢复一次。
            Keyboard.KeyUp += OnEngineKeyUp;
        }

        /// <summary>卸载时退订（Mod 重载后不要留下指向已卸载实例的回调）。</summary>
        public void Shutdown()
        {
            Keyboard.KeyDown -= OnEngineKeyDown;
            Keyboard.KeyUp -= OnEngineKeyUp;
        }

        private void OnEngineKeyUp(Key key)
        {
            int keyIndex = (int)key;
            if (m_restoreArmed != null && keyIndex >= 0 && keyIndex < m_restoreArmed.Length)
                m_restoreArmed[keyIndex] = true;
        }

        private void OnEngineKeyDown(Key key)
        {
            // ---- 通用记录（**所有键**）：边沿型输入都可能被同一类机制吞掉 ----
            //   · 蹲 = `ComponentInput.cs:187` 的 `IsKeyDownOnce(Key.Shift)`；
            //   · 还有 E/C/V/Q/G/T/L/K/J/P/H/R/F/数字键（`:187-241`）。
            // 记录窗口从**事件**起算（与帧首标志无关），到输入阶段（order -11）结算。
            try
            {
                int keyIndex = (int)key;
                if (m_pendingKeys != null && keyIndex >= 0 && keyIndex < m_pendingKeys.Length)
                {
                    m_pendingKeys[keyIndex] = true;
                    m_pendingKeyDeadline[keyIndex] = Time.RealTime + BufferSeconds;
                }
            }
            catch (Exception exception)
            {
                m_inputStageError = exception.GetType().Name + ": " + exception.Message;
            }

            if (key != Key.Space)
                return;
            m_keyDownEvents++;
            // 记下"事件发生这一瞬间"的玩家状态：之后帧首读不到这条边沿时，只有这里能回答
            // "当时到底是不是站在地上"（空中按空格不跳是游戏正确行为，不是丢键）。
            try
            {
                ComponentPlayer player = InputInjector.TryGetPlayer();
                bool standing = player?.ComponentBody?.StandingOnValue.HasValue == true;
                m_lastEventStanding = standing;
                if (standing)
                    m_eventsGrounded++;
                else
                    m_eventsAirborne++;
                m_lastEventActive = Window.IsActive;
                m_lastEventSleeping = player?.ComponentSleep?.SleepFactor > 0f;
                m_lastEventReady = player?.PlayerData?.IsReadyForPlaying ?? false;
                var eventCamera = player?.GameWidget?.ActiveCamera;
                m_lastEventCameraOk = eventCamera != null && eventCamera.IsEntityControlEnabled &&
                    !eventCamera.UsesMovementControls;
                m_lastEventAlive = player?.ComponentHealth?.Health > 0f;
                m_lastEventVelocityY = player?.ComponentBody?.Velocity.Y ?? 0f;
                m_lastEventJumpOrder = player?.ComponentLocomotion?.JumpOrder ?? 0f;
                m_lastEventLastJumpOrder = player?.ComponentLocomotion?.LastJumpOrder ?? 0f;
                m_lastEventFrame = Time.FrameIndex;
            }
            catch (Exception exception)
            {
                // 事件线程上取玩家状态可能失败（游戏状态不线程安全）：计数并记原因，不能让快照把整条链断掉。
                m_eventSnapshotErrors++;
                m_eventSnapshotError = exception.GetType().Name + ": " + exception.Message;
            }
            // 关键：缓冲从**事件**起算，而不是从帧首标志起算 —— 即使这次边沿随后被帧末清零抹掉
            // （帧首观察不到、游戏输入层也读不到），缓冲窗口依然存在，下一帧就能把它补上。
            // 同时也让 `rejected` 能统计到这类"太晚到达"的丢失。
            m_pending = true;
            m_pendingDeadline = Time.RealTime + BufferSeconds;
            m_pendingBlockReason = string.Empty;
        }

        public bool Enabled => m_enabled;

        public void SetEnabled(bool value)
        {
            m_enabled = value;
            if (!value)
            {
                m_pending = false;
                m_riseWatchArmed = false;
            }
            m_lastNote = value ? "jump buffer ON" : "jump buffer OFF";
        }

        /// <summary>帧首调用（游戏线程，泵批次内、早于 `ComponentInput.Update`）。</summary>
        public void Tick()
        {
            try
            {
                m_frames++;
                ComponentPlayer player = InputInjector.TryGetPlayer();
                double now = Time.RealTime;
                bool active = Window.IsActive;
                bool standing = player?.ComponentBody?.StandingOnValue.HasValue == true;
                float velocityY = player?.ComponentBody?.Velocity.Y ?? 0f;
                float lastJumpOrder = player?.ComponentLocomotion?.LastJumpOrder ?? 0f;
                bool alive = player?.ComponentHealth?.Health > 0f;
                bool sleeping = player?.ComponentSleep?.SleepFactor > 0f;
                // 起跳的相机前提必须与引擎口径一致（`ComponentInput.cs:91-117`）：
                //   · 相机要允许实体操控（`IsEntityControlEnabled`）；
                //   · 且**不能**是自带移动控制的那种相机 —— 那一支会把 Jump 直接清成 false。
                // 之前这里写成"必须 UsesMovementControls"，而普通玩家相机该值是 false，
                // 于是缓冲被自己的门槛挡住、一次都没执行（实测 59 次采样全是这个原因）。
                var camera = player?.GameWidget?.ActiveCamera;
                bool cameraOk = camera != null && camera.IsEntityControlEnabled &&
                    !camera.UsesMovementControls;
                bool ready = player?.PlayerData?.IsReadyForPlaying ?? false;
                bool edge = Keyboard.IsKeyDownOnce(Key.Space);

                // 游戏侧真值（与本类有没有看到边沿无关）：
                // `LastJumpOrder > 0` 说明**上一帧**确实产生了跳跃指令；离地且向上说明真的跳起来了。
                if (lastJumpOrder > 0f)
                    m_orderFrames++;
                bool airborneUp = !standing && velocityY > 0.3f;
                if (airborneUp && !m_wasAirborneUp)
                    m_jumpStarts++;
                m_wasAirborneUp = airborneUp;

                // 0) 离地观测：边沿之后短时间内真的离地了就算"跳起来了"
                if (m_riseWatchArmed && now <= m_riseWatchUntil && !standing && velocityY > 0.3f)
                {
                    m_rises++;
                    m_riseWatchArmed = false;
                }
                else if (m_riseWatchArmed && now > m_riseWatchUntil)
                {
                    m_riseWatchArmed = false;
                }

                // 1) 先结算上一次挂起的边沿：游戏有没有产生跳跃指令
                if (m_pending)
                {
                    if (lastJumpOrder > 0f)
                    {
                        m_orders++;
                        m_pending = false;
                        m_cooldownUntil = now + CooldownSeconds;
                        m_lastNote = "edge accepted: jump order seen";
                    }
                    else if (now > m_pendingDeadline)
                    {
                        m_rejected++;
                        m_rejectedReason = string.IsNullOrEmpty(m_pendingBlockReason)
                            ? "no-gate-observed" : m_pendingBlockReason;
                        m_pending = false;
                        m_lastNote = "edge rejected: no jump order within " +
                            (BufferSeconds * 1000.0).ToString("0", CultureInfo.InvariantCulture) + "ms (" +
                            m_rejectedReason + ")";
                    }
                }

                // 2) 本帧新的空格边沿
                if (edge)
                {
                    m_edges++;
                    m_lastEdgeFrame = Time.FrameIndex;
                    m_lastEdgeStanding = standing;
                    m_lastEdgeActive = active;
                    m_lastEdgeSleeping = sleeping;
                    m_lastEdgeCameraOk = cameraOk;
                    m_lastEdgeReady = ready;
                    m_lastEdgeAlive = alive;
                    m_lastEdgeVelocityY = velocityY;
                    m_lastEdgeJumpOrder = player?.ComponentLocomotion?.JumpOrder ?? 0f;
                    m_lastEdgeLastJumpOrder = lastJumpOrder;
                    m_pending = true;
                    m_pendingDeadline = now + BufferSeconds;
                    m_riseWatchArmed = true;
                    m_riseWatchUntil = now + RiseWatchSeconds;
                    m_lastNote = "space edge seen (engine input layer)";
                }
                else if (m_pending)
                {
                    // 只记录"此刻被哪个门槛挡住"，窗口到期时写进 rejectedReason。
                    // 补偿本身不在这里做 —— 它必须发生在**输入阶段**（order -11 的 `PumpInputStage`），
                    // 见类注释：帧首写 JumpOrder 属于绕过原版管线。
                    string blocked = !standing ? "airborne"
                        : !active ? "window-inactive"
                        : !alive ? "dead"
                        : sleeping ? "sleeping"
                        : !cameraOk ? "camera-without-movement-controls"
                        : !ready ? "player-not-ready"
                        : player?.ComponentLocomotion == null ? "no-locomotion"
                        : null;
                    m_pendingBlockReason = blocked ?? "ready-for-input-stage";
                }
            }
            catch (Exception exception)
            {
                m_lastNote = "tick error: " + exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>
        /// **输入阶段钩子（通用于所有键，不只空格）**：由 order **-11** 的 `IUpdateable`（`CursorSoftGuard`）调用，
        /// 位置在 `ComponentInput.Update`（order -10）**之前**、同一个排序 pass 内 —— 与原版输入的
        /// 写入阶段完全一致（原版：消息到达时记录 → 输入帧一次性写进 `PlayerInput`）。
        ///
        /// 只在"事件记录到一次按下、而引擎的边沿却不存在"时出手（`m_enabled` 打开时）：
        ///   · `Keyboard.cs:171-176` —— 引擎认为该键还按着就**不置边沿**（抬起被 `:181-186` 丢弃过）；
        ///   · `Keyboard.Clear()`（失焦 / 软键盘 / 控制台 / 文本输入挂载）会把已置的边沿抹掉。
        /// 恢复方式就是写引擎自己的 `m_keysDownOnceArray`（与 `Keyboard.cs:174` 同一数组、同一语义），
        /// 紧随其后的 `ComponentInput` 会把它当作真实按下读走 —— 双按规则、就绪/睡眠/相机门槛全走原版。
        ///
        /// 门槛按 `ComponentInput.cs:91-117` 分两层：组件级（窗口活跃 + 世界就绪 + 活着 + 未睡 +
        /// 相机允许实体操控）对所有键成立；`Jump`/`ToggleCrouch`/`ToggleCreativeFly` 另需"相机不是
        /// 自带移动控制的那种"。空格额外要求接地，避免凭空刷新 `ComponentInput.cs:76-87` 的
        /// `m_lastJumpTime` 把后续真实按下误判成"0.3 秒内两下 = 切创造飞行"。
        /// </summary>
        public void PumpInputStage()
        {
            try
            {
                m_inputStageCalls++;
                if (m_pendingKeys == null)
                    return;
                if (m_keysDownOnce == null)
                    EnsureDownOnceArray();
                if (m_keysDownOnce == null)
                    return;

                ComponentPlayer player = InputInjector.TryGetPlayer();
                double now = Time.RealTime;
                var camera = player?.GameWidget?.ActiveCamera;
                bool componentOk = player != null && Window.IsActive &&
                    (player.PlayerData?.IsReadyForPlaying ?? false) &&
                    (player.ComponentHealth?.Health ?? 0f) > 0f &&
                    !((player.ComponentSleep?.SleepFactor ?? 0f) > 0f) &&
                    (camera?.IsEntityControlEnabled ?? false);
                bool movementControlsCamera = camera != null && camera.UsesMovementControls;
                bool standing = player?.ComponentBody?.StandingOnValue.HasValue == true;

                for (int i = 0; i < m_pendingKeys.Length; i++)
                {
                    if (!m_pendingKeys[i])
                        continue;
                    Key key = (Key)i;

                    // 引擎已经有这条边沿 → 原版自己会读走，绝不插手。
                    if (Keyboard.IsKeyDownOnce(key))
                    {
                        m_pendingKeys[i] = false;
                        continue;
                    }
                    if (now > m_pendingKeyDeadline[i])
                    {
                        m_pendingKeys[i] = false;
                        m_expiredPresses++;
                        continue;
                    }
                    if (!m_enabled || !m_restoreArmed[i])
                        continue;
                    if (!componentOk)
                    {
                        m_restoreSkippedByGate++;
                        continue;   // 窗口内继续重试，等组件前提恢复
                    }
                    // `ComponentInput.cs:109-117`：自带移动控制的相机那一支会把
                    // Jump / ToggleCrouch / ToggleCreativeFly 清掉，这两个键要额外排除它。
                    if (movementControlsCamera && (key == Key.Space || key == Key.Shift))
                    {
                        m_restoreSkippedByGate++;
                        continue;
                    }
                    if (key == Key.Space && !standing)
                    {
                        m_restoreSkippedByGate++;
                        continue;
                    }

                    // 与原版 `Keyboard.cs:174` 同一数组、同一语义。
                    m_keysDownOnce[i] = true;
                    m_pendingKeys[i] = false;
                    m_restoreArmed[i] = false;   // 等一次抬起再武装，防连发被当成新按下
                    m_edgesRestored++;
                    if (key == Key.Space)
                        m_edgesRestoredSpace++;
                    else
                        m_edgesRestoredOther++;
                    m_lastRestoredKey = key.ToString();
                    m_lastNote = "input stage: engine edge restored (" + m_lastRestoredKey + ")";
                }
            }
            catch (Exception exception)
            {
                m_inputStageError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>反射拿引擎的 `m_keysDownOnceArray`（与 `InputInjector` 同一成员白名单）。</summary>
        private void EnsureDownOnceArray()
        {
            try
            {
                ModManager manager = ModManager.Instance;
                IModParentField fields = manager?.ModParentField;
                if (fields == null)
                    return;
                m_keysDownOnce = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownOnceArray);
            }
            catch (Exception exception)
            {
                m_arrayError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>`jump.status` 的回答。</summary>
        public Dictionary<string, object> Describe()
        {
            double now = Time.RealTime;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enabled"] = m_enabled,
                ["bufferMs"] = (int)(BufferSeconds * 1000.0),
                ["cooldownMs"] = (int)(CooldownSeconds * 1000.0),
                ["frames"] = m_frames,
                ["keyDownEvents"] = m_keyDownEvents,
                ["eventsGrounded"] = m_eventsGrounded,
                ["eventsAirborne"] = m_eventsAirborne,
                ["eventSnapshotErrors"] = m_eventSnapshotErrors,
                ["eventSnapshotError"] = m_eventSnapshotError,
                ["lastEventStanding"] = m_lastEventStanding,
                ["lastEventAirborne"] = !m_lastEventStanding,
                ["lastEventFrame"] = m_lastEventFrame,
                ["lastEventVelocityY"] = m_lastEventVelocityY,
                ["lastEventJumpOrder"] = m_lastEventJumpOrder,
                ["lastEventLastJumpOrder"] = m_lastEventLastJumpOrder,
                ["lastEventActive"] = m_lastEventActive,
                ["lastEventSleeping"] = m_lastEventSleeping,
                ["lastEventReady"] = m_lastEventReady,
                ["lastEventCameraOk"] = m_lastEventCameraOk,
                ["lastEventAlive"] = m_lastEventAlive,
                ["pendingBlockReason"] = m_pendingBlockReason,
                ["rejectedReason"] = m_rejectedReason,
                ["spaceEdges"] = m_edges,
                ["jumpOrders"] = m_orders,
                ["orderFrames"] = m_orderFrames,
                ["jumpStarts"] = m_jumpStarts,
                ["rises"] = m_rises,
                ["rejected"] = m_rejected,
                ["inputStageCalls"] = m_inputStageCalls,
                ["edgesRestored"] = m_edgesRestored,
                ["edgesRestoredSpace"] = m_edgesRestoredSpace,
                ["edgesRestoredOther"] = m_edgesRestoredOther,
                ["lastRestoredKey"] = m_lastRestoredKey,
                ["expiredPresses"] = m_expiredPresses,
                ["restoreSkippedByGate"] = m_restoreSkippedByGate,
                ["inputStageError"] = m_inputStageError,
                ["arrayError"] = m_arrayError,
                ["buffered"] = m_edgesRestored,
                ["pending"] = m_pending,
                ["pendingMs"] = m_pending ? (int)Math.Max(0.0, (m_pendingDeadline - now) * 1000.0) : 0,
                ["cooldownMsLeft"] = (int)Math.Max(0.0, (m_cooldownUntil - now) * 1000.0),
                ["lastEdgeFrame"] = m_lastEdgeFrame,
                ["lastEdgeStanding"] = m_lastEdgeStanding,
                ["lastEdgeWindowActive"] = m_lastEdgeActive,
                ["lastEdgeSleeping"] = m_lastEdgeSleeping,
                ["lastEdgeCameraOk"] = m_lastEdgeCameraOk,
                ["lastEdgeReady"] = m_lastEdgeReady,
                ["lastEdgeAlive"] = m_lastEdgeAlive,
                ["lastEdgeVelocityY"] = m_lastEdgeVelocityY,
                ["lastEdgeJumpOrder"] = m_lastEdgeJumpOrder,
                ["lastEdgeLastJumpOrder"] = m_lastEdgeLastJumpOrder,
                ["lastNote"] = m_lastNote
            };
        }
    }
}
