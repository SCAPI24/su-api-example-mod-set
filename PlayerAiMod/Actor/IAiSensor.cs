using Engine;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>观察到的某个角色/实体的只读快照（值类型，可安全跨帧保存）。</summary>
    public struct AiActorView
    {
        public string Name;
        public int PlayerIndex;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Distance;
        public bool IsSelf;
        public bool IsPlayer;

        /// <summary>
        /// 这是哪一类东西：`player` / `creature` / `pickable`（掉落物）。
        /// 由扩展传感器（<see cref="IAiWorldSensor"/>）填写；只有玩家时它是 `player`。
        /// </summary>
        public string Kind;

        /// <summary>生物的类别掩码（<see cref="Game.CreatureCategory"/>；掉落物为 0）。</summary>
        public int CategoryMask;

        /// <summary>掉落物/方块的方块值（其它情况为 0）。</summary>
        public int Value;

        /// <summary>掉落物的数量（其它情况为 0）。</summary>
        public int Count;

        public override string ToString()
        {
            return (Name ?? "<unnamed>") + (IsSelf ? "(self)" : string.Empty)
                + (string.IsNullOrEmpty(Kind) ? string.Empty : "[" + Kind + "]")
                + " pos=" + Position.ToString()
                + " dist=" + Distance.ToString("0.00");
        }
    }

    /// <summary>自身状态快照（生命体征 + 本体状态）。值类型，可安全跨帧保存。</summary>
    public struct AiSelfState
    {
        public float Health;
        public float Air;
        public float AirCapacity;

        /// <summary>饥饿度（1 = 饱，0 = 饿死边缘）。</summary>
        public float Food;

        /// <summary>耐力（0~1）。</summary>
        public float Stamina;

        /// <summary>困倦度（1 = 撑不住要睡）。</summary>
        public float Sleep;

        /// <summary>体温是否过低/过高由游戏内部判定，这里给原始值。</summary>
        public float Temperature;

        /// <summary>潮湿程度（0~1）。</summary>
        public float Wetness;

        public Vector3 Position;
        public Vector3 Velocity;
        public float YawRadians;

        /// <summary>脚下是否有支撑（<c>ComponentBody.StandingOnValue</c> 有值）。</summary>
        public bool IsOnGround;

        /// <summary>脚下那块方块的值（0 = 悬空）。</summary>
        public int StandingOnValue;

        /// <summary>浸入液体的深度（米，0 = 没在水里）。</summary>
        public float ImmersionDepth;
    }

    /// <summary>地形射线命中结果（只读）。</summary>
    public struct AiBlockHit
    {
        /// <summary>方块 id（<c>Terrain.ExtractContents</c> 之后的值）。</summary>
        public int Contents;

        /// <summary>完整方块值（含 data / light 位）。</summary>
        public int Value;

        /// <summary>方块名（取不到就是 `#<id>`）。</summary>
        public string Name;

        public float Distance;
        public int X;
        public int Y;
        public int Z;
        public Vector3 HitPoint;

        public override string ToString()
        {
            return (Name ?? "?") + "(" + X + "," + Y + "," + Z + ") d=" + Distance.ToString("0.00");
        }
    }

    /// <summary>
    /// 只读观察层。铁律：传感器**只读**，绝不写游戏状态。
    ///
    /// 允许：无限读取（世界/实体/背包/界面/事件）。优势而非特权 —— 读得再快也不改变游戏。
    /// </summary>
    public interface IAiSensor
    {
        /// <summary>世界与角色是否就绪（有 Project、有 ComponentBody 等）。</summary>
        bool IsReady { get; }

        /// <summary>
        /// 本帧注入的输入是否真的会被游戏采纳。
        /// Source: Survivalcraft/Game/ComponentInput.cs:91-94 —— 窗口不在前台或 PlayerData 未就绪时，
        /// 游戏会把 PlayerInput 整个置空；此时"按了键但没反应"，AI 必须能看出来，否则会误判。
        /// </summary>
        bool IsInputAccepted { get; }

        string PlayerName { get; }

        Vector3 Position { get; }
        Vector3 EyePosition { get; }
        Vector3 Velocity { get; }

        /// <summary>水平朝向（弧度），取自身体旋转。</summary>
        float YawRadians { get; }

        /// <summary>俯仰角（弧度）。需要读 ComponentLocomotion 的私有后端字段，移植输入层时一起补。</summary>
        bool TryGetPitch(out float pitchRadians);

        float Health { get; }

        /// <summary>按玩家名查找（大小写不敏感）。联机时远端玩家也是普通 ComponentPlayer。</summary>
        bool TryFindPlayer(string name, out AiActorView view);

        /// <summary>找最近的其它玩家。</summary>
        bool TryFindNearestPlayer(out AiActorView view);
    }

    /// <summary>路径请求的状态（<see cref="IAiWorldSensor"/> 的寻路部分）。</summary>
    public enum AiPathStatus
    {
        /// <summary>还没请求过（或已被清掉）。</summary>
        Idle,

        /// <summary>已排队 / A* 正在算（游戏侧是**后台线程 + 每 250ms 一条**的队列）。</summary>
        Searching,

        /// <summary>路径可用（<c>CopyPath</c> 能取到航点）。</summary>
        Ready,

        /// <summary>没找到路、或请求被拒（队列满）。</summary>
        Failed
    }

    /// <summary>
    /// **扩展**观察层：生命体征 / 生物 / 掉落物 / 方块 / 视线 / 寻路。
    ///
    /// 为什么不直接塞进 <see cref="IAiSensor"/>：纯逻辑自检里的传感器桩（`BtTestSensor`、
    /// 动作包自检的 `DriftingSensor`）只要基础那几项就有意义 —— 逼它们实现"方块射线"，
    /// 只会让桩变成"假装懂世界"的假货。所以扩展能力单独一个接口：
    /// 真实游戏里的 `PlayerSensor` 实现它；服务用 `as` 取，取不到就**跳过写入并说明原因**，
    /// 于是同一棵行为树在纯逻辑自检里也能跑（只是这些键不会被写）。
    ///
    /// 全部方法都只读：射线不落地、不加力、不改任何游戏状态。
    /// </summary>
    public interface IAiWorldSensor
    {
        /// <summary>自身生命体征与本体状态。</summary>
        bool TryGetSelfState(out AiSelfState state);

        /// <summary>
        /// 最近的**生物**（不含玩家）。`categoryMask` 为 0 = 不限类别，
        /// 否则按 <c>Game.CreatureCategory</c> 位掩码过滤（1=陆地掠食者 2=陆地其它 4=水中掠食者 8=水中其它 16=鸟）。
        /// </summary>
        bool TryFindNearestCreature(int categoryMask, float maxDistance, out AiActorView view);

        /// <summary>最近的**掉落物**（Pickable）。</summary>
        bool TryFindNearestPickable(float maxDistance, out AiActorView view);

        /// <summary>从眼睛沿 <paramref name="direction"/> 射线，取遇到的第一个非空气方块。</summary>
        bool TryRaycastBlock(Vector3 direction, float maxDistance, out AiBlockHit hit);

        /// <summary>
        /// 从**指定起点**沿 <paramref name="direction"/> 射线（"通用探测"要能换起点：
        /// 从别人的头、从黑板里的某个点出发）。<paramref name="direction"/> 不必归一化。
        /// </summary>
        bool TryRaycastBlockFrom(Vector3 origin, Vector3 direction, float maxDistance,
            out AiBlockHit hit);

        /// <summary>
        /// 按坐标读一个格子（挖方块时用来判"挖掉了没"）。
        /// 返回 false 只表示**世界没就绪**；格子里是空气时返回 true 且 `contents = 0`。
        /// </summary>
        bool TryPeekBlock(int x, int y, int z, out int contents, out string name);

        /// <summary>到某个世界坐标点的视线是否通畅（不被地形挡住）。`distance` 是到目标的距离。</summary>
        bool HasLineOfSight(Vector3 target, out float distance);

        // ---------------------------------------------------------------- 模型节点（骨骼）

        /// <summary>
        /// 取某个 actor 的**模型节点**（骨骼）世界坐标，例如人形模型的
        /// `Head` / `Body` / `Hand1` / `Hand2` / `Leg1` / `Leg2`。
        ///
        /// 为什么需要它：`AiActorView.Position` 是**脚底**，而"看得见头吗""瞄他的手"这类问题
        /// 需要的是那个具体节点在世界里的位置。人形模型里 `Body` 骨骼携带世界位置
        /// （Source: ComponentHumanModel.cs:411），头/手/腿只带相对旋转，所以
        /// **节点世界坐标 = 静止姿态下的骨骼偏移链 × ComponentBody.Matrix**（Source: ComponentBody.Matrix）。
        ///
        /// 刻意**不读动画后的骨骼**：动画只在"模型对本地相机可见"时才跑
        /// （Source: SubsystemModelsRenderer.cs:77-92 PrepareModel 只处理 IsVisibleForCamera 的模型），
        /// 拿它当判据会在"目标在背后"时读到过期值。静止姿态 + body matrix 与渲染无关，永远可用。
        ///
        /// 找不到骨骼（名字不对/模型没有该节点）返回 false；`world` 置零。
        /// </summary>
        bool TryGetBoneWorldPosition(AiActorView actor, string boneName, out Vector3 world);

        /// <summary>列出该 actor 模型的全部骨骼名与它们的世界坐标（名字写错时用来发现正确名字）。</summary>
        bool TryListBones(AiActorView actor, out List<string> names, out List<Vector3> worldPositions);

        // ---------------------------------------------------------------- 寻路（游戏侧 A*）

        /// <summary>
        /// 异步请求一条路径：起点 = 自己脚下，终点 = <paramref name="destination"/>。
        ///
        /// 走的是游戏自己的 `SubsystemPathfinding.QueuePathSearch`（A* + 路径平滑）——
        /// **这是只读查询**（不改世界、不动角色），只是借用游戏已经调好的寻路器，
        /// 而不是自己写一套会在门口撞墙的直线走法。
        ///
        /// 注意它**不是同步**的：请求进队列，由后台线程每 250ms 处理一条（队列上限 10），
        /// 所以要轮询 <see cref="GetPathStatus"/>。返回 false = 连请求都没发出去（`error` 说明原因）。
        /// </summary>
        bool TryRequestPath(Vector3 destination, float arriveRadius, int maxPositionsToCheck,
            out string error);

        /// <summary>当前路径请求的状态。</summary>
        AiPathStatus GetPathStatus();

        /// <summary>
        /// 把已就绪的路径拷进缓冲区，**顺序是从近到远**（index 0 = 离自己最近的航点）。
        ///
        /// 这个顺序不是随便定的：游戏里 `AStar.BuildPathFromEndNode` 是从**终点往起点**填数组的
        /// （`Path[0]` 是终点），游戏自己的 `ComponentPathfinding` 也是从数组**末尾**取下一个航点。
        /// 这里翻过来放，跟随逻辑就能自然地从 0 往后走。
        /// </summary>
        int CopyPath(Vector3[] buffer);

        /// <summary>丢掉当前请求/路径（不再跟随时调用；不影响别的生物的寻路请求）。</summary>
        void ClearPath();
    }
}
