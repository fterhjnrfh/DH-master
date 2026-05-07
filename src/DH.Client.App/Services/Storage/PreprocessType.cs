namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// 数据预处理技术类型，用于在压缩前降低数据冗余度，提升压缩率
    /// </summary>
    public enum PreprocessType
    {
        /// <summary>
        /// 不进行预处理
        /// </summary>
        None = 0,

        /// <summary>
        /// 一阶差分编码：d[i] = x[i] - x[i-1]，适用于缓变信号
        /// </summary>
        DiffOrder1 = 1,

        /// <summary>
        /// 二阶差分编码：对一阶差分结果再做一次差分，适用于线性趋势信号
        /// </summary>
        DiffOrder2 = 2,

        /// <summary>
        /// 线性预测编码（LPC）：利用前两个样本线性外推预测，存储残差。适用于平滑连续信号
        /// </summary>
        LinearPrediction = 3
    }
}
