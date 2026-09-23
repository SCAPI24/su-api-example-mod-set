using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using ScMultiplayer.Control;
using ScMultiplayer.Diagnostics;
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
        public void HandleGamePlayerHealthMessage(GamePlayerHealthMessage msg, int clientID)
        {
            if (msg == null) return;
            if (IsHost)
            {
                if (clientID <= 0 || msg.PlayerIndex != clientID ||
                    !m_networkPlayerData.TryGetValue(clientID, out PlayerData requestedPlayer) ||
                    requestedPlayer?.ComponentPlayer?.ComponentHealth == null)
                    return;
                ComponentHealth requestedHealth = requestedPlayer.ComponentPlayer.ComponentHealth;
                // 客户端上报的是"这次真实掉了多少"（msg.HealthChange）。客户端本地血量可能被
                // 方案 B 的正值地板钉在 0.02（SuComponentHealth），绝对血量会失真 ——
                // 所以这里按**变化量**在主机自己这份上扣：伤害致命时主机必须真的到 0，
                // 否则客户端怎么点都打不死（实测）。
                float requestedValue = MathUtils.Saturate(
                    requestedHealth.Health + MathUtils.Min(msg.HealthChange, 0f));
                if (msg.HealthChange < -0.0001f && requestedValue < requestedHealth.Health)
                {
                    float requestedPreviousHealth = requestedHealth.Health;
                    // 这里只填一个内部标签，不用作死因（客户端也不显示它）：
                    // 生物攻击 / 饥饿 / 溺水这些**主机在实时模拟**，主机那份角色的 CauseOfDeath
                    // 由引擎自己在 Injure 里写成真死因（ComponentHealth.cs:91-102）；
                    // 客户端上报的只是"本地掉了多少血"（摔落这类主机没算到的部分）。
                    requestedHealth.Injure(requestedHealth.Health - requestedValue, null,
                        ignoreInvulnerability: true, "Client damage request");
                    if (requestedHealth.Health < requestedPreviousHealth - 0.0001f)
                    {
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        requestedPlayer.ComponentPlayer.ComponentCreatureSounds?.PlayPainSound();
                    }
                }
                // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.ProcessSlotItems
                // Eating is predicted by the local clothing inventory. Accept only an upward food
                // edge, then let the normal reliable equipment snapshot carry the consumed item.
                ComponentVitalStats requestedVital = requestedPlayer.ComponentPlayer.ComponentVitalStats;
                float requestedFood = MathUtils.Saturate(msg.Food);
                if (requestedVital != null &&
                    string.Equals(msg.CauseOrSource, "Client food request",
                        StringComparison.Ordinal) &&
                    requestedFood > requestedVital.Food + 0.0001f)
                {
                    ModManager.ModParentField.ModifyParentField(requestedVital, "m_food",
                        requestedFood, typeof(ComponentVitalStats));
                    ModManager.ModParentField.ModifyParentField(requestedVital, "m_lastFood",
                        requestedFood, typeof(ComponentVitalStats));
                }
                // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.StartSickness
                // 客户端进食是本地预测（`ComponentVitalStats` 虽然被替换，但 `Eat` 不是虚方法、
                // 就地跑的是引擎原实现），"吃坏肚子"的判定因此发生在客户端。这里按"只接受向上
                // 边沿"接过来，此后病程由主机掌握并向下递减广播 —— 否则主机那份 0 会立刻把客户端
                // 的恶心/呕吐表现抹掉（实测"吃东西呕吐那些也没有看见"）。
                ComponentSickness requestedSickness =
                    requestedPlayer.ComponentPlayer.Entity.FindComponent<ComponentSickness>();
                if (requestedSickness != null && msg.SicknessDuration > 0.0001f)
                {
                    float currentSickness = ModManager.ModParentField.GetParentField<float>(
                        requestedSickness, "m_sicknessDuration", typeof(ComponentSickness));
                    if (msg.SicknessDuration > currentSickness + 0.0001f)
                    {
                        ModManager.ModParentField.ModifyParentField(requestedSickness,
                            "m_sicknessDuration", msg.SicknessDuration,
                            typeof(ComponentSickness));
                    }
                }
                ComponentSleep requestedSleep = requestedPlayer.ComponentPlayer.ComponentSleep;
                if (requestedSleep != null && requestedSleep.IsSleeping != msg.IsSleeping)
                {
                    // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.WakeUp
                    // A client health snapshot is an observation, not an authority command.
                    // Only the explicit sequence-bearing wake request may end the host's native
                    // sleep session; an unsequenced false value can be stale local presentation
                    // and must never stop host acceleration.
                    if (!msg.IsSleeping)
                    {
                        if (msg.SleepRequestSequence > 0)
                            requestedSleep.WakeUp();
                    }
                    else if (requestedSleep.CanSleep(out _))
                    {
                        requestedSleep.Sleep(true);
                        if (requestedSleep.IsSleeping)
                            UpdateNetworkPlayerRespawnAnchor(clientID, requestedPlayer);
                    }
                }
                if (msg.SleepRequestSequence > 0)
                {
                    // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
                    // Return the host's result for this exact request. Periodic snapshots do not
                    // carry the request sequence and therefore cannot race the local sleep edge.
                    SendAuthoritativePlayerHealth(clientID, requestedPlayer.ComponentPlayer,
                        force: true, sleepRequestSequence: msg.SleepRequestSequence);
                }
                return;
            }
            if (clientID != 0) return;
            // msg.PlayerIndex = 发送方 ClientID, 写入 RemotePlayers
            int remoteClientId = msg.PlayerIndex;
            if (remoteClientId != client.ClientID &&
                !m_networkPlayerData.ContainsKey(remoteClientId))
                return;
            if (msg.AuthoritativeStateSequence <= 0)
                return;
            bool applyAuthoritativeState =
                !m_lastReceivedAuthoritativePlayerStateSequences.TryGetValue(
                    remoteClientId, out int lastAuthoritativeStateSequence) ||
                msg.AuthoritativeStateSequence > lastAuthoritativeStateSequence;
            if (applyAuthoritativeState)
                m_lastReceivedAuthoritativePlayerStateSequences[remoteClientId] =
                    msg.AuthoritativeStateSequence;
            // Source: ScMultiplayer.cs:NetworkMessageSender.SendPlayerHealthMessage
            // A duplicated delayed knockback packet can still carry the only immediate impulse.
            // Its own KnockbackSequence keeps that edge idempotent while the stale vital values
            // below remain rejected.
            if (!applyAuthoritativeState && !msg.HasKnockback)
                return;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer targetPlayer = remoteClientId == client.ClientID
                ? players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData))
                : (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData remoteData)
                    ? remoteData.ComponentPlayer
                    : null);
            float previousHealth = targetPlayer?.ComponentHealth?.Health ?? msg.Health;
            // 方案 B（血量由主机通知、本地只跟随）：本地不再保留任何自己算出来的血量，
            // 所以这里**不再**做旧那套"预测窗口内只许降不许升"的钳制 —— 它会与"每帧跟随主机"
            // 打架：本地血量卡在低位下不来也上不去（实测"最后半格血始终不死"：
            // 本地停在 0.05，每次点击只能上报 0.05，慢点就顶不过主机那边的自然回血）。
            // 现在始终照抄主机权威值，由 SyncClientLocalHealthFromAuthority 每帧写回本地。
            float appliedHealth = msg.Health;
            if (applyAuthoritativeState && remoteClientId == client.ClientID)
                m_localHealthPredictionDeadline = 0.0;
            if (applyAuthoritativeState && remoteClientId == client.ClientID && msg.Health > 0f)
                m_localRespawnPendingUntil = 0.0;
            // 刚复活后的短窗口里，只丢弃**过期的** 0（序号不晚于复活那一刻的旧快照）；
            // 窗口内新发生的死亡序号更大，必须放行 —— 否则会出现
            // "角色界面被关、主机判死被吞掉、人没死"（实测）。
            if (applyAuthoritativeState && remoteClientId == client.ClientID &&
                msg.Health <= 0f && msg.AuthoritativeStateSequence > 0 &&
                msg.AuthoritativeStateSequence <= m_localRespawnStateSequence)
                return;
            int previousWholeLevel = targetPlayer?.PlayerData != null
                ? (int)MathUtils.Floor(MathUtils.Max(targetPlayer.PlayerData.Level, 1f))
                : -1;
            // ⚠️ 死因必须在**写血量之前**落地：下面 ApplyAuthoritativePlayerStats 会把本端血量写成 0，
            // 而 PlayerData 的死亡状态机同帧就会锁存并把死亡界面渲染出来（PlayerData.cs:280-284
            // 读的是当时的 CauseOfDeath）—— 写晚了界面就固定成 Unknown（实测"被狼杀死显示未知"）。
            if (applyAuthoritativeState && remoteClientId == client.ClientID &&
                msg.Health <= 0f && targetPlayer != null)
                ApplyLocalAuthoritativeDeath(targetPlayer, msg);
            if (applyAuthoritativeState)
            {
                ApplyAuthoritativePlayerStats(targetPlayer, appliedHealth, msg.Air, msg.Food,
                    msg.Stamina, msg.Sleep, msg.Temperature, msg.Wetness, msg.Level);
                if (remoteClientId == client.ClientID)
                    UpdateLocalLevelPresentation(targetPlayer, previousWholeLevel, msg.Level);
                (targetPlayer?.ComponentVitalStats as SuComponentVitalStats)?
                    .ApplyAuthoritativeTargetTemperature(msg.TargetTemperature);
                ApplyAuthoritativePlayerEffects(targetPlayer, msg);
                if (remoteClientId == client.ClientID)
                    ApplyLocalConditionCues(targetPlayer, msg);
                if (targetPlayer?.ComponentHealth != null && msg.HealthChange < -0.0001f &&
                    msg.Health < previousHealth - 0.0001f)
                {
                    ModManager.ModParentField.ModifyParentField(
                        targetPlayer.ComponentHealth, "m_lastHealth", previousHealth,
                        typeof(ComponentHealth));
                }
                if (msg.DamageSequence >= 0 && targetPlayer?.ComponentCreatureSounds != null)
                {
                    bool hasDamageBaseline = m_receivedDamageSequences.TryGetValue(
                        remoteClientId, out int lastDamageSequence);
                    if (!hasDamageBaseline || msg.DamageSequence > lastDamageSequence)
                    {
                        m_receivedDamageSequences[remoteClientId] = msg.DamageSequence;
                        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
                        // The first snapshot carries historical sequence state. Only a later edge
                        // is a new injury that should make a sound on this client.
                        if (hasDamageBaseline)
                            targetPlayer.ComponentCreatureSounds.PlayPainSound();
                    }
                }
            }
            if (remoteClientId == client.ClientID)
            {
                if (applyAuthoritativeState)
                {
                    m_hasObservedClientHealth = true;
                    m_observedClientHealth = msg.Health;
                    m_observedClientFood = msg.Food;
                    m_observedClientSleeping = msg.IsSleeping;
                    // 方案 B：只有主机广播的 0 才算"本端死亡"；在那之前客户端不该自己死
                    // （见 SuComponentHealth / SyncClientLocalHealthFromAuthority）。
                    m_localAuthoritativeDeath = msg.Health <= 0f;
                    // 记下"这个死"的权威序号：复活时客户端本地已经把角色重生成满血，
                    // 而主机那份可能还停在 0（复活请求还没被处理）。靠序号区分"过期的主机判死"
                    // 与"复活之后主机新判的死"，见 SyncClientLocalHealthFromAuthority。
                    if (m_localAuthoritativeDeath)
                        m_localDeathSequence = msg.AuthoritativeStateSequence;
                    // 主机最近一次的权威血量：客户端每帧把本地血量写回这个值（本地只跟随主机）。
                    m_lastAuthoritativeLocalHealth = msg.Health;
                }
                if (msg.HasKnockback &&
                    msg.KnockbackSequence > m_lastLocalKnockbackSequence &&
                    targetPlayer?.ComponentBody != null)
                {
                    m_lastLocalKnockbackSequence = msg.KnockbackSequence;
                    targetPlayer.ComponentBody.Velocity = msg.BodyVelocity;
                    // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                    // Apply the host's native stun once so local movement input cannot compete
                    // with the one-shot authoritative knockback impulse.
                    if (targetPlayer.ComponentLocomotion != null)
                    {
                        float elapsedSinceHit = MathUtils.Max(
                            (client.Step - msg.KnockbackServerTick) * ServerTickDuration, 0f);
                        float remainingStun = MathUtils.Max(
                            msg.KnockbackStunTime - elapsedSinceHit, 0f);
                        targetPlayer.ComponentLocomotion.StunTime = MathUtils.Max(
                            targetPlayer.ComponentLocomotion.StunTime,
                            remainingStun);
                    }
                    m_localKnockbackPositionCorrectionUntil = Time.RealTime +
                        LocalKnockbackPositionCorrectionDuration;
                    m_localKnockbackCorrectionStartTick = msg.KnockbackServerTick;
                    m_localInputBodyVelocity = msg.BodyVelocity;
                }
                return;
            }
            if (!applyAuthoritativeState)
                return;
            // Ignore delayed health snapshots from a client that has already left. Without this
            // guard the health handler can recreate the same stale RemotePlayers entry.
            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            state.Health = msg.Health;
            state.MaxHealth = msg.MaxHealth;
            state.IsDead = msg.IsDead;
            if (msg.HasKnockback &&
                msg.KnockbackSequence > state.LastKnockbackSequence)
            {
                state.LastKnockbackSequence = msg.KnockbackSequence;
                // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
                // All observers already receive host position snapshots. Temporarily tighten only
                // their presentation dead zone after a confirmed hit so the struck avatar settles
                // on the same host trajectory without adding another message type or steady traffic.
                state.KnockbackCorrectionUntil = Time.RealTime +
                    LocalKnockbackPositionCorrectionDuration;
                state.KnockbackCorrectionStartTick = msg.KnockbackServerTick;
            }
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Injure
        // 死因（CauseOfDeath）与死亡统计（PlayerStats.AddDeathRecord）在引擎里**只写在 Injure 内**
        // 那一处（ComponentHealth.cs:90-102）。客户端血量是"直接写字段跟随主机"的，不经过 Injure，
        // 所以这两样都要在收到主机判死时由这里补上 —— 否则死亡界面回退成 Unknown、统计里也没有这次死亡。
        // 死因本身**由主机给**：生物攻击（主机端 `Attacked` 会触发，见 EnsureHostSleepWakeHandlers）
        // 与饥饿/溺水/高温都是主机在实时模拟，主机那份角色的 CauseOfDeath 就是真死因。
        // 实测 Unknown 的病根是投递顺序 —— 死亡那一帧"击退"快照先到且当时不带死因，
        // 客户端先落地成 Unknown 并把"一次死亡只落地一次"锁死；现在击退快照也带死因（见
        // ScMultiplayerClientEvents.CaptureHostRemoteKnockbacks），这里再兜一层"拿到真死因就补写"。
        private void ApplyLocalAuthoritativeDeath(ComponentPlayer player, GamePlayerHealthMessage msg)
        {
            if (player?.ComponentHealth == null) return;
            bool dead = msg.Health <= 0f;
            bool wasDead = m_localDeathApplied;
            m_localDeathApplied = dead;
            if (!dead)
            {
                // 复活：下一次死亡重新落地、重新等真死因。
                if (wasDead) m_localDeathCauseResolved = false;
                return;
            }
            string incoming = msg.CauseOrSource;
            bool hasRealIncoming = !string.IsNullOrWhiteSpace(incoming) &&
                !IsInternalRequestCause(incoming);
            // 已经落地过、而且真死因也已经拿到 → 不再动（每秒广播会重复到达）。
            if (wasDead && (m_localDeathCauseResolved || !hasRealIncoming)) return;
            string cause;
            if (hasRealIncoming)
            {
                cause = incoming;
            }
            else
            {
                // 主机这条快照没带死因（历史上"受击击退"那条快照就不带，已修）：
                // 退回本端 CauseOfDeath（本端原生伤害自己跨过 0 时引擎写过一句，例如摔死），
                // 再不行才 Unknown。本 Mod 自己的上报标签（"Client damage request"）
                // **不当作死因显示** —— 那是玩家看不懂的内部串；宁可显示 Unknown，
                // 而且 `resolved` 保持 false，之后拿到真死因还能补上。
                cause = player.ComponentHealth.CauseOfDeath;
                if (string.IsNullOrWhiteSpace(cause)) cause = "Unknown";
            }
            bool resolved = !string.Equals(cause, "Unknown", StringComparison.Ordinal) &&
                !IsInternalRequestCause(cause);
            WriteLocalDeathCause(player, cause, wasDead && !m_localDeathCauseResolved);
            m_localDeathCauseResolved = resolved;
        }

        // 本 Mod 自己在客户端↔主机之间用的上报标签，不是"死因"。
        private static bool IsInternalRequestCause(string cause)
        {
            return string.Equals(cause, "Client damage request", StringComparison.Ordinal) ||
                string.Equals(cause, "Client food request", StringComparison.Ordinal) ||
                string.Equals(cause, "Client state request", StringComparison.Ordinal) ||
                string.Equals(cause, "Client wake request", StringComparison.Ordinal);
        }

        /// <summary>
        /// 写死亡界面读的那句 `CauseOfDeath`，并记一条死亡统计。
        /// <paramref name="updateExistingRecord"/> 为 true 时（这次死亡先前只落到一个占位死因，
        /// 现在真死因到了）改写**上一条**记录的 Cause，而不是再记一条 —— 否则统计里会多出一条。
        /// </summary>
        private void WriteLocalDeathCause(ComponentPlayer player, string cause, bool updateExistingRecord)
        {
            // ⚠️ 死因必须写在血量之前（调用方在写血量前调这里）：PlayerData 的死亡状态机同帧就会锁存，
            // 死亡界面读的就是这一刻的 CauseOfDeath。写不进去也不能连累下面的死亡统计，单独 try 住。
            try
            {
                ModManager.ModParentField.ModifyParentField(player.ComponentHealth,
                    "<CauseOfDeath>k__BackingField", cause, typeof(ComponentHealth));
            }
            catch (Exception)
            {
                // 忽略：最坏情况只是死亡界面那句显示不出来。
            }
            PlayerStats stats = player.PlayerStats;
            if (stats == null) return;
            if (updateExistingRecord)
            {
                // PlayerStats.DeathRecords 是只读包装（PlayerStats.cs:179），
                // 要改上一条只能拿它背后的私有 List（PlayerStats.cs:56）。
                // 出错也不许把死亡流程带崩：最坏情况只是统计里那条仍是 Unknown。
                try
                {
                    List<PlayerStats.DeathRecord> records =
                        ModManager.ModParentField.GetParentField<List<PlayerStats.DeathRecord>>(
                            stats, "m_deathRecords", typeof(PlayerStats));
                    if (records != null && records.Count > 0 &&
                        !string.Equals(records[records.Count - 1].Cause, cause,
                            StringComparison.Ordinal))
                    {
                        PlayerStats.DeathRecord last = records[records.Count - 1];
                        last.Cause = cause;
                        records[records.Count - 1] = last;
                        return;
                    }
                }
                catch (Exception)
                {
                    // 忽略：退回下面再记一条新记录。
                }
            }
            SubsystemTimeOfDay timeOfDay =
                GameManager.Project?.FindSubsystem<SubsystemTimeOfDay>(false);
            // ComponentPlayer 继承自 ComponentCreature，PlayerStats 直接可取
            //（引擎里 ComponentHealth 用的也是同一个对象）。
            stats.AddDeathRecord(new PlayerStats.DeathRecord
            {
                Day = timeOfDay?.Day ?? 0,
                Location = player.ComponentBody?.Position ?? Vector3.Zero,
                Cause = cause
            });
        }

        // Source: Survivalcraft/Game/ComponentLevel.cs:ComponentLevel.AddExperience
        private void UpdateLocalLevelPresentation(ComponentPlayer player, int previousWholeLevel,
            float authoritativeLevel)
        {
            int wholeLevel = (int)MathUtils.Floor(MathUtils.Max(authoritativeLevel, 1f));
            if (m_lastAuthoritativeLocalWholeLevel < 0)
            {
                m_lastAuthoritativeLocalWholeLevel = wholeLevel;
                return;
            }

            bool gainedLevel = player != null &&
                wholeLevel > m_lastAuthoritativeLocalWholeLevel &&
                wholeLevel > previousWholeLevel;
            m_lastAuthoritativeLocalWholeLevel = Math.Max(
                m_lastAuthoritativeLocalWholeLevel, wholeLevel);
            if (!gainedLevel)
                return;

            Project project = GameManager.Project;
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (project == null || audio == null || player.ComponentGui == null)
                return;

            double startTime = Time.FrameStartTime + 0.5;
            Time.QueueTimeDelayedExecution(startTime, () =>
            {
                if (ReferenceEquals(GameManager.Project, project))
                    player.ComponentGui.DisplaySmallMessage("You've gained a level!",
                        Color.White, blinking: true, playNotificationSound: false);
            });
            QueueLevelUpSound(project, audio, startTime, 0.0, -0.2f);
            QueueLevelUpSound(project, audio, startTime, 0.15, -0.03333333f);
            QueueLevelUpSound(project, audio, startTime, 0.3, 2f / 15f);
            QueueLevelUpSound(project, audio, startTime, 0.45, 23f / 60f);
            QueueLevelUpSound(project, audio, startTime, 0.75, -0.03333333f);
            QueueLevelUpSound(project, audio, startTime, 0.9, 23f / 60f);
        }

        // Source: Survivalcraft/Game/ComponentLevel.cs:ComponentLevel.AddExperience
        private static void QueueLevelUpSound(Project project, SubsystemAudio audio,
            double startTime, double delay, float pitch)
        {
            Time.QueueTimeDelayedExecution(startTime + delay, () =>
            {
                if (ReferenceEquals(GameManager.Project, project))
                    audio.PlaySound("Audio/ExperienceCollected", 1f, pitch, 0f, 0f);
            });
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        internal int GetDamageSequence(int playerIndex, float healthChange)
        {
            if (!m_damageSequences.TryGetValue(playerIndex, out int sequence)) sequence = 0;
            if (healthChange < -0.0001f)
                sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            m_damageSequences[playerIndex] = sequence;
            return sequence;
        }

        // Source: SubsystemTime.cs:SubsystemTime.NextFrame
        // Split-screen keeps running while at least one player is outside GameMenuDialog. A
        // network room has players on other devices, so local dialogs must never pause its clock.
        private static void MaintainMultiplayerTimeFlow(Project project)
        {
            if (project == null || client?.IsConnected != true) return;
            SubsystemTime subsystemTime = project.FindSubsystem<SubsystemTime>(false);
            if (subsystemTime == null) return;
            ModManager.ModParentField.ModifyParentField(
                subsystemTime, "m_gameTimeFactor", 1f, typeof(SubsystemTime));
        }

        // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
        // A client owns only its local PlayerData, so vanilla would treat that one sleeping
        // player as "everyone asleep" and run the entire client world at 20 updates per frame.
        // The host already publishes authoritative elapsed world time; clients must keep normal
        // simulation cadence and follow those snapshots instead of duplicating sleep catch-up.
        internal void MaintainClientAuthoritativeTimeFlow(Project project)
        {
            if (IsHost || client?.IsConnected != true || project == null) return;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (time == null || update == null) return;
            ModManager.ModParentField.ModifyParentField(time, "<FixedTimeStep>k__BackingField",
                (float?)null, typeof(SubsystemTime));
            update.UpdatesPerFrame = 1;
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
        // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.Update
        private void ApplyAuthoritativePlayerEffects(
            ComponentPlayer player, GamePlayerHealthMessage message)
        {
            if (player == null || message == null) return;
            bool isLocalPlayer = !m_networkPlayerData.Values.Contains(player.PlayerData);
            if (player.ComponentSleep != null)
            {
                if (message.IsSleeping)
                {
                    if (isLocalPlayer)
                        m_pendingClientSleepRequestSequence = 0;
                    m_pendingClientSleepWakeups.Remove(player.ComponentSleep);
                    if (!player.ComponentSleep.IsSleeping)
                        player.ComponentSleep.Sleep(true);
                    if (message.SleepStartTime > 0.0 &&
                        !double.IsNaN(message.SleepStartTime) &&
                        !double.IsInfinity(message.SleepStartTime))
                    {
                        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
                        // Replace the local request-arrival time with the host's session boundary
                        // so the automatic 180-second/daylight wake test is shared by all peers.
                        ModManager.ModParentField.ModifyParentField(
                            player.ComponentSleep, "m_sleepStartTime",
                            (double?)message.SleepStartTime, typeof(ComponentSleep));
                    }
                    // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
                    // A replicated value of exactly 1 would make the client run the native 20x
                    // all-players-sleep loop before the next circuit update can clamp it.
                    float authoritativeSleepFactor = MathUtils.Min(
                        MathUtils.Clamp(message.SleepFactor, 0f, 1f), 0.999f);
                    ModManager.ModParentField.ModifyParentField(
                        player.ComponentSleep, "m_sleepFactor",
                        authoritativeSleepFactor,
                        typeof(ComponentSleep));
                }
                else if (player.ComponentSleep.IsSleeping)
                {
                    bool stalePreSleepState = isLocalPlayer &&
                        m_pendingClientSleepRequestSequence > 0 &&
                        message.SleepRequestSequence != m_pendingClientSleepRequestSequence;
                    if (!stalePreSleepState)
                    {
                        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.WakeUp
                        // The host's false sleep edge is the authoritative vanilla wake boundary.
                        // Apply it immediately; circuit recovery remains independent and keeps the
                        // electricity timeline paused until the host snapshot/fence is rebased.
                        if (isLocalPlayer)
                            m_pendingClientSleepRequestSequence = 0;
                        m_pendingClientSleepWakeups.Remove(player.ComponentSleep);
                        player.ComponentSleep.WakeUp();
                    }
                    // When stalePreSleepState is true, the periodic false snapshot was created
                    // before the host processed the current request and is not a wake boundary.
                }
            }
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (onFire != null)
                ModManager.ModParentField.ModifyParentField(
                    onFire, "m_fireDuration", MathUtils.Max(message.FireDuration, 0f), typeof(ComponentOnFire));
            if (flu != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    flu, "m_fluDuration", MathUtils.Max(message.FluDuration, 0f), typeof(ComponentFlu));
                ModManager.ModParentField.ModifyParentField(
                    flu, "m_fluOnset", MathUtils.Max(message.FluOnset, 0f), typeof(ComponentFlu));
            }
            if (sickness != null)
                ModManager.ModParentField.ModifyParentField(
                sickness, "m_sicknessDuration", MathUtils.Max(message.SicknessDuration, 0f), typeof(ComponentSickness));
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Sneeze / Cough
        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.NauseaEffect
        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        // 玩家自身能感知的"条件表现"（喷嚏/咳嗽、恶心呕吐、各类提示）全部由主机产生、客户端只播。
        // 只对本机角色播 —— 别人的状态事件不该在本地响起或显示。
        private void ApplyLocalConditionCues(ComponentPlayer player, GamePlayerHealthMessage message)
        {
            if (player == null || message == null) return;
            (player.Entity.FindComponent<ComponentFlu>() as SuComponentFlu)?.ApplyAuthoritativeCues(
                message.SneezeSequence, message.IsSneezing, message.CoughSequence,
                message.IsCoughing, message.BlackoutSeconds);
            (player.Entity.FindComponent<ComponentSickness>() as SuComponentSickness)?
                .ApplyAuthoritativeNausea(message.NauseaSequence, message.NauseaPuked,
                    message.GreenoutSeconds);
            (player.ComponentVitalStats as SuComponentVitalStats)?.ApplyAuthoritativeHint(
                message.HintSequence, message.HintText);
        }

        private bool ShouldDeferClientSleepWakeup()
        {
            if (IsHost || client?.IsConnected != true)
                return false;
            // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
            // CircuitSynchronizer.UpdateHostTimeAccelerationFromFence
            // Once bound, the circuit timeline is the stronger acceleration authority. It can
            // recover from a missing world-info falling edge, so an older outer flag must not
            // keep a ready client asleep forever.
            if (m_circuitSynchronizer != null)
                return m_circuitSynchronizer.IsHostTimeAccelerationActive ||
                    !m_circuitSynchronizer.IsClientBootstrapReady;
            return m_remoteTimeAccelerated;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.UpdateHostTimeAccelerationFromFence
        internal void ConfirmRemoteTimeAccelerationEndedFromCircuitFence()
        {
            if (!IsHost)
                m_remoteTimeAccelerated = false;
        }

        // Source: CircuitSynchronizer.NotifyRemoteTimeAccelerationChanged
        // The reliable world edge or the fence-rate fallback can end the host sleep session.
        // Preserve that fact independently from a single player-health snapshot so a client
        // cannot remain asleep after the authoritative circuit boundary is ready.
        internal void MarkClientSleepWakeBoundaryPending()
        {
            if (!IsHost)
                m_clientSleepWakeBoundaryPending = true;
        }

        // Source: CircuitSynchronizer.IsClientBootstrapReady
        // The host snapshot rebases directly to the final accelerated circuit boundary. Do not
        // replay the night's missing steps. The player wake edge is applied independently from
        // this circuit rebase, so a delayed fence cannot keep the player asleep into the next
        // night.
        private void CompletePendingClientSleepWakeups()
        {
            if (!m_clientSleepWakeBoundaryPending &&
                m_pendingClientSleepWakeups.Count == 0)
                return;

            if (m_clientSleepWakeBoundaryPending)
            {
                SubsystemPlayers players = GameManager.Project?
                    .FindSubsystem<SubsystemPlayers>(false);
                if (players == null)
                    return;

                bool foundLocalPlayer = false;
                foreach (ComponentPlayer player in players.ComponentPlayers.ToArray())
                {
                    if (player?.PlayerData == null ||
                        m_networkPlayerData.Values.Contains(player.PlayerData))
                        continue;
                    foundLocalPlayer = true;
                    if (player.ComponentSleep?.IsSleeping == true)
                        m_pendingClientSleepWakeups.Add(player.ComponentSleep);
                }
                if (!foundLocalPlayer)
                    return;
                m_clientSleepWakeBoundaryPending = false;
            }

            if (m_pendingClientSleepWakeups.Count == 0)
                return;
            foreach (ComponentSleep sleep in m_pendingClientSleepWakeups.ToArray())
            {
                if (sleep?.IsSleeping == true)
                    sleep.WakeUp();
            }
            m_pendingClientSleepWakeups.Clear();
        }

        public void HandleGameKickPlayerMessage(GameKickPlayerMessage msg, int sourceClientID)
        {
            // 仅 Host 可以处理踢人
            if (client.ClientID != 0) return;

            int targetID = msg.TargetClientID;
            Log.Information($"[ScMP] Kick request: ClientID {targetID}, reason: {msg.Reason}");

            // 释放玩家映射
            playerMappingManager.ReleasePlayerIndex(targetID);

            // 通过 Drt 框架断开玩家
            // Comms.Drt 内部管理连接，我们通过 RefuseJoinGame 已经可以阻止加入
            // Peer 层的 DisconnectPeer 需要 PeerData 引用
            Log.Information($"[ScMP] Player {targetID} kicked");
        }

        public void HandleGameWorldInfoMessage(GameWorldInfoMessage1 msg)
        {
            Project project = GameManager.Project;
            if (project == null || IsHost) return;
            // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
            // WorldTimeRevision is incremented by the host for every world snapshot, including
            // the reliable sleep acceleration edges. It is the ordering source for sleep timing;
            // ServerTick alone can reject a delayed reliable falling edge after a newer latest
            // datagram has already arrived.
            if (msg.WorldTimeRevision <= 0 ||
                msg.WorldTimeRevision < m_lastRemoteWorldTimeRevision ||
                (msg.WorldTimeRevision == m_lastRemoteWorldTimeRevision &&
                 msg.ServerTick < m_lastRemoteWorldInfoTick))
                return;
            m_lastRemoteWorldTimeRevision = msg.WorldTimeRevision;
            m_lastRemoteWorldInfoTick = msg.ServerTick;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
            // Only the host executes the accelerated sleep timeline. During the rising/active
            // phase the client retains its one-step clock; the reliable falling edge applies the
            // final host clock once before circuit rebase and visual wakeup.
            bool applyAuthoritativeTime = !msg.IsTimeAccelerated;
            // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDay
            // TimeOfDay depends on both values. Synchronizing only the offset allows the imported
            // client clock to remain minutes away from the host clock.
            if (applyAuthoritativeTime &&
                Math.Abs(gameInfo.TotalElapsedGameTime - msg.TotalElapsedGameTime) > 0.25 &&
                // 世界时钟只允许前进：小幅回退夹掉，避免掉落物的渲染旋转/浮动反复回退
                // （`SubsystemPickables.Draw` 直接用 TotalElapsedGameTime 现算角度）。
                (msg.TotalElapsedGameTime >= gameInfo.TotalElapsedGameTime ||
                 gameInfo.TotalElapsedGameTime - msg.TotalElapsedGameTime >=
                    CircuitSynchronizer.MaximumClockRewindWithoutCorrection))
            {
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "<TotalElapsedGameTime>k__BackingField",
                    msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "m_lastTotalElapsedGameTime",
                    (double?)msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
            }
            if (applyAuthoritativeTime)
            {
                gameInfo.WorldSettings.TimeOfDayMode = msg.CurrentTimeMode;
                if (Math.Abs(timeOfDay.TimeOfDayOffset - msg.TimeOfDayOffset) > 0.0001)
                    timeOfDay.TimeOfDayOffset = msg.TimeOfDayOffset;
            }
            // The latest world state is the clock authority. Refresh the electricity anchor now
            // so an older fence cannot restore pre-sleep or pre-button time on the next circuit step.
            if (applyAuthoritativeTime)
                m_circuitSynchronizer?.UpdateAuthoritativeWorldTime(msg.TotalElapsedGameTime,
                    msg.TimeOfDayOffset, msg.ServerTick, msg.WorldTimeRevision);
            // Source: CircuitSynchronizer.NotifyRemoteTimeAccelerationChanged
            // Notify on every accepted authoritative world snapshot. A fence-rate inference can
            // mark acceleration locally while m_remoteTimeAccelerated is still false; guarding
            // this call by the remote flag would then miss the authoritative wake edge.
            m_remoteTimeAccelerated = msg.IsTimeAccelerated;
            m_circuitSynchronizer?.NotifyRemoteTimeAccelerationChanged(
                msg.IsTimeAccelerated);
            m_remoteWeatherState = msg;
            RecordRemoteWeatherSample(msg);
            m_remoteTerrainHeadSequence = Math.Max(
                m_remoteTerrainHeadSequence, msg.TerrainSequence);
            if (m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
                SuppressClientJoinWeatherPresentation(project);
            else
                ApplyRemoteWeatherState();
        }

        public bool TrySendWorldControlRequest(ComponentPlayer componentPlayer, WorldControlAction actions)
        {
            if (actions == WorldControlAction.None || IsHost || client?.IsConnected != true ||
                componentPlayer == null || m_networkPlayerData.Values.Contains(componentPlayer.PlayerData))
                return false;
            SubsystemGameInfo localGameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (!HasLocalPlayerCapability(PlayerCapabilityFlags.WorldControl) &&
                localGameInfo?.WorldSettings.GameMode != GameMode.Creative)
                return false;

            // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
            // CircuitSynchronizer.ShouldSuppressClientInput
            // A blocking recovery owns the circuit timeline. Preserve every accepted physical
            // click locally and release it in order after the authority fence is healthy again.
            if (ShouldDeferWorldControlRequest() || m_queuedWorldControlRequests.Count > 0)
            {
                if (m_pendingWorldControlRequests.Count + m_queuedWorldControlRequests.Count >=
                    MaximumPendingWorldControlRequests)
                {
                    DisplayWorldControlFeedback(componentPlayer,
                        "World control queue is full. Please wait for synchronization.");
                    return true;
                }
                m_queuedWorldControlRequests.Enqueue(new QueuedWorldControlRequest
                {
                    Actions = actions,
                    ComponentPlayer = componentPlayer
                });
                if (!m_worldControlQueueNoticeShown)
                {
                    m_worldControlQueueNoticeShown = true;
                    DisplayWorldControlFeedback(componentPlayer,
                        "World control queued. Waiting for synchronization.");
                }
                return true;
            }

            SendWorldControlRequestNow(componentPlayer, actions);
            return true;
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.TrySendWorldControlRequest
        private bool ShouldDeferWorldControlRequest() =>
            !IsHost && client?.IsConnected == true &&
            (m_clientTerrainRecoveryActive ||
            m_circuitSynchronizer?.ShouldSuppressClientInput == true);

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.TrySendWorldControlRequest
        private void SendWorldControlRequestNow(ComponentPlayer componentPlayer,
            WorldControlAction actions)
        {
            do
            {
                m_nextWorldControlRequestId = m_nextWorldControlRequestId == int.MaxValue
                    ? 1
                    : m_nextWorldControlRequestId + 1;
            }
            while (m_pendingWorldControlRequests.ContainsKey(m_nextWorldControlRequestId));

            int requestId = m_nextWorldControlRequestId;
            // Register before sending. A low-latency host can return the authoritative result from
            // another network callback before this game frame completes.
            m_pendingWorldControlRequests[requestId] = new PendingWorldControlRequest
            {
                Actions = actions,
                ComponentPlayer = componentPlayer,
                ExpirationTime = Time.RealTime + WorldControlResultTimeout
            };
            try
            {
                NetworkMessageSender.SendWorldControlRequest(requestId, actions);
            }
            catch (Exception ex)
            {
                PendingWorldControlRequest failed =
                    m_pendingWorldControlRequests[requestId];
                failed.TimedOut = true;
                failed.FailureMessage = "World control request failed: " + ex.Message;
                DrainWorldControlResults();
                return;
            }
        }

        private void HandleWorldControlRequest(WorldControlRequestMessage message, int sourceClientId)
        {
            Project project = GameManager.Project;
            if (!IsHost || sourceClientId <= 0 || message == null || message.RequestId <= 0 ||
                project == null ||
                !m_networkPlayerData.ContainsKey(sourceClientId))
                return;
            if (!m_hostWorldControlRequestStates.TryGetValue(sourceClientId,
                    out HostWorldControlRequestState state))
            {
                state = new HostWorldControlRequestState();
                m_hostWorldControlRequestStates.Add(sourceClientId, state);
            }

            // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
            // Each physical click owns a new RequestId. Only a retransmission of an already
            // completed id is deduplicated, and it receives the cached authoritative result.
            if (state.Completed.TryGetValue(message.RequestId,
                    out WorldControlResultMessage completed))
            {
                NetworkMessageSender.SendWorldControlResult(sourceClientId, completed);
                return;
            }
            if (message.RequestId == state.NextExpectedRequestId)
            {
                ProcessOrderedWorldControlRequests(sourceClientId, state, message);
                return;
            }

            long forwardDistance = message.RequestId - (long)state.NextExpectedRequestId;
            if (forwardDistance < 0) forwardDistance += int.MaxValue;
            if (forwardDistance > 0 && forwardDistance <= int.MaxValue / 2 &&
                state.Pending.Count < MaximumPendingWorldControlRequests &&
                !state.Pending.ContainsKey(message.RequestId))
                state.Pending.Add(message.RequestId, message);
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.HandleWorldControlRequest
        private void ProcessOrderedWorldControlRequests(int sourceClientId,
            HostWorldControlRequestState state, WorldControlRequestMessage first)
        {
            WorldControlRequestMessage message = first;
            while (message != null)
            {
                WorldControlResultMessage result = ExecuteWorldControlRequest(
                    message, sourceClientId);
                state.Completed[message.RequestId] = result;
                state.CompletedOrder.Enqueue(message.RequestId);
                while (state.CompletedOrder.Count > MaximumCachedWorldControlResults)
                    state.Completed.Remove(state.CompletedOrder.Dequeue());
                NetworkMessageSender.SendWorldControlResult(sourceClientId, result);
                if (result.Actions != WorldControlAction.None)
                    PublishServerAudit("world.control", sourceClientId,
                        "actions=" + result.Actions + " time=" + result.TimeResult +
                        " rain=" + result.PrecipitationStarted + " fog=" + result.FogStarted +
                        " lightning=" + result.LightningTriggered);

                state.NextExpectedRequestId = message.RequestId == int.MaxValue
                    ? 1
                    : message.RequestId + 1;
                if (!state.Pending.TryGetValue(state.NextExpectedRequestId, out message))
                    break;
                state.Pending.Remove(state.NextExpectedRequestId);
            }
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
        private WorldControlResultMessage ExecuteWorldControlRequest(
            WorldControlRequestMessage message, int sourceClientId)
        {
            Project project = GameManager.Project;
            WorldControlAction validActions = message.Actions &
                (WorldControlAction.TimeOfDay | WorldControlAction.Precipitation |
                WorldControlAction.Fog | WorldControlAction.Lightning);
            var result = new WorldControlResultMessage(message.RequestId, validActions);
            if (project == null) return result;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            bool authorized = gameInfo.WorldSettings.GameMode == GameMode.Creative ||
                sourceClientId > 0 &&
                HasPlayerCapability(sourceClientId, PlayerCapabilityFlags.WorldControl);
            if (!authorized)
            {
                result.Actions = WorldControlAction.None;
                return result;
            }

            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            SubsystemTimeOfDay timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(true);
            ComponentGui hostGui = project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers
                .FirstOrDefault(player => !m_networkPlayerData.Values.Contains(player.PlayerData))?.ComponentGui;
            if (validActions.HasFlag(WorldControlAction.Precipitation))
            {
                if (weather.IsPrecipitationStarted)
                {
                    weather.ManualPrecipitationEnd();
                    hostGui?.DisplaySmallMessage("Precipitation Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualPrecipitationStart();
                    hostGui?.DisplaySmallMessage("Precipitation On", Color.White, false, false);
                }
                result.PrecipitationStarted = weather.IsPrecipitationStarted;
            }
            if (validActions.HasFlag(WorldControlAction.Fog))
            {
                if (weather.IsFogStarted)
                {
                    weather.ManualFogEnd();
                    hostGui?.DisplaySmallMessage("Fog Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualFogStart();
                    hostGui?.DisplaySmallMessage("Fog On", Color.White, false, false);
                }
                result.FogStarted = weather.IsFogStarted;
            }
            if (validActions.HasFlag(WorldControlAction.TimeOfDay))
            {
                // Source: Mod/ScMultiplayer/Modules/Player/
                // ScMultiplayerHealthWorldControlHandlers.cs:HandleWorldControlResult
                // The directed result displays the native time label on the requesting client.
                // The host applies authority here without showing another player's feedback.
                float dawn = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middawn);
                float noon = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midday);
                float dusk = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middusk);
                float midnight = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midnight);
                float nearest = MathUtils.Min(dawn, noon, dusk, midnight);
                if (dawn == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dawn;
                    result.TimeResult = WorldControlTimeResult.Dawn;
                }
                else if (noon == nearest)
                {
                    timeOfDay.TimeOfDayOffset += noon;
                    result.TimeResult = WorldControlTimeResult.Noon;
                }
                else if (dusk == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dusk;
                    result.TimeResult = WorldControlTimeResult.Dusk;
                }
                else
                {
                    timeOfDay.TimeOfDayOffset += midnight;
                    result.TimeResult = WorldControlTimeResult.Midnight;
                }
            }
            if (validActions.HasFlag(WorldControlAction.Lightning) &&
                m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData sourcePlayer) &&
                sourcePlayer?.ComponentPlayer != null)
            {
                double previousStrikeTime = ModManager.ModParentField.GetParentField<double>(
                    sky, "m_lastLightningStrikeTime", typeof(SubsystemSky));
                ComponentCreatureModel model = sourcePlayer.ComponentPlayer.ComponentCreatureModel;
                Matrix eyeMatrix = Matrix.CreateFromQuaternion(model.EyeRotation);
                weather.ManualLightingStrike(model.EyePosition, eyeMatrix.Forward);
                double currentStrikeTime = ModManager.ModParentField.GetParentField<double>(
                    sky, "m_lastLightningStrikeTime", typeof(SubsystemSky));
                result.LightningTriggered = currentStrikeTime > previousStrikeTime;
            }
            SendGameWorldInfoMessage();
            return result;
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        private void HandleWorldControlResult(WorldControlResultMessage message,
            int sourceClientId)
        {
            PendingWorldControlRequest pending = null;
            bool pendingFound = message != null &&
                m_pendingWorldControlRequests.TryGetValue(message.RequestId, out pending);
            if (IsHost || sourceClientId != 0 || message == null || !pendingFound)
                return;
            // A result is self-identifying. Waiting for an earlier button result can suppress the
            // feedback for a later accepted click even though its world mutation already happened.
            m_pendingWorldControlRequests.Remove(message.RequestId);
            DisplayWorldControlResult(message, pending);
        }

        // Source: Mod/ScMultiplayer/Message/WorldControlResultMessage.cs:
        // WorldControlResultMessage.RequestId
        private void DrainWorldControlResults()
        {
            foreach (KeyValuePair<int, PendingWorldControlRequest> item in
                m_pendingWorldControlRequests.Where(entry => entry.Value.TimedOut).ToArray())
            {
                m_pendingWorldControlRequests.Remove(item.Key);
                DisplayWorldControlFeedback(item.Value.ComponentPlayer,
                    item.Value.FailureMessage ??
                    "Host did not confirm the world control request.");
            }
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        private void DisplayWorldControlResult(WorldControlResultMessage message,
            PendingWorldControlRequest pending)
        {

            WorldControlAction confirmed = message.Actions & pending.Actions;
            var feedback = new List<string>();
            if (confirmed.HasFlag(WorldControlAction.Precipitation))
                feedback.Add(message.PrecipitationStarted
                    ? "Precipitation On"
                    : "Precipitation Off");
            if (confirmed.HasFlag(WorldControlAction.Fog))
                feedback.Add(message.FogStarted ? "Fog On" : "Fog Off");
            if (confirmed.HasFlag(WorldControlAction.TimeOfDay) &&
                message.TimeResult != WorldControlTimeResult.None)
            {
                // Source: Mod/ScMultiplayer/Message/WorldControlResultMessage.cs:
                // WorldControlTimeResult
                // Obfuscar can rename enum member identifiers. UI text must not depend on
                // Enum.ToString(), otherwise a Release build displays the renamed identifier.
                string timeText = message.TimeResult switch
                {
                    WorldControlTimeResult.Dawn => "Dawn",
                    WorldControlTimeResult.Noon => "Noon",
                    WorldControlTimeResult.Dusk => "Dusk",
                    WorldControlTimeResult.Midnight => "Midnight",
                    _ => null
                };
                if (timeText != null) feedback.Add(timeText);
            }
            if (confirmed.HasFlag(WorldControlAction.Lightning))
                feedback.Add(message.LightningTriggered ? "Lightning" : "Lightning unavailable");
            if (feedback.Count == 0) return;

            DisplayWorldControlFeedback(pending.ComponentPlayer,
                string.Join("\n", feedback));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        private void DisplayWorldControlFeedback(ComponentPlayer preferredPlayer,
            string message)
        {
            ComponentPlayer localPlayer = preferredPlayer;
            if (localPlayer?.ComponentGui == null)
                localPlayer = m_localReplacementPlayerData?.ComponentPlayer;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (localPlayer?.ComponentGui == null)
                localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
            localPlayer?.ComponentGui.DisplaySmallMessage(
                message, Color.White, false, false);
        }

        private void AdvanceWorldControlFeedbackRequestId()
        {
            m_nextWorldControlFeedbackRequestId =
                m_nextWorldControlFeedbackRequestId == int.MaxValue
                    ? 1
                    : m_nextWorldControlFeedbackRequestId + 1;
        }

        // Source: Engine/Time.cs:Time.RealTime
        private void UpdatePendingWorldControlRequests()
        {
            FlushQueuedWorldControlRequest();
            if (m_pendingWorldControlRequests.Count == 0) return;
            double now = Time.RealTime;
            foreach (PendingWorldControlRequest pending in m_pendingWorldControlRequests
                .Where(item => item.Value.ExpirationTime <= now)
                .Select(item => item.Value).ToArray())
                pending.TimedOut = true;
            DrainWorldControlResults();
        }

        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.TrySendWorldControlRequest
        private void FlushQueuedWorldControlRequest()
        {
            if (m_queuedWorldControlRequests.Count == 0)
            {
                m_worldControlQueueNoticeShown = false;
                return;
            }
            if (IsHost || client?.IsConnected != true)
            {
                m_queuedWorldControlRequests.Clear();
                m_worldControlQueueNoticeShown = false;
                return;
            }
            if (ShouldDeferWorldControlRequest()) return;

            QueuedWorldControlRequest queued = m_queuedWorldControlRequests.Dequeue();
            SendWorldControlRequestNow(queued.ComponentPlayer, queued.Actions);
            if (m_queuedWorldControlRequests.Count == 0)
                m_worldControlQueueNoticeShown = false;
        }

        public void ApplyRemoteWeatherState()
        {
            GameWorldInfoMessage1 msg = m_remoteWeatherState;
            Project project = GameManager.Project;
            if (msg == null || project == null || IsHost) return;
            if (m_worldTransferRegistry.PendingWorldReadyTransferId > 0)
            {
                SuppressClientJoinWeatherPresentation(project);
                return;
            }
            // 主机权威世界设置快照 → 本地应用（季节/选项等；只写有差异的字段）
            WorldSettingsFields.TryApplySnapshot(
                project.FindSubsystem<SubsystemGameInfo>(false)?.WorldSettings, msg.WorldSettings);
            // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            if (weather.IsPrecipitationStarted != msg.IsPrecipitationStarted)
                ConfigureRemotePrecipitationSchedule(weather, msg.IsPrecipitationStarted);
            if (weather.IsFogStarted != msg.IsFogStarted)
                ConfigureRemoteFogSchedule(weather, msg.IsFogStarted);
            if (!m_remoteFogPresentationInitialized)
            {
                ModManager.ModParentField.ModifyParentField(
                    weather, "<FogProgress>k__BackingField", msg.FogProgress, typeof(SubsystemWeather));
                ModManager.ModParentField.ModifyParentField(
                    weather, "<FogIntensity>k__BackingField", msg.FogIntensity, typeof(SubsystemWeather));
                // 降雨强度同样只在第一份样本时对齐一次：之后一律由
                // UpdateRemoteWeatherPresentation 在 2Hz 样本之间插值推进，
                // 不能在这里按样本直接覆写，否则客户端会跟着 2Hz 台阶反复回退。
                ModManager.ModParentField.ModifyParentField(
                    weather, "<PrecipitationIntensity>k__BackingField",
                    msg.PrecipitationIntensity, typeof(SubsystemWeather));
                m_remoteFogPresentationInitialized = true;
            }
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogSeed>k__BackingField", msg.FogSeed, typeof(SubsystemWeather));
            SuppressClientRandomLightning(project);

            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(true);
            if (msg.HasLightningStrike && !m_remoteLightningActive)
            {
                m_remoteLightningActive = true;
                ApplyRemoteLightningVisual(sky, msg.LightningStrikePosition);
            }
            else if (!msg.HasLightningStrike)
            {
                ClearRemoteLightningVisual(sky);
                m_remoteLightningActive = false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.Update
        // Joining clients have not applied the host terrain/circuit boundary yet. Keep weather
        // presentation dormant so rain, snow, fog and lightning work does not compete with apply;
        // the authoritative state is restored as soon as ReadyToPlay clears the barrier.
        private void SuppressClientJoinWeatherPresentation(Project project)
        {
            if (IsHost || project == null ||
                m_worldTransferRegistry.PendingWorldReadyTransferId <= 0)
                return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            ModManager.ModParentField.ModifyParentField(
                weather, "<PrecipitationIntensity>k__BackingField", 0f,
                typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogProgress>k__BackingField", 0f,
                typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogIntensity>k__BackingField", 0f,
                typeof(SubsystemWeather));
            SuppressClientRandomLightning(project);
            if (m_remoteLightningActive)
            {
                ClearRemoteLightningVisual(project.FindSubsystem<SubsystemSky>(false));
                m_remoteLightningActive = false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateFog
        // Disable the client's independent random fog schedule while retaining the original
        // SubsystemWeather renderer and all weather effects.
        private static void ConfigureRemoteFogSchedule(SubsystemWeather weather, bool isStarted)
        {
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (weather == null || gameInfo == null) return;
            double startTime = isStarted
                ? gameInfo.TotalElapsedGameTime
                : double.MaxValue;
            ModManager.ModParentField.ModifyParentField(
                weather, "m_fogStartTime", startTime, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "m_fogEndTime", double.MaxValue, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "m_fogRampTime", float.MaxValue, typeof(SubsystemWeather));
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.ManualPrecipitationStart
        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
        // 客户端不自己决定下雨：只跟着主机的 IsPrecipitationStarted 翻转标志位，并把引擎那条
        // "自己加/减强度"的 ramp 关掉（rampTime = MaxValue → 每帧增量≈0）。
        // 为什么必须关：`ManualPrecipitationStart/End` 会把 rampTime 设成 1 秒，客户端就会以 1 秒
        // 的速度冲到 1、再被 2Hz 样本拉回旧值 —— 这正是"反复回退"的来源。
        // 关掉之后，雨的大小完全由 UpdateRemoteWeatherPresentation 在主机 2Hz 样本之间插值给出，
        // 同时随机天气计划（m_precipitationEndTime == 0 那条分支）也不会在客户端自行触发。
        private static void ConfigureRemotePrecipitationSchedule(SubsystemWeather weather,
            bool isStarted)
        {
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (weather == null || gameInfo == null) return;
            double startTime = isStarted
                ? gameInfo.TotalElapsedGameTime
                : double.MaxValue;
            ModManager.ModParentField.ModifyParentField(
                weather, "m_precipitationStartTime", startTime, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "m_precipitationEndTime", double.MaxValue, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "m_precipitationRampTime", float.MaxValue, typeof(SubsystemWeather));
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateFog
        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
        // 记录主机 2Hz 天气样本：雾（进度/浓度）与降雨强度各留"上一份 / 最新一份"，和它们的到达
        // 时间，供每帧插值使用。
        private void RecordRemoteWeatherSample(GameWorldInfoMessage1 msg)
        {
            if (msg == null || IsHost) return;
            double now = Time.RealTime;
            if (m_remoteFogSampleTime <= 0.0)
            {
                // 第一份样本（刚进房间）：两份都设成它，插值从当前位置开始，避免第一帧跳变。
                m_remoteFogPreviousProgress = msg.FogProgress;
                m_remoteFogPreviousIntensity = msg.FogIntensity;
                m_remoteFogSampleProgress = msg.FogProgress;
                m_remoteFogSampleIntensity = msg.FogIntensity;
                m_remotePrecipitationPreviousIntensity = msg.PrecipitationIntensity;
                m_remotePrecipitationSampleIntensity = msg.PrecipitationIntensity;
                m_remoteFogSampleTime = now;
                m_remoteFogPreviousSampleTime = now;
                m_remoteFogSampleInterval = RemoteFogDefaultSampleInterval;
                return;
            }
            m_remoteFogPreviousProgress = m_remoteFogSampleProgress;
            m_remoteFogPreviousIntensity = m_remoteFogSampleIntensity;
            m_remoteFogSampleProgress = msg.FogProgress;
            m_remoteFogSampleIntensity = msg.FogIntensity;
            m_remotePrecipitationPreviousIntensity = m_remotePrecipitationSampleIntensity;
            m_remotePrecipitationSampleIntensity = msg.PrecipitationIntensity;
            m_remoteFogPreviousSampleTime = m_remoteFogSampleTime;
            m_remoteFogSampleTime = now;
            double interval = now - m_remoteFogPreviousSampleTime;
            // 2Hz 正常是 0.5 秒；网络抖动/长时间停顿都夹到这个范围内，避免插值速率失真。
            m_remoteFogSampleInterval = MathUtils.Clamp(interval, 0.05, 1.5);
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateFog
        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
        // 世界信息是 2Hz：旧写法每帧朝"最新样本"做指数追赶（8/s），结果雾的浓淡/层高会跟着样本台阶
        // 走 —— 玩家看到的就是"数字跳跃"，出现与消失都不连续。降雨以前更糟：每次样本到达都直接
        // 覆写 PrecipitationIntensity，客户端自己那条连续 ramp 被 2Hz 的旧值反复拉回去（"反复回退"）。
        // 现在雾和雨共用同一套：在两份相邻样本之间按到达间隔线性插值，再叠一层很轻的指数平滑把
        // 样本边界的折角磨圆；样本断流时插值系数夹在 1 以内，停在最后一份样本上，不会外推跑飞。
        // 主机侧引擎本来就是按 m_precipitationRampTime 连续加减出这个强度，插值只是把中间值补出来，
        // 权威值仍然完全由主机决定。
        private void UpdateRemoteWeatherPresentation(float dt)
        {
            GameWorldInfoMessage1 msg = m_remoteWeatherState;
            Project project = GameManager.Project;
            if (msg == null || project == null || IsHost) return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather == null) return;
            if (weather.IsFogStarted != msg.IsFogStarted)
                ConfigureRemoteFogSchedule(weather, msg.IsFogStarted);
            if (weather.IsPrecipitationStarted != msg.IsPrecipitationStarted)
                ConfigureRemotePrecipitationSchedule(weather, msg.IsPrecipitationStarted);
            float step = MathUtils.Clamp(dt, 0f, 0.05f);
            double interval = MathUtils.Max(m_remoteFogSampleInterval, 0.05);
            float alpha = (float)MathUtils.Clamp(
                (Time.RealTime - m_remoteFogSampleTime) / interval, 0.0, 1.0);
            float targetProgress = MathUtils.Lerp(
                m_remoteFogPreviousProgress, m_remoteFogSampleProgress, alpha);
            float targetIntensity = MathUtils.Lerp(
                m_remoteFogPreviousIntensity, m_remoteFogSampleIntensity, alpha);
            float targetPrecipitation = MathUtils.Lerp(
                m_remotePrecipitationPreviousIntensity,
                m_remotePrecipitationSampleIntensity, alpha);
            float blend = 1f - (float)Math.Exp(-10f * step);
            float fogProgress = MathUtils.Lerp(weather.FogProgress, targetProgress, blend);
            float fogIntensity = MathUtils.Lerp(weather.FogIntensity, targetIntensity, blend);
            float precipitationIntensity = MathUtils.Lerp(
                weather.PrecipitationIntensity, targetPrecipitation, blend);
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogProgress>k__BackingField", fogProgress, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogIntensity>k__BackingField", fogIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<PrecipitationIntensity>k__BackingField",
                precipitationIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogSeed>k__BackingField", msg.FogSeed, typeof(SubsystemWeather));
        }

        // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdateLightning
        private void SuppressClientRandomLightning(Project project)
        {
            if (IsHost || project == null) return;
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(false);
            if (weather != null)
            {
                ModManager.ModParentField.ModifyParentField(
                    weather, "m_lightningIntensity", 0f, typeof(SubsystemWeather));
            }
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // The original method also damages creatures, starts fires and creates a random explosion.
        // A client replica must only render the host event; terrain effects arrive separately.
        private void ApplyRemoteLightningVisual(SubsystemSky sky, Vector3 position)
        {
            if (sky == null) return;
            SubsystemTime subsystemTime = GameManager.Project?.FindSubsystem<SubsystemTime>(false);
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lastLightningStrikeTime", subsystemTime?.GameTime ?? 0.0,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)position,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 1f, typeof(SubsystemSky));
            // Source: ScMultiplayer.SendWorldControlRequestNow
            // World-control clients do not predict the native strike locally. Every client,
            // including the requester, therefore plays the authoritative rising-edge thunder.
            PlayRemoteThunder(position);
        }

        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.MakeLightningStrike
        // Reproduce only the listener-distance audio branch. Damage, fire and explosions remain
        // host-authoritative and arrive through their existing synchronization paths.
        private void PlayRemoteThunder(Vector3 position)
        {
            SubsystemAudio audio = GameManager.Project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null) return;
            float distance = float.MaxValue;
            foreach (Vector3 listenerPosition in audio.ListenerPositions)
            {
                distance = MathUtils.Min(distance, Vector2.Distance(
                    new Vector2(listenerPosition.X, listenerPosition.Z),
                    new Vector2(position.X, position.Z)));
            }
            if (distance >= 200f) return;
            float pitch = m_audioEventRandom.Float(-0.2f, 0.2f);
            float delay = audio.CalculateDelay(distance);
            if (distance < 40f)
                audio.PlayRandomSound("Audio/ThunderNear", 1f, pitch, 0f, delay);
            else
                audio.PlayRandomSound("Audio/ThunderFar", 0.8f, pitch, 0f, delay);
        }

        private void ClearRemoteLightningVisual(SubsystemSky sky)
        {
            if (sky == null) return;
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikePosition", (Vector3?)null,
                typeof(SubsystemSky));
            ModManager.ModParentField.ModifyParentField(
                sky, "m_lightningStrikeBrightness", 0f, typeof(SubsystemSky));
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
    }
}
