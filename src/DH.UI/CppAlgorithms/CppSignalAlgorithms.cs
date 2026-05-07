using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NewAvalonia.Models;

namespace NewAvalonia.Algorithms.CppSignalAlgorithms
{
    /// <summary>
    /// C++移动平均滤波器算法
    /// </summary>
    public class CppMovingAverageFilter : AlgorithmModuleBase
    {
        [DllImport("SignalAlgorithmsCpp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int moving_average_filter(
            [In] double[] input, 
            int length, 
            [In, Out] double[] output);

        private int _windowSize = 5;
        
        public override string Name => "C++移动平均滤波器";
        
        public override string Description => "使用C++实现的移动平均算法平滑信号，减少噪声";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();

            var result = new double[inputData.Length];
            // 复制输入数据到结果数组
            Array.Copy(inputData, result, inputData.Length);
            
            try
            {
                // 调用C++实现的滤波函数
                int status = moving_average_filter(inputData, inputData.Length, result);
                if (status != 0)
                {
                    System.Console.WriteLine($"C++移动平均滤波器执行失败，返回值: {status}");
                    // 如果C++函数失败，回退到C#实现
                    return FallbackProcess(inputData);
                }
            }
            catch (DllNotFoundException)
            {
                System.Console.WriteLine("SignalAlgorithmsCpp.dll 未找到，使用C#实现");
                return FallbackProcess(inputData);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"调用C++移动平均滤波器时出错: {ex.Message}");
                return FallbackProcess(inputData);
            }

            return result;
        }
        
