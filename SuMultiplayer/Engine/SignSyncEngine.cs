using System;
using System.Collections;
using System.Collections.Generic;
using Engine;
using SuAPI;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// Sign subsystem adapter. The sign table lives in the private
    /// SubsystemSignBlockBehavior.m_textsByPoint dictionary (not terrain cells).
    /// Host owns the authoritative table; clients send edits and receive full snapshots.
    /// </summary>
    public sealed class SignSyncEngine
    {
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private Project m_project;
        private SubsystemSignBlockBehavior m_behavior;
        private int m_sessionSerial;

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
            }
            if (m_project == null)
            {
                m_behavior = null;
                return;
            }
            if (m_behavior == null)
                m_behavior = m_project.FindSubsystem<SubsystemSignBlockBehavior>(false);
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
