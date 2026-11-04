using System;
using System.Collections;
using System.Collections.Generic;
using Engine;
using SuAPI;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// Sign subsystem adapter. The sign table lives in the private
    /// SubsystemSignBlockBehavior.m_textsByPoint dictionary (not terrain cells).
    /// Host owns the authoritative table; clients send edits and receive full snapshots.
    /// </summary>
    public sealed class SignSyncEngine
    {
        public sealed class TriggerData
        {
            public long EventId;
            public long ServerTick;
            public int X, Y, Z;
            public string[] Lines;
            public uint[] Colors;
        }
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private Project m_project;
        private SubsystemSignBlockBehavior m_behavior;
        private int m_sessionSerial;
        private long m_nextTriggerId;
        private readonly Dictionary<string, bool> m_signInputHigh =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<TriggerData> m_triggerScratch = new List<TriggerData>(8);
        private readonly List<PlayerData> m_localPlayersScratch = new List<PlayerData>(4);

        public SignSyncEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModuleDiagnostics diagnostics)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
        }

        public int SessionSerial => m_sessionSerial;

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                m_project = project;
                m_behavior = null;
                m_sessionSerial++;
                m_signInputHigh.Clear();
                m_nextTriggerId = 0;
            }
            if (m_project == null)
            {
                m_behavior = null;
                return;
            }
            if (m_behavior == null)
                m_behavior = m_project.FindSubsystem<SubsystemSignBlockBehavior>(false);
        }

        /// <summary>主机轮询标牌电路的上升沿；只返回文字/颜色表现事件。</summary>
        public int CaptureTriggers(List<TriggerData> target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            if (m_behavior == null || m_project == null) return 0;
            SubsystemElectricity electricity = m_project.FindSubsystem<SubsystemElectricity>(false);
            if (electricity == null) return 0;
            object raw = m_parentField.GetParentField(electricity,
                "m_electricElements", typeof(SubsystemElectricity));
            if (!(raw is IDictionary elements)) return 0;
            foreach (object key in elements.Keys)
            {
                if (!(key is SignElectricElement sign) || sign.CellFaces.Count == 0) continue;
                bool high = false;
                foreach (ElectricConnection connection in sign.Connections)
                {
                    if (connection.ConnectorType == ElectricConnectorType.Output ||
                        connection.NeighborConnectorType == ElectricConnectorType.Input) continue;
                    if (ElectricElement.IsSignalHigh(connection.NeighborElectricElement
                        .GetOutputVoltage(connection.NeighborConnectorFace))) { high = true; break; }
                }
                CellFace face = sign.CellFaces[0];
                string id = face.X + "," + face.Y + "," + face.Z;
                bool wasHigh = m_signInputHigh.TryGetValue(id, out bool previous) && previous;
                m_signInputHigh[id] = high;
                if (!high || wasHigh) continue;
                SignData data = m_behavior.GetSignData(new Point3(face.X, face.Y, face.Z));
                if (data == null) continue;
                var trigger = new TriggerData
                {
                    EventId = ++m_nextTriggerId,
                    ServerTick = (long)(m_project.FindSubsystem<SubsystemTime>(false)?.GameTime ?? 0.0) * 1000L,
                    X = face.X, Y = face.Y, Z = face.Z,
                    Lines = new string[SignRecordData.LineCount],
                    Colors = new uint[SignRecordData.LineCount]
                };
                for (int i = 0; i < SignRecordData.LineCount; i++)
                {
                    trigger.Lines[i] = data.Lines != null && i < data.Lines.Length
                        ? data.Lines[i] ?? string.Empty : string.Empty;
                    trigger.Colors[i] = data.Colors != null && i < data.Colors.Length
                        ? data.Colors[i].PackedValue : 0u;
                }
                target.Add(trigger);
            }
            return target.Count;
        }

        public bool PresentTrigger(P.SignTriggerEvent message, out string reason)
        {
            reason = null;
            if (m_project == null) { reason = "no-project"; return false; }
            if (message.Lines.Count != SignRecordData.LineCount ||
                message.Colors.Count != SignRecordData.LineCount)
            { reason = "line-count"; return false; }
            if (!m_spawner.TryCollectLocalPlayers(m_project, m_localPlayersScratch,
                out _, out string collectReason))
            { reason = collectReason; return false; }
            string text = string.Join("\n", message.Lines).Trim('\n').Replace("\\\n", "");
            Color color = new Color(message.Colors[0]);
            if (color == Color.Black) color = Color.White;
            int max = Math.Max(color.R, Math.Max(color.G, color.B));
            if (max > 0) color *= 255f / max;
            for (int i = 0; i < m_localPlayersScratch.Count; i++)
                m_localPlayersScratch[i].ComponentPlayer?.ComponentGui?.DisplaySmallMessage(
                    text, color, blinking: true, playNotificationSound: true);
            return true;
        }

        public bool TryCapture(List<SignRecordData> target, out long fingerprint, out string reason)
        {
            if (target != null) target.Clear();
            fingerprint = 0L;
            reason = null;
            if (m_behavior == null)
            {
                reason = "sign-subsystem-missing";
                return false;
            }
            try
            {
                IDictionary dictionary = m_parentField.GetParentField<IDictionary>(m_behavior,
                    "m_textsByPoint", typeof(SubsystemSignBlockBehavior));
                if (dictionary == null)
                {
                    reason = "sign-table-missing";
                    return false;
                }
                var points = new List<Point3>(dictionary.Keys.Count);
                foreach (object key in dictionary.Keys)
                    if (key is Point3 point) points.Add(point);
                points.Sort((a, b) =>
                {
                    int compare = a.X.CompareTo(b.X);
                    if (compare != 0) return compare;
                    compare = a.Y.CompareTo(b.Y);
                    return compare != 0 ? compare : a.Z.CompareTo(b.Z);
                });
                var records = new List<SignRecordData>(points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    Point3 point = points[i];
                    SignData data = m_behavior.GetSignData(point);
                    if (data == null) continue;
                    var lines = new string[SignRecordData.LineCount];
                    var colors = new uint[SignRecordData.LineCount];
                    for (int line = 0; line < SignRecordData.LineCount; line++)
                    {
                        lines[line] = data.Lines != null && line < data.Lines.Length
                            ? (data.Lines[line] ?? string.Empty)
                            : string.Empty;
                        colors[line] = data.Colors != null && line < data.Colors.Length
                            ? data.Colors[line].PackedValue
                            : 0u;
                    }
                    records.Add(new SignRecordData(point.X, point.Y, point.Z, lines, colors,
                        data.Url ?? string.Empty));
                }
                if (target != null) target.AddRange(records);
                fingerprint = SignCodec.Hash(records);
                return true;
            }
            catch (Exception error)
            {
                reason = "sign-capture:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        public bool TryApplyClientEdits(int clientId, IReadOnlyList<SignRecordData> edits,
            out string reason)
        {
            reason = null;
            if (m_behavior == null)
            {
                reason = "sign-subsystem-missing";
                return false;
            }
            if (edits == null || edits.Count == 0) return true;
            if (edits.Count > 64)
            {
                reason = "edit-count:" + edits.Count;
                return false;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentBody == null)
            {
                reason = "client-" + clientId + "-has-no-avatar";
                return false;
            }
            Vector3 playerPosition = playerData.ComponentPlayer.ComponentBody.Position;
            SubsystemTerrain terrain = m_project?.FindSubsystem<SubsystemTerrain>(false);
            for (int i = 0; i < edits.Count; i++)
            {
                SignRecordData edit = edits[i];
                if (edit == null)
                {
                    reason = "edit-null";
                    return false;
                }
                var point = new Point3(edit.X, edit.Y, edit.Z);
                if (point.Y < 0 || point.Y > 255)
                {
                    reason = "edit-y:" + point.Y;
                    return false;
                }
                if (Vector3.Distance(playerPosition,
                        new Vector3(point.X + 0.5f, point.Y + 0.5f, point.Z + 0.5f)) > 10f)
                {
                    reason = "edit-distance:" + edit.Describe();
                    return false;
                }
                if (terrain?.Terrain == null)
                {
                    reason = "terrain-missing";
                    return false;
                }
                int value = terrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
                int contents = global::Game.Terrain.ExtractContents(value);
                if (contents <= 0 || contents >= BlocksManager.Blocks.Length ||
                    !(BlocksManager.Blocks[contents] is SignBlock))
                {
                    reason = "edit-not-sign:" + edit.Describe();
                    return false;
                }
                var lines = new string[SignRecordData.LineCount];
                var colors = new Color[SignRecordData.LineCount];
                for (int line = 0; line < SignRecordData.LineCount; line++)
                {
                    lines[line] = edit.Lines[line] ?? string.Empty;
                    colors[line] = new Color(edit.Colors[line]);
                }
                m_behavior.SetSignData(point, lines, colors, edit.Url ?? string.Empty);
            }
            return true;
        }

        public bool TryApplyHostState(IReadOnlyList<SignRecordData> state, out string reason)
        {
            reason = null;
            if (m_behavior == null)
            {
                reason = "sign-subsystem-missing";
                return false;
            }
            try
            {
                IDictionary dictionary = m_parentField.GetParentField<IDictionary>(m_behavior,
                    "m_textsByPoint", typeof(SubsystemSignBlockBehavior));
                if (dictionary == null)
                {
                    reason = "sign-table-missing";
                    return false;
                }
                var wanted = new HashSet<Point3>();
                if (state != null)
                    for (int i = 0; i < state.Count; i++)
                    {
                        SignRecordData sign = state[i];
                        if (sign == null) continue;
                        var point = new Point3(sign.X, sign.Y, sign.Z);
                        wanted.Add(point);
                        var lines = new string[SignRecordData.LineCount];
                        var colors = new Color[SignRecordData.LineCount];
                        for (int line = 0; line < SignRecordData.LineCount; line++)
                        {
                            lines[line] = sign.Lines[line] ?? string.Empty;
                            colors[line] = new Color(sign.Colors[line]);
                        }
                        m_behavior.SetSignData(point, lines, colors, sign.Url ?? string.Empty);
                    }
                var remove = new List<Point3>();
                foreach (object key in dictionary.Keys)
                    if (key is Point3 point && !wanted.Contains(point)) remove.Add(point);
                for (int i = 0; i < remove.Count; i++) dictionary.Remove(remove[i]);
                return true;
            }
            catch (Exception error)
            {
                reason = "sign-apply:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }
    }
}
