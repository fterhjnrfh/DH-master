using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    /// <summary>
    /// 算法模块加载服务
    /// </summary>
    public class AlgorithmModuleLoader
    {
        private readonly List<IAlgorithmModule> _loadedModules = new List<IAlgorithmModule>();
        
        /// <summary>
        /// 从程序集加载算法模块
        /// </summary>
        /// <param name="assemblyPath">程序集路径</param>
        public void LoadFromAssembly(string assemblyPath)
        {
            try
            {
                if (!File.Exists(assemblyPath))
                {
                    throw new FileNotFoundException($"Assembly file not found: {assemblyPath}");
                }
                
                var assembly = Assembly.LoadFrom(assemblyPath);
                LoadFromAssembly(assembly);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading assembly {assemblyPath}: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// 从程序集对象加载算法模块
        /// </summary>
        /// <param name="assembly">程序集对象</param>
        public void LoadFromAssembly(Assembly assembly)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && 
                           typeof(IAlgorithmModule).IsAssignableFrom(t));
                
                foreach (var type in types)
                {
                    var module = Activator.CreateInstance(type) as IAlgorithmModule;
                    if (module != null)
                    {
                        _loadedModules.Add(module);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading modules from assembly: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// 从当前程序集加载内置算法模块
        /// </summary>
        public void LoadBuiltInModules()
        {
            // 加载C#算法模块
            _loadedModules.Add(new NewAvalonia.Algorithms.MovingAverageFilter());
            _loadedModules.Add(new NewAvalonia.Algorithms.GaussianFilter());
            _loadedModules.Add(new NewAvalonia.Algorithms.MedianFilter());
            _loadedModules.Add(new NewAvalonia.Algorithms.SignalSmoother());
            
            // 尝试加载C++算法模块（如果DLL存在）
            LoadCppAlgorithmsSafely();
        }
        
        private void LoadCppAlgorithmsSafely()
        {
            try 
            {
                // 尝试创建C++算法模块实例，如果类型不存在会抛出异常
                var cppMovingAvg = new NewAvalonia.Algorithms.CppSignalAlgorithms.CppMovingAverageFilter();
                var cppGaussian = new NewAvalonia.Algorithms.CppSignalAlgorithms.CppGaussianFilter();
                var cppMedian = new NewAvalonia.Algorithms.CppSignalAlgorithms.CppMedianFilter();
                var cppSmoother = new NewAvalonia.Algorithms.CppSignalAlgorithms.CppSignalSmoother();
                
                // 如果创建实例成功，再检查DLL是否可用
                if (IsCppDllAvailable())
                {
                    _loadedModules.Add(cppMovingAvg);
                    _loadedModules.Add(cppGaussian);
                    _loadedModules.Add(cppMedian);
                    _loadedModules.Add(cppSmoother);
                }
                else
                {
                    System.Console.WriteLine("C++算法库未找到，仅加载C#算法");
                }
            }
            catch (Exception ex) when (ex is TypeLoadException || ex is DllNotFoundException)
            {
                // C++算法类型不存在或DLL未找到，只使用C#算法
                string errorType = ex.GetType().Name;
                System.Console.WriteLine($"C++算法{errorType}，仅加载C#算法模块。详情: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"加载C++算法时出错: {ex.Message}");
                System.Console.WriteLine("继续使用C#算法模块");
            }
        }
        
        [DllImport("SignalAlgorithmsCpp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gaussian_filter(
            [In] double[] input, 
            int length, 
            double sigma, 
            int kernel_size, 
            [In, Out] double[] output);
        
        private bool IsCppDllAvailable()
        {
            try
            {
                // 尝试调用一个简单的函数来检查DLL是否可用
                var testArray = new double[1];
                var outputArray = new double[1];
                // 尝试调用一个函数来验证DLL是否可用
                gaussian_filter(testArray, 1, 1.0, 3, outputArray);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// 获取所有已加载的算法模块
        /// </summary>
        /// <returns>算法模块列表</returns>
        public List<IAlgorithmModule> GetModules()
        {
            return new List<IAlgorithmModule>(_loadedModules);
        }
        
        /// <summary>
        /// 根据名称获取特定算法模块
        /// </summary>
        /// <param name="name">模块名称</param>
        /// <returns>算法模块，如果未找到则返回null</returns>
        public IAlgorithmModule? GetModuleByName(string name)
        {
            return _loadedModules.FirstOrDefault(m => m.Name == name);
        }
        
        /// <summary>
        /// 清空所有已加载的模块
        /// </summary>
        public void Clear()
        {
            _loadedModules.Clear();
        }
    }
}