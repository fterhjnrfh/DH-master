namespace DH.Contracts;

/// <summary>
/// 统一的设备/通道命名工具 —— 全项目唯一命名规则
/// 
/// 核心公式: channelId = deviceId × 100 + channelNumber (1-based)
/// 
/// 通道名格式: AI{deviceId:D2}_CH{channelNumber:D2}
///   • deviceId ≥ 1 时: AI01_CH05, AI02_CH16
///   • deviceId = 0  时: AI00_CH01, AI00_CH64
///
/// TDMS Group 名: AI{deviceId:D2}
/// TDMS Channel 名: AI{deviceId:D2}_CH{channelNumber:D2}
/// TDMS 文件名(多文件模式): {session}_AI{deviceId:D2}_CH{channelNumber:D2}.tdms
/// 
/// UI 设备显示: AI{deviceId:D2}
/// </summary>
public static class ChannelNaming
{
    /// <summary>
    /// 从 channelId 提取设备ID
    /// </summary>
    public static int GetDeviceId(int channelId) => Math.Max(0, channelId / 100);

    /// <summary>
    /// 从 channelId 提取通道号（1-based）
    /// </summary>
    public static int GetChannelNumber(int channelId) => Math.Max(1, channelId % 100);

    /// <summary>
    /// 构建 channelId
    /// </summary>
    public static int MakeChannelId(int deviceId, int channelNumber) => deviceId * 100 + channelNumber;

    /// <summary>
    /// 设备显示名: AI01, AI02, ...
    /// </summary>
    public static string DeviceDisplayName(int deviceId) => $"AI{deviceId:D2}";

    /// <summary>
    /// 从 channelId 生成设备显示名
    /// </summary>
    public static string DeviceDisplayName(int channelId, bool fromChannelId = true)
        => DeviceDisplayName(GetDeviceId(channelId));

    /// <summary>
    /// 通道统一名称: AI01_CH05
    /// </summary>
    public static string ChannelName(int deviceId, int channelNumber)
        => $"AI{deviceId:D2}_CH{channelNumber:D2}";

    /// <summary>
    /// 从 channelId 生成通道统一名称
    /// </summary>
    public static string ChannelName(int channelId)
        => ChannelName(GetDeviceId(channelId), GetChannelNumber(channelId));

    /// <summary>
    /// TDMS Group 名称 = 设备显示名
    /// </summary>
    public static string TdmsGroupName(int channelId)
        => DeviceDisplayName(GetDeviceId(channelId));

    /// <summary>
    /// TDMS Channel 名称 = 通道统一名称
    /// </summary>
    public static string TdmsChannelName(int channelId)
        => ChannelName(channelId);

    /// <summary>
    /// 多文件模式的基础文件名: {session}_AI01_CH05
    /// </summary>
    public static string PerChannelFileName(string sessionName, int channelId)
    {
        var dev = GetDeviceId(channelId);
        var ch = GetChannelNumber(channelId);
        return $"{sessionName}_AI{dev:D2}_CH{ch:D2}";
    }

    /// <summary>
    /// 从统一通道名解析出 channelId（如 "AI01_CH05" → 105）
    /// </summary>
    /// <returns>成功返回 channelId (≥0)，失败返回 -1</returns>
    public static int ParseChannelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;

        // 优先匹配新格式: AI{dd}_CH{dd}
        var m = System.Text.RegularExpressions.Regex.Match(name,
            @"AI(\d+)_CH(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var dev)
            && int.TryParse(m.Groups[2].Value, out var ch))
        {
            return dev * 100 + ch;
        }

        // 兼容旧格式: AI{dd}-{dd}
        m = System.Text.RegularExpressions.Regex.Match(name,
            @"AI(\d+)-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out dev)
            && int.TryParse(m.Groups[2].Value, out ch))
        {
            return dev * 100 + ch;
        }

        // 退化：提取所有数字
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var id) && id >= 0) return id;

        return -1;
    }

    /// <summary>
    /// 从通道名解析设备ID
    /// </summary>
    public static int ParseDeviceId(string name)
    {
        var channelId = ParseChannelName(name);
        return channelId >= 0 ? GetDeviceId(channelId) : -1;
    }
}
