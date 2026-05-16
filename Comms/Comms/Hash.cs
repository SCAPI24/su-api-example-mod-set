using System;
using System.Runtime.CompilerServices;

namespace Comms;

public static class Hash
{
    private const uint Prime32v1 = 2654435761u;

    private const uint Prime32v2 = 2246822519u;

    private const uint Prime32v3 = 3266489917u;

    private const uint Prime32v4 = 668265263u;

    private const uint Prime32v5 = 374761393u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Calculate(byte[] buffer, uint seed = 0u)
    {
        return Calculate(buffer, 0, buffer.Length, seed);
    }

    public unsafe static uint Calculate(byte[] buffer, int offset, int count, uint seed = 0u)
    {
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException();
        }
        int num = count;
        uint num2;
        fixed (byte* ptr = buffer)
        {
            byte* pInput = ptr + offset;
            if (count >= 16)
            {
                var (acc, acc2, acc3, acc4) = InitAccumulators32(seed);
                do
                {
                    num2 = ProcessStripe32(ref pInput, ref acc, ref acc2, ref acc3, ref acc4);
                    num -= 16;
                }
                while (num >= 16);
            }
            else
            {
                num2 = seed + 374761393;
            }
            num2 += (uint)count;
            num2 = ProcessRemaining32(pInput, num2, num);
        }
        return Avalanche32(num2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (uint, uint, uint, uint) InitAccumulators32(uint seed)
    {
        return ((uint)((int)seed + -1640531535 + -2048144777), seed + 2246822519u, seed, seed - 2654435761u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static uint ProcessStripe32(ref byte* pInput, ref uint acc1, ref uint acc2, ref uint acc3, ref uint acc4)
    {
        ProcessLane32(ref pInput, ref acc1);
        ProcessLane32(ref pInput, ref acc2);
        ProcessLane32(ref pInput, ref acc3);
        ProcessLane32(ref pInput, ref acc4);
        return RotateLeft(acc1, 1) + RotateLeft(acc2, 7) + RotateLeft(acc3, 12) + RotateLeft(acc4, 18);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ProcessLane32(ref byte* pInput, ref uint accn)
    {
        uint lane = *(uint*)pInput;
        accn = Round32(accn, lane);
        pInput += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static uint ProcessRemaining32(byte* pInput, uint acc, int remainingLen)
    {
        while (remainingLen >= 4)
        {
            uint num = *(uint*)pInput;
            acc += (uint)((int)num * -1028477379);
            acc = RotateLeft(acc, 17) * 668265263;
            remainingLen -= 4;
            pInput += 4;
        }
        while (remainingLen >= 1)
        {
            byte b = *pInput;
            acc += (uint)(b * 374761393);
            acc = RotateLeft(acc, 11) * 2654435761u;
            remainingLen--;
            pInput++;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Round32(uint accn, uint lane)
    {
        accn += (uint)((int)lane * -2048144777);
        accn = RotateLeft(accn, 13);
        accn *= 2654435761u;
        return accn;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Avalanche32(uint acc)
    {
        acc ^= acc >> 15;
        acc *= 2246822519u;
        acc ^= acc >> 13;
        acc *= 3266489917u;
        acc ^= acc >> 16;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int bits)
    {
        return (value << bits) | (value >> 32 - bits);
    }
}
