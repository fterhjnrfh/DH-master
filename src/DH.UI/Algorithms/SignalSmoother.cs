using System;
using System.Collections.Generic;
using NewAvalonia.Models;

namespace NewAvalonia.Algorithms
{
    /// <summary>
    /// 信号平滑算法（使用Savitzky-Golay滤波器的简化版本）
    /// </summary>
    public class SignalSmoother : AlgorithmModuleBase
    {
        private int _windowSize = 5; // 窗口大小，必须为奇数
        private double _smoothness = 0.5; // 平滑系数
        
        public override string Name => "信号平滑";
        
        public override string Description => "使用加权平均对信号进行平滑处理";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();
                
            // 确保窗口大小为奇数且在合理范围内
            int effectiveWindowSize = Math.Max(3, _windowSize);
            if (effectiveWindowSize % 2 == 0) effectiveWindowSize++; // 确保为奇数
            effectiveWindowSize = Math.Min(effectiveWindowSize, inputData.Length);
            
            var result = new double[inputData.Length];
            
            for (int i = 0; i < inputData.Length; i++)
            {
                // 计算当前点的平滑值
                double sum = 0;
                double weightSum = 0;
                
                int halfWindow = effectiveWindowSize / 2;
                
                for (int j = 0; j < effectiveWindowSize; j++)
                {
                    int dataIndex = i - halfWindow + j;
                    
                    // 边界处理：使用边界值
                    if (dataIndex < 0)
                        dataIndex = 0;
                    else if (dataIndex >= inputData.Length)
                        dataIndex = inputData.Length - 1;
                    
                    // 计算权重：距离越近权重越大
                    double distance = Math.Abs(j - halfWindow) / (double)halfWindow;
                    double weight = 1.0 - (_smoothness * distance); // 使用平滑系数调整权重分布
                    
                    sum += inputData[dataIndex] * weight;
                    weightSum += weight;
                }
                
                result[i] = sum / weightSum;
            }
            
            return result;
        }
        
        public override Dictionary<string, object> GetParameters()
        {
            return new Dictionary<string, object>
            {
                { "WindowSize", _windowSize },
                { "Smoothness", _smoothness }
            };
        }
        
        public override void SetParameters(Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey("WindowSize"))
            {
                _windowSize = Convert.ToInt32(parameters["WindowSize"]);
                if (_windowSize % 2 == 0) _windowSize++; // 确保为奇数
                if (_windowSize < 3) _windowSize = 3;
                if (_windowSize > 15) _windowSize = 15;
            }
            
            if (parameters.ContainsKey("Smoothness"))
            {
                _smoothness = Convert.ToDouble(parameters["Smoothness"]);
                if (_smoothness < 0.0) _smoothness = 0.0;
                if (_smoothness > 1.0) _smoothness = 1.0;
            }
        }
    }
}