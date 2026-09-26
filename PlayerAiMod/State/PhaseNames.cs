using System;

namespace PlayerAiMod
{
    /// <summary>
    /// **相位**（plan §4.13 双状态机）：世界外 / 加载中 / 世界内。这里是不依赖游戏的唯一口径。
    ///
    /// 为什么必须只有一个口径：相位同时决定三件事 ——
    ///   · 喂给 Laya 的摘要长什么样（世界外没有生命/物品，问了也是噪声）；
    ///   · 该跑哪套问题库、哪棵兜底树；
    ///   · **交接**要不要做（释放输入 / 取消在途判定 / 清缓存）。
    /// 这三件事如果各自判一次"世界加载了没"，就会出现"摘要说世界外、树却按世界内点 UI"这种
    /// 自相矛盾的现场，而且**只在边界那一两帧**发生，最难复现。
    ///
    /// 所以：<see cref="StateDigestCompiler"/> 的 `phase` 字段、运行时的相位交接、自检
    /// 全部走 <see cref="Of"/> 这一个函数。
    /// </summary>
    public static class PhaseNames
    {
        /// <summary>世界外（主菜单 / 选档界面）。</summary>
        public const string Front = "front";

        /// <summary>世界已加载、但本端角色还没就绪（**过渡态**）。</summary>
        public const string Loading = "loading";

        /// <summary>世界内，本端角色就绪。</summary>
        public const string World = "world";

        /// <summary>由"世界加载了没 + 本端角色就绪了没"定相位。</summary>
        public static string Of(bool worldLoaded, bool hasPlayer)
        {
            if (!worldLoaded)
                return Front;
            return hasPlayer ? World : Loading;
        }

        public static bool IsWorld(string phase)
        {
            return string.Equals(phase, World, StringComparison.Ordinal);
        }

        public static bool IsFront(string phase)
        {
            return string.Equals(phase, Front, StringComparison.Ordinal);
        }

        /// <summary>过渡态：世界起来了但角色还没就绪 —— 只有这一档"什么都不该推进"。</summary>
        public static bool IsLoading(string phase)
        {
            return string.Equals(phase, Loading, StringComparison.Ordinal);
        }

        /// <summary>是不是本模块认识的相位（不认识的按"变了"处理，走最保守的交接）。</summary>
        public static bool IsKnown(string phase)
        {
            return IsFront(phase) || IsLoading(phase) || IsWorld(phase);
        }
    }
}
