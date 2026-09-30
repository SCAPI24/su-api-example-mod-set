using Engine;
using Game;
using GameEntitySystem;
using System;
using System.Linq;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P7a：本端玩家在**他人领地**里"挖不动" —— 按**冒险模式形态**实现，并且
    /// **不替换 `ComponentMiner`**（任何形式的替换都会重新映射 `IUpdateable`：实测三次都导致
    /// "松开挖掘后手臂不收回、其他客户端看到一直抬手挖掘"）。
    ///
    /// 工具：SuAPI `ModManager.ModParentField` —— 专门用来读写 private 成员：
    ///   · `BindFieldRef&lt;ComponentMiner,T&gt;("m_digStartTime"/"m_digProgress")`：绑定成 **ref**，
    ///     直接写 private 字段、零每帧反射开销；
    ///   · `GetParentField&lt;CellFace?&gt;(miner, "&lt;DigCellFace&gt;k__BackingField")`：读当前挖掘目标。
    ///
    /// 效果等价引擎 `ComponentMiner.Dig()` 里 `!IsLevelSufficientForTool(...)` 那条分支：
    /// 每帧把 `m_digStartTime` 钉到当前时间 ⇒ 引擎自己算出的进度恒 ≈0 ⇒ 裂纹不增长、永远挖不完；
    /// 而 `DigCellFace` / `PokingPhase` 一概不碰 ⇒ 手臂与普通挖掘完全一致。
    ///
    /// ⚠️ 异常必须全部吃掉：上一版（没有 try/catch）异常打断了主机的帧，两端卡在 `Client joining` 之后。
    /// 直通条件：没有领地 / 本端没在挖 / 挖自己领地或公共区域。
    /// </summary>
    public partial class ScMultiplayer
    {
        internal void PinDeniedLocalDigProgress()
        {
            try
            {
                if (RegionClaimCount == 0)
                    return;
                Project project = GameManager.Project;
                SubsystemTime time = project?.FindSubsystem<SubsystemTime>(false);
                if (time == null)
                    return;
                ComponentPlayer localPlayer = project.FindSubsystem<SubsystemPlayers>(false)?
                    .ComponentPlayers.FirstOrDefault(player => player?.PlayerData != null &&
                        !m_networkPlayerData.Values.Contains(player.PlayerData));
                ComponentMiner miner = localPlayer?.ComponentMiner;
                if (miner == null)
                    return;
                // ⚠️ 不要用 `GetParentField<CellFace?>`：装箱的 `CellFace` 转 `CellFace?` 会抛
                // `InvalidCastException`（实测 `event=region.digguard.error InvalidCastException` 每帧刷）。
                // 取 object 再模式匹配，安全。
                object rawFace = ModManager.ModParentField.GetParentField(
                    miner, "<DigCellFace>k__BackingField", typeof(ComponentMiner));
                if (rawFace is not CellFace faceValue)
                    return;
                var face = faceValue;
                var cell = new Point3(face.X, face.Y, face.Z);
                // 主机端本机玩家走 clientId=0 口径；客户端用本机身份直接比对拥有者。
                bool allowed = IsHost
                    ? CanRegionModifyCell(0, cell, out _, out _)
                    : CanLocalPlayerModifyRegionCell(cell, out _, out _);
                if (allowed)
                    return;

                // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
                // ⚠️ 不用 `BindFieldRef`（ref 返回委托在本环境实测抛 `InvalidCastException`），
                // 改用 `ModifyParentField` 写这两个 private 字段（之前版本验证可用）。
                ModManager.ModParentField.ModifyParentField(miner, "m_digStartTime",
                    time.GameTime, typeof(ComponentMiner));
                ModManager.ModParentField.ModifyParentField(miner, "m_digProgress",
                    0f, typeof(ComponentMiner));
            }
            catch (Exception ex)
            {
                // 只记一条，绝不外抛 —— 任何异常都不允许打断帧（否则会卡住加入流程）。
                Diagnostics.ScMultiplayerOperationLog.Write(
                    "event=region.digguard.error " + ex.GetType().Name);
            }
        }
    }
}
