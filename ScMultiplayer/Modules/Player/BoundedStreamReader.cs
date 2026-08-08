using System;
using System.IO;

namespace ScMultiplayer
{
    // Source: ScMultiplayerProfileHandlers.ReadLimitedStream
    // Reads a bounded payload without owning the caller's stream lifetime.
    internal static class BoundedStreamReader
    {
        public static byte[] Read(Stream stream, int maximumBytes)
        {
            if (stream == null) return Array.Empty<byte>();
            if (stream.CanSeek && stream.Length > maximumBytes)
                throw new InvalidOperationException($"Asset is larger than {maximumBytes} bytes.");
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (memory.Length + read > maximumBytes)
                    throw new InvalidOperationException($"Asset is larger than {maximumBytes} bytes.");
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
    }
}
