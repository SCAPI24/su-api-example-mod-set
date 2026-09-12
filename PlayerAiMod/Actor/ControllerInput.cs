using Engine;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// **控制器的动作层**：有玩家输入来源（在世界里）就转发给它，没有（主菜单 / 刚退出世界）
    /// 就退回"只点 UI"（<see cref="UiOnlyActuator"/> 那套：引擎软光标 + 鼠标状态）。
    ///
    /// 关键是 `IsReady` 的语义：**只要有一条通道可用就算就绪** ——
    /// 世界里是玩家输入通道，世界外是 UI 注入通道。所以同一棵树在主菜单也能跑、进世界继续跑。
    /// 世界外的按键/视角类调用**空实现**（不假装成功）：世界外的"按住 W"没有任何意义。
    /// </summary>
    internal sealed class ControllerActuator : IAiActuator
    {
        private readonly IAiActuator m_uiActuator;
        private IPlayerInputProvider m_world;

        public ControllerActuator(IAiActuator uiActuator)
        {
            m_uiActuator = uiActuator;
        }

        /// <summary>当前的世界输入来源（null = 没有世界 / 没有可驱动角色）。</summary>
        public IPlayerInputProvider World
        {
            get { return m_world; }
        }

        public void Bind(IPlayerInputProvider world)
        {
            if (ReferenceEquals(m_world, world))
                return;

            // 换绑之前先让旧的那个把键放掉：不然会出现"角色 A 按住的 W 留在角色 B 身上"。
            if (m_world != null)
                m_world.ReleaseInput();
            m_world = world;
        }

        private IAiActuator WorldActuator
        {
            get
            {
                IPlayerInputProvider world = m_world;
                return world != null && world.IsReady ? world.Actuators : null;
            }
        }

        public bool IsReady
        {
            get
            {
                if (WorldActuator != null)
                    return true;
                return m_uiActuator != null && m_uiActuator.IsReady;
            }
        }

        /// <summary>UI 点击：世界里走玩家的执行器（同一个注入器），世界外走 UI 兜底。</summary>
        public bool UiClick(string selectorOrPoint)
        {
            return UiClick(selectorOrPoint, "direct");
        }

        /// <summary>UI 点击（带点法）：世界里走玩家的执行器（同一个注入器），世界外走 UI 兜底。</summary>
        public bool UiClick(string selectorOrPoint, string mode)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                return world.UiClick(selectorOrPoint, mode);
            return m_uiActuator != null && m_uiActuator.UiClick(selectorOrPoint, mode);
        }

        // ---- 世界外没有意义的动作：没有玩家输入通道就空实现（不假装成功） ----

        public void Look(float yawRadians, float pitchRadians)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.Look(yawRadians, pitchRadians);
        }

        public void LookAt(Vector3 worldPoint)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.LookAt(worldPoint);
        }

        public void LookDelta(float yawRadians, float pitchRadians)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.LookDelta(yawRadians, pitchRadians);
        }

        public void HoldKey(string key, bool down)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.HoldKey(key, down);
        }

        public void PulseKey(string key, int holdMilliseconds)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.PulseKey(key, holdMilliseconds);
        }

        public void MouseButton(string button, bool down)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.MouseButton(button, down);
        }

        public void MouseClick(string button, int holdMilliseconds)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.MouseClick(button, holdMilliseconds);
        }

        public void Wheel(int delta)
        {
            IAiActuator world = WorldActuator;
            if (world != null)
                world.Wheel(delta);
        }

        /// <summary>
        /// 释放**世界输入**（玩家按住的键、鼠标）。
        ///
        /// ⚠️ **故意不动 UI 注入**：`IAiActuator.ReleaseAll()` 会被任务收尾 / 动作包回放结束时
        /// 频繁调用（运行时在世界外甚至每帧都会走一遍释放），而 UI 那套"释放"的语义是
        /// **取消整个注入状态与会话** —— 一次软光标点击要走"移动 → 按下 → 抬起"好几个帧，
        /// 中途被清一次就永远派生不出 Click：表现就是**光标移过去了、界面纹丝不动**
        /// （用户实测报的正是这个）。要撤 UI 注入请显式调 <see cref="CancelUiInjection"/>，
        /// 只有"停止 / 禁用 / 输入释放"这类显式路径才该做。
        /// </summary>
        public void ReleaseAll()
        {
            IPlayerInputProvider world = m_world;
            if (world != null && world.Actuators != null)
                world.Actuators.ReleaseAll();
        }

        /// <summary>真的把 UI 注入也撤掉（取消软光标会话、清残留）。只给显式停用路径用。</summary>
        public void CancelUiInjection()
        {
            if (m_uiActuator != null)
                m_uiActuator.ReleaseAll();
        }
    }

    /// <summary>
    /// **控制器的传感器**：有玩家就转发给玩家传感器，没有就"没有传感器"。
    ///
    /// 世界外故意让 `IsReady=false`：没有角色就没有位置/朝向可比，动作包的漂移检查
    /// 会因此直接跳过（`ScatPlayer.CheckDrift` 本来就在 `Sensors == null || !IsReady` 时跳过），
    /// 而不是报出一堆假的"漂移失败"。
    /// </summary>
    internal sealed class ControllerSensor : IAiSensor, IAiWorldSensor
    {
        private IPlayerInputProvider m_world;

        public void Bind(IPlayerInputProvider world)
        {
            m_world = world;
        }

        private IAiSensor World
        {
            get
            {
                IPlayerInputProvider world = m_world;
                return world != null && world.IsReady ? world.Sensors : null;
            }
        }

        public bool IsReady
        {
            get { return World != null && World.IsReady; }
        }

        public bool IsInputAccepted
        {
            get { return World != null && World.IsInputAccepted; }
        }

        public string PlayerName
        {
            get
            {
                IAiSensor world = World;
                return world != null ? world.PlayerName : null;
            }
        }

        public Vector3 Position
        {
            get
            {
                IAiSensor world = World;
                return world != null ? world.Position : Vector3.Zero;
            }
        }

        public Vector3 EyePosition
        {
            get
            {
                IAiSensor world = World;
                return world != null ? world.EyePosition : Vector3.Zero;
            }
        }

        public Vector3 Velocity
        {
            get
            {
                IAiSensor world = World;
                return world != null ? world.Velocity : Vector3.Zero;
            }
        }

        public float YawRadians
        {
            get
            {
                IAiSensor world = World;
                return world != null ? world.YawRadians : 0f;
            }
        }

        public bool TryGetPitch(out float pitchRadians)
        {
            IAiSensor world = World;
            if (world != null)
                return world.TryGetPitch(out pitchRadians);
            pitchRadians = 0f;
            return false;
        }

        public float Health
        {
            get
            {
                IAiSensor world = World;
                return world != null ? world.Health : 0f;
            }
        }

        public bool TryFindPlayer(string name, out AiActorView view)
        {
            IAiSensor world = World;
            if (world != null)
                return world.TryFindPlayer(name, out view);
            view = default(AiActorView);
            return false;
        }

        public bool TryFindNearestPlayer(out AiActorView view)
        {
            IAiSensor world = World;
            if (world != null)
                return world.TryFindNearestPlayer(out view);
            view = default(AiActorView);
            return false;
        }

        // ---------------------------------------------------------------- 扩展观察层
        //
        // 必须在这里转发：运行时真正拿到的传感器是**控制器传感器**，不是 PlayerSensor。
        // 少了这段，`Service.UpdateSelf` 这类服务会以为"这台机器没有扩展观察能力"而静默跳过
        // —— 那种"功能都在、就是不生效"的问题最难查。

        private IAiWorldSensor WorldExtras
        {
            get { return World as IAiWorldSensor; }
        }

        public bool TryGetSelfState(out AiSelfState state)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryGetSelfState(out state);
            state = default(AiSelfState);
            return false;
        }

        public bool TryFindNearestCreature(int categoryMask, float maxDistance, out AiActorView view)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryFindNearestCreature(categoryMask, maxDistance, out view);
            view = default(AiActorView);
            return false;
        }

        public bool TryFindNearestPickable(float maxDistance, out AiActorView view)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryFindNearestPickable(maxDistance, out view);
            view = default(AiActorView);
            return false;
        }

        public bool TryRaycastBlock(Vector3 direction, float maxDistance, out AiBlockHit hit)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryRaycastBlock(direction, maxDistance, out hit);
            hit = default(AiBlockHit);
            return false;
        }

        public bool TryRaycastBlockFrom(Vector3 origin, Vector3 direction, float maxDistance,
            out AiBlockHit hit)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryRaycastBlockFrom(origin, direction, maxDistance, out hit);
            hit = default(AiBlockHit);
            return false;
        }

        public bool TryPeekBlock(int x, int y, int z, out int contents, out string name)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryPeekBlock(x, y, z, out contents, out name);
            contents = 0;
            name = null;
            return false;
        }

        public bool HasLineOfSight(Vector3 target, out float distance)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.HasLineOfSight(target, out distance);
            distance = 0f;
            return false;
        }

        // 模型节点（骨骼）：**必须转发**，否则"服务静默跳过"（这套接口漏转发的坑踩过不止一次）
        public bool TryGetBoneWorldPosition(AiActorView actor, string boneName, out Vector3 world)
        {
            IAiWorldSensor extras = WorldExtras;
            if (extras != null)
                return extras.TryGetBoneWorldPosition(actor, boneName, out world);
            world = Vector3.Zero;
            return false;
        }

        public bool TryListBones(AiActorView actor, out List<string> names,
            out List<Vector3> worldPositions)
        {
            IAiWorldSensor extras = WorldExtras;
            if (extras != null)
                return extras.TryListBones(actor, out names, out worldPositions);
            names = new List<string>();
            worldPositions = new List<Vector3>();
            return false;
        }

        public bool TryRequestPath(Vector3 destination, float arriveRadius, int maxPositionsToCheck,
            out string error)
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                return world.TryRequestPath(destination, arriveRadius, maxPositionsToCheck, out error);
            error = "no world sensor attached";
            return false;
        }

        public AiPathStatus GetPathStatus()
        {
            IAiWorldSensor world = WorldExtras;
            return world != null ? world.GetPathStatus() : AiPathStatus.Failed;
        }

        public int CopyPath(Vector3[] buffer)
        {
            IAiWorldSensor world = WorldExtras;
            return world != null ? world.CopyPath(buffer) : 0;
        }

        public void ClearPath()
        {
            IAiWorldSensor world = WorldExtras;
            if (world != null)
                world.ClearPath();
        }
    }
}
