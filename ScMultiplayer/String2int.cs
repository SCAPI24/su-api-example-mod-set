using System;

public static class StringExtensions
{
    /// <summary>
    /// 将字符串映射到注册端口范围 (1024-49151)
    /// </summary>
    public static int ToRegisteredPort(this string input)
    {
        return MapToRange(input, 1024, 49151);
    }

    /// <summary>
    /// 将字符串映射到动态/私有端口范围 (49152-65535)
    /// </summary>
    public static int ToDynamicPort(this string input)
    {
        return MapToRange(input, 49152, 65535);
    }

    /// <summary>
    /// 将字符串映射到指定范围的整数值
    /// </summary>
    /// <param name="input">输入字符串</param>
    /// <param name="min">最小值（包含）</param>
    /// <param name="max">最大值（包含）</param>
    /// <returns>映射到指定范围内的整数值</returns>
    public static int MapToRange(this string input, int min, int max)
    {
        if (string.IsNullOrEmpty(input))
            return min; // 返回范围的最小值

        if (min >= max)
            throw new ArgumentException("最小值必须小于最大值");

        // 计算原始哈希值
        int hash = ComputeStringHash(input);

        // 映射到指定范围
        int range = max - min + 1;
        int mappedValue = min + (Math.Abs(hash) % range);

        return mappedValue;
    }

    /// <summary>
    /// 计算字符串的哈希值（基于原始算法）
    /// </summary>
    private static int ComputeStringHash(string input)
    {
        int result = 0;
        int multiplier = 1;

        foreach (char c in input)
        {
            result += c * multiplier;
            multiplier += 29;
        }

        return result;
    }

    /// <summary>
    /// 将字符串转换为基于特定算法的整数值（保持原有方法）
    /// </summary>
    public static int ToCustomInt(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return 0;

        return ComputeStringHash(input);
    }

    /// <summary>
    /// 静态方法版本
    /// </summary>
    public static int ConvertStringToCustomInt(string input)
    {
        return input.ToCustomInt();
    }
}