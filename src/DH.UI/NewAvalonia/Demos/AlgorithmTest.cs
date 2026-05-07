using System;
using System.Collections.Generic;
using System.Linq;
using AlgorithmModule;
using NewAvalonia.Services;

namespace NewAvalonia.Demos
{
    /// <summary>
    /// 演示如何使用算法模块库
    /// </summary>
    public class AlgorithmTest
    {
        /// <summary>
        /// 测试高斯平滑算法
        /// </summary>
        public static void TestGaussianSmooth()
        {
            // 创建一些示例数据
            var inputData = new double[] { 
                1.0, 2.1, 2.9, 4.2, 4.8, 6.1, 7.2, 7.9, 9.1, 10.0,
                10.2, 9.8, 8.1, 7.2, 5.9, 4.1, 3.2, 2.1, 1.0, 0.5 
            };

            // 使用算法管理器执行高斯平滑算法
            var parameters = new Dictionary<string, object>
            {
                { "sigma", 2.0 },
                { "kernelSize", 15 }
            };

            try
            {
                var result = AlgorithmManager.ExecuteAlgorithm("GaussianLikeSmooth", inputData, parameters);
                
                if (result is double[] outputData)
                {
                    Console.WriteLine("高斯平滑算法执行成功！");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"算法执行失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示所有可用的算法
        /// </summary>
        public static void ShowAlgorithms()
        {
            var algorithms = AlgorithmManager.GetAllAlgorithms();
            foreach (var algorithm in algorithms)
            {
                Console.WriteLine($"可用算法: {algorithm.Name}");
            }
        }
    }
}