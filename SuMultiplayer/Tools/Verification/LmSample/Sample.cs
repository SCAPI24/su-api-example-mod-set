using System;
using System.Reflection;
using System.Text.Json;
using SuMultiplayer.Terrain;

namespace LmSample
{
    /// <summary>打印一份真实的 /state 样本（用工具自己的 BuildStateJson + 合成快照），供报告引用。</summary>
    internal static class Sample
    {
        private static void Main()
        {
            var snapshot = new LiveMapSnapshot
            {
                HasViewerCell = true, CellX = -75, CellY = 73, CellZ = 140,
                View = LiveMapView.All, ChunkRadius = 0,
                WatchedClientId = 2, WatchedClientName = "Android User"
            };
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = -1, Name = "Host", IsHost = true, ChunkX = -5, ChunkZ = 8,
                HasCell = true, CellX = -75, CellY = 73, CellZ = 140
            });
            snapshot.Centers.Add(new LiveMapCenter
            {
                ClientId = 2, Name = "Android User", IsHost = false, ChunkX = -13, ChunkZ = 13,
                HasCell = true, CellX = -207, CellY = 70, CellZ = 217
            });
            snapshot.Clients.Add(new LiveMapClient { ClientId = 2, Name = "Android User", ReportedChunkCount = 2 });
            snapshot.ClientChunks.Add(new LiveMapClientChunk { ClientId = 2, X = -13, Z = 13 });
            snapshot.ClientChunks.Add(new LiveMapClientChunk { ClientId = 2, X = -12, Z = 13 });
            var chunk = new LiveMapChunk { X = -5, Z = 8, State = 2, PendingClients = 2 };
            for (int i = 0; i < LiveMapChunk.Columns; i++) { chunk.Contents[i] = 2; chunk.Top[i] = 64; }
            snapshot.Chunks.Add(chunk);
            // 第二块 n=0：证明"0 也照实写出，网页不画数字"
            var chunkZero = new LiveMapChunk { X = -4, Z = 8 };
            for (int i = 0; i < LiveMapChunk.Columns; i++) { chunkZero.Contents[i] = 3; chunkZero.Top[i] = 64; }
            snapshot.Chunks.Add(chunkZero);

            Type type = typeof(SuMPLiveMap.Program);
            type.GetField("s_snapshot", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, snapshot);
            type.GetField("s_snapshotMs", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, Environment.TickCount64);
            var json = (string)type.GetMethod("BuildStateJson", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);

            // 把超长的 base64 地形字段缩短，方便阅读（结构不变）
            using JsonDocument doc = JsonDocument.Parse(json);
            var options = new JsonWriterOptions { Indented = true };
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, options))
            {
                writer.WriteStartObject();
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name == "chunks")
                    {
                        writer.WritePropertyName("chunks");
                        writer.WriteStartArray();
                        foreach (JsonElement c in p.Value.EnumerateArray())
                        {
                            writer.WriteStartObject();
                            foreach (JsonProperty f in c.EnumerateObject())
                            {
                                if (f.Name == "d") { writer.WriteString("d", "<base64 768B 略>"); continue; }
                                f.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        continue;
                    }
                    p.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}
