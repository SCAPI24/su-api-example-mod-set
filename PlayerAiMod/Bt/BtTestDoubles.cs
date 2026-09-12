using Engine;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 自检共用的假件（**不碰游戏**）：<see cref="BtSelfTest"/> 与 <see cref="PackageSelfTest"/> 都用它，
    /// 免得两套自检各自维护一份"假传感器/假执行器"而慢慢漂移。
    /// </summary>
    internal sealed class BtTestSensor : IAiSensor
    {
        public AiActorView Target;

        public bool HasTarget;

        public bool Ready = true;

        public bool InputAccepted = true;

        /// <summary>让"找得到目标"的用例显式设定目标（不设定时所有查找都失败）。</summary>
        public void SetTarget(AiActorView view)
        {
            Target = view;
            HasTarget = true;
        }

        public void ClearTarget()
        {
            Target = default(AiActorView);
            HasTarget = false;
        }

        public bool IsReady
        {
            get { return Ready; }
        }

        public bool IsInputAccepted
        {
            get { return InputAccepted; }
        }

        public string PlayerName
        {
            get { return "self"; }
        }

        public Vector3 Position
        {
            get { return Vector3.Zero; }
        }

        public Vector3 EyePosition
        {
            get { return new Vector3(0f, 1.6f, 0f); }
        }

        public Vector3 Velocity
        {
            get { return Vector3.Zero; }
        }

        public float YawRadians
        {
            get { return 0f; }
        }

        public float Health
        {
            get { return 1f; }
        }

        public bool TryGetPitch(out float pitchRadians)
        {
            pitchRadians = 0f;
            return false;
        }

        public bool TryFindPlayer(string name, out AiActorView view)
        {
            view = Target;
            return HasTarget;
        }

        public bool TryFindNearestPlayer(out AiActorView view)
        {
            view = Target;
            return HasTarget;
        }
    }

    /// <summary>假执行器：记录被按住的键与调用次数，用来断言"收尾有没有释放输入"。</summary>
    internal sealed class BtTestActuator : IAiActuator
    {
        public readonly List<string> HeldKeys = new List<string>();

        public int LookAtCalls;

        public int ReleaseAllCalls;

        public bool IsReady
        {
            get { return true; }
        }

        public void Look(float yawRadians, float pitchRadians)
        {
        }

        public void LookAt(Vector3 worldPoint)
        {
            LookAtCalls++;
        }

        /// <summary>累计视角增量（回放自检断言"转了多少"）。</summary>
        public float LookDeltaYaw;
        public float LookDeltaPitch;
        public int LookDeltaCalls;

        public void LookDelta(float yawRadians, float pitchRadians)
        {
            LookDeltaCalls++;
            LookDeltaYaw += yawRadians;
            LookDeltaPitch += pitchRadians;
        }

        /// <summary>本执行器拒绝所有按键（模拟"键名在这台机器上不存在/输入不可用"）。</summary>
        public bool RejectKeys;

        /// <summary>被拒绝的次数（自检据此断言"没有静默丢输入"）。</summary>
        public int RejectedKeyCount;

        /// <summary>按过的键（脉冲）历史。</summary>
        public readonly List<string> PulsedKeys = new List<string>();

        /// <summary>历史上出现过的所有按住键（回放结束后仍可查"到底按没按过 W"）。</summary>
        public readonly List<string> HeldKeysSeen = new List<string>();

        /// <summary>回放里做过的 UI 点击（选择器/坐标），自检据此断言"菜单点击真的被重放了"。</summary>
        public readonly List<string> UiClicks = new List<string>();

        public void HoldKey(string key, bool down)
        {
            if (RejectKeys)
            {
                RejectedKeyCount++;
                return;
            }

            if (down)
            {
                if (!HeldKeysSeen.Contains(key))
                    HeldKeysSeen.Add(key);
                if (!HeldKeys.Contains(key))
                    HeldKeys.Add(key);
            }
            else
            {
                HeldKeys.Remove(key);
            }
        }

        public void PulseKey(string key, int holdMilliseconds)
        {
            if (RejectKeys)
            {
                RejectedKeyCount++;
                return;
            }
            PulsedKeys.Add(key);
        }

        public void MouseButton(string button, bool down)
        {
        }

        public void MouseClick(string button, int holdMilliseconds)
        {
        }

        public void Wheel(int delta)
        {
        }

        public bool UiClick(string selectorOrPoint)
        {
            UiClicks.Add(selectorOrPoint);
            return true;
        }

        public void ReleaseAll()
        {
            ReleaseAllCalls++;
            HeldKeys.Clear();
        }
    }

    /// <summary>
    /// 自检共用的"AI 宿主"（<see cref="IAiTreeHost"/> 的假件，不碰游戏）。
    ///
    /// 命令层自检与模式层自检都用它：前者验证"命令 → 装载 → 状态"，后者把真正的
    /// <see cref="AiStateMachine"/> 绑上来验证模式推导。带上一个可选的模式机，
    /// 于是 <see cref="Mode"/> 和 <see cref="AiActor"/> 用的是同一套映射（<see cref="AiModeMap"/>）。
    /// </summary>
    internal sealed class AiTestHost : IAiTreeHost
    {
        private readonly AiBlackboard m_blackboard = new AiBlackboard();
        private readonly BtRuntime m_tree;

        public AiTestHost(string name = "selftest-host")
        {
            HostName = name;
            TestSensor = new BtTestSensor();
            TestActuator = new BtTestActuator();
            m_tree = new BtRuntime(m_blackboard, TestSensor, TestActuator);
        }

        /// <summary>假传感器（用例可以设目标、关掉"输入被接受"）。</summary>
        public BtTestSensor TestSensor { get; }

        /// <summary>假执行器：`ReleaseAllCalls` 是判断"收尾有没有放键"的权威计数。</summary>
        public BtTestActuator TestActuator { get; }

        /// <summary>被要求释放输入的次数（用例据此断言"收尾有没有放键"）。</summary>
        public int ReleaseCount;

        public int LoadTreeCalls;

        public string HostName { get; }

        public bool Enabled { get; set; } = true;

        public bool Ready { get; set; } = true;

        /// <summary>可选的模式层状态机（绑上来后 Mode 由它推导）。</summary>
        public AiStateMachine Machine { get; set; }

        /// <summary>是否处于全局暂停（真实实现里来自 PlayerAiRuntime）。</summary>
        public bool Paused { get; set; }

        public bool IsReady
        {
            get { return Ready; }
        }

        public BtRuntime Tree
        {
            get { return m_tree; }
        }

        public AiBlackboard Blackboard
        {
            get { return m_blackboard; }
        }

        public IAiSensor Sensors
        {
            get { return TestSensor; }
        }

        public IAiActuator Actuators
        {
            get { return TestActuator; }
        }

        public bool HasTree
        {
            get { return m_tree.TreeId != null || m_tree.SourcePackage != null; }
        }

        /// <summary>录制标记（用例可以直接置位来验证"录制模式"的进入/退出）。</summary>
        public bool IsRecording { get; set; }

        public AiMode Mode
        {
            get
            {
                if (Machine == null)
                    return HasTree ? AiMode.Tree : AiMode.Idle;
                bool faulted = Machine.IsFaulted || m_tree.LastError != null;
                return AiModeMap.FromState(Machine.CurrentStateId, Paused, faulted);
            }
        }

        public string MachineStateId
        {
            get { return Machine != null ? Machine.CurrentStateId : null; }
        }

        public string MachinePreviousStateId
        {
            get { return Machine != null ? Machine.PreviousStateId : null; }
        }

        public float MachineTimeInState
        {
            get { return Machine != null ? Machine.TimeInState : 0f; }
        }

        public int MachineTransitionCount
        {
            get { return Machine != null ? Machine.TransitionCount : 0; }
        }

        public bool MachineFaulted
        {
            get { return Machine != null && Machine.IsFaulted; }
        }

        public bool LoadTree(CompiledTree compiled, bool start)
        {
            LoadTreeCalls++;
            if (compiled == null || compiled.Root == null || compiled.HasErrors)
                return false;

            m_tree.ReplaceRoot(compiled.Root, false, compiled.PackageId,
                compiled.SourcePath, compiled.SourceHash);
            if (start)
                m_tree.Start();
            return true;
        }

        public void StopTree(string reason)
        {
            m_tree.Stop(reason ?? "stopped");
            m_tree.ReplaceRoot(null, false);
            ReleaseInput();
        }

        public void ReleaseInput()
        {
            ReleaseCount++;
            if (Actuators != null)
                Actuators.ReleaseAll();
        }
    }
}
