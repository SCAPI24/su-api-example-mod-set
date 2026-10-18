using System;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 注入点：替换 <c>Game.SubsystemSpawn</c>（旧实现 SuSubsystemSpawn 的同一条注入路径）。
    ///
    /// 行为（逐条对应旧实现）：
    ///   · 客户端（联机会话中）= **完全不跑原生生成**：不分配 spawn chunk、不恢复 SpawnsData、
    ///     不按本机视野 Despawn；客户端只显示主机分配的复制体。
    ///     Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemSpawn.cs
    ///   · 主机 = 照常跑原生模型，同时用"远端脱困保护"包住原生 Update，避免远端角色附近的
    ///     AutoDespawn 生物被引擎按本机 GameWidget 判定回收。
    ///     Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:1041-1089
    ///
    /// Coordinator 由生物模块每帧从组合根注入（实例引用），没有静态状态。
    /// </summary>
    public sealed class SuSpawnSubsystem : SubsystemSpawn, IUpdateable
    {
        public CreatureWorldAdapter Coordinator { get; set; }

        void IUpdateable.Update(float dt)
        {
            CreatureWorldAdapter coordinator = Coordinator;
            if (coordinator != null && coordinator.SuppressNativeSpawning) return;

            IDisposable protection = coordinator?.BeginRemoteDespawnProtectionScope();
            try
            {
                base.Update(dt);
            }
            finally
            {
                protection?.Dispose();
            }
        }
    }

    /// <summary>
    /// 注入点：替换 <c>Game.SubsystemCreatureSpawn</c>（旧实现 SuSubsystemCreatureSpawn）。
    ///
    ///   · 客户端（联机会话中）= **不跑 SpawnRandomCreature / SpawnChunkCreatures**，
    ///     从根上杜绝"客户端自己随机生成一套动物"。
    ///     Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemCreatureSpawn.cs
    ///   · 主机 = 照常跑原生选择；原生 Update 期间把联机角色的 GameWidget 临时挂进
    ///     SubsystemGameWidgets，让 SpawnRandomCreature 围绕远端角色选点。
    ///     Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:834-867
    /// </summary>
    public sealed class SuCreatureSpawnSubsystem : SubsystemCreatureSpawn, IUpdateable
    {
        public CreatureWorldAdapter Coordinator { get; set; }

        void IUpdateable.Update(float dt)
        {
            CreatureWorldAdapter coordinator = Coordinator;
            if (coordinator != null && coordinator.SuppressNativeSpawning) return;

            IDisposable views = coordinator?.BeginRemoteSimulationViewScope();
            try
            {
                base.Update(dt);
            }
            finally
            {
                views?.Dispose();
            }
        }
    }
}
