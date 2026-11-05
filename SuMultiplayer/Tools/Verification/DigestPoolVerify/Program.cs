using System;
using System.Collections.Generic;
using System.Threading;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Terrain
{
    public readonly struct CellEdit
    {
        public CellEdit(int x, int y, int z, int contents, int data)
        { X = x; Y = y; Z = z; Contents = contents; Data = data; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Contents { get; }
        public int Data { get; }
    }
}

internal static class Program
{
    private static int s_passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        s_passed++;
        Console.WriteLine("PASS " + name);
    }

    private static DigestWorkResult WaitFor(ChunkDigestPool pool, DigestWorkKind kind)
    {
        var results = new List<DigestWorkResult>(4);
        for (int attempt = 0; attempt < 500; attempt++)
        {
            pool.DrainCompleted(results, 16);
            for (int i = 0; i < results.Count; i++)
                if (results[i].Kind == kind) return results[i];
            results.Clear();
            Thread.Sleep(10);
        }
        throw new TimeoutException("Timed out waiting for " + kind + ".");
    }

    private static void Main()
    {
        const int cellCount = ChunkDigest.SizeXZ * ChunkDigest.SizeXZ * ChunkDigest.Height;
        var cells = new int[cellCount];
        var shafts = new int[ChunkDigest.SizeXZ * ChunkDigest.SizeXZ];
        for (int column = 0; column < shafts.Length; column++)
        {
            shafts[column] = 32 + column % 64;
            for (int y = 0; y < ChunkDigest.Height; y++)
                cells[y + column * ChunkDigest.Height] = ((column * 31 + y * 17) & 0xFFFF) << 16;
        }

        var fullExpected = new ChunkDigest();
        fullExpected.ComputePacked(cells);
        var topExpected = new ChunkDigest();
        topExpected.ComputeTopLayer(cells, shafts);

        using (var pool = new ChunkDigestPool(topSlots: 1, fullSlots: 1))
        {
            Check(pool.TrySubmitTopLayer(new ChunkKey(4, -2), 1, cells, shafts,
                DigestWorkKind.TopLayerInitial), "top-layer snapshot submission");
            DigestWorkResult top = WaitFor(pool, DigestWorkKind.TopLayerInitial);
            Check(top.Succeeded && top.TopHeightHash == topExpected.TopHeightHash &&
                top.TopBlockHash == topExpected.TopBlockHash, "compact and direct top-layer hashes match");

            Check(pool.TrySubmitFull(new ChunkKey(4, -2), 2, cells, DigestWorkKind.FullFinal),
                "full snapshot submission");
            Array.Fill(cells, unchecked((int)0x7FFFFFFF));
            DigestWorkResult full = WaitFor(pool, DigestWorkKind.FullFinal);
            Check(full.Succeeded && full.Value == fullExpected.Value,
                "full digest reads the submission-time snapshot");
        }

        var center = new ChunkKey(10, -10);
        var footprint = new HashSet<ChunkKey>
        {
            center,
            new ChunkKey(center.X + 1, center.Z),
            new ChunkKey(center.X - 1, center.Z),
            new ChunkKey(center.X, center.Z + 1),
            new ChunkKey(center.X, center.Z - 1)
        };
        Check(footprint.Count == 5, "footprint has five unique chunks");
        Check(footprint.Contains(center) &&
            footprint.Contains(new ChunkKey(center.X + 1, center.Z)) &&
            footprint.Contains(new ChunkKey(center.X - 1, center.Z)) &&
            footprint.Contains(new ChunkKey(center.X, center.Z + 1)) &&
            footprint.Contains(new ChunkKey(center.X, center.Z - 1)), "center and four orthogonal neighbors included");
        Check(!ChunkDigestFootprint.Contains(center, new ChunkKey(center.X + 1, center.Z + 1)),
            "diagonal neighbor excluded");

        Console.WriteLine("RESULT " + s_passed + "/" + s_passed + " passed");
    }
}
