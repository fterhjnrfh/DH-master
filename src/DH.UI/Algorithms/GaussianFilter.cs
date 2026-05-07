using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.Models;

namespace NewAvalonia.Algorithms
{
    /// <summary>
    /// 高斯滤波算法
    /// </summary>
    public class GaussianFilter : AlgorithmModuleBase
    {
        private int _kernelSize = 5; // 核大小，必须为奇数
        private double _sigma = 1.0; // 高斯函数的标准差
        
        public override string Name => "高斯滤波";
        
        public override string Description => "使用高斯核对信号进行平滑滤波";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();
                
            // 确保核大小为奇数且在合理范围内
            int effectiveKernelSize = Math.Max(1, _kernelSize);
            if (effectiveKernelSize % 2 == 0) effectiveKernelSize++; // 确保为奇数
            effectiveKernelSize = Math.Min(effectiveKernelSize, inputData.Length);
            
            var result = new double[inputData.Length];
            var kernel = GenerateGaussianKernel(effectiveKernelSize, _sigma);
            
            // 应用卷积
            for (int i = 0; i < inputData.Length; i++)
            {
                double sum = 0;
                double weightSum = 0;
                
                int halfKernel = effectiveKernelSize / 2;
                
                for (int j = 0; j < effectiveKernelSize; j++)
                {
                    int dataIndex = i - halfKernel + j;
                    
                    // 边界处理：使用镜像方式
                    if (dataIndex < 0)
                        dataIndex = -dataIndex;
                    else if (dataIndex >= inputData.Length)
                        dataIndex = 2 * inputData.Length - dataIndex - 2;
                    
                    sum += inputData[dataIndex] * kernel[j];
                    weightSum += kernel[j];
                }
                
                result[i] = sum / weightSum;
            }
            
            return result;
        }
        
        private double[] GenerateGaussianKernel(int size, double sigma)
        {
            var kernel = new double[size];
            int center = size / 2;
            double sum = 0;
            
            for (int i = 0; i < size; i++)
            {
                double x = i - center;
                kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
                sum += kernel[i];
            }
            
            // 归一化
            for (int i = 0; i < size; i++)
            {
                kernel[i] /= sum;
            }
            
            return kernel;
        }
        
        public override Dictionary<string, object> GetParameters()
        {
            return new Dictionary<string, object>
            {
                { "KernelSize", _kernelSize },
                { "Sigma", _sigma }
            };
        }
        
        public override void SetParameters(Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey("KernelSize"))
            {
                _kernelSize = Convert.ToInt32(parameters["KernelSize"]);
                if (_kernelSize % 2 == 0) _kernelSize++; // 确保为奇数
                if (_kernelSize < 1) _kernelSize = 1;
                if (_kernelSize > 19) _kernelSize = 19; // 限制最大大小
            }
            
            if (parameters.ContainsKey("Sigma"))
            {
                _sigma = Convert.ToDouble(parameters["Sigma"]);
                if (_sigma < 0.1) _sigma = 0.1; // 最小值
                if (_sigma > 5.0) _sigma = 5.0; // 最大值
            }
        }
    }
}