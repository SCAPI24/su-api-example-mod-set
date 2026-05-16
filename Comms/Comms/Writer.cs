using System;
using System.Net;
using System.Text;

namespace Comms;

public class Writer
{
    private int _Position;

    private int _Length;

    private byte[] Bytes = new byte[16];

    public int Position
    {
        get
        {
            return _Position;
        }
        set
        {
            _Length = Length;
            if (value < 0 || value > _Length)
            {
                throw new InvalidOperationException("Position out of bounds.");
            }
            _Position = value;
        }
    }

    public int Length
    {
        get
        {
            return Math.Max(_Position, _Length);
        }
        set
        {
            if (value < 0)
            {
                throw new InvalidOperationException("Length out of bounds.");
            }
            _Length = Length;
            if (value > _Length)
            {
                EnsureCapacity(value);
                Array.Clear(Bytes, _Length, value - _Length);
                _Length = value;
            }
            else if (value < _Length)
            {
                _Length = value;
                _Position = value;
            }
        }
    }

    public byte[] GetBytes()
    {
        byte[] array = new byte[Length];
        Array.Copy(Bytes, array, array.Length);
        return array;
    }

    public void WriteBoolean(bool value)
    {
        WriteByte(value ? ((byte)1) : ((byte)0));
    }

    public void WriteByte(byte value)
    {
        EnsureCapacity(_Position + 1);
        Bytes[_Position++] = value;
    }

    public void WriteChar(char value)
    {
        EnsureCapacity(_Position + 2);
        Bytes[_Position++] = (byte)value;
        Bytes[_Position++] = (byte)((int)value >> 8);
    }

    public void WriteInt16(short value)
    {
        EnsureCapacity(_Position + 2);
        Bytes[_Position++] = (byte)value;
        Bytes[_Position++] = (byte)(value >> 8);
    }

    public void WriteUInt16(ushort value)
    {
        WriteInt16((short)value);
    }

    public void WriteInt32(int value)
    {
        EnsureCapacity(_Position + 4);
        Bytes[_Position++] = (byte)value;
        Bytes[_Position++] = (byte)(value >> 8);
        Bytes[_Position++] = (byte)(value >> 16);
        Bytes[_Position++] = (byte)(value >> 24);
    }

    public void WriteUInt32(uint value)
    {
        WriteInt32((int)value);
    }

    public void WriteInt64(long value)
    {
        EnsureCapacity(_Position + 8);
        Bytes[_Position++] = (byte)value;
        Bytes[_Position++] = (byte)(value >> 8);
        Bytes[_Position++] = (byte)(value >> 16);
        Bytes[_Position++] = (byte)(value >> 24);
        Bytes[_Position++] = (byte)(value >> 32);
        Bytes[_Position++] = (byte)(value >> 40);
        Bytes[_Position++] = (byte)(value >> 48);
        Bytes[_Position++] = (byte)(value >> 56);
    }

    public void WriteUInt64(ulong value)
    {
        WriteInt64((long)value);
    }

    public unsafe void WriteSingle(float value)
    {
        WriteInt32(*(int*)(&value));
    }

    public unsafe void WriteDouble(double value)
    {
        WriteInt64(*(long*)(&value));
    }

    public void WritePackedInt32(int value)
    {
        EnsureCapacity(_Position + 5);
        uint num;
        for (num = (uint)value; num >= 128; num >>= 7)
        {
            Bytes[_Position++] = (byte)(num | 0x80);
        }
        Bytes[_Position++] = (byte)num;
    }

    public void WriteFixedBytes(byte[] bytes)
    {
        if (bytes != null)
        {
            WriteFixedBytes(bytes, 0, bytes.Length);
        }
    }

    public void WriteFixedBytes(byte[] bytes, int start, int count)
    {
        EnsureCapacity(_Position + count);
        Array.Copy(bytes, start, Bytes, _Position, count);
        _Position += count;
    }

    public void WriteBytes(byte[] bytes)
    {
        if (bytes != null)
        {
            WriteBytes(bytes, 0, bytes.Length);
        }
        else
        {
            WriteByte(0);
        }
    }

    public void WriteBytes(byte[] bytes, int start, int count)
    {
        EnsureCapacity(_Position + 5 + count);
        WritePackedInt32(count);
        Array.Copy(bytes, start, Bytes, _Position, count);
        _Position += count;
    }

    public void WriteString(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            EnsureCapacity(_Position + 5 + byteCount);
            WritePackedInt32(byteCount);
            _Position += Encoding.UTF8.GetBytes(value, 0, value.Length, Bytes, _Position);
        }
        else
        {
            WriteByte(0);
        }
    }

    public void WriteIPEndPoint(IPEndPoint address)
    {
        WriteBytes(address.Address.GetAddressBytes());
        WriteInt16((short)address.Port);
    }

    private void EnsureCapacity(int capacity)
    {
        if (capacity > Bytes.Length)
        {
            int num;
            for (num = Bytes.Length; num < capacity; num *= 2)
            {
            }
            byte[] array = new byte[num];
            Array.Copy(Bytes, array, _Position);
            Bytes = array;
        }
    }
}
