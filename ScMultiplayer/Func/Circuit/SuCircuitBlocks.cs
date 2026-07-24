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

        internal void ApplyNetworkPress()
        {
            base.Press();
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
        private bool m_networkDispenseAllowed = true;
        private double? m_networkLastDispenseTime;
        private readonly SubsystemBlockEntities m_subsystemBlockEntities;

        public SuDispenserElectricElement(SubsystemElectricity subsystemElectricity,
            Point3 point)
            : base(subsystemElectricity, point)
        {
            m_subsystemBlockEntities = subsystemElectricity.Project
                .FindSubsystem<SubsystemBlockEntities>(true);
        }

        // Source: Survivalcraft/Game/DispenserElectricElement.cs:
        // DispenserElectricElement.Simulate
        public override bool Simulate()
        {
            if (CalculateHighInputsCount() > 0)
            {
                double gameTime = SubsystemElectricity.SubsystemTime.GameTime;
                if (m_networkDispenseAllowed && (!m_networkLastDispenseTime.HasValue ||
                    gameTime - m_networkLastDispenseTime > 0.1))
                {
                    m_networkDispenseAllowed = false;
                    m_networkLastDispenseTime = gameTime;
                    if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
                    {
                        CellFace face = CellFaces[0];
                        m_subsystemBlockEntities.GetBlockEntity(face.X, face.Y, face.Z)?
                            .Entity.FindComponent<ComponentDispenser>()?.Dispense();
                    }
                }
            }
            else
            {
                m_networkDispenseAllowed = true;
            }
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
            SubsystemElectricity.Project.FindSubsystem<SubsystemPistonBlockBehavior>(true)
                .AdjustPiston(CellFaces[0].Point, length);
            return false;
        }
    }
}
