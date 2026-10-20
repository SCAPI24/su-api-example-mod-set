using System;
using System.Globalization;
using Game;
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
