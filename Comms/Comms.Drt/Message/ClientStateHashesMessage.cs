using System;

namespace Comms.Drt;

internal class ClientStateHashesMessage : Message
{
    public int FirstHashStep;

    public ushort[] Hashes;

    internal override void Read(Reader reader)
    {
        FirstHashStep = reader.ReadPackedInt32();
        byte[] array = reader.ReadBytes();
        Hashes = new ushort[array.Length / 2];
        Buffer.BlockCopy(array, 0, Hashes, 0, array.Length);
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(FirstHashStep);
        byte[] array = new byte[2 * Hashes.Length];
        Buffer.BlockCopy(Hashes, 0, array, 0, array.Length);
        writer.WriteBytes(array);
    }
}
