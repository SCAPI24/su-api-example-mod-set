using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// `ComponentHealth` 的联机替换：**客户端不该自己死**（方案 B）。
    ///
    /// 引擎里"死"和"倒地"都只看血量这一个值：
    ///   · 死亡判定：`HealthChange = Health - m_lastHealth`，只有
    ///     `Health == 0 && HealthChange < 0` 才执行死亡处理（ComponentHealth.cs:251 / :267）；
    ///   · 倒地：`ComponentHumanModel.Update:86`，条件是 `IsSleeping || Health <= 0f`；
    ///   · `PlayerData` 的死亡状态机（PlayerData.cs:266-273）只要看到一次 `Health <= 0f`
    ///     就记下 `m_playerDeathTime` 并切到 `PlayerDead`（死亡提示 + 死亡相机 + 复活界面），
    ///     之后不会再退回 `Playing`。
    ///
    /// 问题（实测）：客户端本地**也会跑原生伤害**（窒息 / 摔落 / 岩浆 / 饥饿 / 尖刺），
    /// 因此能在主机不知情的情况下把本地血量打到 0 —— 于是本地"死了"（死亡界面锁存、
    /// 身体开始按倒地处理），而主机那份角色还活着；随后主机 1Hz 的权威血量（正值）把本地
    /// 血量顶回 >0 —— 结果就是"复活界面还开着、时不时还在提示饥饿扣血、人却站着"，
    /// 而另一端看这个角色一直是活的。
    ///
    /// 分工（与 `SuComponentVitalStats` 同一套路）：
    ///   · **主机 / 未联机**：完全走原生 `base.Update(dt)`，一个字都不改；
    ///   · **客户端**：照常跑原生伤害（痛叫等原生表现保留），但**原生这一帧刚累积的红屏会被扣掉**
    ///     （见 `SyncClientLocalHealthFromAuthority` 里 `nativeRedGain` 那段），只在伤害算完之后
    ///     把**本端自己**的血量钉在一条正值地板上，并撤销这一帧可能已被死亡状态机锁存的状态。
    ///     真正的死亡只认主机广播的 0：那时 `m_localAuthoritativeDeath` 已置位（见
    ///     `HandleGamePlayerHealthMessage`），本组件不再拦，死亡 / 倒地 / 复活全部按原生走。
    /// </summary>
    public class SuComponentHealth : ComponentHealth, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        // `ComponentHealth.Update` 不是 virtual，因此按 SuComponentVitalStats 的做法
        // 在子类里**重新实现 IUpdateable**，让 SubsystemUpdate 走到这里。
        void IUpdateable.Update(float dt)
        {
            ScMultiplayer instance = ScMultiplayer.currentInstance;
            // 跑原生更新**之前**先跟随一次：本地血量被写回主机最近一次的权威值，
            // 于是原生伤害（含身体界面"骷髅头强制重生"按钮那种在 UI 里落下的伤害）
            // 不会在本帧跨过 0，死亡处理 / 状态机锁存 / 关身体界面都不会发生；
            // 本地被打掉的那部分记成待上报差额，交给主机去施加。
            // ⚠️ insideNativeUpdate 用来区分伤害来源：原生伤害（窒息/岩浆/摔落/挤压）都发生在
            // `base.Update(dt)` **之内**，而 UI 骷髅头、尖刺、爆炸这些发生在原生更新**之外**。
            // 两者在上报时的判据不同 —— 见 SyncClientLocalHealthFromAuthority 里"命中"的判定。
            float healthBeforeFollow = Health;
            instance?.SyncClientLocalHealthFromAuthority(m_componentPlayer, this,
                insideNativeUpdate: false);
            // 预跟随这一下如果**自己**把血写成了 0（主机那份在加入过程中把本端当成 0/未就绪），
            // 原生紧接着会看到 `1 → 0` 的跨越（`ComponentHealth.cs:251`），把它当成一次"重伤"
            // —— 痛叫 + 红屏 `+4.0`（`:256`）—— 可这一下不是任何伤害造成的，是本端自己写下去的。
            bool followWroteZero = Health <= 0.0001f && healthBeforeFollow > 0.0001f;
            float redBefore = ReadRedScreenFactor();
            base.Update(dt);
            // 原生更新自己造成的伤害（饥饿/窒息/岩浆/摔落…）也在这次调用里落到血量上，再跟随一次。
            bool pinned = instance != null && instance.SyncClientLocalHealthFromAuthority(
                m_componentPlayer, this, insideNativeUpdate: true);
            // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update:251-261
            // **不许为"本端自己写下去的血量"闪红屏** —— 用户报的"加入房间满屏红、血却是满的"。
            // 实测（加入房间那一帧）：主机那份在加入过程中把本端当成 0（`authHealth=0.000`），
            // 预跟随于是把本地血量写成 0；原生紧接着在 `:251` 算出 `HealthChange = 0 - 1 = -1.000`
            // —— 可这一下并不是任何伤害造成的，是本端自己写下去的 —— 于是痛叫 + 红屏
            // `m_redScreenFactor += -4f * HealthChange` 直接 **+4.0**（`:256`），同帧抬进
            // `RedoutFactor`（`:261`）；下一帧主机给出真实血量 1，红屏却按 `:144` 衰减约 3 秒。
            // 上面的 `followWroteZero` 抓的就是这一帧。
            // 另外，跟随整段不生效的帧（`pinned == false`，例如本端此刻还是"临时网络角色"）里，
            // 本端算出来的掉血同样作数不了，也不许红屏上涨。
            // 真正的受伤表现统一由主机确认那条路给（见 `TriggerLocalDamageFeedback`）。
            if (instance != null && instance.IsMultiplayerClientSession && (!pinned || followWroteZero))
            {
                float redNow = ReadRedScreenFactor();
                if (redNow > redBefore)
                {
                    WriteRedScreenFactor(redBefore);
                    // `RedoutFactor` 同帧已经按上涨后的值抬过了，一并压回去，否则这一帧仍会画一次
                    //（它每帧由 `ComponentScreenOverlays.Update:69` 归零后由 `ComponentHealth` 重算）。
                    if (m_componentPlayer?.ComponentScreenOverlays != null)
                        m_componentPlayer.ComponentScreenOverlays.RedoutFactor =
                            MathUtils.Min(m_componentPlayer.ComponentScreenOverlays.RedoutFactor, redBefore);
                }
            }
        }

        private float ReadRedScreenFactor() => ScMultiplayer.ModManager.ModParentField
            .GetParentField<float>(this, "m_redScreenFactor", typeof(ComponentHealth));

        private void WriteRedScreenFactor(float value) => ScMultiplayer.ModManager.ModParentField
            .ModifyParentField(this, "m_redScreenFactor", value, typeof(ComponentHealth));
    }
}