        /// <summary>
        /// C#回退实现
        /// </summary>
        private double[] FallbackProcess(double[] inputData)
        {
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

    /// <summary>
    /// C++高斯滤波器算法
    /// </summary>
    public class CppGaussianFilter : AlgorithmModuleBase
    {
        [DllImport("SignalAlgorithmsCpp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gaussian_filter(
            [In] double[] input, 
            int length, 
            double sigma, 
            int kernel_size, 
            [In, Out] double[] output);

        private double _sigma = 1.0;
        private int _kernelSize = 5;
        
        public override string Name => "C++高斯滤波器";
        
        public override string Description => "使用C++实现的高斯算法平滑信号，减少噪声";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();

            var result = new double[inputData.Length];
            // 复制输入数据到结果数组
            Array.Copy(inputData, result, inputData.Length);
            
            try
            {
                // 调用C++实现的滤波函数
                int status = gaussian_filter(inputData, inputData.Length, _sigma, _kernelSize, result);
                if (status != 0)
                {
                    System.Console.WriteLine($"C++高斯滤波器执行失败，返回值: {status}");
                    // 如果C++函数失败，回退到C#实现
                    return FallbackProcess(inputData);
                }
            }
            catch (DllNotFoundException)
            {
                System.Console.WriteLine("SignalAlgorithmsCpp.dll 未找到，使用C#实现");
                return FallbackProcess(inputData);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"调用C++高斯滤波器时出错: {ex.Message}");
                return FallbackProcess(inputData);
            }

            return result;
        }
        
        /// <summary>
        /// C#回退实现
        /// </summary>
        private double[] FallbackProcess(double[] inputData)
        {
            var kernel = GenerateGaussianKernel(_kernelSize, _sigma);
            var result = new double[inputData.Length];
            
            int halfKernel = _kernelSize / 2;
            
            for (int i = 0; i < inputData.Length; i++)
            {
                double sum = 0;
                
                for (int j = 0; j < _kernelSize; j++)
                {
                    int idx = i - halfKernel + j;
                    
                    // 边界处理
                    if (idx < 0) idx = -idx;
                    else if (idx >= inputData.Length) idx = 2 * inputData.Length - idx - 2;
                    
                    if (idx >= 0 && idx < inputData.Length)
                    {
                        sum += inputData[idx] * kernel[j];
                    }
                }
                
                result[i] = sum;
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
                { "Sigma", _sigma },
                { "KernelSize", _kernelSize }
            };
        }
        
        public override void SetParameters(Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey("Sigma"))
            {
                _sigma = Convert.ToDouble(parameters["Sigma"]);
                if (_sigma <= 0) _sigma = 1.0;
            }
            
            if (parameters.ContainsKey("KernelSize"))
            {
                _kernelSize = Convert.ToInt32(parameters["KernelSize"]);
                // 确保核大小为奇数
                if (_kernelSize % 2 == 0) _kernelSize++;
                if (_kernelSize < 1) _kernelSize = 3;
                if (_kernelSize > 21) _kernelSize = 21; // 限制最大尺寸
            }
        }
    }

    /// <summary>
    /// C++中值滤波器算法
    /// </summary>
    public class CppMedianFilter : AlgorithmModuleBase
    {
        [DllImport("SignalAlgorithmsCpp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int median_filter(
            [In] double[] input, 
            int length, 
            [In, Out] double[] output);

        private int _windowSize = 3;
        
        public override string Name => "C++中值滤波器";
        
        public override string Description => "使用C++实现的中值算法平滑信号，减少噪声";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();

            var result = new double[inputData.Length];
            // 复制输入数据到结果数组
            Array.Copy(inputData, result, inputData.Length);
            
            try
            {
                // 调用C++实现的滤波函数
                int status = median_filter(inputData, inputData.Length, result);
                if (status != 0)
                {
                    System.Console.WriteLine($"C++中值滤波器执行失败，返回值: {status}");
                    // 如果C++函数失败，回退到C#实现
                    return FallbackProcess(inputData);
                }
            }
            catch (DllNotFoundException)
            {
                System.Console.WriteLine("SignalAlgorithmsCpp.dll 未找到，使用C#实现");
                return FallbackProcess(inputData);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"调用C++中值滤波器时出错: {ex.Message}");
                return FallbackProcess(inputData);
            }

            return result;
        }
        
        /// <summary>
        /// C#回退实现
        /// </summary>
        private double[] FallbackProcess(double[] inputData)
        {
            var result = new double[inputData.Length];
            int halfWindow = _windowSize / 2;
            
            for (int i = 0; i < inputData.Length; i++)
            {
                var window = new List<double>();
                
                // 构建窗口
                for (int j = -halfWindow; j <= halfWindow; j++)
                {
                    int idx = i + j;
                    
                    // 边界处理
                    if (idx < 0) idx = -idx;
                    else if (idx >= inputData.Length) 
                        idx = 2 * inputData.Length - idx - 2;
                    
                    if (idx >= 0 && idx < inputData.Length)
                    {
                        window.Add(inputData[idx]);
                    }
                }
                
                // 排序并取中值
                window.Sort();
                result[i] = window[window.Count / 2];
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
                if (_windowSize % 2 == 0) _windowSize++;
                if (_windowSize < 1) _windowSize = 3;
                if (_windowSize > 19) _windowSize = 19; // 限制最大尺寸
            }
        }
    }

    /// <summary>
    /// C++信号平滑算法
    /// </summary>
    public class CppSignalSmoother : AlgorithmModuleBase
    {
        [DllImport("SignalAlgorithmsCpp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int signal_smooth(
            [In] double[] input, 
            int length, 
            [In, Out] double[] output);

        private int _windowSize = 5;
        private double _smoothness = 0.5;
        
        public override string Name => "C++信号平滑";
        
        public override string Description => "使用C++实现的信号平滑算法";
        
        public override double[] Process(double[] inputData)
        {
            ArgumentNullException.ThrowIfNull(inputData);
            if (inputData.Length == 0)
                return Array.Empty<double>();

            var result = new double[inputData.Length];
            // 复制输入数据到结果数组
            Array.Copy(inputData, result, inputData.Length);
            
            try
            {
                // 调用C++实现的平滑函数
                int status = signal_smooth(inputData, inputData.Length, result);
                if (status != 0)
                {
                    System.Console.WriteLine($"C++信号平滑执行失败，返回值: {status}");
                    // 如果C++函数失败，回退到C#实现
                    return FallbackProcess(inputData);
                }
            }
            catch (DllNotFoundException)
            {
                System.Console.WriteLine("SignalAlgorithmsCpp.dll 未找到，使用C#实现");
                return FallbackProcess(inputData);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"调用C++信号平滑时出错: {ex.Message}");
                return FallbackProcess(inputData);
            }

            return result;
        }
        
        /// <summary>
        /// C#回退实现
        /// </summary>
        private double[] FallbackProcess(double[] inputData)
        {
            var result = new double[inputData.Length];
            int halfWindow = _windowSize / 2;
            
            for (int i = 0; i < inputData.Length; i++)
            {
                double sum = 0;
                double weightSum = 0;
                
                for (int j = 0; j < _windowSize; j++)
                {
                    int idx = i - halfWindow + j;
                    double distance = Math.Abs(j - halfWindow) / (double)halfWindow;
                    double weight = 1.0 - (_smoothness * distance);
                    
                    // 边界处理
                    if (idx < 0) idx = -idx;
                    else if (idx >= inputData.Length) 
                        idx = 2 * inputData.Length - idx - 2;
                    
                    if (idx >= 0 && idx < inputData.Length)
                    {
                        sum += inputData[idx] * weight;
                        weightSum += weight;
                    }
                }
                
                if (weightSum > 0)
                {
                    result[i] = sum / weightSum;
                }
                else
                {
                    result[i] = inputData[i]; // 保持原值
                }
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
                // 确保窗口大小为奇数
                if (_windowSize % 2 == 0) _windowSize++;
                if (_windowSize < 1) _windowSize = 3;
                if (_windowSize > 19) _windowSize = 19;
            }
            
            if (parameters.ContainsKey("Smoothness"))
            {
                _smoothness = Convert.ToDouble(parameters["Smoothness"]);
                if (_smoothness < 0) _smoothness = 0;
                if (_smoothness > 1) _smoothness = 1;
            }
        }
    }
}