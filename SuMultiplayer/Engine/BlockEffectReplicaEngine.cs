using System;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    public readonly struct BlockDestroyEffectRecord
    {
        public BlockDestroyEffectRecord(int x, int y, int z, int oldValue, int newValue,
            uint flags, int sourceClientId)
        {
            X = x; Y = y; Z = z;
            OldValue = oldValue; NewValue = newValue;
            Flags = flags; SourceClientId = sourceClientId;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int OldValue { get; }
        public int NewValue { get; }
        public uint Flags { get; }
        /// <summary>0 = 主机本地破坏；&gt;0 = 该客户端编辑请求在主机落地。</summary>
        public int SourceClientId { get; }
    }

    /// <summary>
    /// “方块破坏表现”的引擎适配器（**纯表现**，不写格 / 不生成实体）。
    ///
    /// 只处理树叶类（LeavesBlock / FallenLeavesBlock）：
    ///   · 引擎 DestroyCell 已经会建碎屑；本类只负责把同一事件广播给远端；
    ///   · 客户端编辑落到主机时走 ChangeCell，不会建碎屑；本类补建主机表现；
    ///   · 客户端收到事件后按 oldValue 重建同一份 BlockDebrisParticleSystem /
    ///     LeavesParticleSystem，绝不调用 DestroyCell/GetDigValue，避免二次写格。
    /// </summary>
    public sealed class BlockEffectReplicaEngine : IBlockEffectSink
    {
        public const uint FlagDebris = 1;
        public const uint FlagLeaves = 2;

        public event Action<BlockDestroyEffectRecord> HostBlockDestroyed;

        public void OnHostBlockDestroyed(int x, int y, int z, int oldValue, int newValue,
            int sourceClientId)
        {
            uint flags = Classify(oldValue, newValue);
            if (flags == 0) return;
            ModLog.VerboseInfo("[SuMP][block-effect] host leaf event cell=" + x + "," + y + "," + z +
                " old=" + oldValue + " new=" + newValue + " flags=" + flags +
                " source=" + sourceClientId);
            // 客户端编辑：主机路径是 ChangeCell（不会自己生成碎屑）→ 由这里补主机表现。
            // 主机本地：引擎 DestroyCell 已经生成过，绝不重复建。
            if (sourceClientId > 0)
                Present(x, y, z, oldValue, flags, out _);
            HostBlockDestroyed?.Invoke(new BlockDestroyEffectRecord(x, y, z, oldValue,
                newValue, flags, sourceClientId));
        }

        public bool Present(int x, int y, int z, int oldValue, uint flags, out string reason)
        {
            reason = null;
            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            SubsystemGameWidgets views = project?.FindSubsystem<SubsystemGameWidgets>(false);
            if (terrain?.Terrain == null || particles == null)
            {
                reason = "没有 SubsystemTerrain / SubsystemParticles";
                return false;
            }
            if (y < 1 || y > 255 || oldValue == 0)
            {
                reason = "坐标或 oldValue 非法";
                return false;
            }
            int contents = global::Game.Terrain.ExtractContents(oldValue);
            Block block = contents > 0 && BlocksManager.Blocks != null &&
                contents < BlocksManager.Blocks.Length ? BlocksManager.Blocks[contents] : null;
            if (block == null || !IsLeafBlock(block))
            {
                reason = "不是树叶方块";
                return false;
            }
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float distance = views == null ? 0f : views.CalculateDistanceFromNearestView(center);
            int made = 0;
            if ((flags & FlagDebris) != 0)
            {
                if (distance >= 16f)
                {
                    reason = "超出碎屑距离 16m";
                }
                else
                {
                    try
                    {
                        particles.AddParticleSystem(block.CreateDebrisParticleSystem(
                            terrain, center, oldValue, 1f));
                        made++;
                    }
                    catch (Exception e)
                    {
                        reason = "创建碎屑粒子失败：" + e.GetType().Name + " " + e.Message;
                        return false;
                    }
                }
            }
            if ((flags & FlagLeaves) != 0)
            {
                if (distance >= 32f)
                {
                    if (made == 0) reason = "超出落叶粒子距离 32m";
                }
                else
                {
                    try
                    {
                        // createFallenLeaves:false —— 落叶层是主机权威格，客户端不能因表现再写格。
                        particles.AddParticleSystem(new LeavesParticleSystem(terrain,
                            new Point3(x, y, z), 8, fadeIn: false, createFallenLeaves: false,
                            oldValue));
                        made++;
                    }
                    catch (Exception e)
                    {
                        reason = "创建落叶粒子失败：" + e.GetType().Name + " " + e.Message;
                        return false;
                    }
                }
            }
            if (made == 0)
            {
                reason ??= "本端距离外，未创建任何粒子";
                return false;
            }
            return true;
        }

        public static uint Classify(int oldValue, int newValue)
        {
            int oldContents = global::Game.Terrain.ExtractContents(oldValue);
            int newContents = global::Game.Terrain.ExtractContents(newValue);
            if (oldContents == 0) return 0;
            if (BlocksManager.Blocks == null || oldContents >= BlocksManager.Blocks.Length) return 0;
            Block block = BlocksManager.Blocks[oldContents];
            if (!IsLeafBlock(block)) return 0;
            uint flags = 0;
            int newData = global::Game.Terrain.ExtractData(newValue);
            if (oldContents == newContents)
            {
                // data-only（树叶挖掉后 contents 仍是树叶、data 变成 shaken/Winter）。
                // 只有 GetDigValue 的“手动摇晃”才需要表现；季相规范化/世界演化不产生粒子。
                if (block is DeciduousLeavesBlock &&
                    DeciduousLeavesBlock.GetIsShaken(newData))
                    flags |= FlagLeaves;
                return flags;
            }
            // 引擎 DestroyCell 只有 contents 真的被换掉才 showDebris。
            flags |= FlagDebris;
            // 主机端 GetDigValue 已生成落叶粒子；old data 可能已规范化成 Winter，
            // 旧逻辑据季节条件会漏发 FlagLeaves。所有 DeciduousLeavesBlock 都带落叶表现。
            if (block is DeciduousLeavesBlock) flags |= FlagLeaves;
            return flags;
        }

        private static bool IsLeafBlock(Block block) =>
            block is LeavesBlock || block is FallenLeavesBlock;
    }

    /// <summary>TerrainBridge → 表现适配器的单一入口（主机侧方块移除观察器调用）。</summary>
    public interface IBlockEffectSink
    {
        void OnHostBlockDestroyed(int x, int y, int z, int oldValue, int newValue,
            int sourceClientId);
    }
}
