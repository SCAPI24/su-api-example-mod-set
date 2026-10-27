using System;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace SuMultiplayer.Engine;

/// <summary>
/// 引擎 <see cref="SubsystemPlayers"/> 的**完整子类替换**（Database `Class` 参数替换：
/// <c>Game.SubsystemPlayers</c> → 本类，见 Plug/SuMultiplayerMod.cs 的 modInjector.Register）。
///
/// 为什么必须是"子类"而不是"新子系统"：modInjector.Register 只改 Database 里 Subsystem 条目的
/// Class 参数（Source: EntitySystem/SuAPI/ModInjector.cs:147 ProcessParameter），引擎加载世界时
/// 会用替换类实例化该子系统；如果替换类不是 SubsystemPlayers 的子类/同契约，引擎会报
/// 「Required subsystem Game.SubsystemPlayers does not exist in project.」并拒绝加载世界。
///
/// 本类只**追加**一个"镜像宿主注册表"能力，其余行为全部继承自引擎实现：
///   · <c>Host&lt;PlayerIndex&gt;</c> = 归属 clientId（字符串）
///   · <c>AutoHost&lt;PlayerIndex&gt;</c> = "1" 表示该角色由联机调试功能自动创建
/// 数据写在子系统的 <see cref="Subsystem.ValuesDictionary"/> 里，随世界存档原样往返
/// （Source: EntitySystem/GameEntitySystem/Project.cs 存档遍历子系统；TemplatesDatabase/ValuesDictionary.cs:Save）。
/// 不使用反射、不覆盖/修改引擎的 PlayersData / AddPlayerData / RemovePlayerData / Save / Load。
/// </summary>
public class SuSubsystemPlayers : SubsystemPlayers
{
    /// <summary>
    /// 引擎读取 Players 段时，如果本端是下载世界的客户端且 HelloAck 已给出记录位置，
    /// 直接把本机 PlayerData 的出生点预置成最后离线位置 —— 这样引擎第一次出生/渲染
    /// 就在记录位置，不会先出现在下载世界的主机角色位置再瞬移。
    ///
    /// Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Load
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Load（SpawnPosition）
    /// </summary>
    private PlayerData m_clientSpawnPlayer;
    private Vector3 m_clientSpawnPosition;
    private Vector3 m_clientSpawnAnchor;
    private bool m_clientSpawnApplied;

    protected override void Load(ValuesDictionary valuesDictionary)
    {
        base.Load(valuesDictionary);
        if (!ClientSpawnPlacement.TryTake(out Vector3 position, out Vector3 spawnPosition))
            return;
        try
        {
            if (PlayersData.Count == 0)
            {
                global::Engine.Log.Warning(
                    "[SuMP][client-spawn] 下载世界 Players 段为空 → 无法预置本机角色出生点" +
                    "（联机角色模块之后仍会按 HelloAck 记录位置精确贴合）");
                return;
            }
            PlayerData local = PlayersData[0];
            // 下载世界的 Project.xml 里通常还带着**主机自己的玩家实体**（MalePlayer，
            // PlayerIndex=1，位置=主机进入地图时的位置）。引擎在 Project 构造时就会把这个
            // 实体加载出来并挂相机；只改 SpawnPosition 根本拦不住它，于是客户端会先出现
            // 在主机位置再瞬移（实机反馈 2026-10-27）。
            // 这里记住本机 PlayerData + 目标位置，等 Project 触发 Subsystem.OnEntityAdded
            // （组件已加载、GameScreen 还没开始画）时把实体位置直接搬过去。
            m_clientSpawnPlayer = local;
            m_clientSpawnPosition = position;
            m_clientSpawnAnchor = spawnPosition;
            local.SpawnPosition = position;
            global::Engine.Log.Information(
                "[SuMP][client-spawn] 下载世界加载时预置本机角色出生点=" + position +
                "（主机记录最后离线位置；复活点 " + spawnPosition +
                "；等玩家实体加载后立刻搬过去）");
        }
        catch (Exception e)
        {
            global::Engine.Log.Warning(
                "[SuMP][client-spawn] 预置本机角色出生点失败：" + e.GetType().Name + " " +
                e.Message + "（联机角色模块之后仍会按 HelloAck 记录位置精确贴合）");
        }
    }

