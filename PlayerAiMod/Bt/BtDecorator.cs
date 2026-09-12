using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 装饰器的中断模式 —— 对齐 UE 的 `Observer Aborts`
    /// （None / Self / Lower Priority / Both）。
    /// </summary>
    public enum BtAbortMode
    {
        /// <summary>不中断任何东西（纯条件装饰器：只在节点获得焦点时判一次）。</summary>
        None,
        /// <summary>条件不再满足时中断自己及自己子树。</summary>
        Self,
        /// <summary>条件满足时中断本节点右侧的兄弟分支（更高优先级的分支抢走执行权）。</summary>
        LowerPriority,
        /// <summary>两者都中断。</summary>
        Both
    }

    /// <summary>
    /// 装饰器基类：**不是树节点**，而是挂在节点上的辅助对象
    /// （与 UE 的 `UBTDecorator` 挂在 Composite/Task 上一致）。
    ///
    /// 两种用法：
    ///   · 条件型：<see cref="EvaluateCondition"/> 为假 → 该节点本次判失败（并中断正在运行的分支）。
    ///   · 改写型：<see cref="ModifyResult"/> 把子结果改成别的（ForceSuccess / Inverter / Loop …）。
    /// 观察者（Observer）另由运行时统一驱动，实现"条件变化时中断"。
    /// </summary>
    public abstract class BtDecorator
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>挂载目标（由 <see cref="BtNode.AddDecorator"/> 设置）。</summary>
        public BtNode Host { get; internal set; }

        /// <summary>观察者中断模式（默认 None = 只在获得焦点时判一次）。</summary>
        public BtAbortMode Abort { get; set; } = BtAbortMode.None;

        /// <summary>条件取反（UE 的 Inverse Condition）。</summary>
        public bool Inverse { get; set; }

        /// <summary>观察者是否已被激活（用于观察者检查：只对活动分支生效）。</summary>
        public bool ObserverActive { get; internal set; }

        public abstract string NodeType { get; }

        /// <summary>条件求值（不含 <see cref="Inverse"/>）。</summary>
        protected abstract bool EvaluateCondition(BtContext context);

        /// <summary>结果改写钩子（默认不改）。</summary>
        public virtual BtResult ModifyResult(BtContext context, BtResult result)
        {
            return result;
        }

        /// <summary>最终条件（含取反）。</summary>
        public bool CheckCondition(BtContext context)
        {
            bool value;
            try
            {
                value = EvaluateCondition(context);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][BT] decorator " + Describe() + " threw "
                    + exception.GetType().Name + ": " + exception.Message + " -> treated as false");
                value = false;
            }

            return Inverse ? !value : value;
        }

        public virtual void ResetState()
        {
            ObserverActive = false;
        }

        public string Describe()
        {
            return NodeType + "#" + (Id ?? "?") + (string.IsNullOrEmpty(Name) ? string.Empty : "(" + Name + ")")
                + (Abort != BtAbortMode.None ? " abort=" + Abort : string.Empty)
                + (Inverse ? " inverse" : string.Empty);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 服务基类 —— 挂在**组合节点**上，只在该组合节点处于活动路径时按间隔执行
    /// （对齐 UE 的 `UBTService`：`Interval` + `RandomDeviation`，不参与 tick 链的结果）。
    /// 典型用途：周期性刷新黑板（最近玩家、距离、血量）。
    /// </summary>
    public abstract class BtService
    {
        private double m_nextTickTime = double.NegativeInfinity;

        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>执行间隔（秒）。0 表示每帧都执行。</summary>
        public float Interval { get; set; } = 0.25f;

        /// <summary>随机抖动（秒）：避免多个服务在同一帧扎堆。</summary>
        public float RandomDeviation { get; set; }

        /// <summary>首次执行前是否要等一个间隔（UE 的 `bTickOnActivation` 反向语义）。</summary>
        public bool TickOnActivation { get; set; } = true;

        public abstract string NodeType { get; }

        public int TickCount { get; private set; }

        private static readonly Random s_random = new Random();

        public void Tick(BtContext context)
        {
            if (m_nextTickTime == double.NegativeInfinity)
            {
                if (!TickOnActivation)
                {
                    m_nextTickTime = context.Time + Interval;
                    return;
                }
                m_nextTickTime = context.Time;
            }

            if (context.Time < m_nextTickTime)
                return;

            float wait = Interval;
            if (RandomDeviation > 0f)
                wait += (float)(s_random.NextDouble() * 2.0 - 1.0) * RandomDeviation;

            m_nextTickTime = context.Time + Math.Max(0f, wait);
            TickCount++;

            try
            {
                OnTick(context);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][BT] service " + NodeType + "#" + (Id ?? "?") + " threw "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        protected abstract void OnTick(BtContext context);

        public void ResetState()
        {
            m_nextTickTime = double.NegativeInfinity;
        }

        public override string ToString()
        {
            return NodeType + "#" + (Id ?? "?") + " interval=" + Interval.ToString("0.00") + "s";
        }
    }
}
