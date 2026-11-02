using System;
using Engine;
using Game;
using SuAPI;

namespace SuMultiplayer.Engine
{
    public static class CircuitRandomSequence
    {
        private const uint FnvPrime = 16777619u;
        private static uint s_seed;
        private static bool s_enabled;

        public static void Configure(uint seed, bool enabled)
        {
            s_seed = seed;
            s_enabled = enabled;
        }

        public static uint SeedFromSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException(
                    "Circuit random sequence requires the active multiplayer session id.");
            uint hash = 2166136261u;
            unchecked
            {
                foreach (char value in sessionId)
                {
                    hash = (hash ^ (byte)value) * FnvPrime;
                    hash = (hash ^ (byte)(value >> 8)) * FnvPrime;
                }
            }
            return hash;
        }

        public static bool TryGet(Point3 point, int circuitStep, int purpose,
            out uint value)
        {
            value = 0;
            if (!s_enabled) return false;
            uint hash = 2166136261u;
            Hash(ref hash, unchecked((int)s_seed));
            Hash(ref hash, point.X);
            Hash(ref hash, point.Y);
            Hash(ref hash, point.Z);
            Hash(ref hash, circuitStep);
            Hash(ref hash, purpose);
            value = hash;
            return true;
        }

        private static void Hash(ref uint hash, int value)
        {
            unchecked
            {
                hash = (hash ^ (byte)value) * FnvPrime;
                hash = (hash ^ (byte)(value >> 8)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 16)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 24)) * FnvPrime;
            }
        }
    }

    // Source: Survivalcraft/Game/RandomGeneratorBlock.cs:RandomGeneratorBlock.CreateElectricElement
    // The simple type name remains RandomGeneratorBlock because database recipes and
    // loot entries resolve block types by name after the SuAPI block replacement.
    public sealed class RandomGeneratorBlock : Game.RandomGeneratorBlock
    {
        public override ElectricElement CreateElectricElement(
            SubsystemElectricity subsystemElectricity, int value, int x, int y, int z)
        {
            return new SuRandomGeneratorElectricElement(subsystemElectricity,
                new CellFace(x, y, z, GetFace(value)));
        }
    }

    // Source: Survivalcraft/Game/RandomGeneratorElectricElement.cs:RandomGeneratorElectricElement.Simulate
    // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.TryGetDeterministicRandom
    public sealed class SuRandomGeneratorElectricElement : RandomGeneratorElectricElement
    {
        private readonly ModFieldRef<SuRandomGeneratorElectricElement, bool> m_clockAllowedField;
        private readonly ModFieldRef<SuRandomGeneratorElectricElement, float> m_voltageField;

        public SuRandomGeneratorElectricElement(SubsystemElectricity subsystemElectricity,
            CellFace cellFace)
            : base(subsystemElectricity, cellFace)
        {
            IModParentField parentField = Program.ModManager?.ModParentField;
            if (parentField == null)
                throw new InvalidOperationException("SuAPI ModParentField is unavailable for random circuit state.");
            m_clockAllowedField = parentField.BindFieldRef<SuRandomGeneratorElectricElement, bool>(
                "m_clockAllowed");
            m_voltageField = parentField.BindFieldRef<SuRandomGeneratorElectricElement, float>(
                "m_voltage");
        }

        public override bool Simulate()
        {
            CellFace cellFace = CellFaces[0];
            int circuitStep = SubsystemElectricity.CircuitStep;
            if (!CircuitRandomSequence.TryGet(cellFace.Point, circuitStep, 0,
                    out uint voltageRandom))
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
                CircuitRandomSequence.TryGet(cellFace.Point, circuitStep, 1,
                    out uint delayRandom);
                int delaySteps = 25 + (int)(delayRandom % 51u);
                SubsystemElectricity.QueueElectricElementForSimulation(this,
                    circuitStep + delaySteps);
            }

            if (voltage == previousVoltage) return false;
            SubsystemElectricity.WritePersistentVoltage(cellFace.Point, voltage);
            return true;
        }
    }
}
