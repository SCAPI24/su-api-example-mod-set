using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 状态基类。
    ///
    /// 分工：状态只负责"驻留期间做什么"，**不负责**决定去哪个状态 —— 转移由
    /// <see cref="AiStateMachine"/> 依据转移表（触发 + 守卫 + 优先级）统一裁决。
    /// 这样"复杂状态切换"的规则集中在一处，便于验证与调试。
    /// </summary>
    public abstract class AiState
    {
        private static readonly string[] s_noTags = new string[0];

        protected AiState(string id)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("State id must not be empty.", nameof(id));
            Id = id;
        }

        /// <summary>状态唯一标识（对应 <see cref="AiStateIds"/> 中的常量）。</summary>
        public string Id { get; }

        /// <summary>显示名（日志/调试用），默认同 Id。</summary>
        public virtual string DisplayName
        {
            get { return Id; }
        }

        /// <summary>
        /// 标签：给转移守卫做"成组判断"用（例如 "unsafe"、"busy"），
        /// 避免在守卫里写一长串状态 ID 比较。
        /// </summary>
        public virtual string[] Tags
        {
            get { return s_noTags; }
        }

        /// <summary>
        /// 是否允许被打断。返回 false 的状态只接受 <c>RequiresInterruptible = false</c> 的转移，
        /// 用于保护"不能中途放弃"的关键段落（例如正在放方块的最后一帧）。
        /// </summary>
        public virtual bool IsInterruptible
        {
            get { return true; }
        }

        /// <summary>进入状态：分配资源、设置意图、写黑板。异常会被状态机捕获并回退到安全状态。</summary>
        public virtual void Enter(AiStateContext context) { }

        /// <summary>驻留期间每帧调用一次（在帧首 tick 内，早于游戏读取输入）。</summary>
        public virtual void Tick(AiStateContext context) { }

        /// <summary>离开状态：必须在这里撤销本状态造成的影响（释放按键、关闭界面等）。</summary>
        public virtual void Exit(AiStateContext context) { }

        public bool HasTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return false;
            string[] tags = Tags;
            if (tags == null)
                return false;
            for (int i = 0; i < tags.Length; i++)
            {
                if (string.Equals(tags[i], tag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
