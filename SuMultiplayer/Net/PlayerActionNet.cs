using System;
using Engine;
using Google.Protobuf;
using PA = SuMultiplayer.Protocol.Action;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 玩家**动作**的表现种类（协议 + 领域共用的唯一枚举）。
    ///
    /// Source: Mod/ScMultiplayer/Message/PlayerActionMessage.cs:PlayerActionType
    ///   （旧枚举含请求/结果语义；这里只保留**表现动作**，请求/结果的权威链在编辑权威与地形链）
    /// Source: Mod/ScMultiplayer/Message/PlayerAimMessage.cs:PlayerAimAction
    ///   （Start / Update / Release / Cancel —— Update 不单独成消息：瞄准射线只在边沿带一次）
    ///
    /// 数值与 <c>Protocol/action.proto</c> 的 <c>PlayerActionKind</c> 一一对应（append-only）。
    /// </summary>
    public enum PlayerActionKind
    {
        Unspecified = 0,
        Dig = 1,
        Place = 2,
        Use = 3,
        Eat = 4,
        Throw = 5,
        Hit = 6,
        Jump = 7,
        AimStart = 8,
        AimCancel = 9,
        AimRelease = 10
    }

    /// <summary>
    /// 一条动作快照（纯值）。由 <see cref="PlayerActionDetector"/> 在**发送端**从引擎事实判定，
    /// 经 <see cref="PlayerActionCodec"/> 上线，接收端只用来重放抬手/瞄准表现。
    /// </summary>
    public readonly struct PlayerActionSnapshot
    {
        public PlayerActionSnapshot(int sequence, int serverTick, PlayerActionKind kind,
            float swingPhase, bool sustained, int activeSlotIndex, int itemValue, int itemCount,
            bool hasAim, Vector3 aimOrigin, Vector3 aimDirection,
            bool hasCell, int cellX, int cellY, int cellZ, int cellFace, float digProgress)
        {
            Sequence = sequence;
            ServerTick = serverTick;
            Kind = kind;
            SwingPhase = swingPhase;
            Sustained = sustained;
            ActiveSlotIndex = activeSlotIndex;
            ItemValue = itemValue;
            ItemCount = itemCount;
            HasAim = hasAim;
            AimOrigin = aimOrigin;
            AimDirection = aimDirection;
            HasCell = hasCell;
            CellX = cellX;
            CellY = cellY;
            CellZ = cellZ;
            CellFace = cellFace;
            DigProgress = digProgress;
        }

        /// <summary>发送方每个 clientId 单调递增的动作序号（接收端丢弃重复/迟到）。</summary>
        public int Sequence { get; }
        public int ServerTick { get; }
        public PlayerActionKind Kind { get; }
        /// <summary>动作发生时的 ComponentMiner.PokingPhase（0..1）。</summary>
        public float SwingPhase { get; }
        /// <summary>true = 该动作仍在持续（挖掘中）；接收端让相位继续循环。</summary>
        public bool Sustained { get; }
        public int ActiveSlotIndex { get; }
        public int ItemValue { get; }
        public int ItemCount { get; }
        public bool HasAim { get; }
        public Vector3 AimOrigin { get; }
        public Vector3 AimDirection { get; }
        public bool HasCell { get; }
        public int CellX { get; }
        public int CellY { get; }
        public int CellZ { get; }
        public int CellFace { get; }
        public float DigProgress { get; }

        public bool HasAction => Kind != PlayerActionKind.Unspecified;

        /// <summary>
        /// 非有限值一律丢弃（NaN 写进模型相位会让骨骼矩阵变成 NaN）。
        /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:41-42
        ///         （旧实现先做 readOnlyState.IsFinite 校验）
        /// </summary>
        public bool IsFinite =>
            IsFiniteValue(SwingPhase) && IsFiniteValue(DigProgress) &&
            IsFiniteValue(AimOrigin.X) && IsFiniteValue(AimOrigin.Y) && IsFiniteValue(AimOrigin.Z) &&
            IsFiniteValue(AimDirection.X) && IsFiniteValue(AimDirection.Y) &&
            IsFiniteValue(AimDirection.Z);

        private static bool IsFiniteValue(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// 动作消息的编解码（proto：Protocol/action.proto 的 <c>PlayerAction</c>）。
    /// 失败必须显式（返回 false + 原因）：调用方丢弃并写一行，不拿默认值顶替。
    /// </summary>
    public static class PlayerActionCodec
    {
        public static PA.PlayerAction ToMessage(int clientId, in PlayerActionSnapshot action)
        {
            var message = new PA.PlayerAction
            {
                ClientId = clientId,
                Sequence = action.Sequence,
                ServerTick = action.ServerTick,
                Kind = (PA.PlayerActionKind)(int)action.Kind,
                SwingPhase = action.SwingPhase,
                Sustained = action.Sustained,
                ActiveSlotIndex = action.ActiveSlotIndex,
                ItemValue = action.ItemValue,
                ItemCount = action.ItemCount,
                HasAim = action.HasAim,
                HasCell = action.HasCell,
                CellX = action.CellX,
                CellY = action.CellY,
                CellZ = action.CellZ,
                CellFace = action.CellFace,
                DigProgress = action.DigProgress
            };
            if (action.HasAim)
            {
                message.AimOrigin = new PP.Vec3
                {
                    X = action.AimOrigin.X, Y = action.AimOrigin.Y, Z = action.AimOrigin.Z
                };
                message.AimDirection = new PP.Vec3
                {
                    X = action.AimDirection.X,
                    Y = action.AimDirection.Y,
                    Z = action.AimDirection.Z
                };
            }
            return message;
        }

        public static byte[] Encode(int clientId, in PlayerActionSnapshot action) =>
            ToMessage(clientId, in action).ToByteArray();

        public static bool TryConvert(PA.PlayerAction message, out int clientId,
            out PlayerActionSnapshot action, out string error)
        {
            clientId = message?.ClientId ?? -1;
            action = default;
            error = null;
            if (message == null)
            {
                error = "动作消息为空";
                return false;
            }
            int kind = (int)message.Kind;
            if (kind < (int)PlayerActionKind.Dig || kind > (int)PlayerActionKind.AimRelease)
            {
                error = "动作种类非法：" + kind;
                return false;
            }
            Vector3 origin = Vector3.Zero;
            Vector3 direction = Vector3.Zero;
            if (message.HasAim)
            {
                if (message.AimOrigin == null || message.AimDirection == null)
                {
                    error = "声明 has_aim 却缺少瞄准射线";
                    return false;
                }
                origin = new Vector3(message.AimOrigin.X, message.AimOrigin.Y, message.AimOrigin.Z);
                direction = new Vector3(message.AimDirection.X, message.AimDirection.Y,
                    message.AimDirection.Z);
            }
            action = new PlayerActionSnapshot(message.Sequence, message.ServerTick,
                (PlayerActionKind)kind, message.SwingPhase, message.Sustained,
                message.ActiveSlotIndex, message.ItemValue, message.ItemCount,
                message.HasAim, origin, direction, message.HasCell,
                message.CellX, message.CellY, message.CellZ, message.CellFace, message.DigProgress);
            if (!action.IsFinite)
            {
                error = "动作载荷含 NaN/Inf（phase/progress/aim）";
                action = default;
                return false;
            }
            return true;
        }

        public static bool TryDecode(byte[] payload, out int clientId,
            out PlayerActionSnapshot action, out string error)
        {
            clientId = -1;
            action = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            PA.PlayerAction message;
            try
            {
                message = PA.PlayerAction.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析动作消息失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(message, out clientId, out action, out error);
        }
    }

    /// <summary>
    /// 动画状态位（与 proto 的 <c>PlayerMotionFlags</c> 数值一一对应）。
    /// Source: Survivalcraft/Game/ComponentHumanModel.cs:ComponentHumanModel.Update
    ///         （MovementAnimationPhase / m_punchPhase / m_aimHandAngle / m_rowLeft/m_rowRight）
    /// </summary>
    [Flags]
    public enum PlayerMotionFlags
    {
        None = 0,
        Walking = 1,
        Running = 2,
        Jumping = 4,
        Swimming = 8,
        Flying = 16,
        Crouching = 32,
        Grounded = 64,
        OnFire = 128,
        Hurt = 256,
        Aiming = 512,
        Attacking = 1024,
        Digging = 2048,
        Using = 4096,
        Eating = 8192,
        RowLeft = 16384,
        RowRight = 32768
    }

    /// <summary>
    /// 一帧的动画状态（纯值）。发送端按**变化**发（32Hz 上限），接收端把它当"持续状态"。
    /// walk/jump/turn 三个 order 与旧位置的快照同口径（Source: GamePlayerPositionMessage.cs:78-160），
    /// 但引擎的 <c>ComponentLocomotion.Update</c> 每帧先清空 Last*Order 再由模型读取，
    /// 所以远端化身**不**用它们驱动骨骼（见报告"表现边界"）；它们用于动画状态位与诊断。
    /// </summary>
    public readonly struct PlayerMotionSnapshot
    {
        public PlayerMotionSnapshot(int serverTick, PlayerMotionFlags flags, bool hasWalkOrder,
            Vector2 walkOrder, float jumpOrder, Vector2 turnOrder, float immersion,
            int activeSlotIndex)
        {
            ServerTick = serverTick;
            Flags = flags;
            HasWalkOrder = hasWalkOrder;
            WalkOrder = walkOrder;
            JumpOrder = jumpOrder;
            TurnOrder = turnOrder;
            Immersion = immersion;
            ActiveSlotIndex = activeSlotIndex;
        }

        public int ServerTick { get; }
        public PlayerMotionFlags Flags { get; }
        public bool HasWalkOrder { get; }
        public Vector2 WalkOrder { get; }
        public float JumpOrder { get; }
        public Vector2 TurnOrder { get; }
        public float Immersion { get; }
        public int ActiveSlotIndex { get; }

        public bool Has(PlayerMotionFlags flag) => (Flags & flag) == flag;

        /// <summary>
        /// 变化驱动判据（量化后再比：浮点噪声不该产生网络流量）。
        /// 与 <see cref="PlayerActionDetector"/> 一起支撑"(e) 未变化不发包"。
        /// </summary>
        public bool EqualsState(in PlayerMotionSnapshot other) =>
            Flags == other.Flags && ActiveSlotIndex == other.ActiveSlotIndex &&
            HasWalkOrder == other.HasWalkOrder &&
            Near(WalkOrder.X, other.WalkOrder.X) && Near(WalkOrder.Y, other.WalkOrder.Y) &&
            Near(JumpOrder, other.JumpOrder, 0.01f) &&
            Near(TurnOrder.X, other.TurnOrder.X) && Near(TurnOrder.Y, other.TurnOrder.Y) &&
            Near(Immersion, other.Immersion, 0.05f);

        /// <summary>量化到 1/100（order）与 1/20（浸水）后再比较，避免浮点抖动变成流量。</summary>
        private static bool Near(float left, float right, float tolerance = 0.01f) =>
            MathUtils.Abs(left - right) <= tolerance;

        public bool IsFinite =>
            IsFiniteValue(WalkOrder.X) && IsFiniteValue(WalkOrder.Y) &&
            IsFiniteValue(JumpOrder) && IsFiniteValue(TurnOrder.X) && IsFiniteValue(TurnOrder.Y) &&
            IsFiniteValue(Immersion);

        private static bool IsFiniteValue(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>动画状态消息的编解码（proto：Protocol/action.proto 的 <c>PlayerMotionState</c>）。</summary>
    public static class PlayerMotionCodec
    {
        public static PA.PlayerMotionState ToMessage(int clientId, in PlayerMotionSnapshot motion)
        {
            var message = new PA.PlayerMotionState
            {
                ClientId = clientId,
                ServerTick = motion.ServerTick,
                Flags = (uint)(int)motion.Flags,
                HasWalkOrder = motion.HasWalkOrder,
                JumpOrder = motion.JumpOrder,
                Immersion = motion.Immersion,
                ActiveSlotIndex = motion.ActiveSlotIndex,
                TurnOrder = new PP.Vec2 { X = motion.TurnOrder.X, Y = motion.TurnOrder.Y }
            };
            if (motion.HasWalkOrder)
            {
                message.WalkOrder = new PP.Vec2
                {
                    X = motion.WalkOrder.X, Y = motion.WalkOrder.Y
                };
            }
            return message;
        }

        public static byte[] Encode(int clientId, in PlayerMotionSnapshot motion) =>
            ToMessage(clientId, in motion).ToByteArray();

        public static bool TryConvert(PA.PlayerMotionState message, out int clientId,
            out PlayerMotionSnapshot motion, out string error)
        {
            clientId = message?.ClientId ?? -1;
            motion = default;
            error = null;
            if (message == null)
            {
                error = "动画状态消息为空";
                return false;
            }
            if (message.Flags > (uint)(PlayerMotionFlags.Walking | PlayerMotionFlags.Running |
                PlayerMotionFlags.Jumping | PlayerMotionFlags.Swimming | PlayerMotionFlags.Flying |
                PlayerMotionFlags.Crouching | PlayerMotionFlags.Grounded | PlayerMotionFlags.OnFire |
                PlayerMotionFlags.Hurt | PlayerMotionFlags.Aiming | PlayerMotionFlags.Attacking |
                PlayerMotionFlags.Digging | PlayerMotionFlags.Using | PlayerMotionFlags.Eating |
                PlayerMotionFlags.RowLeft | PlayerMotionFlags.RowRight))
            {
                error = "动画状态位含未定义位：0x" + message.Flags.ToString("X");
                return false;
            }
            Vector2 walkOrder = Vector2.Zero;
            if (message.HasWalkOrder)
            {
                if (message.WalkOrder == null)
                {
                    error = "声明 has_walk_order 却缺少 walk_order";
                    return false;
                }
                walkOrder = new Vector2(message.WalkOrder.X, message.WalkOrder.Y);
            }
            Vector2 turnOrder = message.TurnOrder == null
                ? Vector2.Zero
                : new Vector2(message.TurnOrder.X, message.TurnOrder.Y);
            motion = new PlayerMotionSnapshot(message.ServerTick, (PlayerMotionFlags)(int)message.Flags,
                message.HasWalkOrder, walkOrder, message.JumpOrder, turnOrder, message.Immersion,
                message.ActiveSlotIndex);
            if (!motion.IsFinite)
            {
                error = "动画状态含 NaN/Inf";
                motion = default;
                return false;
            }
            return true;
        }

        public static bool TryDecode(byte[] payload, out int clientId,
            out PlayerMotionSnapshot motion, out string error)
        {
            clientId = -1;
            motion = default;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            PA.PlayerMotionState message;
            try
            {
                message = PA.PlayerMotionState.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                error = "解析动画状态消息失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(message, out clientId, out motion, out error);
        }
    }
}
