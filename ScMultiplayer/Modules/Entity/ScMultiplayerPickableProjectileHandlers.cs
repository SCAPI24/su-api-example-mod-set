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
        private void UpdateRemotePickablePresentations(float dt)
        {
            if (IsHost || GameManager.Project == null) return;
            float step = MathUtils.Clamp(dt, 0f, 0.05f);
            double now = Time.RealTime;
            SubsystemFluidBlockBehavior fluidBehavior = GameManager.Project.FindSubsystem<
                SubsystemFluidBlockBehavior>(false);
            foreach (KeyValuePair<ushort, RemotePickableNetworkState> item in
                m_remotePickableStates.ToArray())
            {
                if (!m_remotePickables.TryGetValue(item.Key, out Pickable pickable) ||
                    pickable == null || pickable.ToRemove)
                    continue;
                RemotePickableNetworkState state = item.Value;
                float age = (float)MathUtils.Clamp(now - state.LastUpdateTime, 0.0, 0.25);
                Vector3 predictedPosition = state.Position + state.Velocity * age;
                bool isInFluid = fluidBehavior?.CalculateFlowSpeed(
                    Terrain.ToCell(pickable.Position.X),
                    Terrain.ToCell(pickable.Position.Y + 0.1f),
                    Terrain.ToCell(pickable.Position.Z)) != null;
                // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                // 静止判定不能只看速度：引擎里落地静止的拾取物位置一动不动（落地那一步丢弃本
                // tick 位移 vector = position），但 velocity 每 tick 都被重力 - 反弹（取反 * 0.25）
                // 折腾出 ±0.1~0.5 m/s 的残余。只看速度阈值会把这类"原地不动"判成在运动，于是按
                // `state.Position + state.Velocity * age` 外推 → 掉落物往地下沉，下一帧再被主机
                // 位置拉回（玩家看到的就是"原地时往地下掉，然后又被拉回"）。
                // 主机侧现在会把这种残余速度清成 0（ScMultiplayerWorldSync.cs 发布处），这里再用
                // "两帧快照位置没动"兜一层，两头都不依赖单一判据。
                bool hostPositionStable = state.HasPreviousPosition &&
                    Vector3.DistanceSquared(state.Position, state.PreviousPosition) <
                        RestingPickablePositionEpsilonSquared;
                bool isResting = !state.FlyToPosition.HasValue &&
                    (state.Velocity.LengthSquared() < 0.04f || hostPositionStable);
                if (isResting && !isInFluid)
                {
                    pickable.Position = state.Position;
                    pickable.Velocity = Vector3.Zero;
                    state.PresentationVelocity = Vector3.Zero;
                    state.PresentationInitialized = true;
                }
                else
                {
                    Vector3 error = predictedPosition - pickable.Position;
                    if (!state.PresentationInitialized)
                    {
                        pickable.Position = state.Position;
                        pickable.Velocity = state.Velocity;
                        state.PresentationVelocity = state.Velocity;
                        state.PresentationInitialized = true;
                    }
                    else
                    {
                        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                        // Preserve native collision and buoyancy, but translate every positional
                        // error toward the host so even sub-block drift cannot remain unresolved.
                        float positionResponse = isInFluid ? 5f : 12f;
                        float positionBlend = 1f - (float)Math.Exp(-positionResponse * step);
                        Vector3 positionCorrection = error * positionBlend;
                        float maxPositionStep = (isInFluid ? 5f : 24f) * step;
                        float positionCorrectionLength = positionCorrection.Length();
                        if (positionCorrectionLength > maxPositionStep)
                            positionCorrection *= maxPositionStep / positionCorrectionLength;
                        pickable.Position += positionCorrection;
                        if (Vector3.DistanceSquared(pickable.Position, predictedPosition) < 0.0004f)
                            pickable.Position = predictedPosition;

                        float correctionHorizon = isInFluid ? 0.45f : 0.18f;
                        Vector3 correctionVelocity = error / correctionHorizon;
                        float maxCorrectionSpeed = isInFluid ? 4f : 12f;
                        float correctionSpeed = correctionVelocity.Length();
                        if (correctionSpeed > maxCorrectionSpeed)
                            correctionVelocity *= maxCorrectionSpeed / correctionSpeed;
                        Vector3 desiredVelocity = state.Velocity + correctionVelocity;
                        float response = isInFluid ? 5f : 10f;
                        float blend = 1f - (float)Math.Exp(-response * step);
                        state.PresentationVelocity = Vector3.Lerp(
                            state.PresentationVelocity, desiredVelocity, blend);
                        pickable.Velocity = state.PresentationVelocity;
                    }
                }
                if (m_pendingPickablePickups.TryGetValue(item.Key,
                    out PendingPickablePickupPresentation pickup))
                {
                    Vector3 target = ResolvePickupPresentationTarget(
                        pickup.CollectorClientId, pickable.Position);
                    if (now < pickup.CompleteTime)
                    {
                        pickable.FlyToPosition = target;
                    }
                    else
                    {
                        m_pendingPickablePickups.Remove(item.Key);
                        if (pickup.RemainingCount <= 0)
                        {
                            pickable.ToRemove = true;
                            m_remotePickables.Remove(item.Key);
                            m_remotePickableRecords.Remove(item.Key);
                            m_remotePickableStates.Remove(item.Key);
                            continue;
                        }
                        pickable.Count = pickup.RemainingCount;
                        pickable.FlyToPosition = state.FlyToPosition;
                    }
                }
                else
                {
                    pickable.FlyToPosition = state.FlyToPosition;
                }
            }
        }

        private Pickable EnsureRemotePickable(ushort id, Vector3 position, Vector3 velocity)
        {
            if (m_remotePickables.TryGetValue(id, out Pickable existing) && existing != null && !existing.ToRemove)
                return existing;
            if (!m_remotePickableRecords.TryGetValue(id, out RemotePickableRecord record)) return null;
            SubsystemPickables subsystem = GameManager.Project?.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null) return null;
            Pickable pickable;
            m_applyingNetworkPickable = true;
            try
            {
                pickable = subsystem.AddPickable(record.Value, record.Count,
                    position, velocity, record.StuckMatrix);
            }
            finally
            {
                m_applyingNetworkPickable = false;
            }
            if (pickable != null) m_remotePickables[id] = pickable;
            return pickable;
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        internal void RequestNearbyPickableAcquisition()
        {
            if (IsHost || client?.IsConnected != true || GameManager.Project == null ||
                Time.RealTime < m_nextPickableAcquireScanTime)
                return;
            m_nextPickableAcquireScanTime = Time.RealTime + 0.05;

            ComponentPlayer player = GameManager.Project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers.FirstOrDefault(item =>
                    item?.ComponentBody != null && item.ComponentMiner?.Inventory != null &&
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
            if (player?.ComponentHealth?.Health <= 0f) return;
            SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null) return;
            Vector3 target = player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f);
            double now = Time.RealTime;

            foreach (ushort stale in m_pendingPickableAcquireRequests.Keys.Where(id =>
                !m_remotePickables.ContainsKey(id) || !m_remotePickableRecords.ContainsKey(id))
                .ToArray())
                m_pendingPickableAcquireRequests.Remove(stale);

            foreach (KeyValuePair<ushort, Pickable> item in m_remotePickables.ToArray())
            {
                Pickable pickable = item.Value;
                if (pickable == null || pickable.ToRemove) continue;
                // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                // Preserve the native creation grace. Requesting a replicated throw sooner lets
                // the host collect it before its authoritative trajectory leaves the player.
                if (gameInfo.TotalElapsedGameTime - pickable.CreationTime <= 0.5) continue;
                float distanceSquared = Vector3.DistanceSquared(target, pickable.Position);
                if (distanceSquared >= 3.0625f || pickable.StuckMatrix.HasValue) continue;
                pickable.FlyToPosition = target + 0.1f * MathUtils.Sqrt(distanceSquared) *
                    player.ComponentBody.Velocity;
                if (distanceSquared >= 1f) continue;

                if (!m_pendingPickableAcquireRequests.TryGetValue(item.Key,
                        out PendingPickableAcquireRequest pending))
                {
                    m_nextPickableAcquireRequestId = m_nextPickableAcquireRequestId == int.MaxValue
                        ? 1
                        : m_nextPickableAcquireRequestId + 1;
                    pending = new PendingPickableAcquireRequest
                    {
                        RequestId = m_nextPickableAcquireRequestId
                    };
                    m_pendingPickableAcquireRequests[item.Key] = pending;
                }
                else if (pending.Rejected)
                {
                    if (now - pending.LastSendTime < 0.75) continue;
                    m_nextPickableAcquireRequestId = m_nextPickableAcquireRequestId == int.MaxValue
                        ? 1
                        : m_nextPickableAcquireRequestId + 1;
                    pending.RequestId = m_nextPickableAcquireRequestId;
                    pending.Rejected = false;
                }
                if (now - pending.LastSendTime < 0.75) continue;
                pending.LastSendTime = now;
                // 客户端决策记录（同 dig.send）：确实向主机发出了拾取请求。只写 Logs/Client。
                ScMultiplayerOperationLog.Write("event=pick.send id=" +
                    item.Key.ToString(CultureInfo.InvariantCulture) +
                    " request=" + pending.RequestId.ToString(CultureInfo.InvariantCulture) +
                    " dist=" + MathUtils.Sqrt(distanceSquared).ToString("0.###", CultureInfo.InvariantCulture));
                NetworkMessageSender.SendPickableMessage(new PickableSyncMessage
                {
                    Action = PickableSyncMessage.PickAction.RequestAcquire,
                    Id = item.Key,
                    RequestId = pending.RequestId,
                    Position = target
                }, 0);
            }
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        private void HandlePickableAcquireRequest(PickableSyncMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || message.RequestId <= 0 || sourceClientId <= 0)
                return;
            long key = ((long)sourceClientId << 32) | message.Id;
            if (m_processedPickableAcquireRequests.TryGetValue(key,
                    out ProcessedPickableAcquireRequest processed) &&
                message.RequestId <= processed.RequestId)
            {
                if (message.RequestId == processed.RequestId && processed.Response != null)
                    NetworkMessageSender.SendPickableMessage(processed.Response, sourceClientId);
                return;
            }

            Pickable pickable = m_hostPickableIds.FirstOrDefault(item =>
                item.Value == message.Id).Key;
            SubsystemGameInfo gameInfo = GameManager.Project?
                .FindSubsystem<SubsystemGameInfo>(false);
            var response = new PickableSyncMessage
            {
                Action = PickableSyncMessage.PickAction.Acquire,
                Id = message.Id,
                RequestId = message.RequestId,
                CollectorClientId = -1,
                ServerTick = client?.Step ?? 0,
                Count = pickable?.Count ?? 0
            };
            bool accepted = false;
            if (pickable != null && !pickable.ToRemove && gameInfo != null &&
                gameInfo.TotalElapsedGameTime - pickable.CreationTime > 0.5 &&
                IsFinite(message.Position) &&
                m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) &&
                playerData?.ComponentPlayer?.ComponentBody != null &&
                playerData.ComponentPlayer.ComponentHealth?.Health > 0f &&
                Vector3.DistanceSquared(message.Position, pickable.Position) <= 1.21f &&
                Vector3.DistanceSquared(playerData.ComponentPlayer.ComponentBody.Position +
                    new Vector3(0f, 0.75f, 0f), message.Position) <= 4f)
            {
                int contents = Terrain.ExtractContents(pickable.Value);
                if (contents == 248)
                {
                    playerData.ComponentPlayer.ComponentLevel?.AddExperience(
                        pickable.Count, playSound: true);
                    pickable.Count = 0;
                    accepted = true;
                }
                else
                {
                    IInventory inventory = playerData.ComponentPlayer.ComponentMiner?.Inventory;
                    if (inventory != null &&
                        ComponentInventoryBase.FindAcquireSlotForItem(inventory,
                            pickable.Value) >= 0)
                    {
                        int previousCount = pickable.Count;
                        pickable.Count = ComponentInventoryBase.AcquireItems(
                            inventory, pickable.Value, pickable.Count);
                        accepted = pickable.Count < previousCount;
                        if (accepted)
                        {
                            // 基准必须在 MarkHostInventoryAuthoritative 之前取：它会清掉
                            // “上次已发给该客户端的背包”缓存，而那份缓存正是本次增量的比较基准。
                            m_lastSentInventoryValues.TryGetValue(sourceClientId, out int[] baseValues);
                            m_lastSentInventoryCounts.TryGetValue(sourceClientId, out int[] baseCounts);
                            int[] currentValues = CaptureInventoryValues(inventory);
                            int[] currentCounts = CaptureInventoryCounts(inventory);
                            MarkHostInventoryAuthoritative(sourceClientId);
                            // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AcquireItems
                            // 拾取通常只改一格（少数情况两格）。只把变化的格子作为稀疏增量发给拾取者：
                            // 创造模式背包 1622 格，整包一份 ≈12.7 KB，以前每次拾取都广播给所有人，
                            // 是“捡东西时带宽 30KB/s→280KB/s”的根因。
                            if (baseValues != null && baseCounts != null &&
                                TryBuildInventoryDelta(baseValues, baseCounts,
                                    currentValues, currentCounts, out int[] changedIndices,
                                    out _, out _, out int[] changedValues, out int[] changedCounts))
                            {
                                response.HasInventoryDelta = true;
                                response.SlotIndices = changedIndices;
                                response.SlotValues = changedValues;
                                response.SlotCounts = changedCounts;
                                // 增量已让客户端在这几格上与主机对齐，把缓存推进到当前值，
                                // 免得 1 Hz 那条路再补发一份整包（5 秒关键帧仍照旧）。
                                m_lastSentInventoryValues[sourceClientId] = currentValues;
                                m_lastSentInventoryCounts[sourceClientId] = currentCounts;
                            }
                        }
                    }
                }
            }

            response.Count = pickable?.Count ?? 0;
            if (accepted)
            {
                response.CollectorClientId = sourceClientId;
                response.PlaySound = response.Count == 0;
                if (response.Count == 0 && pickable != null)
                {
                    m_authoritativePickableAcquireIds.Add(message.Id);
                    pickable.ToRemove = true;
                }
            }
            processed = new ProcessedPickableAcquireRequest
            {
                RequestId = message.RequestId,
                ProcessedTime = Time.RealTime,
                Response = response
            };
            m_processedPickableAcquireRequests[key] = processed;
            foreach (long stale in m_processedPickableAcquireRequests.Where(item =>
                Time.RealTime - item.Value.ProcessedTime > 30.0).Select(item => item.Key).ToArray())
                m_processedPickableAcquireRequests.Remove(stale);
            if (!accepted)
            {
                // 拒绝只回给请求者：客户端据此把这次请求标成被拒，稍后重试。
                NetworkMessageSender.SendPickableMessage(response, sourceClientId);
            }
            else
            {
                // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                // 其他客户端只需要知道“哪个掉落物被谁捡走了”：广播一条不带背包的边界消息。
                NetworkMessageSender.SendPickableMessage(new PickableSyncMessage
                {
                    Action = PickableSyncMessage.PickAction.Acquire,
                    Id = response.Id,
                    RequestId = response.RequestId,
                    CollectorClientId = response.CollectorClientId,
                    ServerTick = response.ServerTick,
                    Count = response.Count,
                    PlaySound = response.PlaySound
                }, -1);
                // 拾取者自己再收一条只属于自己的“被拾取物品进入的格子”增量。
                if (response.HasInventoryDelta)
                    NetworkMessageSender.SendPickableMessage(response, sourceClientId);
            }
        }

        private void HandlePickableSyncMessage(PickableSyncMessage message, int sourceClientId)
        {
            if (message == null || GameManager.Project == null) return;
            if (IsHost)
            {
                if (message.Action == PickableSyncMessage.PickAction.RequestAcquire)
                    HandlePickableAcquireRequest(message, sourceClientId);
                return;
            }
            if (sourceClientId != 0) return;
            switch (message.Action)
            {
                case PickableSyncMessage.PickAction.Create:
                    m_remotePickableRecords[message.Id] = new RemotePickableRecord
                    {
                        Value = message.Value,
                        Count = message.Count,
                        StuckMatrix = message.StuckMatrix
                    };
                    if (!m_remotePickableStates.TryGetValue(message.Id,
                        out RemotePickableNetworkState createdState))
                    {
                        createdState = new RemotePickableNetworkState
                        {
                            Position = message.Position,
                            Velocity = message.Velocity,
                            PresentationVelocity = message.Velocity,
                            FlyToPosition = message.FlyToPosition,
                            LastUpdateTime = Time.RealTime
                        };
                        m_remotePickableStates[message.Id] = createdState;
                    }
                    Pickable created = EnsureRemotePickable(message.Id,
                        createdState.Position, createdState.Velocity);
                    if (created != null)
                    {
                        created.FlyToPosition = createdState.FlyToPosition;
                        created.StuckMatrix = message.StuckMatrix;
                    }
                    break;
                case PickableSyncMessage.PickAction.UpdatePosition:
                    foreach (PickableSyncMessage.PickablePos state in message.Positions)
                    {
                        if (!m_remotePickableStates.TryGetValue(state.Id,
                            out RemotePickableNetworkState networkState))
                        {
                            networkState = new RemotePickableNetworkState
                            {
                                PresentationVelocity = state.Velocity
                            };
                            m_remotePickableStates[state.Id] = networkState;
                        }
                        networkState.PreviousPosition = networkState.Position;
                        networkState.HasPreviousPosition = true;
                        networkState.Position = state.Position;
                        networkState.Velocity = state.Velocity;
                        networkState.FlyToPosition = state.FlyToPosition;
                        networkState.LastUpdateTime = Time.RealTime;
                        Pickable pickable = EnsureRemotePickable(state.Id, state.Position, state.Velocity);
                        if (pickable == null) continue;
                        pickable.FlyToPosition = state.FlyToPosition;
                    }
                    break;
                case PickableSyncMessage.PickAction.Acquire:
                    HandleAuthoritativePickableAcquire(message);
                    break;
                case PickableSyncMessage.PickAction.Delete:
                    if (m_remotePickables.TryGetValue(message.Id, out Pickable removed) && removed != null)
                    {
                        // 自己还没烧掉过（本地那份还没轮到它的 1Hz 检查相位）就补放音效，
                        // 见 PlayAuthoritativePickableBurnEffects。
                        if (!removed.ToRemove)
                            PlayAuthoritativePickableBurnEffects(removed);
                        removed.ToRemove = true;
                    }
                    m_remotePickables.Remove(message.Id);
                    m_remotePickableRecords.Remove(message.Id);
                    m_remotePickableStates.Remove(message.Id);
                    m_pendingPickablePickups.Remove(message.Id);
                    m_pendingPickableAcquireRequests.Remove(message.Id);
                    break;
                case PickableSyncMessage.PickAction.SetFlyTo:
                    if (m_remotePickables.TryGetValue(message.Id, out Pickable target) && target != null)
                        target.FlyToPosition = message.FlyToPosition;
                    if (m_remotePickableStates.TryGetValue(
                        message.Id, out RemotePickableNetworkState targetState))
                    {
                        targetState.FlyToPosition = message.FlyToPosition;
                        targetState.LastUpdateTime = Time.RealTime;
                    }
                    break;
                case PickableSyncMessage.PickAction.WaterSplash:
                    SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
                    SubsystemParticles particles = GameManager.Project.FindSubsystem<SubsystemParticles>(false);
                    particles?.AddParticleSystem(new WaterSplashParticleSystem(
                        terrain, message.Position, large: false));
                    GameManager.Project.FindSubsystem<SubsystemAudio>(false)?.PlayRandomSound(
                        "Audio/Splashes", 1f, m_audioEventRandom.Float(-0.2f, 0.2f),
                        message.Position, 6f, autoDelay: true);
                    break;
            }
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        // 被火/热方块烧掉的拾取物，引擎是在**本地**播 `Audio/Sizzles`（SubsystemPickables.cs:343-348）
        // 才让玩家听到"烧掉"的声音。客户端虽然也在跑同一个分支，但它受
        // `PeriodicGameTimeEvent(1.0, pickable.GetHashCode() % 100 / 100)` 节流、最快也要 1 秒才轮到
        // 一次，而主机一烧掉就把 Delete 发过来（8Hz 快照，约 125ms）——本地那份常常还没走到那个
        // 分支就被删掉了，于是客户端听不到声音。
        // 这里在收到权威 Delete 时按**同一条件**补放一次（只放音效：爆炸/地形改动是主机权威，
        // 由主机执行并经正常地形同步到达，客户端不能自己跑 TryExplodeBlock）。
        private void PlayAuthoritativePickableBurnEffects(Pickable pickable)
        {
            if (IsHost || pickable == null) return;
            Project project = GameManager.Project;
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            SubsystemFireBlockBehavior fireBehavior =
                project?.FindSubsystem<SubsystemFireBlockBehavior>(false);
            if (terrain == null || fireBehavior == null) return;
            int x = Terrain.ToCell(pickable.Position.X);
            int y = Terrain.ToCell(pickable.Position.Y + 0.1f);
            int z = Terrain.ToCell(pickable.Position.Z);
            // 104 = `SubsystemFireBlockBehavior.HandledBlocks`（火焰方块）—— 与引擎那段判断一致。
            bool onFire = terrain.Terrain.GetCellContents(x, y, z) == 104 ||
                fireBehavior.IsCellOnFire(x, y, z);
            if (!onFire) return;
            project.FindSubsystem<SubsystemAudio>(false)?.PlayRandomSound(
                "Audio/Sizzles", 1f, m_audioEventRandom.Float(-0.2f, 0.2f),
                pickable.Position, 3f, autoDelay: true);
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        private void HandleAuthoritativePickableAcquire(PickableSyncMessage message)
        {
            bool confirmsLocalRequest = message.CollectorClientId == client.ClientID &&
                m_pendingPickableAcquireRequests.TryGetValue(message.Id,
                    out PendingPickableAcquireRequest localRequest) &&
                localRequest.RequestId == message.RequestId;
            if (message.CollectorClientId < 0 && message.Count > 0 &&
                m_pendingPickableAcquireRequests.TryGetValue(message.Id,
                    out PendingPickableAcquireRequest rejected) &&
                rejected.RequestId == message.RequestId)
            {
                rejected.Rejected = true;
                rejected.LastSendTime = Time.RealTime;
            }
            else
            {
                m_pendingPickableAcquireRequests.Remove(message.Id);
            }
            bool hasFullInventory = message.SlotValues != null && message.SlotCounts != null &&
                message.SlotValues.Length > 0 && message.SlotCounts.Length > 0;
            // 客户端决策记录（同 dig.result）：这次拾取由谁完成、还剩多少、随包带回几格背包增量。
            // 拾取在创造模式下经常被"无限仓库"吸收（快捷栏看不出变化），这条日志是唯一可靠的核对点。
            // 只写 Logs/Client，不进 Game.log。
            ScMultiplayerOperationLog.Write("event=pick.result id=" +
                message.Id.ToString(CultureInfo.InvariantCulture) +
                " collector=" + message.CollectorClientId.ToString(CultureInfo.InvariantCulture) +
                " self=" + client.ClientID.ToString(CultureInfo.InvariantCulture) +
                " remaining=" + message.Count.ToString(CultureInfo.InvariantCulture) +
                " delta=" + (message.HasInventoryDelta ? message.SlotIndices.Length : 0).ToString(CultureInfo.InvariantCulture) +
                " full=" + (message.SlotValues?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                " sound=" + message.PlaySound.ToString(CultureInfo.InvariantCulture));
            if (message.CollectorClientId == client.ClientID &&
                message.ServerTick >= m_lastAuthoritativeLocalInventoryTick &&
                (message.HasInventoryDelta || hasFullInventory))
            {
                SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
                ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                IInventory inventory = localPlayer?.ComponentMiner?.Inventory;
                if (inventory != null)
                {
                    if (message.HasInventoryDelta)
                    {
                        // 拾取增量：只写“被拾取物品进入的格子”，本地权威镜像同步推进这几格。
                        ApplyInventoryDelta(inventory, message.SlotIndices,
                            message.SlotValues, message.SlotCounts);
                        AdvanceAuthoritativeLocalInventoryMirror(inventory,
                            message.SlotIndices, message.SlotValues, message.SlotCounts);
                    }
                    else
                    {
                        ApplyInventory(inventory, message.SlotValues, message.SlotCounts);
                        int slotsCount = Math.Min(inventory.SlotsCount,
                            Math.Min(message.SlotValues.Length, message.SlotCounts.Length));
                        m_authoritativeLocalSlotValues = message.SlotValues.Take(slotsCount).ToArray();
                        m_authoritativeLocalSlotCounts = message.SlotCounts.Take(slotsCount).ToArray();
                    }
                    m_lastAuthoritativeLocalInventoryTick = message.ServerTick;
                    m_hasAuthoritativeLocalInventory = true;
                    m_lastLocalInventoryValues = CaptureInventoryValues(inventory);
                    m_lastLocalInventoryCounts = CaptureInventoryCounts(inventory);
                }
            }

            if (m_remotePickableRecords.TryGetValue(message.Id,
                out RemotePickableRecord record))
                record.Count = message.Count;
            if (message.Count > 0)
            {
                if (m_remotePickables.TryGetValue(message.Id, out Pickable partial) &&
                    partial != null)
                    partial.Count = message.Count;
                return;
            }

            if (message.CollectorClientId < 0)
            {
                if (m_remotePickables.TryGetValue(message.Id, out Pickable missing) &&
                    missing != null)
                    missing.ToRemove = true;
                m_remotePickables.Remove(message.Id);
                m_remotePickableRecords.Remove(message.Id);
                m_remotePickableStates.Remove(message.Id);
                m_pendingPickablePickups.Remove(message.Id);
                return;
            }

            Vector3 target = ResolvePickupPresentationTarget(
                message.CollectorClientId, Vector3.Zero);
            double duration = 0.12;
            if (m_remotePickables.TryGetValue(message.Id, out Pickable pickable) &&
                pickable != null)
            {
                float distance = Vector3.Distance(pickable.Position, target);
                duration = MathUtils.Clamp(distance / 6f, 0.08f, 0.3f);
                pickable.FlyToPosition = target;
            }
            // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
            // Play from the reliable acquisition edge. Presentation cleanup must not cancel it.
            if (message.PlaySound && (message.RequestId <= 0 ||
                message.CollectorClientId != client.ClientID || confirmsLocalRequest))
            {
                Vector3 soundPosition = pickable?.Position ?? target;
                GameManager.Project.FindSubsystem<SubsystemAudio>(false)?.PlaySound(
                    "Audio/PickableCollected", 0.7f, -0.4f,
                    soundPosition, 2f, autoDelay: false);
            }
            m_pendingPickablePickups[message.Id] = new PendingPickablePickupPresentation
            {
                CollectorClientId = message.CollectorClientId,
                RemainingCount = message.Count,
                CompleteTime = Time.RealTime + duration
            };
        }

        // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerContainerHandlers.cs:ApplyInventoryDelta
        // 稀疏增量之后，本地那份“主机权威背包镜像”也要按同一批格子推进，否则容器事务的漂移比较
        // （ArraysEqual(currentPlayerValues, m_authoritativeLocalSlotValues)）会把这几格误判成漂移。
        private void AdvanceAuthoritativeLocalInventoryMirror(IInventory inventory,
            int[] indices, int[] values, int[] counts)
        {
            int slotsCount = inventory?.SlotsCount ?? 0;
            if (slotsCount <= 0) return;
            if (!m_hasAuthoritativeLocalInventory ||
                m_authoritativeLocalSlotValues.Length != slotsCount ||
                m_authoritativeLocalSlotCounts.Length != slotsCount)
            {
                // 镜像缺失或长度不符：本地背包刚按增量对齐过，直接以它作为主机权威基准。
                m_authoritativeLocalSlotValues = CaptureInventoryValues(inventory);
                m_authoritativeLocalSlotCounts = CaptureInventoryCounts(inventory);
                m_hasAuthoritativeLocalInventory = true;
                return;
            }
            int length = Math.Min(indices?.Length ?? 0,
                Math.Min(values?.Length ?? 0, counts?.Length ?? 0));
            for (int i = 0; i < length; i++)
            {
                int index = indices[i];
                if (index < 0 || index >= slotsCount) continue;
                m_authoritativeLocalSlotValues[index] = NormalizeCrossbowValue(values[i]);
                m_authoritativeLocalSlotCounts[index] = counts[i];
            }
        }

        private Vector3 ResolvePickupPresentationTarget(int collectorClientId, Vector3 fallback)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer player;
            if (collectorClientId == client.ClientID)
            {
                player = players?.ComponentPlayers.FirstOrDefault(item =>
                    !m_networkPlayerData.Values.Contains(item.PlayerData));
            }
            else
            {
                player = m_networkPlayerData.TryGetValue(collectorClientId,
                    out PlayerData playerData) ? playerData?.ComponentPlayer : null;
            }
            return player?.ComponentBody != null
                ? player.ComponentBody.Position + new Vector3(0f, 0.75f, 0f)
                : fallback;
        }

        private void SynchronizeProjectiles()
        {
            SubsystemProjectiles subsystem = GameManager.Project?.FindSubsystem<SubsystemProjectiles>(false);
            if (subsystem == null) return;
            if (IsHost)
            {
                var active = new HashSet<Projectile>(subsystem.Projectiles.Where(p => p != null && !p.ToRemove));
                foreach (Projectile projectile in active)
                {
                    bool isNewProjectile = !m_hostProjectileIds.TryGetValue(projectile, out ushort id);
                    if (isNewProjectile) id = GetOrCreateHostProjectileId(projectile);
                    ushort ownerClientId = GetProjectileOwnerClientId(projectile);
                    int projectileTimelineStep = client.Step;
                    if (m_hostProjectileReleaseCompensationSteps.TryGetValue(projectile,
                        out int releaseCompensationSteps))
                        projectileTimelineStep = unchecked(projectileTimelineStep -
                            releaseCompensationSteps);
                    var message = new ProjectileSyncMessage(id,
                        isNewProjectile
                            ? ProjectileSyncMessage.ProjectileType.Add
                            : ProjectileSyncMessage.ProjectileType.Update,
                        projectile.Value, projectile.Position, projectile.Velocity,
                        projectile.AngularVelocity, projectile.TrailOffset, ownerClientId,
                        projectileTimelineStep, projectile.IsIncendiary);
                    // Source: ScMultiplayer.cs:HandleProjectileSyncMessage
                    NetworkMessageSender.SendScheduledMessage(-1, message,
                        sequenced: message.Action != ProjectileSyncMessage.ProjectileType.Update,
                        latest: message.Action == ProjectileSyncMessage.ProjectileType.Update);
                }
                foreach (KeyValuePair<Projectile, ushort> item in m_hostProjectileIds.ToArray())
                {
                    if (active.Contains(item.Key)) continue;
                    NetworkMessageSender.SendScheduledMessage(-1, new ProjectileSyncMessage(
                        item.Value, ProjectileSyncMessage.ProjectileType.Remove, 0,
                        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0,
                        client.Step, false), sequenced: true);
                    m_hostProjectileIds.Remove(item.Key);
                    m_hostProjectileReleaseCompensationSteps.Remove(item.Key);
                }
                return;
            }

            var remoteSet = new HashSet<Projectile>(m_remoteProjectiles.Values.Where(p => p != null));
            foreach (Projectile projectile in subsystem.Projectiles.ToArray())
            {
                if (projectile == null || remoteSet.Contains(projectile)) continue;
                ComponentPlayer ownerPlayer = projectile.Owner?.Entity?.FindComponent<ComponentPlayer>();
                bool isLocallyOwned = ownerPlayer != null &&
                    !m_networkPlayerData.Values.Contains(ownerPlayer.PlayerData);
                if (isLocallyOwned)
                {
                    if (!m_clientPredictedProjectiles.ContainsKey(projectile))
                        m_clientPredictedProjectiles[projectile] = Time.RealTime;
                    if (Time.RealTime - m_clientPredictedProjectiles[projectile] <=
                        ClientProjectilePredictionGrace)
                        continue;
                }
                projectile.ToRemove = true;
                m_clientPredictedProjectiles.Remove(projectile);
            }
            foreach (Projectile predicted in m_clientPredictedProjectiles.Keys.Where(
                projectile => projectile == null || projectile.ToRemove ||
                    !subsystem.Projectiles.Contains(projectile)).ToArray())
                m_clientPredictedProjectiles.Remove(predicted);
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.FireProjectile
        private ushort GetProjectileOwnerClientId(Projectile projectile)
        {
            ComponentPlayer owner = projectile?.Owner?.Entity?.FindComponent<ComponentPlayer>();
            if (owner == null) return ushort.MaxValue;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (!ReferenceEquals(item.Value, owner.PlayerData)) continue;
                return item.Key >= ushort.MaxValue ? ushort.MaxValue : (ushort)item.Key;
            }
            return 0;
        }

        private ushort GetOrCreateHostProjectileId(Projectile projectile)
        {
            if (projectile == null) return 0;
            if (m_hostProjectileIds.TryGetValue(projectile, out ushort id)) return id;
            do
            {
                id = m_nextProjectileId++;
            }
            while (id == 0 || m_hostProjectileIds.ContainsValue(id));
            m_hostProjectileIds[projectile] = id;
            return id;
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.FireProjectile
        // A fast projectile can hit and be removed before the next 32Hz snapshot. Publish its
        // creation immediately and reliably so clients can still render the shot.
        private void BroadcastNewHostProjectile(Projectile projectile)
        {
            if (!IsHost || client?.IsConnected != true || projectile == null ||
                projectile.ToRemove)
                return;
            ushort id = GetOrCreateHostProjectileId(projectile);
            if (id == 0) return;
            int projectileTimelineStep = client.Step;
            if (m_hostProjectileReleaseCompensationSteps.TryGetValue(projectile,
                out int releaseCompensationSteps))
            {
                projectileTimelineStep = unchecked(projectileTimelineStep -
                    releaseCompensationSteps);
            }
            NetworkMessageSender.SendScheduledMessage(-1, new ProjectileSyncMessage(
                id, ProjectileSyncMessage.ProjectileType.Add, projectile.Value,
                projectile.Position, projectile.Velocity, projectile.AngularVelocity,
                projectile.TrailOffset, GetProjectileOwnerClientId(projectile),
                projectileTimelineStep, projectile.IsIncendiary),
                sequenced: true, batchable: false);
        }

        internal int GetProjectileOwnerClientIdForHit(Projectile projectile)
        {
            ushort ownerClientId = GetProjectileOwnerClientId(projectile);
            return ownerClientId == ushort.MaxValue ? -1 : ownerClientId;
        }

        internal bool IsLocalPredictedProjectile(Projectile projectile)
        {
            return !IsHost && IsLocallyOwnedProjectile(projectile);
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        internal void PublishAuthoritativeProjectileHit(Projectile projectile,
            int ownerClientId, Vector3 hitPoint, Vector3 hitDirection, float damage)
        {
            if (!IsHost || client?.IsConnected != true || projectile == null ||
                ownerClientId <= 0 || damage <= 0f)
                return;
            ushort id = GetOrCreateHostProjectileId(projectile);
            if (id == 0) return;
            var message = new ProjectileSyncMessage(id,
                ProjectileSyncMessage.ProjectileType.Hit, projectile.Value,
                hitPoint, hitDirection,
                projectile.Owner?.ComponentBody?.Velocity ?? Vector3.Zero,
                Vector3.Zero, (ushort)ownerClientId, client.Step,
                projectile.IsIncendiary)
            {
                HitDamage = damage
            };
            NetworkMessageSender.SendProjectileHit(ownerClientId, message);
        }

        private void HandleProjectileSyncMessage(ProjectileSyncMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            if (message.Action == ProjectileSyncMessage.ProjectileType.Hit)
            {
                HandleProjectileHitResult(message);
                return;
            }
            SubsystemProjectiles subsystem = GameManager.Project?.FindSubsystem<SubsystemProjectiles>(false);
            if (subsystem == null) return;
            if (message.Action == ProjectileSyncMessage.ProjectileType.Remove)
            {
                if (m_remoteProjectiles.TryGetValue(message.ProjectileId, out Projectile removed))
                    removed.ToRemove = true;
                m_remoteProjectiles.Remove(message.ProjectileId);
                return;
            }
            float age = MathUtils.Clamp(
                (client.Step - message.ServerStep) * ServerTickDuration, 0f, 0.35f);
            Vector3 targetPosition = message.Position + message.Velocity * age;
            targetPosition.Y -= 5f * age * age;
            Vector3 targetVelocity = message.Velocity + new Vector3(0f, -10f * age, 0f);
            bool adoptedPrediction = false;
            bool createdProjectile = false;
            if (!m_remoteProjectiles.TryGetValue(message.ProjectileId, out Projectile projectile) ||
                projectile == null || projectile.ToRemove)
            {
                // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.FireProjectile
                // Add can beat the 50ms prediction scan. Inspect live projectiles directly and
                // only adopt a prediction owned by the ClientID carried in the host message.
                projectile = FindClientPredictedProjectile(subsystem, message, targetPosition,
                    targetVelocity);
                if (projectile != null)
                {
                    adoptedPrediction = true;
                    m_clientPredictedProjectiles.Remove(projectile);
                    projectile.Owner = null;
                }
                else
                {
                    projectile = subsystem.AddProjectile(message.Value, targetPosition,
                        targetVelocity, message.AngularVelocity, null);
                    createdProjectile = projectile != null;
                }
                if (projectile == null) return;
                m_remoteProjectiles[message.ProjectileId] = projectile;
            }
            projectile.Value = message.Value;
            if (createdProjectile)
            {
                projectile.Position = targetPosition;
                projectile.Velocity = targetVelocity;
                projectile.AngularVelocity = message.AngularVelocity;
            }
            else
            {
                float distanceSquared = Vector3.DistanceSquared(projectile.Position, targetPosition);
                projectile.Position = distanceSquared > 25f
                    ? targetPosition
                    : Vector3.Lerp(projectile.Position, targetPosition,
                        adoptedPrediction ? 0.2f : 0.35f);
                projectile.Velocity = Vector3.Lerp(projectile.Velocity, targetVelocity,
                    adoptedPrediction ? 0.25f : 0.5f);
                projectile.AngularVelocity = Vector3.Lerp(projectile.AngularVelocity,
                    message.AngularVelocity, 0.5f);
            }
            projectile.TrailOffset = message.TrailOffset;
            projectile.IsIncendiary = message.IsFireProjectile;
            RemoveDuplicateClientPredictedProjectiles(subsystem, projectile, message,
                targetVelocity);
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.FireProjectile
        // The owning client predicts one projectile while the host sends the authoritative one.
        // If adoption misses because the prediction crossed a collision boundary, remove only
        // same-value, same-direction predictions born at the same point; separate rapid shots
        // remain untouched.
        private void RemoveDuplicateClientPredictedProjectiles(
            SubsystemProjectiles subsystem, Projectile authoritative,
            ProjectileSyncMessage message, Vector3 targetVelocity)
        {
            if (authoritative == null || message.OwnerEntityId != client.ClientID)
                return;
            float maxDistanceSquared = ClientProjectileDuplicateDistance *
                ClientProjectileDuplicateDistance;
            foreach (Projectile candidate in subsystem.Projectiles.ToArray())
            {
                if (candidate == null || candidate == authoritative || candidate.ToRemove ||
                    candidate.Value != message.Value ||
                    !IsLocallyOwnedProjectile(candidate) ||
                    Vector3.DistanceSquared(candidate.Position, authoritative.Position) >
                        maxDistanceSquared ||
                    !AreProjectileDirectionsCompatible(candidate.Velocity, targetVelocity))
                    continue;
                candidate.ToRemove = true;
                m_clientPredictedProjectiles.Remove(candidate);
            }
        }

        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        private void HandleProjectileHitResult(ProjectileSyncMessage message)
        {
            if (message.OwnerEntityId != client.ClientID || message.HitDamage <= 0f ||
                !IsFinite(message.Position) || !IsFinite(message.Velocity))
                return;
            long hitKey = ((long)message.ProjectileId << 32) | (uint)message.ServerStep;
            if (!m_displayedProjectileHits.Add(hitKey)) return;
            if (m_displayedProjectileHits.Count > 512) m_displayedProjectileHits.Clear();

            Vector3 direction = message.Velocity.LengthSquared() > 0.0001f
                ? Vector3.Normalize(message.Velocity)
                : Vector3.UnitY;
            string text = (0f - message.HitDamage).ToString("0", CultureInfo.InvariantCulture);
            var particleSystem = new HitValueParticleSystem(
                message.Position + 0.75f * direction,
                direction + message.AngularVelocity, Color.White, text);
            GameManager.Project?.FindSubsystem<SubsystemParticles>(false)?
                .AddParticleSystem(particleSystem);
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.Projectiles
        private Projectile FindClientPredictedProjectile(SubsystemProjectiles subsystem,
            ProjectileSyncMessage message, Vector3 targetPosition, Vector3 targetVelocity)
        {
            if (message.OwnerEntityId != client.ClientID) return null;
            var remoteSet = new HashSet<Projectile>(m_remoteProjectiles.Values.Where(
                item => item != null));
            return subsystem.Projectiles
                .Where(candidate => candidate != null && !candidate.ToRemove &&
                    !remoteSet.Contains(candidate) && candidate.Value == message.Value &&
                    IsLocallyOwnedProjectile(candidate) &&
                    Vector3.DistanceSquared(candidate.Position, targetPosition) <= 144f &&
                    AreProjectileDirectionsCompatible(candidate.Velocity, targetVelocity))
                .OrderBy(candidate =>
                    Vector3.DistanceSquared(candidate.Position, targetPosition) +
                    0.02f * Vector3.DistanceSquared(candidate.Velocity, targetVelocity))
                .FirstOrDefault();
        }

        private bool IsLocallyOwnedProjectile(Projectile projectile)
        {
            ComponentPlayer owner = projectile?.Owner?.Entity?.FindComponent<ComponentPlayer>();
            return owner != null && !m_networkPlayerData.Values.Contains(owner.PlayerData);
        }

        private static bool AreProjectileDirectionsCompatible(Vector3 first, Vector3 second)
        {
            float firstLengthSquared = first.LengthSquared();
            float secondLengthSquared = second.LengthSquared();
            return firstLengthSquared < 1f || secondLengthSquared < 1f ||
                Vector3.Dot(first / MathUtils.Sqrt(firstLengthSquared),
                    second / MathUtils.Sqrt(secondLengthSquared)) >= 0.5f;
        }

        public void BroadcastExplosion(int x, int y, int z, float pressure,
            bool incendiary, bool noSound)
        {
            if (!IsHost || client?.IsConnected != true) return;
            var message = new ExplosionSyncMessage(
                new Vector3(x, y, z), pressure, 0, incendiary, noSound);
            // Source: ScMultiplayer.cs:HandleExplosionSyncMessage
            NetworkMessageSender.SendScheduledMessage(-1, message);
        }

        private void HandleExplosionSyncMessage(ExplosionSyncMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            Point3 point = new Point3(message.Position);
            SubsystemExplosions subsystem = GameManager.Project?
                .FindSubsystem<SubsystemExplosions>(false);
            if (subsystem is SuSubsystemExplosions synchronized)
                synchronized.ApplyNetworkExplosion(message.Position, message.Radius,
                    message.IsIncendiary, message.NoExplosionSound);
            else
                subsystem?.AddExplosion(point.X, point.Y, point.Z, message.Radius,
                    message.IsIncendiary, message.NoExplosionSound);
        }

    }
}
