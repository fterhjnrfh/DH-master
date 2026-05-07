namespace DH.Configmanage.MockConfig;

public sealed class MockConfig
{
    /*
    奈奎斯特采样间隔
     SampleRate > MockSignalFrequency * 20
    */

    /// <summary>采样率 (Hz)，全局统一</summary>
    public int SampleRate { get; set; } = 100;

    /// <summary>单帧样本数量</summary>
    public int FrameSize { get; set; } = 500;

    /// <summary>默认显示窗口时长 (秒)</summary>
    public int WindowSeconds { get; set; } = 5;

    /// <summary>信号频率 (Hz)，用于 MockDevice</summary>
    public double MockSignalFrequency { get; set; } = 5;

    /// <summary>信号振幅</summary>
    public double MockAmplitude { get; set; } = 1.0;

    // 单例模式（或用依赖注入）
    private static readonly Lazy<MockConfig> _instance = new(() => new MockConfig());
    public static MockConfig Instance => _instance.Value;
    private MockConfig() { }
}