    /// <summary>
    /// Project 构造加载实体时（组件 OnEntityAdded 已执行、GameScreen 尚未渲染）：
    /// 如果是下载世界的本机玩家实体，直接把实体搬到主机记录的最后离线位置。
    /// 这一步与旧实现 ReplaceLocalPlayerData 的目标相同（移除/重建本机角色，立即在记录
    /// 位置出生），但这里连一帧"主机位置"都不给引擎画出来。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:1407-1469
    /// </summary>
    protected override void OnEntityAdded(Entity entity)
    {
        base.OnEntityAdded(entity);
        if (m_clientSpawnApplied || m_clientSpawnPlayer == null || entity == null) return;
        ComponentPlayer player = entity.FindComponent<ComponentPlayer>();
        if (player == null || !ReferenceEquals(player.PlayerData, m_clientSpawnPlayer)) return;
        try
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body != null)
            {
                body.Position = m_clientSpawnPosition;
                body.Velocity = Vector3.Zero;
            }
            m_clientSpawnPlayer.SpawnPosition = m_clientSpawnAnchor;
            m_clientSpawnApplied = true;
            global::Engine.Log.Information(
                "[SuMP][client-spawn] 本机玩家实体加载时已搬到记录位置=" +
                m_clientSpawnPosition + "（复活点=" + m_clientSpawnAnchor +
                "；不再出现主机位置）");
        }
        catch (Exception e)
        {
            global::Engine.Log.Warning(
                "[SuMP][client-spawn] 搬动加载中的本机玩家实体失败：" + e.GetType().Name + " " +
                e.Message + "（联机角色模块第一次 tick 仍会精确贴合）");
        }
    }

    private const string HostKeyPrefix = "Host";
    private const string AutoKeyPrefix = "AutoHost";

    /// <summary>该 PlayerIndex 是否被标记为"本功能自动创建的镜像宿主"。</summary>
    public bool IsAutoHost(int playerIndex) => TryGetAutoHost(playerIndex, out _);

    public bool TryGetAutoHost(int playerIndex, out int clientId)
    {
        clientId = 0;
        if (playerIndex < 0 || playerIndex >= MaxPlayers) return false;
        ValuesDictionary values = ValuesDictionary;
        if (values == null) return false;
        if (!string.Equals(values.GetValue<string>(AutoKeyPrefix + playerIndex, null), "1",
                StringComparison.Ordinal))
            return false;
        int.TryParse(values.GetValue<string>(HostKeyPrefix + playerIndex, "0"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out clientId);
        return true;
    }

    public bool MarkAutoHost(int playerIndex, int clientId, out string reason)
    {
        reason = null;
        if (playerIndex < 0 || playerIndex >= MaxPlayers)
        {
            reason = "PlayerIndex=" + playerIndex + " 超出引擎上限 0.." + (MaxPlayers - 1);
            return false;
        }
        ValuesDictionary values = ValuesDictionary;
        if (values == null)
        {
            reason = "子系统 ValuesDictionary 还没初始化（世界还没加载完）";
            return false;
        }
        values.SetValue(AutoKeyPrefix + playerIndex, "1");
        values.SetValue(HostKeyPrefix + playerIndex, clientId.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>清掉某个 PlayerIndex 的 auto 标记（宿主被移除后调用）。</summary>
    public void UnmarkAutoHost(int playerIndex)
    {
        ValuesDictionary values = ValuesDictionary;
        if (values == null) return;
        if (playerIndex < 0 || playerIndex >= MaxPlayers) return;
        values.SetValue(AutoKeyPrefix + playerIndex, "0");
        values.SetValue(HostKeyPrefix + playerIndex, "0");
    }
}
