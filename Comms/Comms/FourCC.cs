using System.Text;

namespace Comms;

public static class FourCC
{
    public static int Parse(string fourcc)
    {
        return (int)(((uint)fourcc[3] << 24) | ((uint)fourcc[2] << 16) | ((uint)fourcc[1] << 8) | fourcc[0]);
    }

    public static string Write(int fourcc)
    {
        StringBuilder stringBuilder = new(4);
        stringBuilder.Append((char)(byte)fourcc);
        stringBuilder.Append((char)(byte)(fourcc >> 8));
        stringBuilder.Append((char)(byte)(fourcc >> 16));
        stringBuilder.Append((char)(byte)(fourcc >> 24));
        return stringBuilder.ToString();
    }
}
