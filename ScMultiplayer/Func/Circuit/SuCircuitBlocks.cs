using Engine;
using Game;
using SuAPI;
using System;

namespace ScMultiplayer
{
    public sealed class ButtonBlock : Game.ButtonBlock
    {
        // Source: Survivalcraft/Game/ButtonBlock.cs:ButtonBlock.CreateElectricElement
        public override ElectricElement CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            int face = GetFace(value);
            return new SuButtonElectricElement(subsystemElectricity,
                new CellFace(x, y, z, face), value);
        }
    }

    public sealed class SuButtonElectricElement : ButtonElectricElement
    {
        public SuButtonElectricElement(SubsystemElectricity subsystemElectricity,
            CellFace cellFace, int value)
            : base(subsystemElectricity, cellFace, value)
        {
        }

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:ButtonElectricElement.OnInteract
        public override bool OnInteract(TerrainRaycastResult raycastResult,
            ComponentMiner componentMiner)
        {
            if (ScMultiplayer.currentInstance?.CircuitSynchronizer?
                .TryScheduleExternalInput(this, 0f) == true)
                return true;
            return base.OnInteract(raycastResult, componentMiner);
        }

        // Source: Survivalcraft/Game/ButtonElectricElement.cs:
        // ButtonElectricElement.OnHitByProjectile
        public override void OnHitByProjectile(CellFace cellFace, WorldItem worldItem)
        {
            if (ScMultiplayer.currentInstance?.CircuitSynchronizer?
                .TryScheduleExternalInput(this, 0f) == true)
                return;
            base.OnHitByProjectile(cellFace, worldItem);
        }
    }

    public sealed class PressurePlateBlock : Game.PressurePlateBlock
    {
        // Source: Survivalcraft/Game/PressurePlateBlock.cs:
        // PressurePlateBlock.CreateElectricElement
        public override ElectricElement CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            int face = GetFace(value);
            return new SuPressurePlateElectricElement(subsystemElectricity,
                new CellFace(x, y, z, face));
        }
    }

    public sealed class SuPressurePlateElectricElement : PressurePlateElectricElement
    {
        private const int PressureHoldSteps = 12;

        private readonly ModFieldRef<SuPressurePlateElectricElement, float>
            m_pressureField;
        private readonly ModFieldRef<SuPressurePlateElectricElement, float>
            m_voltageField;
        private int m_lastNetworkPressCircuitStep = int.MinValue;

        public SuPressurePlateElectricElement(SubsystemElectricity subsystemElectricity,
            CellFace cellFace)
            : base(subsystemElectricity, cellFace)
        {
            m_pressureField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuPressurePlateElectricElement, float>("m_pressure");
            m_voltageField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuPressurePlateElectricElement, float>("m_voltage");
        }

        // Source: Survivalcraft/Game/PressurePlateElectricElement.cs:
        // PressurePlateElectricElement.OnCollide
        public override void OnCollide(CellFace cellFace, float velocity,
            ComponentBody componentBody)
        {
            if (ScMultiplayer.currentInstance?.CircuitSynchronizer?
                .TryScheduleExternalInput(this, componentBody.Mass) == true)
            {
                componentBody.ApplyImpulse(new Vector3(0f, -2E-05f, 0f));
                return;
            }
            base.OnCollide(cellFace, velocity, componentBody);
        }

        // Source: Survivalcraft/Game/PressurePlateElectricElement.cs:
        // PressurePlateElectricElement.OnHitByProjectile
        public override void OnHitByProjectile(CellFace cellFace, WorldItem worldItem)
        {
            int contents = Terrain.ExtractContents(worldItem.Value);
            float pressure = BlocksManager.Blocks[contents].Density;
            if (ScMultiplayer.currentInstance?.CircuitSynchronizer?
                .TryScheduleExternalInput(this, pressure) == true)
                return;
            base.OnHitByProjectile(cellFace, worldItem);
        }

        internal void ApplyNetworkPressure(float pressure)
        {
            base.Press(pressure);
            m_lastNetworkPressCircuitStep = SubsystemElectricity.CircuitStep;
        }

        // Source: Survivalcraft/Game/PressurePlateElectricElement.cs:
        // PressurePlateElectricElement.Simulate
        public override bool Simulate()
        {
            if (ScMultiplayer.client?.IsConnected != true)
                return base.Simulate();

            ref float pressure = ref m_pressureField(this);
            ref float voltage = ref m_voltageField(this);
            float previousVoltage = voltage;
            if (pressure > 0f && SubsystemElectricity.CircuitStep -
                m_lastNetworkPressCircuitStep < PressureHoldSteps)
            {
                voltage = PressureToVoltage(pressure);
                SubsystemElectricity.QueueElectricElementForSimulation(this,
                    SubsystemElectricity.CircuitStep + 10);
            }
            else
            {
                if (ElectricElement.IsSignalHigh(voltage))
                {
                    CellFace cellFace = CellFaces[0];
                    SubsystemElectricity.SubsystemAudio.PlaySound("Audio/BlockPlaced",
                        0.6f, -0.1f, new Vector3(cellFace.X, cellFace.Y, cellFace.Z),
                        2.5f, autoDelay: true);
                }
                voltage = 0f;
                pressure = 0f;
            }
            return voltage != previousVoltage;
        }

        // Source: Survivalcraft/Game/PressurePlateElectricElement.cs:
        // PressurePlateElectricElement.PressureToVoltage
        private static float PressureToVoltage(float pressure)
        {
            if (pressure <= 0f) return 0f;
            if (pressure < 1f) return 8f / 15f;
            if (pressure < 2f) return 0.6f;
            if (pressure < 5f) return 2f / 3f;
            if (pressure < 25f) return 11f / 15f;
            if (pressure < 100f) return 0.8f;
            if (pressure < 250f) return 13f / 15f;
            if (pressure < 500f) return 14f / 15f;
            return 1f;
        }
    }

    public sealed class RandomGeneratorBlock : Game.RandomGeneratorBlock
    {
        // Source: Survivalcraft/Game/RandomGeneratorBlock.cs:
        // RandomGeneratorBlock.CreateElectricElement
        public override ElectricElement CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            int face = GetFace(value);
            return new SuRandomGeneratorElectricElement(subsystemElectricity,
                new CellFace(x, y, z, face));
        }
    }

    public sealed class SuRandomGeneratorElectricElement : RandomGeneratorElectricElement
    {
        private readonly ModFieldRef<SuRandomGeneratorElectricElement, bool>
            m_clockAllowedField;
        private readonly ModFieldRef<SuRandomGeneratorElectricElement, float>
            m_voltageField;

        public SuRandomGeneratorElectricElement(SubsystemElectricity subsystemElectricity,
            CellFace cellFace)
            : base(subsystemElectricity, cellFace)
        {
            m_clockAllowedField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuRandomGeneratorElectricElement, bool>("m_clockAllowed");
            m_voltageField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuRandomGeneratorElectricElement, float>("m_voltage");
        }

        // Source: Survivalcraft/Game/RandomGeneratorElectricElement.cs:
        // RandomGeneratorElectricElement.Simulate
        public override bool Simulate()
        {
            CircuitSynchronizer synchronizer =
                ScMultiplayer.currentInstance?.CircuitSynchronizer;
            CellFace cellFace = CellFaces[0];
            if (synchronizer?.TryGetDeterministicRandom(cellFace.Point,
                SubsystemElectricity.CircuitStep, 0, out uint voltageRandom) != true)
                return base.Simulate();

            ref bool clockAllowed = ref m_clockAllowedField(this);
            ref float voltage = ref m_voltageField(this);
            float previousVoltage = voltage;
            bool clockEdge = false;
            bool hasClockInput = false;
            foreach (ElectricConnection connection in Connections)
            {
                if (connection.ConnectorType == ElectricConnectorType.Output ||
                    connection.NeighborConnectorType == ElectricConnectorType.Input)
                    continue;
                if (ElectricElement.IsSignalHigh(connection.NeighborElectricElement
                    .GetOutputVoltage(connection.NeighborConnectorFace)))
                {
                    if (clockAllowed)
                    {
                        clockEdge = true;
                        clockAllowed = false;
                    }
                }
                else
                {
                    clockAllowed = true;
                }
                hasClockInput = true;
            }

            if (hasClockInput)
            {
                if (clockEdge) voltage = (voltageRandom & 15u) / 15f;
            }
            else
            {
                voltage = (voltageRandom & 15u) / 15f;
                synchronizer.TryGetDeterministicRandom(cellFace.Point,
                    SubsystemElectricity.CircuitStep, 1, out uint delayRandom);
                int delaySteps = 25 + (int)(delayRandom % 51u);
                SubsystemElectricity.QueueElectricElementForSimulation(this,
                    SubsystemElectricity.CircuitStep + delaySteps);
            }

            if (voltage == previousVoltage) return false;
            SubsystemElectricity.WritePersistentVoltage(cellFace.Point, voltage);
            return true;
        }
    }

    public sealed class DetonatorBlock : Game.DetonatorBlock
    {
        // Source: Survivalcraft/Game/DetonatorBlock.cs:DetonatorBlock.CreateElectricElement
        public override ElectricElement CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            return new SuDetonatorElectricElement(subsystemElectricity,
                new CellFace(x, y, z, GetFace(value)));
        }
    }

    public sealed class SuDetonatorElectricElement : DetonatorElectricElement
    {
        public SuDetonatorElectricElement(SubsystemElectricity subsystemElectricity,
            CellFace cellFace)
            : base(subsystemElectricity, cellFace)
        {
        }

        // Source: Survivalcraft/Game/DetonatorElectricElement.cs:
        // DetonatorElectricElement.Simulate
        public override bool Simulate()
        {
            if (CalculateHighInputsCount() > 0 && IsWorldEffectAuthority)
                base.Detonate();
            return false;
        }

        // Source: Survivalcraft/Game/DetonatorElectricElement.cs:
        // DetonatorElectricElement.OnHitByProjectile
        public override void OnHitByProjectile(CellFace cellFace, WorldItem worldItem)
        {
            if (IsWorldEffectAuthority) base.OnHitByProjectile(cellFace, worldItem);
        }

        private static bool IsWorldEffectAuthority =>
            ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost;
    }

    public sealed class DispenserBlock : Game.DispenserBlock, IElectricElementBlock
    {
        // Source: Survivalcraft/Game/DispenserBlock.cs:
        // DispenserBlock.CreateElectricElement
        ElectricElement IElectricElementBlock.CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            return new SuDispenserElectricElement(subsystemElectricity,
                new Point3(x, y, z));
        }
    }

    public sealed class SuDispenserElectricElement : DispenserElectricElement
    {
        public SuDispenserElectricElement(SubsystemElectricity subsystemElectricity,
            Point3 point)
            : base(subsystemElectricity, point)
        {
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:AddElectricElement
        // Circuit recovery can rebuild the element after the original topology pass. Queue one
        // authoritative evaluation so a dispenser connected to an already-high wire does not
        // miss its first trigger.
        public override void OnAdded()
        {
            base.OnAdded();
            SubsystemElectricity.QueueElectricElementForSimulation(this,
                SubsystemElectricity.CircuitStep + 1);
        }

        // Source: Survivalcraft/Game/DispenserElectricElement.cs:
        // DispenserElectricElement.Simulate
        public override bool Simulate()
        {
            // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerLifecycle.cs:
            // connectionSM.OnPlayingEnter
            // Only the authoritative world executes the native dispenser side effect. Keeping
            // the original implementation on that endpoint preserves its private edge latch,
            // cooldown, block-entity lookup, item removal and fallback behavior exactly.
            bool worldEffectAuthority = ScMultiplayer.IsHost ||
                ScMultiplayer.client?.ClientID == 0;
            if (ScMultiplayer.client?.IsConnected == true && !worldEffectAuthority)
                return false;
            // 《玩家领地》P7d：发射器不得把物品"**放入**"他人领地（设计稿 §34.2 的"放入"）。
            // 这里是最干净的行为层短路点：`Simulate` 是虚方法、且引擎发射路径
            // （`DispenserElectricElement.Simulate` → `ComponentDispenser.Dispense`）**没有任何虚钩子**
            // （`Dispense` / `DispenseItem` / `AddPickable` / `FireProjectile` 全非虚）⇒
            // 只有在元件这一层挡住才是"拦在源头"。
            if (!CanDispenseIntoRegion())
                return false;
            return base.Simulate();
        }

        /// <summary>
        /// 领地口径与 P7c（流体）一致：**只挡"从领地外放入领地内"**。
        /// 发射器自身若已在某块领地内 ⇒ 属于"领地内自己的机械"，放行；
        /// 否则拥有者自己领地里的发射器会被判越权（`CanRegionModifyCell(0, …)` 按主机身份判定，
        /// 必然不是拥有者）而彻底失效。
        /// 落点算法照抄引擎：`ComponentDispenser.DispenseItem` 用
        /// `dispenserCenter + 0.6f * CellFace.FaceToVector3(face)`，0.6 的偏移仍落在相邻格里。
        /// </summary>
        private bool CanDispenseIntoRegion()
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
                return true;
            SubsystemTerrain terrain = SubsystemElectricity?.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return true;
            Point3 position = CellFaces[0].Point;
            if (mod.OwnerClaimAt(position) != null)
                return true;
            int data = Terrain.ExtractData(
                terrain.Terrain.GetCellValue(position.X, position.Y, position.Z));
            Vector3 vector = CellFace.FaceToVector3(Game.DispenserBlock.GetDirection(data));
            var target = new Point3(position.X + (int)vector.X,
                position.Y + (int)vector.Y, position.Z + (int)vector.Z);
            if (target.Y < 0 || target.Y > 255)
                return true;
            if (mod.CanRegionModifyCell(0, target, out RegionClaim claim, out string reason))
                return true;
            mod.NotifyRegionModificationDenied(0, target, claim, "dispense", reason);
            return false;
        }
    }

    public sealed class PistonBlock : Game.PistonBlock, IElectricElementBlock
    {
        // Source: Survivalcraft/Game/PistonBlock.cs:PistonBlock.CreateElectricElement
        ElectricElement IElectricElementBlock.CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            return new SuPistonElectricElement(subsystemElectricity,
                new Point3(x, y, z));
        }
    }

    public sealed class SuPistonElectricElement : PistonElectricElement
    {
        private int m_networkLastLength = -1;

        public SuPistonElectricElement(SubsystemElectricity subsystemElectricity,
            Point3 point)
            : base(subsystemElectricity, point)
        {
        }

        // Source: Survivalcraft/Game/PistonElectricElement.cs:
        // PistonElectricElement.Simulate
        public override bool Simulate()
        {
            float voltage = 0f;
            foreach (ElectricConnection connection in Connections)
            {
                if (connection.ConnectorType != ElectricConnectorType.Output &&
                    connection.NeighborConnectorType != ElectricConnectorType.Input)
                {
                    voltage = MathUtils.Max(voltage,
                        connection.NeighborElectricElement.GetOutputVoltage(
                            connection.NeighborConnectorFace));
                }
            }
            int length = MathUtils.Max((int)(voltage * 15.999f) - 7, 0);
            if (length == m_networkLastLength) return false;
            m_networkLastLength = length;
            // Source: Survivalcraft/Game/PistonElectricElement.cs:
            // PistonElectricElement.Simulate
            // Only the authority commits a native piston move. Clients receive the moving-block
            // set for animation and the final cells through authoritative world synchronization.
            if (ScMultiplayer.client?.IsConnected == true && !ScMultiplayer.IsHost)
                return false;
            // 《玩家领地》P7d：**活塞推入他人领地 ⇒ 不推进**（设计稿 §34.2 的行为层短路）。
            // 放在这里而不是只靠 `SuSubsystemPistonBlockBehavior` 的"改回式"兜底，是因为
            // 改回式把"目的地格"复原成空气时，被推方块已经从原格移走 ⇒ 方块会丢。
            // 在这里直接不 `AdjustPiston`，活塞根本不伸、方块原地不动。
            // 口径与 P7c 一致：活塞自身已在某块领地内 ⇒ 放行（领地内自己的机械）。
            if (!CanPistonExtend(length))
                return false;
            SubsystemElectricity.Project.FindSubsystem<SubsystemPistonBlockBehavior>(true)
                .AdjustPiston(CellFaces[0].Point, length);
            return false;
        }

        /// <summary>
        /// 活塞要伸出的这一段会不会推进他人领地。
        /// 覆盖 `length + 2` 格：`length` 格是活塞自己的臂（轴+头），再多一格是**被头顶掉、
        /// 会被推到更前面**的那个方块的目的地（`length=1` 时 1 格臂 + 被推方块共占 2 格）。
        /// </summary>
        private bool CanPistonExtend(int length)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null || length <= 0)
                return true;
            SubsystemTerrain terrain = SubsystemElectricity?.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return true;
            Point3 position = CellFaces[0].Point;
            if (mod.OwnerClaimAt(position) != null)
                return true;
            int data = Terrain.ExtractData(
                terrain.Terrain.GetCellValue(position.X, position.Y, position.Z));
            Vector3 vector = CellFace.FaceToVector3(Game.PistonBlock.GetFace(data));
            int reach = MathUtils.Min(length + 2, 10);
            for (int step = 1; step <= reach; step++)
            {
                var cell = new Point3(position.X + (int)vector.X * step,
                    position.Y + (int)vector.Y * step, position.Z + (int)vector.Z * step);
                if (cell.Y < 0 || cell.Y > 255)
                    return true;
                if (mod.CanRegionModifyCell(0, cell, out RegionClaim claim, out string reason))
                    continue;
                mod.NotifyRegionModificationDenied(0, cell, claim, "piston", reason);
                return false;
            }
            return true;
        }
    }
}
