using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 主机侧爆炸检测（**新文件**，不复制引擎模拟算法）：
    ///
    /// 引擎的 <c>SubsystemExplosions.Update</c> 是"消费 m_queuedExplosions → SimulateExplosion →
    /// PostprocessExplosions"的循环，链式爆炸是在 SimulateExplosion 里再次调用
    /// <c>TryExplodeBlock → AddExplosion</c> 追加到同一个队列、并在**同一次 Update** 内被消费掉的。
    /// 因此仅仅快照"Update 之前的队列"会漏掉链式爆炸。
    ///
    /// 做法（不改引擎、不碰 base 的私有实现语义）：
    ///   · 复刻 base.Update 的队列消费顺序（同样的 ±4 格批处理、同样的 SimulateExplosion /
    ///     PostprocessExplosions 调用、同样的 m_projectilesCount / m_generatedProjectiles 重置）；
    ///   · 每次 SimulateExplosion 前后比较队列长度：**新追加的条目 = 链式爆炸**，
    ///     按值键（格 + 压力 + 燃烧位）计数标记，等它们被消费时按
    ///     <see cref="ExplosionKinds.Chain"/> 上报；
    ///   · 同一个 Update 内同一格 + 同一压力的重复链式条目只上报一次（chainDeduped）。
    ///
    /// 反射入口全部来自实例注入的 <see cref="IModParentField"/> / <see cref="IModParentMethod"/>
    /// （与既有 ReplicaGates 相同的做法）；任何字段/方法缺失都会 Error 一次并让调用方回退到
    /// base.Update（主机爆炸照常，但明确说明"没有广播"——不是静默兜底）。
    /// </summary>
    internal sealed class ExplosionReplicaHost
    {
        private const int BatchRadius = 4;

        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly ExplosionReplicaEngine m_owner;

        private FieldInfo m_pressureField;
        private FieldInfo m_surroundingField;
        private FieldInfo m_projectilesCountField;
        private FieldInfo m_generatedProjectilesField;
        private MethodInfo m_simulateMethod;
        private MethodInfo m_postprocessMethod;

        private FieldInfo m_xField;
        private FieldInfo m_yField;
        private FieldInfo m_zField;
        private FieldInfo m_pressureValueField;
        private FieldInfo m_incendiaryField;
        private FieldInfo m_noSoundField;

        private bool m_reflectionReady;
        private bool m_reflectionFailed;
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// 本帧之后仍未消费的链式爆炸（跨帧保留）。
        /// **不能用对象引用**：m_queuedExplosions 是 List&lt;ExplosionData&gt;（值类型），
        /// 每次通过 IList 索引都会重新装箱，引用永远不相等。这里用值键 + 计数。
        /// </summary>
        private readonly Dictionary<ChainKey, int> m_chainPending = new Dictionary<ChainKey, int>();

        public ExplosionReplicaHost(IModParentField parentField, IModParentMethod parentMethod,
            ExplosionReplicaEngine owner)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public long ChainDeduped { get; private set; }
        public long DetectedExplosions { get; private set; }
        public long DetectionFailures { get; private set; }
        public long DamageEvents { get; private set; }

        public string Describe() => "detected=" + DetectedExplosions +
            " chainDeduped=" + ChainDeduped + " detectionFailures=" + DetectionFailures +
            " damageEvents=" + DamageEvents;

        /// <summary>
        /// true = 本帧已由这里完整消费（调用方不要再跑 base.Update）；
        /// false = 检测器不可用 / 出错 → 调用方必须走 base.Update（主机本地爆炸正常）。
        /// </summary>
        public bool TryUpdate(SuSubsystemExplosions subsystem, float dt)
        {
            Project project = subsystem.Project;
            if (project == null) return false;
            if (!EnsureReflection(out string reflectionReason))
            {
                m_owner.LogDetectionProblemOnce("reflection:" + reflectionReason,
                    "爆炸检测器无法访问引擎私有成员（" + reflectionReason + "）→ 本帧回退 base.Update；" +
                    "主机爆炸仍然执行，但**不会**广播 ExplosionEvent");
                DetectionFailures++;
                return false;
            }

            IList queue;
            try
            {
                queue = m_parentField.GetParentField<IList>(subsystem, "m_queuedExplosions",
                    typeof(SubsystemExplosions));
            }
            catch (Exception e)
            {
                DetectionFailures++;
                m_owner.LogDetectionProblemOnce("queue:" + e.GetType().Name,
                    "读取 m_queuedExplosions 失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本帧回退 base.Update");
                return false;
            }
            if (queue == null || queue.Count == 0) return true;

            if (!TryRead(queue[0], out int centerX, out int centerY, out int centerZ,
                    out float _, out bool _, out bool _))
            {
                DetectionFailures++;
                m_owner.LogDetectionProblemOnce("entry-layout",
                    "m_queuedExplosions 的条目布局与预期不符（找不到 X/Y/Z/Pressure/IsIncendiary/" +
                    "NoExplosionSound）→ 本帧回退 base.Update");
                return false;
            }

            var emitted = new List<HostExplosionRecord>(queue.Count);
            var chainKeysThisUpdate = new HashSet<string>(StringComparer.Ordinal);
            List<PlayerHealthSnapshot> healthBefore = CapturePlayerHealth(project);
            bool consumedAny = false;

            try
            {
                SetPressureMaps(subsystem, centerX, centerY, centerZ);
                m_parentField.ModifyParentField(subsystem, "m_projectilesCount", 0,
                    typeof(SubsystemExplosions));
                if (m_generatedProjectilesField.GetValue(subsystem) is IDictionary generated)
                    generated.Clear();
                else
                    throw new InvalidOperationException("m_generatedProjectiles 不是 IDictionary");

                bool playSound = false;
                int index = 0;
                while (index < queue.Count)
                {
                    object entry = queue[index];
                    if (!TryRead(entry, out int x, out int y, out int z, out float pressure,
                            out bool incendiary, out bool noSound))
                        throw new InvalidOperationException("队列条目在消费过程中布局变化");

                    if (MathUtils.Abs(x - centerX) > BatchRadius ||
                        MathUtils.Abs(y - centerY) > BatchRadius ||
                        MathUtils.Abs(z - centerZ) > BatchRadius)
                    {
                        index++;
                        continue;
                    }

                    queue.RemoveAt(index);
                    consumedAny = true;
                    bool isChain = ConsumePendingChain(x, y, z, pressure, incendiary, noSound);
                    int blockValue = ReadCellValue(project, x, y, z);
                    int beforeCount = queue.Count;
                    InvokeSimulate(subsystem, x, y, z, pressure, incendiary);
                    playSound |= !noSound;
                    for (int added = beforeCount; added < queue.Count; added++)
                    {
                        if (TryRead(queue[added], out int ax, out int ay, out int az,
                                out float ap, out bool ai, out bool an))
                            AddPendingChain(ax, ay, az, ap, ai, an);
                    }

                    ClassifyBlock(blockValue, isChain, out int kind, out bool cosmeticOnly);
                    string chainKey = x + "," + y + "," + z + "," +
                        BitConverter.SingleToInt32Bits(pressure) + "," + (incendiary ? 1 : 0);
                    if (isChain && !chainKeysThisUpdate.Add(chainKey))
                    {
                        ChainDeduped++;
                        continue;
                    }
                    emitted.Add(new HostExplosionRecord(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                        kind, ExplosionVisualPolicy.RadiusFromPressure(pressure), (uint)blockValue,
                        cosmeticOnly, pressure, incendiary));
                    DetectedExplosions++;
                }

                InvokePostprocess(subsystem, playSound);
            }
            catch (Exception e)
            {
                DetectionFailures++;
                m_owner.LogDetectionProblemOnce("update:" + e.GetType().Name,
                    "爆炸检测循环异常（" + e.GetType().Name + " " + e.Message +
                    "）→ " + (consumedAny
                        ? "队列已部分消费；本帧不再补跑 base.Update（避免二次爆炸），" +
                          "已检测到的 " + emitted.Count + " 条仍会广播"
                        : "尚未消费任何队列条目；本帧回退 base.Update，主机爆炸仍会执行") +
                    "，请查看上一帧日志");
                if (!consumedAny) return false;
                for (int i = 0; i < emitted.Count; i++)
                {
                    HostExplosionRecord record = emitted[i];
                    m_owner.EmitHostExplosion(in record);
                }
                return true; // 已经消费掉部分队列，绝不能再让 base.Update 重跑同一批爆炸。
            }
            finally
            {
                if (!subsystem.ShowExplosionPressure)
                {
                    try
                    {
                        m_parentField.ModifyParentField(subsystem, "m_pressureByPoint", null,
                            typeof(SubsystemExplosions));
                        m_parentField.ModifyParentField(subsystem, "m_surroundingPressureByPoint",
                            null, typeof(SubsystemExplosions));
                    }
                    catch (Exception)
                    {
                        // 清理失败不影响爆炸结果；下一帧会重新构造。
                    }
                }
            }

            for (int i = 0; i < emitted.Count; i++)
            {
                HostExplosionRecord record = emitted[i];
                m_owner.EmitHostExplosion(in record);
            }
            ReportPlayerDamage(project, healthBefore);
            PruneChainKeys(queue);
            return true;
        }

        // ------------------------------------------------------------ 队列条目 / 分类

        private bool EnsureReflection(out string reason)
        {
            reason = null;
            if (m_reflectionReady) return true;
            if (m_reflectionFailed)
            {
                reason = "reflection-init-failed";
                return false;
            }
            Type type = typeof(SubsystemExplosions);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic |
                                       BindingFlags.Public;
            m_pressureField = type.GetField("m_pressureByPoint", flags);
            m_surroundingField = type.GetField("m_surroundingPressureByPoint", flags);
            m_projectilesCountField = type.GetField("m_projectilesCount", flags);
            m_generatedProjectilesField = type.GetField("m_generatedProjectiles", flags);
            if (m_pressureField == null || m_surroundingField == null ||
                m_projectilesCountField == null || m_generatedProjectilesField == null)
            {
                reason = "private-field-missing";
                m_reflectionFailed = true;
                return false;
            }
            try
            {
                m_simulateMethod = m_parentMethod.GetInstanceMethodInfo(type, "SimulateExplosion",
                    new[] { typeof(int), typeof(int), typeof(int), typeof(float), typeof(bool) });
                m_postprocessMethod = m_parentMethod.GetInstanceMethodInfo(type,
                    "PostprocessExplosions", new[] { typeof(bool) });
            }
            catch (Exception e)
            {
                reason = "private-method-" + e.GetType().Name;
                m_reflectionFailed = true;
                return false;
            }
            m_reflectionReady = true;
            return true;
        }

        private bool TryRead(object entry, out int x, out int y, out int z, out float pressure,
            out bool incendiary, out bool noSound)
        {
            x = 0;
            y = 0;
            z = 0;
            pressure = 0f;
            incendiary = false;
            noSound = false;
            if (entry == null) return false;
            if (m_xField == null)
            {
                Type entryType = entry.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic |
                                           BindingFlags.Public;
                m_xField = entryType.GetField("X", flags);
                m_yField = entryType.GetField("Y", flags);
                m_zField = entryType.GetField("Z", flags);
                m_pressureValueField = entryType.GetField("Pressure", flags);
                m_incendiaryField = entryType.GetField("IsIncendiary", flags);
                m_noSoundField = entryType.GetField("NoExplosionSound", flags);
            }
            if (m_xField == null || m_yField == null || m_zField == null ||
                m_pressureValueField == null || m_incendiaryField == null || m_noSoundField == null)
                return false;
            x = (int)m_xField.GetValue(entry);
            y = (int)m_yField.GetValue(entry);
            z = (int)m_zField.GetValue(entry);
            pressure = (float)m_pressureValueField.GetValue(entry);
            incendiary = (bool)m_incendiaryField.GetValue(entry);
            noSound = (bool)m_noSoundField.GetValue(entry);
            return true;
        }

        private void SetPressureMaps(SuSubsystemExplosions subsystem, int x, int y, int z)
        {
            object pressureMap = CreateSparseMap(m_pressureField.FieldType, x, y, z);
            object surroundingMap = CreateSparseMap(m_surroundingField.FieldType, x, y, z);
            m_parentField.ModifyParentField(subsystem, "m_pressureByPoint", pressureMap,
                typeof(SubsystemExplosions));
            m_parentField.ModifyParentField(subsystem, "m_surroundingPressureByPoint", surroundingMap,
                typeof(SubsystemExplosions));
        }

        private static object CreateSparseMap(Type mapType, int x, int y, int z)
        {
            if (mapType == null || !mapType.IsGenericType)
                throw new InvalidOperationException("压力表类型不是泛型：" + mapType);
            Type elementType = mapType.GetGenericArguments()[0];
            object outside = elementType.IsValueType
                ? Activator.CreateInstance(elementType)
                : null;
            ConstructorInfo ctor = mapType.GetConstructor(
                new[] { typeof(int), typeof(int), typeof(int), elementType });
            if (ctor == null)
                throw new MissingMethodException(mapType.FullName, ".ctor(int,int,int,T)");
            return ctor.Invoke(new[] { (object)x, y, z, outside });
        }

        private void InvokeSimulate(SuSubsystemExplosions subsystem, int x, int y, int z,
            float pressure, bool incendiary)
        {
            m_simulateMethod.Invoke(subsystem,
                new object[] { x, y, z, pressure, incendiary });
        }

        private void InvokePostprocess(SuSubsystemExplosions subsystem, bool playSound)
        {
            m_postprocessMethod.Invoke(subsystem, new object[] { playSound });
        }

        private static int ReadCellValue(Project project, int x, int y, int z)
        {
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            return terrain?.Terrain == null ? 0 : terrain.Terrain.GetCellValue(x, y, z);
        }

        private void ClassifyBlock(int blockValue, bool isChain, out int kind, out bool cosmeticOnly)
        {
            cosmeticOnly = false;
            if (isChain)
            {
                kind = ExplosionKinds.Chain;
                return;
            }
            int contents = global::Game.Terrain.ExtractContents(blockValue);
            Block block = contents > 0 && BlocksManager.Blocks != null &&
                          contents < BlocksManager.Blocks.Length
                ? BlocksManager.Blocks[contents]
                : null;
            if (block is FireworksBlock)
            {
                kind = ExplosionKinds.Firework;
                cosmeticOnly = true;
            }
            else if (block is BombBlock || block is IncendiaryBombBlock)
            {
                kind = ExplosionKinds.Throwable;
            }
            else if (block is GunpowderKegBlock)
            {
                kind = ExplosionKinds.Keg;
            }
            else if (block is DetonatorBlock)
            {
                kind = ExplosionKinds.Detonator;
            }
            else
            {
                kind = ExplosionKinds.Block;
            }
        }

        // ------------------------------------------------------------ 玩家伤害（主机已结算，这里只上报差异）

        private sealed class PlayerHealthSnapshot
        {
            public PlayerData Player;
            public ComponentHealth Health;
            public int ClientId;
            public string Identity;
            public string Name;
            public float Before;
        }

        private List<PlayerHealthSnapshot> CapturePlayerHealth(Project project)
        {
            var result = new List<PlayerHealthSnapshot>();
            if (m_owner.Spawner == null) return result;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return result;
            foreach (PlayerData player in players.PlayersData)
            {
                ComponentHealth health = player.ComponentPlayer?.ComponentHealth;
                if (health == null) continue;
                int clientId = ResolveClientId(project, player);
                if (clientId < 0) continue;
                string identity = clientId > 0 && m_owner.Spawner.TryGetRecordKey(clientId, out string key)
                    ? key
                    : string.Empty;
                result.Add(new PlayerHealthSnapshot
                {
                    Player = player,
                    Health = health,
                    ClientId = clientId,
                    Identity = identity,
                    Name = player.Name,
                    Before = health.Health
                });
            }
            return result;
        }

        private int ResolveClientId(Project project, PlayerData player)
        {
            if (m_owner.Spawner.TryGetLocalPlayer(project, out PlayerData local, out _) &&
                ReferenceEquals(local, player))
                return 0;
            foreach (int clientId in m_owner.Spawner.ClientIds)
            {
                if (m_owner.Spawner.TryGet(clientId, out PlayerData remote) &&
                    ReferenceEquals(remote, player))
                    return clientId;
            }
            return -1;
        }

        private void ReportPlayerDamage(Project project, List<PlayerHealthSnapshot> before)
        {
            if (before == null || before.Count == 0) return;
            for (int i = 0; i < before.Count; i++)
            {
                PlayerHealthSnapshot snapshot = before[i];
                if (snapshot.Health == null) continue;
                float now = snapshot.Health.Health;
                float damage = snapshot.Before - now;
                if (damage <= 1e-6f) continue;
                Vector3 position = snapshot.Player.ComponentPlayer?.ComponentBody?.Position ??
                                   Vector3.Zero;
                DamageEvents++;
                m_owner.EmitHostDamage(new HostDamageRecord(snapshot.ClientId, snapshot.Identity,
                    snapshot.Name, position, damage, now));
            }
        }

        // ------------------------------------------------------------ 限流清理

        private void PruneChainKeys(IList queue)
        {
            if (m_chainPending.Count == 0 || queue == null) return;
            var stillQueued = new Dictionary<ChainKey, int>();
            foreach (object entry in queue)
            {
                if (!TryRead(entry, out int x, out int y, out int z, out float pressure,
                        out bool incendiary, out bool noSound)) continue;
                var key = new ChainKey(x, y, z, pressure, incendiary, noSound);
                if (!m_chainPending.ContainsKey(key)) continue;
                stillQueued.TryGetValue(key, out int count);
                stillQueued[key] = count + 1;
            }
            m_chainPending.Clear();
            foreach (KeyValuePair<ChainKey, int> item in stillQueued)
                m_chainPending[item.Key] = item.Value;
        }

        private void AddPendingChain(int x, int y, int z, float pressure, bool incendiary,
            bool noSound)
        {
            var key = new ChainKey(x, y, z, pressure, incendiary, noSound);
            m_chainPending.TryGetValue(key, out int count);
            m_chainPending[key] = count + 1;
        }

        private bool ConsumePendingChain(int x, int y, int z, float pressure, bool incendiary,
            bool noSound)
        {
            var key = new ChainKey(x, y, z, pressure, incendiary, noSound);
            if (!m_chainPending.TryGetValue(key, out int count) || count <= 0) return false;
            if (count == 1) m_chainPending.Remove(key);
            else m_chainPending[key] = count - 1;
            return true;
        }

        private readonly struct ChainKey : IEquatable<ChainKey>
        {
            private readonly int m_x;
            private readonly int m_y;
            private readonly int m_z;
            private readonly int m_pressureBits;
            private readonly bool m_incendiary;
            private readonly bool m_noSound;

            public ChainKey(int x, int y, int z, float pressure, bool incendiary, bool noSound)
            {
                m_x = x;
                m_y = y;
                m_z = z;
                m_pressureBits = BitConverter.SingleToInt32Bits(pressure);
                m_incendiary = incendiary;
                m_noSound = noSound;
            }

            public bool Equals(ChainKey other) => m_x == other.m_x && m_y == other.m_y &&
                m_z == other.m_z && m_pressureBits == other.m_pressureBits &&
                m_incendiary == other.m_incendiary && m_noSound == other.m_noSound;

            public override bool Equals(object obj) => obj is ChainKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = m_x;
                    hash = (hash * 397) ^ m_y;
                    hash = (hash * 397) ^ m_z;
                    hash = (hash * 397) ^ m_pressureBits;
                    hash = (hash * 397) ^ (m_incendiary ? 1 : 0);
                    hash = (hash * 397) ^ (m_noSound ? 2 : 0);
                    return hash;
                }
            }
        }

        public void Reset()
        {
            m_loggedOnce.Clear();
            m_chainPending.Clear();
        }
    }

    internal readonly struct HostExplosionRecord
    {
        public HostExplosionRecord(Vector3 position, int kind, float radius, uint blockValue,
            bool cosmeticOnly, float pressure, bool incendiary)
        {
            Position = position;
            Kind = kind;
            Radius = radius;
            BlockValue = blockValue;
            CosmeticOnly = cosmeticOnly;
            Pressure = pressure;
            Incendiary = incendiary;
        }

        public Vector3 Position { get; }
        public int Kind { get; }
        public float Radius { get; }
        public uint BlockValue { get; }
        public bool CosmeticOnly { get; }
        public float Pressure { get; }
        public bool Incendiary { get; }
    }

    internal readonly struct HostDamageRecord
    {
        public HostDamageRecord(int clientId, string identity, string name, Vector3 position,
            float damage, float healthAfter)
        {
            ClientId = clientId;
            Identity = identity ?? string.Empty;
            Name = name ?? string.Empty;
            Position = position;
            Damage = damage;
            HealthAfter = healthAfter;
        }

        public int ClientId { get; }
        public string Identity { get; }
        public string Name { get; }
        public Vector3 Position { get; }
        public float Damage { get; }
        public float HealthAfter { get; }
    }
}
