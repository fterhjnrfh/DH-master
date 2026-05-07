using System;
using System.Collections.Generic;
using System.Linq;
using AlgorithmModule; // 引用算法模块

namespace NewAvalonia.Services
{
    /// <summary>
    /// 算法管理器，用于处理动态库中的算法
    /// </summary>
    public class AlgorithmManager
    {
        /// <summary>
        /// 获取所有可用的算法
        /// </summary>
        /// <returns>算法列表</returns>
        public static List<IAlgorithm> GetAllAlgorithms()
        {
            return AlgorithmFactory.GetAllAlgorithms();
        }

        /// <summary>
        /// 根据名称获取算法
        /// </summary>
        /// <param name="name">算法名称</param>
        /// <returns>算法实例，如果不存在则返回null</returns>
        public static IAlgorithm? GetAlgorithm(string name)
        {
            return AlgorithmFactory.GetAlgorithm(name);
        }

        /// <summary>
        /// 检查算法是否存在
        /// </summary>
        /// <param name="name">算法名称</param>
        /// <returns>如果存在返回true，否则返回false</returns>
        public static bool HasAlgorithm(string name)
        {
            return AlgorithmFactory.HasAlgorithm(name);
        }

        /// <summary>
        /// 执行算法
        /// </summary>
        /// <param name="algorithmName">算法名称</param>
        /// <param name="input">输入数据</param>
        /// <param name="parameters">算法参数</param>
        /// <returns>处理结果</returns>
        public static object ExecuteAlgorithm(string algorithmName, object input, Dictionary<string, object>? parameters = null)
        {
            var algorithm = AlgorithmFactory.GetAlgorithm(algorithmName);
            if (algorithm == null)
            {
                throw new ArgumentException($"Algorithm '{algorithmName}' not found.");
            }

            return algorithm.Process(input, parameters);
        }
    }
}