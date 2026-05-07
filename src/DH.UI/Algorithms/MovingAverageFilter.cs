using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.Models;

namespace NewAvalonia.Algorithms
{
    /// <summary>
    /// 简单移动平均滤波器算法
    /// </summary>
    public class MovingAverageFilter : AlgorithmModuleBase
    {
        private int _windowSize = 5;
        
        public override string Name => "移动平均滤波器";
        
        public override string Description => "使用移动平均算法平滑信号，减少噪声";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();
                
            var result = new double[inputData.Length];
            
            for (int i = 0; i < inputData.Length; i++)
            {
                double sum = 0;
                int count = 0;
                
                // 计算窗口内的平均值
                for (int j = Math.Max(0, i - _windowSize / 2); 
                     j <= Math.Min(inputData.Length - 1, i + _windowSize / 2); 
                     j++)
                {
                    sum += inputData[j];
                    count++;
                }
                
                result[i] = sum / count;
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
                // 确保窗口大小为奇数
                if (_windowSize % 2 == 0)
                    _windowSize++;
                    
                // 确保窗口大小至少为1
                if (_windowSize < 1)
                    _windowSize = 1;
            }
        }
    }
}