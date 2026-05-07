using System;

namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// 数据预处理工具：在压缩前对采样数据进行变换以降低冗余度，提升压缩率。
    /// 支持一阶差分、二阶差分和线性预测编码。
    /// </summary>
    internal static class DataPreprocessor
    {
        /// <summary>
        /// 对数据应用指定的预处理变换
        /// </summary>
        public static double[] Apply(double[] data, PreprocessType type)
        {
            if (data == null || data.Length <= 1)
                return data ?? Array.Empty<double>();

            return type switch
            {
                PreprocessType.DiffOrder1 => ApplyDiffOrder1(data),
                PreprocessType.DiffOrder2 => ApplyDiffOrder2(data),
                PreprocessType.LinearPrediction => ApplyLinearPrediction(data),
                _ => data,
            };
        }

        /// <summary>
        /// 对数据应用逆预处理变换（恢复原始数据）
        /// </summary>
        public static double[] Reverse(double[] data, PreprocessType type)
        {
            if (data == null || data.Length <= 1)
                return data ?? Array.Empty<double>();

            return type switch
            {
                PreprocessType.DiffOrder1 => ReverseDiffOrder1(data),
                PreprocessType.DiffOrder2 => ReverseDiffOrder2(data),
                PreprocessType.LinearPrediction => ReverseLinearPrediction(data),
                _ => data,
            };
        }

        #region 一阶差分编码
        /// <summary>
        /// 一阶差分编码：result[0] = data[0]；result[i] = data[i] - data[i-1]
        /// </summary>
        private static double[] ApplyDiffOrder1(double[] data)
        {
            var result = new double[data.Length];
            result[0] = data[0];
            for (int i = 1; i < data.Length; i++)
                result[i] = data[i] - data[i - 1];
            return result;
        }

        /// <summary>
        /// 一阶差分解码（逆变换）：data[0] = result[0]；data[i] = data[i-1] + result[i]
        /// </summary>
        private static double[] ReverseDiffOrder1(double[] data)
        {
            var result = new double[data.Length];
            result[0] = data[0];
            for (int i = 1; i < data.Length; i++)
                result[i] = result[i - 1] + data[i];
            return result;
        }
        #endregion

        #region 二阶差分编码
        /// <summary>
        /// 二阶差分编码：对数据连续做两次一阶差分
        /// </summary>
        private static double[] ApplyDiffOrder2(double[] data)
        {
            var first = ApplyDiffOrder1(data);
            return ApplyDiffOrder1(first);
        }

        /// <summary>
        /// 二阶差分解码：对数据连续做两次一阶差分的逆变换
        /// </summary>
        private static double[] ReverseDiffOrder2(double[] data)
        {
            var first = ReverseDiffOrder1(data);
            return ReverseDiffOrder1(first);
        }
        #endregion

        #region 线性预测编码
        /// <summary>
        /// 线性预测编码：利用前两个样本的线性外推值作为预测，存储残差。
        /// result[0] = data[0]（保留首样本）
        /// result[1] = data[1] - data[0]（首次差分）
        /// result[i] = data[i] - (2*data[i-1] - data[i-2])（线性外推残差），i >= 2
        /// </summary>
        private static double[] ApplyLinearPrediction(double[] data)
        {
            var result = new double[data.Length];
            result[0] = data[0];
            if (data.Length > 1)
                result[1] = data[1] - data[0];
            for (int i = 2; i < data.Length; i++)
            {
                double predicted = 2.0 * data[i - 1] - data[i - 2];
                result[i] = data[i] - predicted;
            }
            return result;
        }

        /// <summary>
        /// 线性预测解码（逆变换）：从残差恢复原始数据
        /// data[0] = result[0]
        /// data[1] = data[0] + result[1]
        /// data[i] = (2*data[i-1] - data[i-2]) + result[i]，i >= 2
        /// </summary>
        private static double[] ReverseLinearPrediction(double[] data)
        {
            var result = new double[data.Length];
            result[0] = data[0];
            if (data.Length > 1)
                result[1] = result[0] + data[1];
            for (int i = 2; i < data.Length; i++)
            {
                double predicted = 2.0 * result[i - 1] - result[i - 2];
                result[i] = predicted + data[i];
            }
            return result;
        }
        #endregion
    }
}
