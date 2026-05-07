using System;
using System.Collections.Generic;
using System.Linq;

namespace AlgorithmModule
{
    /// <summary>
    /// 高斯平滑算法实现
    /// </summary>
    public class GaussianLikeSmoothAlgorithm : IAlgorithm
    {
        public string Name => "GaussianLikeSmooth";
        public string Description => "近似高斯平滑（一维可分离核，标准差可调）";
        public string Version => "1.0.0";
        public string Author => "Demo";

        public Dictionary<string, object> DefaultParameters => new Dictionary<string, object>
        {
            { "sigma", 5.0 },
            { "kernelSize", 31 }
        };

        public object Process(object input, IDictionary<string, object>? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (input is not double[] inputArray)
            {
                throw new ArgumentException("Input must be of type double[]", nameof(input));
            }

            // 获取参数
            double sigma = GetParameterValue(parameters, "sigma", 5.0);
            int kernelSize = GetParameterValue(parameters, "kernelSize", 31);

            // 参数验证
            if (kernelSize < 5) kernelSize = 5;
            if (kernelSize % 2 == 0) kernelSize++; // 确保核大小为奇数

            return ProcessInternal(inputArray, sigma, kernelSize);
        }

        private static T GetParameterValue<T>(IDictionary<string, object>? parameters, string paramName, T defaultValue)
        {
            if (parameters == null)
            {
                return defaultValue;
            }

            if (parameters.TryGetValue(paramName, out var value) && value is not null)
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }

            return defaultValue;
        }

        private double[] ProcessInternal(double[] input, double sigma, int kernelSize)
        {
            if (input.Length == 0)
            {
                return Array.Empty<double>();
            }

            int half = kernelSize / 2;
            // 构建一维高斯核
            double twoSigma2 = 2 * sigma * sigma;
            var kernel = new double[kernelSize];
            double sum = 0;
            for (int i = -half, idx = 0; i <= half; i++, idx++)
            {
                double v = Math.Exp(-(i * i) / twoSigma2);
                kernel[idx] = v;
                sum += v;
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

            // 边界处理：镜像法
            double Mirror(double[] arr, int index)
            {
                if (index < 0) return arr[-index];
                if (index >= arr.Length) return arr[2 * arr.Length - index - 2];
                return arr[index];
            }

            var output = new double[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                double acc = 0;
                for (int k = -half, idx = 0; k <= half; k++, idx++)
                {
                    acc += kernel[idx] * Mirror(input, i + k);
                }
                output[i] = acc;
            }

            return output;
        }
    }
}