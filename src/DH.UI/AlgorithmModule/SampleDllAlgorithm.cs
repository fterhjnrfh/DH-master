using System;
using System.Collections.Generic;
using System.Linq;

namespace AlgorithmModule
{
    /// <summary>
    /// 示例 DLL 算法实现
    /// 这个类可以直接编译成 DLL 文件，供主程序加载使用
    /// </summary>
    public class SampleDllAlgorithm : IAlgorithm
    {
        public string Name => "SampleDllAlgorithm";
        public string Description => "示例 DLL 算法 - 简单移动平均";
        public string Version => "1.0.0";
        public string Author => "Example";

        public Dictionary<string, object> DefaultParameters => new Dictionary<string, object>
        {
            { "windowSize", 5 },
            { "multiplier", 1.0 }
        };

        public object Process(object input, IDictionary<string, object>? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (input is not double[] inputArray)
            {
                throw new ArgumentException("Input must be of type double[]", nameof(input));
            }

            // 获取参数
            int windowSize = GetParameterValue(parameters, "windowSize", 5);
            double multiplier = GetParameterValue(parameters, "multiplier", 1.0);

            // 参数验证
            if (windowSize < 1) windowSize = 1;

            return ProcessInternal(inputArray, windowSize, multiplier);
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

        private double[] ProcessInternal(double[] input, int windowSize, double multiplier)
        {
            if (input.Length == 0)
            {
                return Array.Empty<double>();
            }

            var output = new double[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                double sum = 0;
                int count = 0;
                
                // 计算窗口内的平均值
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(input.Length - 1, i + windowSize / 2);
                
                for (int j = start; j <= end; j++)
                {
                    sum += input[j];
                    count++;
                }
                
                double average = count > 0 ? sum / count : input[i];
                output[i] = average * multiplier;
            }

            return output;
        }
    }
    
    /// <summary>
    /// 或者使用静态方法的实现方式（不需要实现 IAlgorithm 接口）
    /// 这种方式更简单，只需要确保有正确签名的 Process 方法
    /// </summary>
    public static class SimpleMovingAverageDll
    {
        /// <summary>
        /// 处理方法，必须有这个签名才能被主程序加载
        /// </summary>
        /// <param name="input">输入的 double 数组</param>
        /// <param name="parameters">参数字典</param>
        /// <returns>处理后的 double 数组</returns>
        public static double[] Process(double[] input, IDictionary<string, object>? parameters)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (input.Length == 0) return Array.Empty<double>();
            
            // 从参数中获取窗口大小，默认为 5
            int windowSize = 5;
            if (parameters != null && parameters.ContainsKey("windowSize"))
            {
                windowSize = Convert.ToInt32(parameters["windowSize"]);
            }
            
            if (windowSize < 1) windowSize = 1;
            
            var output = new double[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                double sum = 0;
                int count = 0;
                
                // 计算窗口内的平均值
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(input.Length - 1, i + windowSize / 2);
                
                for (int j = start; j <= end; j++)
                {
                    sum += input[j];
                    count++;
                }
                
                output[i] = count > 0 ? sum / count : input[i];
            }
            
            return output;
        }
    }
}