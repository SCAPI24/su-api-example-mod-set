using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using ScMultiplayer.Core;
using ScMultiplayer.Control;
using ScMultiplayer.Modules.Join;
using ScMultiplayer.Diagnostics;
using ScMultiplayer.Transport;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        // ====================================================================
        // 发送: 生命值 (周期性)
        // ====================================================================
        private void SendGamePlayerHealthMessage(bool force)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null || !IsHost) return;
            var players = subsystemPlayers.ComponentPlayers;

            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
                SendAuthoritativePlayerHealth(client.ClientID, item, force);
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                    SendAuthoritativePlayerHealth(remote.Key, remote.Value.ComponentPlayer, force);
            }
        }

        private void SendAuthoritativePlayerHealth(int networkClientId, ComponentPlayer player,
            bool force, int sleepRequestSequence = 0)
        {
            if (player?.ComponentHealth == null || player.ComponentVitalStats == null) return;
            AuthoritativePlayerStateSnapshot current =
                CaptureAuthoritativePlayerState(player);
            bool hasPrevious = m_lastSentAuthoritativePlayerStates.TryGetValue(
                networkClientId, out AuthoritativePlayerStateSnapshot previous);
            if (!force && hasPrevious && !current.HasMeaningfulChangeFrom(previous))
                return;

            bool sleepAcceleration = IsSleepAccelerationActive(GameManager.Project);
            bool healthDecreased = hasPrevious &&
                current.Health < previous.Health - 0.0001f;
            bool sleepEdge = hasPrevious && current.IsSleeping != previous.IsSleeping;
            if (!force && sleepAcceleration && hasPrevious && !healthDecreased && !sleepEdge &&
                m_nextSleepHealthSendTimes.TryGetValue(networkClientId, out double nextSend) &&
                Time.RealTime < nextSend)
                return;

            float healthChange = hasPrevious ? current.Health - previous.Health : 0f;
            // 死亡时把死因一起发过去：客户端血量是"直接写字段跟随"的，不经过 Injure，
            // 因此本端 CauseOfDeath 为空（界面显示 Unknown）、死亡统计也不会记
            //（ComponentHealth.cs:90-102 里 CauseOfDeath 与 AddDeathRecord 都只在 Injure 内）。
            NetworkMessageSender.SendPlayerHealthMessage(networkClientId, player, healthChange,
                cause: current.Health <= 0f ? player.ComponentHealth.CauseOfDeath : null,
                sleepRequestSequence: sleepRequestSequence);
            m_lastSentAuthoritativePlayerStates[networkClientId] = current;
            if (sleepAcceleration)
                m_nextSleepHealthSendTimes[networkClientId] = Time.RealTime + 0.5;
        }

        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
        private AuthoritativePlayerStateSnapshot CaptureAuthoritativePlayerState(
            ComponentPlayer player)
        {
            ComponentVitalStats vital = player.ComponentVitalStats;
            float targetTemperature = ModManager.ModParentField.GetParentField<float>(
                vital, "m_targetTemperature", typeof(ComponentVitalStats));
            return new AuthoritativePlayerStateSnapshot(player.ComponentHealth.Health,
                player.ComponentHealth.Air, vital.Food, vital.Stamina, vital.Sleep,
                vital.Temperature, targetTemperature, vital.Wetness,
                MathUtils.Max(player.PlayerData.Level, 1f),
                player.ComponentSleep?.IsSleeping == true);
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Attacked
        // Vanilla only wakes in some fatigue/health ranges. A network sleep session must leave
        // the shared accelerated-time state on any animal attack, otherwise the host can keep
        // advancing the world while a damaged player remains asleep on another client.
        private void EnsureHostSleepWakeHandlers(Project project)
        {
            if (!IsHost || project == null) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;
            HashSet<ComponentHealth> active = new HashSet<ComponentHealth>();
            foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
            {
                ComponentHealth health = player?.ComponentHealth;
                if (health == null || player.ComponentSleep == null) continue;
                active.Add(health);
                if (m_hostSleepWakeHandlers.ContainsKey(health)) continue;
                ComponentPlayer capturedPlayer = player;
                Action<ComponentCreature> handler = attacker =>
                {
                    if (attacker?.Entity?.FindComponent<ComponentPlayer>() != null ||
                        capturedPlayer.ComponentSleep?.IsSleeping != true)
                        return;
                    capturedPlayer.ComponentSleep.WakeUp();
                    PublishHostSleepWakeState(capturedPlayer);
                };
                m_hostSleepWakeHandlers[health] = handler;
                health.Attacked += handler;
            }
            foreach (KeyValuePair<ComponentHealth, Action<ComponentCreature>> item in
                m_hostSleepWakeHandlers.ToArray())
            {
                if (active.Contains(item.Key)) continue;
                item.Key.Attacked -= item.Value;
                m_hostSleepWakeHandlers.Remove(item.Key);
            }
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        // ComponentSleep has several wake paths that do not share an event. Compare the host's
        // authoritative component state once per world frame and publish both sleep and wake
        // edges without relying on the client's local simulation.
        private void PublishHostSleepStateTransitions(Project project)
        {
            if (!IsHost || project == null || client?.IsConnected != true) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;

            HashSet<int> activeClientIds = new HashSet<int>();
            foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
            {
                if (!TryGetHostNetworkClientId(player, out int clientId)) continue;
                activeClientIds.Add(clientId);
                bool isSleeping = player.ComponentSleep?.IsSleeping == true;
                if (m_hostObservedSleepStates.TryGetValue(clientId,
                        out bool previousSleeping) && previousSleeping != isSleeping)
                {
                    PublishHostSleepWakeState(player);
                }
                m_hostObservedSleepStates[clientId] = isSleeping;
            }

            foreach (int clientId in m_hostObservedSleepStates.Keys
                .Where(id => !activeClientIds.Contains(id)).ToArray())
                m_hostObservedSleepStates.Remove(clientId);
        }

        private bool TryGetHostNetworkClientId(ComponentPlayer player, out int clientId)
        {
            clientId = client?.ClientID ?? 0;
            if (player == null) return false;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (ReferenceEquals(item.Value?.ComponentPlayer, player))
                {
                    clientId = item.Key;
                    return true;
                }
            }
            return player.PlayerData != null && clientId == 0;
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // Vanilla stores a separate sleep start for every player. Once a multiplayer host
        // enters the shared accelerated-time window, move all sleeping players to one host
        // boundary so they cannot wake on different frames and leave clients on different
        // circuit timelines.
        private void MaintainHostSleepAccelerationSession(Project project)
        {
            if (!IsHost || project == null) return;
            SubsystemTime subsystemTime = project.FindSubsystem<SubsystemTime>(false);
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemTime == null || players == null) return;

            bool accelerated = subsystemTime.FixedTimeStep.HasValue;
            if (!accelerated)
            {
                bool accelerationEnded = m_hostSleepAccelerationSessionActive;
                m_hostSleepAccelerationSessionActive = false;
                if (accelerationEnded && client?.IsConnected == true)
                {
                    // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
                    // Publish the authoritative falling edge before player wake snapshots. The
                    // ordinary world stream is replaceable and only 2Hz outside acceleration;
                    // waiting for it lets a client wake while its circuit still has the pre-sleep
                    // counter values.
                    SendGameWorldInfoMessage(reliable: true);
                    foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
                        PublishHostSleepWakeState(player);
                }
                return;
            }
            if (m_hostSleepAccelerationSessionActive) return;

            m_hostSleepAccelerationSessionActive = true;
            // Source: Mod/ScMultiplayer/Modules/Player/
            // ScMultiplayerHealthWorldControlHandlers.cs:HandleGameWorldInfoMessage
            // Publish the rising edge reliably. Clients freeze at their current one-step timeline
            // and wait for the falling-edge snapshot instead of following accelerated host time.
            if (client?.IsConnected == true)
                SendGameWorldInfoMessage(reliable: true);
            foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
            {
                if (player?.ComponentSleep?.IsSleeping != true) continue;
                // ComponentSleep owns the vanilla per-player start time. Do not replace it with
                // a shared acceleration timestamp; the original 180-second/daylight rule uses
                // the time at which each player actually entered sleep.
                PublishHostSleepWakeState(player);
            }
        }

        private void DetachHostSleepWakeHandlers()
        {
            foreach (KeyValuePair<ComponentHealth, Action<ComponentCreature>> item in
                m_hostSleepWakeHandlers.ToArray())
                item.Key.Attacked -= item.Value;
            m_hostSleepWakeHandlers.Clear();
        }

        private void PublishHostSleepWakeState(ComponentPlayer player)
        {
            if (!IsHost || player == null || client?.IsConnected != true) return;
            if (!TryGetHostNetworkClientId(player, out int networkClientId)) return;
            SendAuthoritativePlayerHealth(networkClientId, player, force: true);
            m_hostObservedSleepStates[networkClientId] = player.ComponentSleep?.IsSleeping == true;
        }

        // Source: Mod/ScMultiplayer/Message/GamePlayerHealthMessage.cs:Write
        internal int GetNextAuthoritativePlayerStateSequence(int playerIndex)
        {
            if (!m_authoritativePlayerStateSequences.TryGetValue(playerIndex,
                out int sequence) || sequence == int.MaxValue)
                sequence = 0;
            sequence++;
            m_authoritativePlayerStateSequences[playerIndex] = sequence;
            return sequence;
        }

        // Source: Survivalcraft/Game/VitalStatsWidget.cs:VitalStatsWidget.Update
        // Client-side UI damage is a request. The host accepts only a lower health value and
        // remains authoritative for the resulting health, events and death state.
        /// <summary>
        /// 方案 B：**血量由主机通知，本地只跟随**。由 <see cref="SuComponentHealth"/> 在原生更新前后各调用一次。
        ///
        /// 为什么必须这样做：死亡判定（ComponentHealth.cs:251/267）、倒地
        /// （ComponentHumanModel.Update:86）都只看 `Health`，而 `PlayerData` 的死亡状态机
        /// （PlayerData.cs:266-273）看到一次 `Health <= 0f` 就锁存并停在复活界面。
        /// 客户端本地也跑原生伤害（窒息/摔落/岩浆/饥饿/尖刺，以及身体界面那个"骷髅头强制重生"
        /// 按钮一次 -0.1，VitalStatsWidget.cs:178），能在主机不知情时把本地血量打到 0
        /// → "本地已死、主机还活"，随后主机 1Hz 权威血量（正值）把本地血量顶回 >0
        /// → 复活界面还在、还在扣血、人却站着。
        ///
        /// 现在的分工：本地**不保留**任何自己算出来的血量变化 —— 每帧把它写回主机最近一次给的
        /// 权威值；本地被原生伤害打掉的那部分**当场直接上报**给主机
        /// （`m_localDamageReportAccumulator` 只做小额合并，且只由"发出去了"清零）。
        /// 由 `SendClientDamageRequest` 如实报给主机，**由主机施加**。于是本地血条只会跟着主机走，
        /// 既不会停在地板值、也不会因为本地伤害而抖动；本地永远不会自己跨 0，
        /// 真死只认主机广播的 0（`m_localAuthoritativeDeath`）—— 那时故意不写 `m_lastHealth`，
        /// 让原生看到 0 的跨越，死亡处理才会正常发生。
        /// </summary>
        internal void SyncClientLocalHealthFromAuthority(ComponentPlayer player, ComponentHealth health,
            bool insideNativeUpdate)
        {
            if (player == null || health == null) return;
            if (IsHost || client?.IsConnected != true) return;
            if (player.PlayerData == null ||
                m_networkPlayerData.Values.Contains(player.PlayerData)) return;
            // 主机没判死、本端血却是正的 → 这一次"死"已经过去了（本端自己复活过，或那只是幽灵死亡）。
            // 必须在这里复位死亡落地标记：否则下一次**真正的**死亡会被当成"同一场死亡已经记过"
            // 而既不写死因也不记统计（实测："后续几次被狮子咬死，游戏统计并没有"）。
            if (!m_localAuthoritativeDeath && health.Health > 0f)
            {
                m_localDeathApplied = false;
                m_localDeathCauseResolved = false;
            }
            // 刚复活、主机还没确认的这段窗口：本地血量要有东西钉住。
            // 不钉住时，复活传送带来的摔落判定 / 身边的动物能在本地把血打到 0，死亡状态机再次锁存 ——
            // 现象是"点复活后立刻又出现死亡界面（死因 Unknown、点不动）、统计里也没有这次死亡"（实测）。
            // 但**只钉血、不吞伤害**：曾经在窗口里把本地扣血整个丢掉（连上报都不发），
            // 结果那段时间点骷髅头扣血完全无效、"最后半格血点击扣血，不死"，
            // 要等窗口过期（最多 5 秒）才恢复（实测）。扣血照常上报，死不死由主机决定。
            // ⚠️ 判别"过期的主机判死"与"复活之后新判的死"必须用**序号**，不能只看死亡标志：
            // 客户端本地重生是引擎自己完成的（PlayerData 换掉实体、新实体满血），
            // 而主机那份可能还停在 0（复活请求还没被处理）。只看标志的话，这个"0"会在复活瞬间
            // 把本地血量写回 0 —— 实测就是"点复活后屏幕全红、又冒出一个死亡界面、站着不能动"。
            // 序号不晚于复活那一刻的 0 是过期的（不跟随）；序号更大的才是复活之后主机新判的死（必须生效）。
            bool staleAuthoritativeDeath = m_localAuthoritativeDeath &&
                m_localDeathSequence <= m_localRespawnStateSequence;
            bool freshAuthoritativeDeath = m_localAuthoritativeDeath && !staleAuthoritativeDeath;
            bool inRespawnWindow = !freshAuthoritativeDeath &&
                (staleAuthoritativeDeath || Time.RealTime < m_localRespawnPendingUntil);
            // 还没收到过主机的权威血量：什么都不做（否则会把刚进世界的满血误写成一格）。
            // 复活窗口是例外：那时正要紧的就是"主机还没回话"。
            if (!inRespawnWindow && !m_hasObservedClientHealth) return;
            bool authoritativeDead = m_localAuthoritativeDeath;
            // 目标值就是主机的权威血量本身（**不设显示地板**）。
            // 曾经加过一条 0.11 的地板来避免"点击把本地打到 0 → 关面板"，但那会让血条说谎：
            // 主机只剩 0.077 时血条仍显示 1 格，玩家以为还能再挨一下，一点就死
            //（实测："还有2格半或者2格的时候，点击扣血，血变为1格，然后角色就死了"）。
            // 现在如实跟随：血条显示多少就是主机有多少；致命那一下（主机 <= 0.1）本来就该致命。
            // 复活窗口里唯一的地板是"主机那份还写着 0（复活还没被处理）"时先用引擎重生的满血 1；
            // 主机一旦给出正值就照常跟随，避免血条长时间说谎。
            float baseline;
            if (inRespawnWindow)
                baseline = m_lastAuthoritativeLocalHealth > 0f ? m_lastAuthoritativeLocalHealth : 1f;
            else
                baseline = authoritativeDead ? 0f : m_lastAuthoritativeLocalHealth;
            float current = health.Health;
            if (current < baseline - 0.0001f)
            {
                // ⚠️ 上报的是**本地这次真实掉的血**（相对上一次钉住的值），不是"相对主机的差额"：
                // 本地贴在地板上时，相对差额只有零点几，而上报真实掉落（一次 0.1）才能让主机被扣到 0。
                float lost = baseline - current;
                // ⚠️ "命中"的判据分两种来源（见 SuComponentHealth 传进来的 insideNativeUpdate）：
                //   · **原生之外**的扣血（UI 骷髅头一次 -0.1、尖刺、爆炸…）本身就是一次性命中：
                //     哪怕本地只剩 0.005、这一下只掉 0.005，也要按标称 0.1 上报。
                //     实测：主机那份因为自然回血总比血条显示的高一点点，一次 -0.1 打下去常剩 ~0.005；
                //     这一下如果按"掉多少报多少"就低于上报阈值(0.02)，被当成小额攒账**永远发不出去**
                //     —— 血条显示还有半格，怎么点都不死；而血刚好一格时掉落 0.1 过了阈值，所以能死。
                //   · **原生之内**的扣血（窒息/岩浆/摔落，每帧 ~0.006）保持原判据，继续攒账，
                //     否则最后一格血里每帧都会按 0.1 上报，主机瞬间被扣光。
                if (lost >= LocalHitDamageThreshold || (!insideNativeUpdate && lost > 0.0001f)) lost = MathUtils.Max(lost, LocalHitDamage);
                // ⚠️ 这里**不**自己闪红/闪血条了：本地这段扣血会立刻上报给主机，主机扣完再把快照发回来，
                // 由 HandleGamePlayerHealthMessage 按"本端这次实际下降的量"统一补受伤表现
                //（那条路同时覆盖动物咬这种本地根本看不到的扣血）。两处都做，一次伤害会闪两下。
                // 立刻把这次本地伤害**直接上报**给主机，不再攒账等 SendClientDamageRequest：
                // 攒账会被主机 1Hz 广播和本方法每帧两次调用搅乱（实测"有时只闪红、不掉血"）。
                // 饥饿/窒息那种每帧 0.006 的小额先攒到阈值再发，免得每帧一条消息。
                m_localDamageReportAccumulator += lost;
                if (m_localDamageReportAccumulator >= LocalDamageReportThreshold)
                {
                    // 死因**不从这里上报**：生物攻击（狼咬等）与饥饿/溺水/高温都是主机在实时模拟，
                    // 主机那份角色身上引擎自己会写 CauseOfDeath（主机端 `Attacked` 事件确实会触发 ——
                    // 见 EnsureHostSleepWakeHandlers 用它叫醒睡着的玩家）。客户端这里只报"掉了多少血"。
                    NetworkMessageSender.SendPlayerHealthMessage(client.ClientID, player,
                        -m_localDamageReportAccumulator, "Client damage request");
                    m_localDamageReportAccumulator = 0f;
                }
            }
            else if (current > baseline + 0.0001f)
            {
                // 本地自己涨到了目标值之上（原生 Harmless 回血）：这点差额已经作废。
                // ⚠️ 只有"高于"才清账；相等时不能清 —— 本方法每帧调用两次，
                // 相等也清会把前一次刚攒下、还没发出的差额抹掉。
                m_localDamageReportAccumulator = 0f;
            }
            ModManager.ModParentField.ModifyParentField(health, "<Health>k__BackingField",
                baseline, typeof(ComponentHealth));
            // 目标值 > 0 时同步基准；为 0（主机判死）时**故意不写** `m_lastHealth`，
            // 让下一帧原生 Update 看到 0 的跨越，死亡处理 / 状态机锁存才会正常发生。
            if (baseline > 0f)
                ModManager.ModParentField.ModifyParentField(health, "m_lastHealth",
                    baseline, typeof(ComponentHealth));
            // 只有"复活之后主机新判的死"才停下不动；主机那份**过期的** 0 不算（那种情况下
            // 本端其实已经被引擎重生过了，必须继续撤销本地死亡锁存，否则角色站在死亡界面里动不了）。
            if (freshAuthoritativeDeath) return;
            // 兜底：万一原生的死亡锁存先于主机判定发生，撤销它（主机判死那条路不会走到这里）。
            UndoLocalDeathLatch(player);
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead
        // 撤销本端可能已经发生的原生死亡锁存（`m_playerDeathTime` 一旦写下就不会自己退回）。
        // 主机没判死时用它把"幽灵死亡"退回去，否则角色会卡在死亡界面点不动、也无法复活（实测）。
        private void UndoLocalDeathLatch(ComponentPlayer player)
        {
            PlayerData data = player?.PlayerData;
            if (data == null) return;
            object deathTime = ModManager.ModParentField.GetParentField(
                data, "m_playerDeathTime", typeof(PlayerData));
            if (deathTime == null) return;
            ModManager.ModParentField.ModifyParentField(data, "m_playerDeathTime", null,
                typeof(PlayerData));
            StateMachine stateMachine = ModManager.ModParentField.GetParentField<StateMachine>(
                data, "m_stateMachine", typeof(PlayerData));
            if (stateMachine != null && stateMachine.CurrentState != "Playing")
                stateMachine.TransitionTo("Playing");
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        // 原生在扣血那一帧做两件反馈：红屏累积 `m_redScreenFactor += -4f * HealthChange`（:256）、
        // 生命条闪烁 `HealthBarWidget.Flash(...)`（:257）。客户端改成"本地只跟随主机"之后，
        // 本地血量在同一帧就被写回权威值，原生那两行看不到这次扣血，所以在这里补上。
        private void TriggerLocalDamageFeedback(ComponentPlayer player, float lost)
        {
            if (lost <= 0.0001f) return;
            player.ComponentGui?.HealthBarWidget?.Flash(
                MathUtils.Clamp((int)(lost * 30f), 0, 10));
            ComponentHealth health = player.ComponentHealth;
            if (health == null) return;
            float red = ModManager.ModParentField.GetParentField<float>(
                health, "m_redScreenFactor", typeof(ComponentHealth));
            ModManager.ModParentField.ModifyParentField(health, "m_redScreenFactor",
                MathUtils.Min(red + 4f * lost, 1f), typeof(ComponentHealth));
        }

        private void SendClientDamageRequest()
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            ComponentHealth health = localPlayer?.ComponentHealth;
            ComponentVitalStats vital = localPlayer?.ComponentVitalStats;
            if (health == null || vital == null) return;
            if (!m_hasObservedClientHealth)
            {
                m_hasObservedClientHealth = true;
                m_observedClientHealth = health.Health;
                m_observedClientFood = vital.Food;
                m_observedClientSleeping = localPlayer.ComponentSleep?.IsSleeping == true;
                return;
            }
            // ⚠️ 血量变化**不再**走这条路：本地只跟随主机，扣血已经在
            // SyncClientLocalHealthFromAuthority 里**当场直接上报**（这条路会被主机 1Hz 广播
            // 和每帧两次调用搅乱，实测"有时只闪红、不掉血"）。这里只保留饱食 / 睡觉的请求。
            bool foodIncreased = vital.Food > m_observedClientFood + 0.0001f;
            bool isSleeping = localPlayer.ComponentSleep?.IsSleeping == true;
            bool sleepChanged = isSleeping != m_observedClientSleeping;
            int sleepRequestSequence = 0;
            if (sleepChanged && isSleeping)
                sleepRequestSequence = BeginClientSleepRequest();
            if (foodIncreased || sleepChanged)
            {
                NetworkMessageSender.SendPlayerHealthMessage(
                    client.ClientID, localPlayer, 0f,
                    foodIncreased ? "Client food request" : "Client state request",
                    sleepRequestSequence: sleepRequestSequence);
            }
            m_observedClientHealth = health.Health;
            m_observedClientFood = vital.Food;
            m_observedClientSleeping = isSleeping;
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        // Manual wake is a host-authoritative request. Do not clear the local sleep state before
        // the host ends acceleration and the client has applied the final circuit snapshot.
        internal bool RequestClientWakeUp(ComponentPlayer localPlayer)
        {
            if (IsHost || client?.IsConnected != true || localPlayer?.ComponentSleep == null ||
                !localPlayer.ComponentSleep.IsSleeping ||
                m_networkPlayerData.Values.Contains(localPlayer.PlayerData))
                return false;
            int sleepRequestSequence = BeginClientSleepRequest();
            NetworkMessageSender.SendPlayerHealthMessage(client.ClientID, localPlayer, 0f,
                "Client wake request", isSleepingOverride: false,
                sleepRequestSequence: sleepRequestSequence);
            return true;
        }

        private int BeginClientSleepRequest()
        {
            if (m_nextClientSleepRequestSequence == int.MaxValue)
                m_nextClientSleepRequestSequence = 0;
            m_pendingClientSleepRequestSequence = ++m_nextClientSleepRequestSequence;
            return m_pendingClientSleepRequestSequence;
        }

        // ====================================================================
        // 渲染远程玩家
        // ====================================================================
        private void RenderRemotePlayers()
        {
            if (!client.IsConnected || RemotePlayers.Count == 0) return;

            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var players = subsystemPlayers.ComponentPlayers;
            if (players.Count == 0) return;

            // 获取本地玩家相机
            var localPlayer = players[0];
            var camera = localPlayer.GameWidget?.ActiveCamera;
            if (camera == null) return;

            // 延迟初始化 PrimitivesRenderer3D
            if (m_primitivesRenderer3D == null)
                m_primitivesRenderer3D = new PrimitivesRenderer3D();

            float cubeSize = 0.4f;
            var color = Color.White;
            double now = Time.RealTime;

            foreach (var kvp in RemotePlayers)
            {
                var state = kvp.Value;
                // 超过 5 秒没有更新, 跳过
                if (now - state.LastUpdateTime > 5.0) continue;

                Vector3 pos = state.Position;
                Vector3 offset = new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p1 = pos + new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p2 = pos + new Vector3(cubeSize, 0, -cubeSize);
                Vector3 p3 = pos + new Vector3(cubeSize, 2 * cubeSize, cubeSize);
                Vector3 p4 = pos + new Vector3(-cubeSize, 2 * cubeSize, cubeSize);

                var flatBatch = m_primitivesRenderer3D.FlatBatch();
                flatBatch.QueueQuad(p1, p2, p3, p4, color);
            }

            m_primitivesRenderer3D.Flush(camera.ViewProjectionMatrix);
        }

        // ====================================================================
        // Client_GameStep: 处理每 Tick 的网络事件
        // ====================================================================
        private void Client_GameStep(GameStepData obj)
        {
            // 加入
            foreach (var item in obj.Joins)
            {
                Log.Information($"[ScMP] Client joining: {item.ClientID}");
                // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:CreateTickMessage
                PublishServerAudit("join.host_received", item.ClientID,
                    "endpoint=" + item.Address + " bytes=" +
                    (item.JoinRequestBytes?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
                m_departedRemoteClientIds.Remove(item.ClientID);
                // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Handle
                // A single existing peer accepts or refuses a join. Only the room owner is allowed
                // to decide, otherwise another client can accept before the host requests a profile.
                if (!IsHost) continue;
                int joiningClientId = item.ClientID;
                IPEndPoint joiningAddress = item.Address;
                byte[] joinRequestBytes = item.JoinRequestBytes;
                QueueEndOfFrameAction(() => HandleHostJoinRequest(
                    joiningClientId,
                    joiningAddress,
                    joinRequestBytes));
            }

            // 输入消息
            if (m_messageRouter != null)
            {
                foreach (var item in obj.Inputs)
                    Client_DirectInput(item.ClientID, item.InputBytes);
            }
            else
            {
                // Source: Mod/ScMultiplayer/Control/NetworkMessageRouter.cs:
                // NetworkMessageRouter.Route
                // Keep the compatibility switch only as an initialization fallback. Normal
                // traffic always enters the router created during OnLoad.
                foreach (var item in obj.Inputs)
                {
                    if (!NetworkMessageIngress.TryDecode(item.InputBytes, client.Address.Port,
                        out Message message, out string decodeError))
                    {
                        if (!string.IsNullOrEmpty(decodeError))
                            Log.Error($"[ScMP] Failed to parse message: ClientID={item.ClientID}, " +
                                decodeError);
                        continue;
                    }

                    switch (message)
                    {
                    case SyncBatchMessage syncBatch:
                        foreach (byte[] payload in syncBatch.Payloads)
                        {
                            try
                            {
                                if (Message.Read(payload) is SyncBatchMessage)
                                    throw new InvalidOperationException("Nested sync batch is not allowed.");
                                Client_DirectInput(item.ClientID, payload);
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[ScMP] Failed to unpack sync batch item: {ex.Message}");
                            }
                        }
                        break;
                    case ChatMessage chat:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleChatMessage(chat, item.ClientID));
                        break;
                    case GamePlayerPositionMessage pos:
                        QueueEndOfFrameAction(() => HandleGamePlayerPositionMessage(pos, item.ClientID));
                        break;
                    case GamePlayerPositionsMessage positions:
                        QueueEndOfFrameAction(() =>
                        {
                            if (positions.Players == null) return;
                            foreach (GamePlayerPositionMessage position in positions.Players)
                                HandleGamePlayerPositionMessage(position, item.ClientID);
                        });
                        break;
                    case GamePlayerInputMessage playerInput:
                        QueueEndOfFrameAction(() => HandleGamePlayerInputMessage(
                            playerInput, item.ClientID));
                        break;
                    case MountActionMessage mountAction:
                        QueuePriorityInputAction(() => HandleMountActionMessage(
                            mountAction, item.ClientID));
                        break;
                    case MountStateMessage mountState:
                        QueuePriorityInputAction(() => HandleMountStateMessage(
                            mountState, item.ClientID));
                        break;
                    case PlayerAimMessage playerAim:
                        QueueEndOfFrameAction(() => HandlePlayerAimMessage(playerAim, item.ClientID));
                        break;
                    case PlayerActionMessage playerAction:
                        if (playerAction.Action == PlayerActionType.JumpRequest)
                            QueuePriorityInputAction(() =>
                                HandlePlayerActionMessage(playerAction, item.ClientID));
                        else
                            QueueEndOfFrameAction(() =>
                                HandlePlayerActionMessage(playerAction, item.ClientID));
                        break;
                    case TerrainDigRequestMessage terrainDigRequest:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainDigRequest(terrainDigRequest, item.ClientID));
                        break;
                    case TerrainDigResultMessage terrainDigResult:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainDigResult(terrainDigResult, item.ClientID));
                        break;
                    case DigPresentationMessage digPresentation:
                        QueueEndOfFrameAction(() => HandleDigPresentationMessage(
                            digPresentation, item.ClientID));
                        break;
                    case GameModifiedCellsMessage cells:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleModifiedCellsMessage(cells, item.ClientID));
                        break;
                    case TerrainRecoveryMessage terrainRecovery:
                        QueueEndOfFrameAction(() =>
                            HandleTerrainRecoveryMessage(terrainRecovery, item.ClientID));
                        break;
                    case TerrainChunkSyncMessage terrainChunkSync:
                        m_terrainChunkSyncActions.Enqueue(new QueuedTerrainChunkSync
                        {
                            Message = terrainChunkSync,
                            SourceClientId = item.ClientID,
                            EnqueuedTimestamp = Stopwatch.GetTimestamp()
                        });
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        if (item.ClientID == 0)
                            Dispatcher.Dispatch(() =>
                                NetworkMessageHandler.HandleWorldInfoMessage(worldInfo, item.ClientID));
                        break;
                    case WorldControlRequestMessage worldControl:
                        QueueEndOfFrameAction(() => HandleWorldControlRequest(worldControl, item.ClientID));
                        break;
                    case WorldControlResultMessage worldControlResult:
                        QueueEndOfFrameAction(() => HandleWorldControlResult(
                            worldControlResult, item.ClientID));
                        break;
                    case PlayerProfileMessage playerProfile:
                        QueueEndOfFrameAction(() => HandlePlayerProfileMessage(playerProfile, item.ClientID));
                        break;
                    case PlayerSkinAssetMessage playerSkinAsset:
                        QueueEndOfFrameAction(() => HandlePlayerSkinAssetMessage(
                            playerSkinAsset, item.ClientID));
                        break;
                    case PlayerEquipmentMessage playerEquipment:
                        QueueEndOfFrameAction(() => HandlePlayerEquipmentMessage(
                            playerEquipment, item.ClientID));
                        break;
                    case EditableDataRequestMessage editableDataRequest:
                        QueueEndOfFrameAction(() => HandleEditableDataRequest(
                            editableDataRequest, item.ClientID));
                        break;
                    case EditableDataStateMessage editableDataState:
                        QueueEndOfFrameAction(() => HandleEditableDataState(
                            editableDataState, item.ClientID));
                        break;
                    case CircuitSyncMessage circuitSync:
                        QueueEndOfFrameAction(() => m_circuitSynchronizer?.HandleMessage(
                            circuitSync, item.ClientID));
                        break;
                    case WorldObjectSyncMessage worldObjectSync:
                        QueueEndOfFrameAction(() => m_worldObjectSynchronizer?.HandleMessage(
                            worldObjectSync, item.ClientID));
                        break;
                    case GamePakWorldMessage pakWorld:
                        if (item.ClientID == 0)
                            QueueWorldTransferAction(() =>
                                NetworkMessageHandler.HandlePakWorldMessage(pakWorld, item.ClientID));
                        break;
                    case GamePakWorldChunkMessage worldChunk:
                        if (item.ClientID == 0)
                            QueueWorldTransferAction(() => HandleGamePakWorldChunkMessage(worldChunk));
                        break;
                    case GamePakWorldReadyMessage worldReady:
                        QueueWorldTransferAction(() =>
                            HandleGamePakWorldReadyMessage(worldReady, item.ClientID));
                        break;
                    case GamePakWorldRepairRequestMessage repairRequest:
                        QueueWorldTransferAction(() =>
                            HandleGamePakWorldRepairRequestMessage(repairRequest, item.ClientID));
                        break;
                    case GamePlayerHealthMessage health:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandlePlayerHealthMessage(health, item.ClientID));
                        break;
                    case GameKickPlayerMessage kick:
                        QueueEndOfFrameAction(() => HandleGameKickPlayerMessage(kick, item.ClientID));
                        break;
                    case EntityMessage entityMessage:
                        QueueEndOfFrameAction(() => HandleAnimalEntityMessage(entityMessage, item.ClientID));
                        break;
                    case BodyUpdateMessage bodyUpdate:
                        QueueEndOfFrameAction(() => HandleAnimalBodyUpdate(bodyUpdate, item.ClientID));
                        break;
                    case AnimalInteractionMessage animalInteraction:
                        QueueEndOfFrameAction(() => HandleAnimalInteractionMessage(
                            animalInteraction, item.ClientID));
                        break;
                    case AnimalSoundMessage animalSound:
                        QueueEndOfFrameAction(() => HandleAnimalSoundMessage(
                            animalSound, item.ClientID));
                        break;
                    case MeleeHitResultMessage meleeHitResult:
                        QueueEndOfFrameAction(() => HandleMeleeHitResultMessage(
                            meleeHitResult, item.ClientID));
                        break;
                    case PickableSyncMessage pickableSync:
                        QueueEndOfFrameAction(() => HandlePickableSyncMessage(pickableSync, item.ClientID));
                        break;
                    case ProjectileSyncMessage projectileSync:
                        QueueEndOfFrameAction(() => HandleProjectileSyncMessage(projectileSync, item.ClientID));
                        break;
                    case ExplosionSyncMessage explosionSync:
                        QueueEndOfFrameAction(() => HandleExplosionSyncMessage(explosionSync, item.ClientID));
                        break;
                    case ContainerSyncMessage containerSync:
                        QueueEndOfFrameAction(() => HandleContainerSyncMessage(containerSync, item.ClientID));
                        break;
                        default:
                            Log.Error($"[ScMP] Unknown message type: {message.GetType().Name}");
                            break;
                    }
                }
            }

            // Source: Comms/Comms.Drt/Data/GameStepData.cs:GameStepData.Inputs
            // A transport leave can share a GameStep with the peer's final already-received
            // reliable messages. Queue removal only after those messages so host-authoritative
            // equipment and container changes are applied before the player record is captured.
            foreach (var item in obj.Leaves)
            {
                Log.Information($"[ScMP] Client left: {item.ClientID}");
                PublishServerAudit("connection.leave", item.ClientID, null);
                if (!IsHost && item.ClientID == 0)
                {
                    HandleHostDisconnected();
                    continue;
                }
                int departedClientId = item.ClientID;
                m_controlUnit?.Context.Connections.MarkDisconnected(
                    departedClientId, Time.RealTime);
                // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerAwayPlayers.cs
                // 主机侧先把"断线"当成可能的重连：挂起化身等客户端自动重连，宽限期满才真正离开。
                if (TryBeginAway(departedClientId))
                    continue;
                QueueEndOfFrameAction(() =>
                {
                    m_circuitSynchronizer?.NotifyClientDeparted(departedClientId);
                    if (!IsHost)
                        m_departedRemoteClientIds.Add(departedClientId);
                    RemoveNetworkPlayer(departedClientId);
                    playerMappingManager.ReleasePlayerIndex(departedClientId);
                });
            }
        }

        // Source: Comms.Drt/Func/Client/Client.cs:Client.DirectInput
        // Reuse the normal message dispatcher while keeping direct network callbacks away from
        // game objects. Individual handlers enqueue their work on Frame.Update.
        private void Client_DirectInput(int sourceClientId, byte[] inputBytes)
        {
            if (!NetworkMessageIngress.TryDecode(inputBytes, client.Address.Port,
                out Message message, out string decodeError))
            {
                if (!string.IsNullOrEmpty(decodeError))
                    Log.Error($"[ScMP] Failed to parse message: ClientID={sourceClientId}, " +
                        decodeError);
                return;
            }

            (m_messageRouter ??= new NetworkMessageRouter(this)).Route(
                sourceClientId, message, inputBytes?.Length ?? 0);
        }

        // Source: Mod/Comms/Comms.Drt/Data/GameStepData.cs:GameStepData.JoinData
        private void HandleHostJoinRequest(
            int joiningClientId,
            IPEndPoint joiningAddress,
            byte[] joinRequestBytes)
        {
            if (!IsHost || m_hostJoinRequests.ContainsKey(joiningClientId))
                return;

            // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerHealthAndIngress.cs:HandleHostJoinRequest
            PublishServerAudit("join.host_processing", joiningClientId,
                "endpoint=" + joiningAddress + " bytes=" +
                (joinRequestBytes?.Length ?? 0).ToString(CultureInfo.InvariantCulture));

            // Source: Mod/ScMultiplayer/Message/GameWorldInfoMessage.cs:
            // GameWorldInfoMessage.Read
            // Reject an incompatible multiplayer Mod before it can reserve a player slot.
            GameWorldInfoMessage worldInfo;
            try
            {
                worldInfo = Message.Read(joinRequestBytes) as GameWorldInfoMessage;
            }
            catch (Exception ex)
            {
                PublishServerAudit("join.refused", joiningClientId,
                    "reason=invalid_request error=" + ex.GetType().Name);
                client.RefuseJoinGame(joiningClientId,
                    "Invalid join request: " + ex.Message);
                Log.Error($"[ScMP] Failed to parse ClientID {joiningClientId} join: " +
                    ex.Message);
                return;
            }
            if (worldInfo == null)
            {
                PublishServerAudit("join.refused", joiningClientId,
                    "reason=invalid_request_type");
                client.RefuseJoinGame(joiningClientId, "Invalid join request type");
                return;
            }
            if (!Message.IsProtocolCompatible(worldInfo.MultiplayerModVersion,
                    worldInfo.MultiplayerProtocolVersion,
                    worldInfo.MultiplayerProtocolHash,
                    worldInfo.MultiplayerBuildFingerprint))
            {
                string remoteProtocol = Message.GetProtocolLabel(
                    worldInfo.MultiplayerModVersion,
                    worldInfo.MultiplayerProtocolVersion,
                    worldInfo.MultiplayerProtocolHash,
                    worldInfo.MultiplayerBuildFingerprint);
                string hostProtocol = Message.GetProtocolLabel(
                    Message.ModVersion, Message.ProtocolVersion,
                    Message.ProtocolHash, Message.BuildFingerprint);
                string reason = $"{ProtocolMismatchReasonPrefix}: " +
                    $"host={hostProtocol}, client={remoteProtocol}";
                Log.Warning($"[ScMP] Refused incompatible ClientID {joiningClientId}: " +
                    $"host={hostProtocol}, client={remoteProtocol}");
                PublishServerAudit("join.refused", joiningClientId,
                    "reason=protocol_mismatch remote=" + remoteProtocol);
                client.RefuseJoinGame(joiningClientId, reason);
                return;
            }

            if (!TryReserveNetworkPlayerIndex(joiningClientId,
                out int assignedPlayerIndex))
            {
                Log.Information($"[ScMP] Game full, refusing ClientID {joiningClientId}");
                PublishServerAudit("join.refused", joiningClientId, "reason=game_full");
                client.RefuseJoinGame(joiningClientId, "Game is full");
                return;
            }
            if (playerMappingManager.AssignPlayerIndex(joiningClientId) == -1)
            {
                m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                Log.Information($"[ScMP] Game full, refusing ClientID {joiningClientId}");
                PublishServerAudit("join.refused", joiningClientId, "reason=game_full");
                client.RefuseJoinGame(joiningClientId, "Game is full");
                return;
            }
            m_controlUnit?.Context.Connections.Register(
                joiningClientId,
                assignedPlayerIndex,
                joiningAddress?.ToString(),
                isHost: false,
                Core.PlayerConnectionPhase.Reserved,
                Time.RealTime);

            try
            {
                if (SuPlayScreen.WorldData == null ||
                    SuPlayScreen.WorldDataName != worldInfo.Name ||
                    SuPlayScreen.WorldDataLastSaveTime != worldInfo.LastSaveTime)
                {
                    m_controlUnit?.Context.Connections.MarkDisconnected(joiningClientId, Time.RealTime);
                    m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                    playerMappingManager.ReleasePlayerIndex(joiningClientId);
                    PublishServerAudit("join.refused", joiningClientId,
                        "reason=host_snapshot_unavailable");
                    client.RefuseJoinGame(joiningClientId, "Host world snapshot is unavailable");
                    return;
                }

                EnsurePlayerRecordsLoaded();
                string recordKey = PlayerRecordKeyResolver.GetPlayerRecordKey(
                    worldInfo.PlayerIdentity,
                    worldInfo.PlayerName);
                bool isNewApproval = !m_playerRecords.TryGetValue(
                    recordKey,
                    out NetworkPlayerRecord joiningRecord);
                if (isNewApproval)
                {
                    if (!IsValidRequestedProfile(worldInfo))
                    {
                        m_controlUnit?.Context.Connections.MarkDisconnected(joiningClientId, Time.RealTime);
                        m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                        playerMappingManager.ReleasePlayerIndex(joiningClientId);
                        PublishServerAudit("join.refused", joiningClientId,
                            "reason=player_profile_required");
                        client.RefuseJoinGame(joiningClientId, PlayerProfileRequiredReason);
                        return;
                    }
                    joiningRecord = CreateInitialPlayerRecord(worldInfo);
                }

                var request = new HostJoinRequest
                {
                    ClientId = joiningClientId,
                    Address = joiningAddress,
                    RecordKey = recordKey,
                    PlayerRecord = joiningRecord,
                    IsNewApproval = isNewApproval,
                    ReceivedTime = Time.RealTime
                };
                Log.Information($"[ScMP] Reserved PlayerIndex {assignedPlayerIndex} for " +
                    $"ClientID {joiningClientId} ({joiningRecord.Name})");
                PublishServerAudit("join.host_reserved", joiningClientId,
                    "playerIndex=" + assignedPlayerIndex.ToString(CultureInfo.InvariantCulture) +
                    " autoApprove=" + ScMultiplayerSettings.AutoApproveJoinRequests);

                if (ScMultiplayerSettings.AutoApproveJoinRequests)
                {
                    ApproveHostJoinRequest(request);
                    return;
                }

                m_hostJoinRequests.Add(joiningClientId, request);
                TryShowNextHostJoinRequest();
            }
            catch (Exception ex)
            {
                m_controlUnit?.Context.Connections.MarkDisconnected(joiningClientId, Time.RealTime);
                m_reservedNetworkPlayerIndices.Remove(joiningClientId);
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                PublishServerAudit("join.refused", joiningClientId,
                    "reason=processing_error error=" + ex.GetType().Name);
                try
                {
                    client.RefuseJoinGame(joiningClientId, "Invalid join request: " + ex.Message);
                }
                catch
                {
                }
                Log.Error($"[ScMP] Failed to process ClientID {joiningClientId} join: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.MaxPlayers
        // MaxPlayers is a local split-screen count, not an index range. Network avatars are
        // detached from PlayersData, so admission and the actual PlayerIndex reservation must be
        // maintained together by the multiplayer host.
        private bool TryReserveNetworkPlayerIndex(int clientId, out int playerIndex)
        {
            if (m_reservedNetworkPlayerIndices.TryGetValue(clientId, out playerIndex))
                return true;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
            {
                playerIndex = -1;
                return false;
            }
            int playerCount = players.PlayersData.Count +
                m_networkPlayerData.Count(item => item.Key > 0) +
                m_reservedNetworkPlayerIndices.Count;
            if (playerCount >= ScMultiplayerSettings.MaxPlayers)
            {
                playerIndex = -1;
                return false;
            }
            playerIndex = FindAvailableNetworkPlayerIndex(players);
            m_reservedNetworkPlayerIndices[clientId] = playerIndex;
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.AddPlayerData
        private int FindAvailableNetworkPlayerIndex(SubsystemPlayers players)
        {
            var used = new HashSet<int>(players.PlayersData.Select(item => item.PlayerIndex));
            used.UnionWith(m_networkPlayerData.Values.Select(item => item.PlayerIndex));
            used.UnionWith(m_reservedNetworkPlayerIndices.Values);
            int playerIndex = 0;
            while (used.Contains(playerIndex)) playerIndex++;
            return playerIndex;
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Save
        // The engine hands PlayerIndex out from m_nextPlayerIndex (AddPlayerData) and persists
        // that counter into Project.xml (SubsystemPlayers.Save). Network avatars and the
        // client-side local player replacement reuse a specific index by temporarily lowering
        // the counter; if it is left at or below an existing player index, the next entity gets
        // a duplicate index and binds to a PlayerData that already owns a ComponentPlayer
        // (PlayerData.OnEntityAdded throws "More than 1 player with PlayerIndex N added to
        // world."). Repair an inconsistent counter, upward only, so healthy worlds are never
        // touched and no world loses its NextPlayerIndex.
        private static void RepairEnginePlayerIndexCounter(SubsystemPlayers players)
        {
            if (players == null) return;
            int maxPlayerIndex = -1;
            foreach (PlayerData playerData in players.PlayersData)
            {
                if (playerData != null && playerData.PlayerIndex > maxPlayerIndex)
                    maxPlayerIndex = playerData.PlayerIndex;
            }
            int nextPlayerIndex = ModManager.ModParentField.GetParentField<int>(
                players, "m_nextPlayerIndex", typeof(SubsystemPlayers));
            if (nextPlayerIndex <= maxPlayerIndex)
            {
                ModManager.ModParentField.ModifyParentField(
                    players, "m_nextPlayerIndex", maxPlayerIndex + 1, typeof(SubsystemPlayers));
            }
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.Dialogs
        private void UpdateHostJoinRequests()
        {
            DeferDismissedHostJoinDecision();

            if (!IsHost || m_hostJoinRequests.Count == 0)
                return;

            HostJoinRequest[] expired = m_hostJoinRequests.Values
                .Where(request => Time.RealTime - request.ReceivedTime >= 285.0)
                .ToArray();
            foreach (HostJoinRequest request in expired)
                RejectHostJoinRequest(request, "Host approval timed out.");

            if (ScMultiplayerSettings.AutoApproveJoinRequests)
            {
                foreach (HostJoinRequest request in m_hostJoinRequests.Values.ToArray())
                    ApproveHostJoinRequest(request);
            }
            else
            {
                TryShowNextHostJoinRequest();
            }
        }

        private void TryShowNextHostJoinRequest()
        {
            DeferDismissedHostJoinDecision();
            if (!IsHost || ScMultiplayerSettings.AutoApproveJoinRequests ||
                m_activeJoinDecisionDialog != null)
            {
                return;
            }
            HostJoinRequest request = m_hostJoinRequests.Values
                .Where(item => !item.Deferred)
                .OrderBy(item => item.ReceivedTime)
                .FirstOrDefault();
            if (request != null)
                ShowHostJoinDecision(request);
        }

        // Source: Survivalcraft/Game/ListSelectionDialog.cs:ListSelectionDialog.Update
        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HideDialog
        // An outside click dismisses ListSelectionDialog without invoking its selection callback.
        // Finalize that path before reopening MP request UI so the same transport request remains
        // pending and can be approved later instead of retaining a stale active-dialog marker.
        private void DeferDismissedHostJoinDecision()
        {
            if (m_activeJoinDecisionDialog == null ||
                DialogsManager.Dialogs.Contains(m_activeJoinDecisionDialog))
                return;
            if (m_hostJoinRequests.TryGetValue(
                m_activeJoinDecisionClientId, out HostJoinRequest dismissed))
            {
                dismissed.Deferred = true;
            }
            m_activeJoinDecisionDialog = null;
            m_activeJoinDecisionClientId = -1;
        }

        // Source: Survivalcraft/Game/ListSelectionDialog.cs:ListSelectionDialog
        private void ShowHostJoinDecision(HostJoinRequest request)
        {
            if (request == null || !m_hostJoinRequests.ContainsKey(request.ClientId))
                return;

            string[] decisions = { "Allow", "Reject", "Decide" };
            var dialog = new ListSelectionDialog(
                "Join Request: " + GetHostJoinRequestLabel(request),
                decisions,
                60f,
                item => item.ToString(),
                item =>
                {
                    m_activeJoinDecisionDialog = null;
                    m_activeJoinDecisionClientId = -1;
                    string decision = item?.ToString();
                    if (decision == decisions[0])
                        ApproveHostJoinRequest(request);
                    else if (decision == decisions[1])
                        RejectHostJoinRequest(request, "Host declined the join request.");
                    else
                    {
                        request.Deferred = true;
                        TryShowNextHostJoinRequest();
                    }
                });
            m_activeJoinDecisionDialog = dialog;
            m_activeJoinDecisionClientId = request.ClientId;
            // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HasDialogs
            // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.UpdateInputFromMouseAndKeyboard
            // Attach to the local host player's GUI so the stock input guard treats the join
            // approval list as a gameplay modal and prevents mouse/keyboard passthrough.
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player?.PlayerData != null && !m_networkPlayerData.Values.Contains(player.PlayerData));
            DialogsManager.ShowDialog(localPlayer?.GuiWidget ?? ScreensManager.RootWidget, dialog);
        }

        private void ApproveHostJoinRequest(HostJoinRequest request)
        {
            if (request == null)
                return;
            m_hostJoinRequests.Remove(request.ClientId);
            CloseActiveJoinDecision(request.ClientId);
            AcceptNetworkPlayerJoin(
                request.ClientId,
                request.RecordKey,
                request.PlayerRecord,
                request.IsNewApproval);
            TryShowNextHostJoinRequest();
        }

        private void RejectHostJoinRequest(HostJoinRequest request, string reason)
        {
            if (request == null)
                return;
            m_hostJoinRequests.Remove(request.ClientId);
            CloseActiveJoinDecision(request.ClientId);
            m_reservedNetworkPlayerIndices.Remove(request.ClientId);
            m_controlUnit?.Context.Connections.MarkDisconnected(request.ClientId, Time.RealTime);
            playerMappingManager.ReleasePlayerIndex(request.ClientId);
            PublishServerAudit("join.rejected", request.ClientId, "reason=" + reason);
            try
            {
                client.RefuseJoinGame(request.ClientId, reason);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ScMP] Could not refuse ClientID {request.ClientId}: {ex.Message}");
            }
            TryShowNextHostJoinRequest();
        }

        private void CloseActiveJoinDecision(int clientId)
        {
            if (m_activeJoinDecisionClientId != clientId)
                return;
            Dialog dialog = m_activeJoinDecisionDialog;
            m_activeJoinDecisionDialog = null;
            m_activeJoinDecisionClientId = -1;
            if (dialog != null && DialogsManager.Dialogs.Contains(dialog))
                DialogsManager.HideDialog(dialog);
        }

        private static string GetHostJoinRequestLabel(HostJoinRequest request)
        {
            string name = string.IsNullOrWhiteSpace(request?.PlayerRecord?.Name)
                ? "Player"
                : request.PlayerRecord.Name;
            return request?.Address == null ? name : name + " | " + request.Address;
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.AcceptJoinGame
        private void AcceptNetworkPlayerJoin(int joiningClientId, string recordKey,
            NetworkPlayerRecord joiningRecord, bool isNewApproval = false)
        {
            try
            {
                m_controlUnit?.Context.Connections.TryTransition(
                    joiningClientId, Core.PlayerConnectionPhase.Joining, Time.RealTime);
                m_lastSentInventoryValues.Remove(joiningClientId);
                m_lastSentInventoryCounts.Remove(joiningClientId);
                m_playerRecords[recordKey] = joiningRecord;
                RegisterPlayerSkinHash(joiningClientId, joiningRecord);
                RequestSkinAssetIfMissing(joiningClientId, joiningRecord?.SkinName,
                    joiningRecord?.PlayerClass ?? PlayerClass.Male,
                    joiningRecord?.SkinSha256);
                m_pendingAcceptedJoinKeys[joiningClientId] = recordKey;
                m_forceContainerFullSync = true;
                m_playerRecordsDirty = true;
                SavePlayerRecords();
                var joinJournal = new JoinCatchUpJournal
                {
                    StartTick = client.Step
                };
                m_joinCatchUpRegistry.Journals[joiningClientId] = joinJournal;
                HostedWorldSnapshot snapshot = CaptureHostedWorldSnapshot();
                // Source: ScMultiplayer.CaptureHostedWorldSnapshot
                // Changes through this tick are already inside the exported archive.
                joinJournal.StartTick = snapshot.Tick;
                // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SendDataMessageToAllClients
                // Keep large direct broadcasts off this endpoint until its world is loaded.
                // Ordered ticks continue so the joining client's expected step stays current.
                SetServerClientGameTrafficEnabled(joiningClientId, enabled: false);
                PublishServerAudit("join.host_accept_send", joiningClientId,
                    "step=" + snapshot.Tick.ToString(CultureInfo.InvariantCulture) +
                    " worldBytes=" + snapshot.WorldData.Length.ToString(CultureInfo.InvariantCulture));
                client.AcceptJoinGame(joiningClientId);
                BeginWorldTransfer(
                    snapshot.Name, snapshot.WorldData, snapshot.LastSaveTime, joiningClientId,
                    m_sessionRandomSeed, snapshot.TerrainSequence,
                    snapshot.RandomStates, joiningRecord);
                Log.Information($"[ScMP] Accepted ClientID {joiningClientId} and queued live world snapshot " +
                    $"(Tick={snapshot.Tick}, Bytes={snapshot.WorldData.Length})");
                PublishServerAudit("join.snapshot_queued", joiningClientId,
                    "bytes=" + snapshot.WorldData.Length.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                SetServerClientGameTrafficEnabled(joiningClientId, enabled: true);
                RemoveNetworkPlayer(joiningClientId);
                if (isNewApproval)
                {
                    m_playerRecords.Remove(recordKey);
                    m_playerRecordsDirty = true;
                    SavePlayerRecords();
                }
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                client.RefuseJoinGame(joiningClientId, "Failed to prepare player: " + ex.Message);
                Log.Error($"[ScMP] Failed to accept ClientID {joiningClientId}: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private HostedWorldSnapshot CaptureHostedWorldSnapshot()
        {
            Project project = GameManager.Project ??
                throw new InvalidOperationException("The hosted project is not loaded.");
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            // Source: ScMultiplayer.FlushPendingTerrainBroadcasts
            // The exported host world is the joining client's complete terrain baseline. Seal
            // current revision metadata first; any later terrain sequence is join catch-up.
            EnsureHostTerrainSyncStateLoaded();
            FlushPendingTerrainBroadcasts();
            MergePendingTerrainChanges();
            SaveHostTerrainSyncState();
            GameManager.SaveProject(waitForCompletion: true, showErrorDialog: false);

            string snapshotDirectory = Storage.CombinePaths(
                Storage.GetDirectoryName(gameInfo.DirectoryName),
                ".ScMpJoinSnapshot-" + Guid.NewGuid().ToString("N"));
            string snapshotSystemPath = Storage.GetSystemPath(snapshotDirectory);
            byte[] exportedWorld;
            try
            {
                WorldSnapshotFileCopier.CopyDirectory(
                    Storage.GetSystemPath(gameInfo.DirectoryName), snapshotSystemPath);
                using var stream = new MemoryStream();
                WorldsManager.ExportWorld(snapshotDirectory, stream);
                exportedWorld = stream.ToArray();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(snapshotSystemPath))
                        Directory.Delete(snapshotSystemPath, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[ScMP] Failed to remove temporary join snapshot: {ex.Message}");
                }
            }

            var networkPlayerIndices = new HashSet<string>(m_networkPlayerData.Values
                .Where(player => player != null)
                .Select(player => player.PlayerIndex.ToString(CultureInfo.InvariantCulture)));
            byte[] sanitizedWorld = WorldArchiveSanitizer.RemoveNetworkPlayers(
                exportedWorld, networkPlayerIndices);
            WorldInfo worldInfo = WorldsManager.GetWorldInfo(gameInfo.DirectoryName);
            return new HostedWorldSnapshot
            {
                Name = worldInfo?.WorldSettings?.Name ?? SuPlayScreen.WorldDataName,
                WorldData = sanitizedWorld,
                LastSaveTime = worldInfo?.LastSaveTime ?? DateTime.Now,
                Tick = client.Step,
                TerrainSequence = m_hostTerrainSequence,
                RandomStates = CaptureSubsystemRandomStates()
            };
        }

        // Source: RuthlessConquest/Net/Client.cs:Client.Client
        // The world uses Comms reliable UDP with an application-level sliding window and repair
        // requests. A full SHA-256 keeps the independently delivered chunks end-to-end verifiable.
        private void BeginWorldTransfer(string name, byte[] worldData, DateTime lastSaveTime,
            int targetClientId, int randomSeed, long terrainSequence,
            Dictionary<string, long> randomStates, NetworkPlayerRecord playerRecord)
        {
            if (worldData == null || worldData.Length == 0 ||
                worldData.Length > MaximumWorldTransferSize)
                throw new InvalidOperationException("Cached world data has an invalid size.");
            m_nextWorldTransferId = m_nextWorldTransferId == int.MaxValue
                ? 1
                : m_nextWorldTransferId + 1;
            int transferId = m_nextWorldTransferId;
            int chunkCount = (worldData.Length + WorldTransferChunkSize - 1) /
                WorldTransferChunkSize;
            var manifest = new GamePakWorldMessage(name, Array.Empty<byte>(), lastSaveTime,
                targetClientId, randomSeed, randomStates, playerRecord)
            {
                TransferId = transferId,
                ChunkCount = chunkCount,
                TotalLength = worldData.Length,
                WorldSha256 = SHA256.HashData(worldData),
                TerrainSequenceBaseline = terrainSequence
            };
            m_worldTransferRegistry.OutgoingTransfers[targetClientId] = new OutgoingWorldTransfer
            {
                TransferId = transferId,
                TargetClientId = targetClientId,
                StartTime = Time.RealTime,
                WorldData = worldData,
                ChunkCount = chunkCount,
                Manifest = manifest,
                ChunkLastQueueTimes = new double[chunkCount]
            };
            m_joinCatchUpRegistry.TransfersAwaitingReady[targetClientId] = transferId;
            m_joinCatchUpRegistry.HostProjectReadyTransfers.Remove(targetClientId);
            m_joinCatchUpRegistry.CompletedReadyTransfers.Remove(targetClientId);
            // The joining peer requests the manifest after ConnectAccepted has been processed.
            // Sending it here can be ACKed by Comm and then discarded by Peer because the
            // application connection is not established yet.
        }

        // Source: NetworkMessageSender.SendScheduledMessage
        // Joining peers receive the captured journal/checkpoint only. Sending normal broadcasts to
        // them as well fills the same reliable window and can strand the join completion marker.
        internal bool RecordJoinCatchUpMessage(byte[] payload, bool sequenced, bool latest,
            bool recordPayload = true)
        {
            if (!IsHost || payload == null || payload.Length == 0 ||
                m_joinCatchUpRegistry.Journals.Count == 0)
                return false;
            // Source: NetworkMessageSender.SendScheduledMessage
            // Latest-state position/body samples are immediately refreshed after normal traffic
            // is enabled. Recording every sample here creates a large obsolete replay burst and
            // delays the reliable join completion marker without adding authoritative history.
            if (latest || !recordPayload) return true;
            foreach (JoinCatchUpJournal journal in m_joinCatchUpRegistry.Journals.Values)
            {
                if (journal.TotalBytes + payload.Length > MaximumJoinCatchUpBytes)
                {
                    journal.DroppedMessages++;
                    continue;
                }
                byte[] copy = new byte[payload.Length];
                Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                var item = new JoinCatchUpMessage
                {
                    Payload = copy,
                    Sequenced = sequenced,
                    Latest = latest
                };
                // Source: ScMultiplayer.cs:ScMultiplayer.SealAndSendJoinCatchUp
                // Once the cutoff is sealed, new live messages wait in a second buffer and can
                // no longer extend the acknowledged catch-up batch indefinitely.
                if (journal.CutoffSealed)
                    journal.PostCutoffMessages.Add(item);
                else
                    journal.Messages.Add(item);
                journal.TotalBytes += copy.Length;
            }
            return true;
        }

        // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.SendDataMessageToAllClients
        // The normal broadcast route cannot exclude a single peer. During join, emit one copy to
        // each ready peer and leave the joining peer's authoritative state in its bounded journal.
        internal bool SendLiveBroadcastToReadyClients(byte[] payload, bool sequenced, bool latest)
        {
            if (!IsHost || client == null || payload == null || payload.Length == 0 ||
                m_joinCatchUpRegistry.Journals.Count == 0)
                return false;
            foreach (ServerClient remote in GetConnectedRemoteClients())
            {
                if (remote.ClientID <= 0 || m_joinCatchUpRegistry.Journals.ContainsKey(remote.ClientID))
                    continue;
                NetworkMessageSender.SendRawPayload(remote.ClientID, payload, sequenced, latest);
            }
            return true;
        }

        private void FlushJoinCatchUpJournal(int targetClientId)
        {
            if (!m_joinCatchUpRegistry.Journals.TryGetValue(targetClientId,
                out JoinCatchUpJournal journal))
                return;
            JoinCatchUpMessage[] batch = journal.Messages.ToArray();
            journal.Messages.Clear();
            journal.TotalBytes = 0;
            journal.ReplayRound++;
            PendingJoinCatchUp pending = GetOrCreatePendingJoinCatchUp(targetClientId);
            foreach (JoinCatchUpMessage item in batch)
            {
                if (item?.Payload == null) continue;
                pending.Messages.Enqueue(item);
            }
            Log.Information($"[ScMP] Join catch-up batch queued: ClientID={targetClientId}, " +
                $"Round={journal.ReplayRound}, StartTick={journal.StartTick}, " +
                $"Messages={batch.Length}, Bytes={batch.Sum(item => item?.Payload?.Length ?? 0)}, " +
                $"Dropped={journal.DroppedMessages}");
            if (journal.DroppedMessages > 0)
                Log.Warning($"[ScMP] Join catch-up limit reached for ClientID={targetClientId}; " +
                    $"{journal.DroppedMessages} transient messages were replaced by subsequent full-state sync.");
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.RecordJoinCatchUpMessage
        private void DrainPostCutoffJournal(int targetClientId, JoinCatchUpJournal journal)
        {
            JoinCatchUpMessage[] batch = journal.PostCutoffMessages.ToArray();
            journal.PostCutoffMessages.Clear();
            journal.TotalBytes = 0;
            PendingJoinCatchUp pending = GetOrCreatePendingJoinCatchUp(targetClientId);
            foreach (JoinCatchUpMessage item in batch)
            {
                if (item?.Payload == null) continue;
                pending.Messages.Enqueue(item);
            }
            Log.Information($"[ScMP] Join post-cutoff batch queued: ClientID={targetClientId}, " +
                $"Messages={batch.Length}, Bytes={batch.Sum(item => item?.Payload?.Length ?? 0)}, " +
                $"Dropped={journal.DroppedMessages}");
        }

        private PendingJoinCatchUp GetOrCreatePendingJoinCatchUp(int targetClientId)
        {
            if (!m_joinCatchUpRegistry.Pending.TryGetValue(targetClientId, out PendingJoinCatchUp pending))
            {
                pending = new PendingJoinCatchUp { TargetClientId = targetClientId };
                m_joinCatchUpRegistry.Pending.Add(targetClientId, pending);
            }
            return pending;
        }

        private void QueueJoinCatchUpPayload(int targetClientId, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            GetOrCreatePendingJoinCatchUp(targetClientId).Messages.Enqueue(
                new JoinCatchUpMessage { Payload = payload, Sequenced = true });
        }

        // Source: ScMultiplayer.cs:SendPendingWorldTransferChunks
        // Catch-up uses the same spare-bandwidth bucket as the initial world archive. Completion
        // remains ordered after the last catch-up payload on the reliable transport, but must not
        // wait for the application's bulk-window estimate to fall below its limit. That extra
        // gate can strand the completion marker while the client is already waiting in recovery.
        private void SendPendingJoinCatchUps()
        {
            if (m_joinCatchUpRegistry.Pending.Count == 0) return;
            int budget = GetJoinTransferSendBudget(
                m_networkPlayerData.Any(item => item.Key > 0));
            int[] targetClientIds = m_joinCatchUpRegistry.Pending.Keys.OrderBy(id => id).ToArray();
            int attempts = targetClientIds.Length * (budget + 1);
            int cursor = m_worldTransferCursor % Math.Max(1, targetClientIds.Length);
            while (budget > 0 && attempts-- > 0 && targetClientIds.Length > 0)
            {
                int targetClientId = targetClientIds[cursor];
                cursor = (cursor + 1) % targetClientIds.Length;
                if (!m_joinCatchUpRegistry.Pending.TryGetValue(targetClientId, out PendingJoinCatchUp pending))
                    continue;
                if (pending.Messages.Count == 0)
                {
                    m_joinCatchUpRegistry.Pending.Remove(targetClientId);
                    pending.CompletionAction?.Invoke();
                    continue;
                }

                JoinCatchUpMessage item = pending.Messages.Peek();
                if (item?.Payload == null)
                {
                    pending.Messages.Dequeue();
                    continue;
                }
                int estimatedPackets = EstimateReliableRelayPackets(item.Payload.Length);
                int unackedPackets = GetWorldTransferRelayUnackedPackets(targetClientId);
                int joinWindow = GetWorldTransferUnackedPacketLimit(targetClientId);
                // Source: Mod/Comms/Comms/Comm.cs:Comm.SendMessages
                // A reliable message can occupy several 1024-byte UDP packets. Reserve its full
                // estimated packet count before queueing it, otherwise one weather-driven terrain
                // checkpoint can jump past the join window and strand the completion marker.
                bool oversizedMessageCanStart = estimatedPackets > joinWindow &&
                    unackedPackets == 0;
                if (!oversizedMessageCanStart &&
                    unackedPackets + estimatedPackets > joinWindow)
                    continue;
                if (!TryReserveJoinTransferBytes(null, item.Payload.Length))
                    break;
                // Leave the final join-control packet room in the critical reserve.
                if (!TryReserveReliableRelayPackets(targetClientId, estimatedPackets,
                        joinCritical: false))
                {
                    RefundJoinTransferBytes(null, item.Payload.Length);
                    continue;
                }
                pending.Messages.Dequeue();
                NetworkMessageSender.SendRawPayload(targetClientId, item.Payload,
                    sequenced: true, latest: false, relayReservationAlreadyHeld: true);
                RecordJoinTransferBytesSent(item.Payload.Length);
                if (m_joinCatchUpRegistry.Journals.TryGetValue(targetClientId,
                    out JoinCatchUpJournal journal))
                {
                    journal.TotalMessagesSent++;
                    journal.TotalBytesSent += item.Payload.Length;
                }
                budget--;
            }
            m_worldTransferCursor = cursor;
        }

        private void SendPendingWorldTransferChunks()
        {
            if (m_worldTransferRegistry.OutgoingTransfers.Count == 0) return;
            // Source: ScMultiplayer.cs:RequestMissingWorldTransferChunks
            // Source: RuthlessConquest/Net/ServerGame.cs:ServerGame.Run
            // Keep a small reliable-UDP window so delayed ACKs on a lossy remote link do not turn
            // premature retransmissions into a self-sustaining burst.
            bool gameplayActive = m_networkPlayerData.Any(item => item.Key > 0);
            int budget = GetJoinTransferSendBudget(gameplayActive);
            int[] targetClientIds = m_worldTransferRegistry.OutgoingTransfers.Keys.OrderBy(id => id).ToArray();
            if (targetClientIds.Length == 0) return;
            m_worldTransferCursor %= targetClientIds.Length;
            int attemptsRemaining = targetClientIds.Length * (budget + 1);
            while (budget > 0 && attemptsRemaining-- > 0)
            {
                int targetClientId = targetClientIds[m_worldTransferCursor];
                m_worldTransferCursor = (m_worldTransferCursor + 1) % targetClientIds.Length;
                if (!m_worldTransferRegistry.OutgoingTransfers.TryGetValue(targetClientId,
                    out OutgoingWorldTransfer transfer) ||
                    !transfer.StartRequested ||
                    (transfer.InitialSendComplete && transfer.RepairChunkIndices.Count == 0))
                    continue;
                int chunkIndex;
                bool isRepair = transfer.RepairChunkIndices.Count > 0;
                if (isRepair)
                {
                    chunkIndex = transfer.RepairChunkIndices.Dequeue();
                    transfer.QueuedRepairChunkIndices.Remove(chunkIndex);
                }
                else
                {
                    int windowEnd = Math.Min(transfer.ChunkCount,
                        transfer.HighestContiguousChunkIndex + 1 +
                        GetWorldTransferChunkWindow(targetClientId));
                    if (transfer.NextChunkIndex >= windowEnd)
                        continue;
                    chunkIndex = transfer.NextChunkIndex++;
                }
                int payloadBytes = Math.Min(WorldTransferChunkSize,
                    transfer.WorldData.Length - chunkIndex * WorldTransferChunkSize);
                if (!TryReserveJoinTransferBytes(transfer, payloadBytes))
                {
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                    {
                        transfer.NextChunkIndex--;
                    }
                    if (!JoinTransferBudgetPolicy.HasTokens(m_joinTransferTokens,
                        JoinTransferBudgetPolicy.EstimatePacketBytes(payloadBytes)))
                        break;
                    continue;
                }
                int estimatedRelayPackets = WorldTransferRelayPackets;
                if (!TryReserveReliableRelayPackets(targetClientId,
                        estimatedRelayPackets))
                {
                    RefundJoinTransferBytes(transfer, payloadBytes);
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                    {
                        transfer.NextChunkIndex--;
                    }
                    continue;
                }
                if (!QueueWorldTransferChunk(transfer, chunkIndex,
                        estimatedRelayPackets))
                {
                    ReleaseReliableRelayPackets(targetClientId, estimatedRelayPackets);
                    RefundJoinTransferBytes(transfer, payloadBytes);
                    if (isRepair)
                    {
                        if (transfer.QueuedRepairChunkIndices.Add(chunkIndex))
                            transfer.RepairChunkIndices.Enqueue(chunkIndex);
                    }
                    else
                        transfer.NextChunkIndex--;
                    break;
                }
                transfer.ChunkLastQueueTimes[chunkIndex] = Time.RealTime;
                budget--;
                if (!transfer.InitialSendComplete &&
                    transfer.NextChunkIndex >= transfer.ChunkCount)
                {
                    transfer.InitialSendComplete = true;
                    Log.Information($"[ScMP] World transfer initially queued: ClientID={transfer.TargetClientId}, " +
                        $"Transfer={transfer.TransferId}, Chunks={transfer.ChunkCount}");
                }
            }
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
        private void StartWorldTransferSender()
        {
            if (m_worldTransferSendTask != null) return;
            m_worldTransferSendCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = m_worldTransferSendCancellation.Token;
            m_worldTransferSendTask = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await m_worldTransferSendSignal.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    int burstCount = 0;
                    while (m_worldTransferSendQueue.TryDequeue(
                        out WorldTransferChunkSendWork work))
                    {
                        Interlocked.Decrement(ref m_worldTransferQueuedWorkCount);
                        if (work == null)
                            continue;
                        if (work.Generation != Volatile.Read(ref m_worldTransferGeneration) ||
                            cancellationToken.IsCancellationRequested)
                        {
                            ReleaseReliableRelayPackets(work.TargetClientId,
                                work.ReservedPackets);
                            continue;
                        }
                        try
                        {
                            int offset = work.ChunkIndex * WorldTransferChunkSize;
                            int count = Math.Min(WorldTransferChunkSize,
                                work.WorldData.Length - offset);
                            var data = new byte[count];
                            Array.Copy(work.WorldData, offset, data, 0, count);
                            NetworkMessageSender.SendPakWorldChunk(work.TargetClientId,
                                new GamePakWorldChunkMessage
                                {
                                    TransferId = work.TransferId,
                                    TargetClientId = work.TargetClientId,
                                    ChunkIndex = work.ChunkIndex,
                                    ChunkCount = work.ChunkCount,
                                    TotalLength = work.WorldData.Length,
                                    Data = data
                                }, relayReservationAlreadyHeld: true);
                            RecordJoinTransferBytesSent(data.Length);
                            if (++burstCount % 4 == 0)
                                await Task.Delay(1, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            ReleaseReliableRelayPackets(work.TargetClientId,
                                work.ReservedPackets);
                            if (!cancellationToken.IsCancellationRequested)
                                Log.Error($"[ScMP] World transfer sender failed: {ex.Message}");
                        }
                    }
                }
            }, cancellationToken);
        }

        private bool QueueWorldTransferChunk(OutgoingWorldTransfer transfer, int chunkIndex,
            int reservedPackets)
        {
            if (transfer == null || chunkIndex < 0 || chunkIndex >= transfer.ChunkCount ||
                Interlocked.CompareExchange(ref m_worldTransferQueuedWorkCount, 0, 0) >=
                    MaximumQueuedWorldTransferChunks)
                return false;
            Interlocked.Increment(ref m_worldTransferQueuedWorkCount);
            m_worldTransferSendQueue.Enqueue(new WorldTransferChunkSendWork
            {
                Generation = Volatile.Read(ref m_worldTransferGeneration),
                TransferId = transfer.TransferId,
                TargetClientId = transfer.TargetClientId,
                ChunkIndex = chunkIndex,
                ChunkCount = transfer.ChunkCount,
                WorldData = transfer.WorldData,
                ReservedPackets = reservedPackets
            });
            m_worldTransferSendSignal.Release();
            return true;
        }

        // Source: ScMultiplayer.cs:HandleGamePakWorldChunkMessage
        private void RequestMissingWorldTransferChunks()
        {
            double now = Time.RealTime;
            foreach (IncomingWorldTransfer transfer in m_worldTransferRegistry.IncomingTransfers.Values.ToArray())
            {
                if (transfer == null || transfer.Chunks == null ||
                    transfer.ReceivedChunkCount >= transfer.Chunks.Length ||
                    now - transfer.LastStatusRequestTime < WorldTransferProgressStatusInterval)
                    continue;
                bool stalled = now - transfer.LastProgressTime >= WorldTransferRepairInterval;
                bool requestRepair = stalled &&
                    now - transfer.LastRepairRequestTime >= WorldTransferRepairRequestInterval;
                int missingEnd = stalled
                    ? Math.Min(transfer.HighestContiguousChunkIndex + 1 +
                        WorldTransferWindowChunks, transfer.Chunks.Length)
                    : Math.Min(transfer.HighestReceivedChunkIndex + 1,
                        transfer.Chunks.Length);
                int[] missing = requestRepair
                    ? Enumerable.Range(
                            transfer.HighestContiguousChunkIndex + 1,
                            Math.Max(0, missingEnd - transfer.HighestContiguousChunkIndex - 1))
                        .Where(index => transfer.Chunks[index] == null)
                        .Take(MaximumWorldTransferRepairChunks)
                        .ToArray()
                    : Array.Empty<int>();
                transfer.LastStatusRequestTime = now;
                if (missing.Length > 0)
                {
                    transfer.LastRepairRequestTime = now;
                    transfer.RepairRequestCount++;
                }
                NetworkMessageSender.SendPakWorldRepairRequest(
                    new GamePakWorldRepairRequestMessage
                    {
                        TransferId = transfer.TransferId,
                        RequestManifest = transfer.Manifest == null,
                        HighestContiguousChunkIndex = transfer.HighestContiguousChunkIndex,
                        HighestReceivedChunkIndex = transfer.HighestReceivedChunkIndex,
                        MissingChunkIndices = missing
                    });
            }
        }

        private void HandleGamePakWorldRepairRequestMessage(
            GamePakWorldRepairRequestMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 ||
                !m_worldTransferRegistry.OutgoingTransfers.TryGetValue(sourceClientId,
                    out OutgoingWorldTransfer transfer) ||
                (message.TransferId > 0 && transfer.TransferId != message.TransferId))
                return;
            if (message.RequestManifest && transfer.Manifest != null)
            {
                NetworkMessageSender.SendPakWorldManifest(sourceClientId, transfer.Manifest);
            }
            transfer.StartRequested = true;
            transfer.HighestContiguousChunkIndex = Math.Max(
                transfer.HighestContiguousChunkIndex,
                Math.Min(message.HighestContiguousChunkIndex, transfer.ChunkCount - 1));
            foreach (int index in message.MissingChunkIndices ?? Array.Empty<int>())
            {
                if (index < 0 || index >= transfer.NextChunkIndex ||
                    index >= transfer.ChunkLastQueueTimes.Length ||
                    Time.RealTime - transfer.ChunkLastQueueTimes[index] <
                        WorldTransferRepairRequestInterval ||
                    !transfer.QueuedRepairChunkIndices.Add(index))
                    continue;
                transfer.RepairChunkIndices.Enqueue(index);
                transfer.RepairChunkQueueCount++;
            }
        }

        // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Clients
        private int GetWorldTransferRelayUnackedPackets(int targetClientId)
        {
            IPEndPoint address = GetServerClientAddress(targetClientId);
            if (address == null)
                return MaximumWorldTransferUnackedPackets;
            return GetReliableRelayUnackedPackets(targetClientId);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Clients
        private static IPEndPoint GetServerClientAddress(int targetClientId)
        {
            if (server == null || client == null)
                return null;
            ServerGame game = server.Games.FirstOrDefault(item => item.GameID == client.GameID);
            return game?.Clients.FirstOrDefault(item =>
                item.ClientID == targetClientId)?.Address;
        }

        // Source: Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.SetClientGameTrafficEnabled
        private void SetServerClientGameTrafficEnabled(int targetClientId, bool enabled)
        {
            if (server == null || client == null) return;
            ServerGame game = server.Games.FirstOrDefault(item => item.GameID == client.GameID);
            game?.SetClientGameTrafficEnabled(targetClientId, enabled);
        }

    }
}
