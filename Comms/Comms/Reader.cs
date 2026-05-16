using System;
using System.Net;
using System.Text;

namespace Comms;

public class Reader
{
    private int _Position;

    private byte[] Bytes;

    public int Position
    {
        get
        {
            return _Position;
        }
        set
        {
            if (value < 0 || value > Length)
            {
                throw new InvalidOperationException("Position out of bounds.");
            }
            _Position = value;
        }
    }

    public int Length => Bytes.Length;

    public Reader(byte[] bytes)
    {
        Bytes = bytes;
    }

    public bool ReadBoolean()
    {
        return ReadByte() != 0;
    }

    public byte ReadByte()
    {
        if (_Position + 1 > Length)
        {
            throw new InvalidOperationException("Reading beyond end of data.");
        }
        return Bytes[_Position++];
    }

    public char ReadChar()
    {
        return (char)ReadInt16();
    }

    public short ReadInt16()
    {
        if (_Position + 2 > Length)
        {
            throw new InvalidOperationException("Reading beyond end of data.");
        }
        return (short)(Bytes[_Position++] | (Bytes[_Position++] << 8));
    }

    public ushort ReadUInt16()
    {
        return (ushort)ReadInt16();
    }

    public int ReadInt32()
    {
        if (_Position + 4 > Length)
        {
            throw new InvalidOperationException("Reading beyond end of data.");
        }
        return Bytes[_Position++] | (Bytes[_Position++] << 8) | (Bytes[_Position++] << 16) | (Bytes[_Position++] << 24);
    }

    public uint ReadUInt32()
    {
        return (uint)ReadInt32();
    }

    public long ReadInt64()
    {
        if (_Position + 8 > Length)
        {
            throw new InvalidOperationException("Reading beyond end of data.");
        }

        // 读取低32位
        uint low = (uint)(Bytes[_Position++] | (Bytes[_Position++] << 8) | (Bytes[_Position++] << 16) | (Bytes[_Position++] << 24));

        // 读取高32位  
        uint high = (uint)(Bytes[_Position++] | (Bytes[_Position++] << 8) | (Bytes[_Position++] << 16) | (Bytes[_Position++] << 24));

        // 组合成64位整数
        return (long)(((ulong)high << 32) | low);
    }

    public ulong ReadUInt64()
    {
        return (ulong)ReadInt64();
    }

    public unsafe float ReadSingle()
    {
        int num = ReadInt32();
        return *(float*)(&num);
    }

    public unsafe double ReadDouble()
    {
        long num = ReadInt64();
        return *(double*)(&num);
    }

    public int ReadPackedInt32()
    {
        int num = 0;
        int num2 = 0;
        byte b;
        do
        {
            if (num2 == 35)
            {
                throw new InvalidOperationException("Corrupt 7-bit packed int.");
            }
            b = ReadByte();
            num |= (b & 0x7F) << num2;
            num2 += 7;
        }
        while ((b & 0x80) != 0);
        return num;
    }

    public int ReadPackedInt32(int minValue, int maxValue)
    {
        int num = ReadPackedInt32();
        if (num < minValue)
        {
            throw new InvalidOperationException("Value too small.");
        }
        if (num > maxValue)
        {
            throw new InvalidOperationException("Value too large.");
        }
        return num;
    }

    public byte[] ReadFixedBytes(int count)
    {
        if (_Position + count > Bytes.Length)
        {
            throw new InvalidOperationException("Reading beyond end of data.");
        }
        byte[] array = new byte[count];
        Array.Copy(Bytes, _Position, array, 0, count);
        _Position += count;
        return array;
    }

    public byte[] ReadBytes()
    {
        int count = ReadPackedInt32();
        return ReadFixedBytes(count);
    }

    public string ReadString()
    {
        return Encoding.UTF8.GetString(ReadBytes());
    }

    public IPEndPoint ReadIPEndPoint()
    {
        byte[] array = ReadBytes();
        ushort num = ReadUInt16();
        return new IPEndPoint(new IPAddress(array), num);
    }
}
