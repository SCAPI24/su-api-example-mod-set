using Google.Protobuf.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SuMultiplayer.Net
{
    // Source: ScMultiplayer/Message/Message.cs:Message.ProtocolHash, BuildFingerprint, IsProtocolCompatible
    public static class MultiplayerModCompatibility
    {
        // Source: SuMultiplayer/ModInfo.xml:ModInfo.Version
        public const string ModVersion = "0.1.0";
        public const uint ProtocolVersion = 1;

        private static readonly FileDescriptor[] s_descriptors =
        {
            global::SuMultiplayer.Protocol.Ambient.AmbientReflection.Descriptor,
            global::SuMultiplayer.Protocol.Action.ActionReflection.Descriptor,
            global::SuMultiplayer.Protocol.Authority.AuthorityReflection.Descriptor,
            global::SuMultiplayer.Protocol.BlockuseReflection.Descriptor,
            global::SuMultiplayer.Protocol.ChatReflection.Descriptor,
            global::SuMultiplayer.Protocol.Circuit.CircuitReflection.Descriptor,
            global::SuMultiplayer.Protocol.Creature.CreatureReflection.Descriptor,
            global::SuMultiplayer.Protocol.EditableReflection.Descriptor,
            global::SuMultiplayer.Protocol.Effects.EffectsReflection.Descriptor,
            global::SuMultiplayer.Protocol.Entities.EntitiesReflection.Descriptor,
            global::SuMultiplayer.Protocol.Explosion.ExplosionReflection.Descriptor,
            global::SuMultiplayer.Protocol.FurnitureReflection.Descriptor,
            global::SuMultiplayer.Protocol.Melee.MeleeReflection.Descriptor,
            global::SuMultiplayer.Protocol.Mount.MountReflection.Descriptor,
            global::SuMultiplayer.Protocol.Piston.PistonReflection.Descriptor,
            global::SuMultiplayer.Protocol.PlayerprofileReflection.Descriptor,
            global::SuMultiplayer.Protocol.Position.PositionReflection.Descriptor,
            global::SuMultiplayer.Protocol.ProtocolReflection.Descriptor,
            global::SuMultiplayer.Protocol.Remotesound.RemotesoundReflection.Descriptor,
            global::SuMultiplayer.Protocol.Respawn.RespawnReflection.Descriptor,
            global::SuMultiplayer.Protocol.SelfDamage.SelfdamageReflection.Descriptor,
            global::SuMultiplayer.Protocol.SignReflection.Descriptor,
            global::SuMultiplayer.Protocol.SkinReflection.Descriptor,
            global::SuMultiplayer.Protocol.StatsReflection.Descriptor,
            global::SuMultiplayer.Protocol.Terrain.TerrainReflection.Descriptor,
            global::SuMultiplayer.Protocol.Transfer.TransferReflection.Descriptor,
            global::SuMultiplayer.Protocol.Vitals.VitalsReflection.Descriptor,
            global::SuMultiplayer.Protocol.Voice.VoiceReflection.Descriptor,
            global::SuMultiplayer.Protocol.WorldControl.WorldcontrolReflection.Descriptor,
            global::SuMultiplayer.Protocol.WorldInfo.WorldinfoReflection.Descriptor
        };

        private static readonly string s_protocolHash = ComputeProtocolHash();

        private static readonly string s_buildFingerprint = Convert.ToHexString(
            SHA256.HashData(typeof(MultiplayerModCompatibility).Assembly.ManifestModule
                .ModuleVersionId.ToByteArray())).ToLowerInvariant();

        public static string ProtocolHash => s_protocolHash;

        public static string BuildFingerprint => s_buildFingerprint;

        public static bool IsCompatible(string modVersion, uint protocolVersion,
            string protocolHash, string buildFingerprint)
        {
            return string.Equals(modVersion, ModVersion, StringComparison.Ordinal) &&
                protocolVersion == ProtocolVersion &&
                string.Equals(protocolHash, ProtocolHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(buildFingerprint, BuildFingerprint, StringComparison.OrdinalIgnoreCase);
        }

        public static string Describe(string modVersion, uint protocolVersion,
            string protocolHash, string buildFingerprint)
        {
            return "mod " + Normalize(modVersion) + ", protocol v" + protocolVersion + "/" +
                Abbreviate(protocolHash) + ", build " + Abbreviate(buildFingerprint);
        }

        private static string Normalize(string value) =>
            string.IsNullOrWhiteSpace(value) ? "missing" : value.Trim();

        private static string Abbreviate(string value)
        {
            string normalized = Normalize(value);
            return normalized.Length > 12 ? normalized.Substring(0, 12) : normalized;
        }

        private static string ComputeProtocolHash()
        {
            var descriptors = new List<FileDescriptor>(s_descriptors);
            descriptors.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            using (var manifest = new MemoryStream())
            {
                WriteString(manifest, "SuMultiplayer-Wire|" + ModVersion + "|" + ProtocolVersion);
                foreach (FileDescriptor descriptor in descriptors)
                {
                    WriteString(manifest, descriptor.Name);
                    byte[] data = descriptor.SerializedData.ToByteArray();
                    WriteInt32(manifest, data.Length);
                    manifest.Write(data, 0, data.Length);
                }
                return Convert.ToHexString(SHA256.HashData(manifest.ToArray())).ToLowerInvariant();
            }
        }

        private static void WriteString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            WriteInt32(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
