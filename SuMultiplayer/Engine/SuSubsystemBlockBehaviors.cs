using System;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// SubsystemBlockBehaviors 外壳：在原版按方块内容建立 behavior 表之后，
    /// 给**每个 contents** 的数组末尾追加一个只观察 OnBlockRemoved 的表现观察器。
    ///
    /// 为什么需要：引擎 DestroyCell/ChangeCell 的碎屑与树叶粒子不在 CellChange 流里，
    /// 而 SubsystemTerrain.ChangeCell（非虚）只会调用该 contents 的 behavior 回调。
    /// 观察器不写格、不生成实体，只把 (oldValue,newValue,x,y,z) 交给 TerrainBridge；
    /// 主机本地挖掘的粒子已经由引擎自己创建，观察器只负责广播；
    /// 客户端编辑落到主机时走 ChangeCell（没有粒子），由 BlockEffectReplicaEngine 补主机表现。
    /// </summary>
    internal static class BlockEffectObserverInstaller
    {
        private const string FieldName = "m_blockBehaviorsByContents";
        private static bool s_missingFieldLogged;

        public static bool TryInstall(SubsystemTerrain terrain)
        {
            if (terrain == null) return false;
            Project project = terrain.Project ?? GameManager.Project;
            if (project == null)
            {
                Log.Warning("[SuMP] blockEffect observer: terrain/Project 为空，" +
                    "本轮无法安装观察器");
                return false;
            }
            SubsystemBlockBehaviors behaviors = project.FindSubsystem<SubsystemBlockBehaviors>(false);
            if (behaviors == null)
            {
                Log.Warning("[SuMP] blockEffect observer: 找不到 SubsystemBlockBehaviors" +
                    "（Project 已存在）→ 本轮无法安装观察器");
                return false;
            }
            try
            {
                FieldInfo field = typeof(SubsystemBlockBehaviors).GetField(FieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(behaviors) is not SubsystemBlockBehavior[][] arrays)
                {
                    if (!s_missingFieldLogged)
                    {
                        s_missingFieldLogged = true;
                        Log.Warning("[SuMP] blockEffect observer: 引擎字段 " + FieldName +
                            " 不存在/类型不符 → 方块破坏表现无法广播（明确失败）");
                    }
                    return false;
                }
                var observer = new HostBlockEffectObserver(terrain);
                for (int i = 0; i < arrays.Length; i++)
                {
                    SubsystemBlockBehavior[] original = arrays[i] ?? Array.Empty<SubsystemBlockBehavior>();
                    var extended = new SubsystemBlockBehavior[original.Length + 1];
                    Array.Copy(original, extended, original.Length);
                    extended[original.Length] = observer;
                    arrays[i] = extended;
                }
                SuMultiplayer.Engine.ModLog.Info("[SuMP][block-effect] block-removal observer installed: " +
                    arrays.Length + " 个 contents 的 behavior 表已追加观察器");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[SuMP] blockEffect observer 安装失败：" + e.GetType().Name + " " +
                    e.Message + "（方块破坏表现无法广播）");
                return false;
            }
        }
    }

    /// <summary>
    /// 只观察方块移除的 behavior；不参与任何写格/实体行为。
    /// 它不依赖 Subsystem 基类的 SubsystemTerrain 字段，避免为新增行为走 Database 注册。
    /// </summary>
    internal sealed class HostBlockEffectObserver : SubsystemBlockBehavior
    {
        private readonly SubsystemTerrain m_terrain;

        public HostBlockEffectObserver(SubsystemTerrain terrain)
        {
            m_terrain = terrain;
        }

        public override int[] HandledBlocks => Array.Empty<int>();

        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (m_terrain is SuSubsystemTerrain shell)
                shell.Bridge?.NotifyHostBlockDestroyed(x, y, z, value, newValue);
        }

        /// <summary>
        /// 树叶被挖时 contents 不变、只有 data 变（秋季 GetDigValue 把 data 改成
        /// IsShaken + Winter），引擎走的是 OnBlockModified 而不是 OnBlockRemoved。
        /// 旧观察器只接 Removed，所以主机端看得到 GetDigValue 生成的落叶粒子，
        /// 但同步事件从未产生。这里补上 data-only 的破坏表现入口。
        /// </summary>
        public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
        {
            if (m_terrain is SuSubsystemTerrain shell)
                shell.Bridge?.NotifyHostBlockDestroyed(x, y, z, oldValue, value);
        }
    }
}
