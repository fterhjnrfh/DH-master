using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.Models;

namespace NewAvalonia.Algorithms
{
    /// <summary>
    /// 中值滤波算法
    /// </summary>
    public class MedianFilter : AlgorithmModuleBase
    {
        private int _windowSize = 3; // 窗口大小，必须为奇数
        
        public override string Name => "中值滤波";
        
        public override string Description => "使用中值滤波去除信号中的噪声";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();
                
            // 确保窗口大小为奇数且在合理范围内
            int effectiveWindowSize = Math.Max(1, _windowSize);
            if (effectiveWindowSize % 2 == 0) effectiveWindowSize++; // 确保为奇数
            effectiveWindowSize = Math.Min(effectiveWindowSize, inputData.Length);
            
            var result = new double[inputData.Length];
            
            for (int i = 0; i < inputData.Length; i++)
            {
                // 创建当前窗口的数据副本
                var windowValues = new List<double>();
                
                int halfWindow = effectiveWindowSize / 2;
                
                for (int j = -halfWindow; j <= halfWindow; j++)
                {
                    int dataIndex = i + j;
                    
                    // 边界处理：使用镜像方式
                    if (dataIndex < 0)
                        dataIndex = -dataIndex;
                    else if (dataIndex >= inputData.Length)
                        dataIndex = 2 * inputData.Length - dataIndex - 2;
                    
                    windowValues.Add(inputData[dataIndex]);
                }
                
                // 对窗口数据排序并取中值
                windowValues.Sort();
                int midIndex = windowValues.Count / 2;
                result[i] = windowValues[midIndex];
            }
            
            return result;
        }
        
        public override Dictionary<string, object> GetParameters()
        {
            return new Dictionary<string, object>
            {
                { "WindowSize", _windowSize }
            };
        }
        
        public override void SetParameters(Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey("WindowSize"))
            {
                _windowSize = Convert.ToInt32(parameters["WindowSize"]);
                if (_windowSize % 2 == 0) _windowSize++; // 确保为奇数
                if (_windowSize < 1) _windowSize = 1;
                if (_windowSize > 19) _windowSize = 19; // 限制最大大小
            }
        }
    }
}