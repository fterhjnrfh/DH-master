using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AlgorithmModule
{
    /// <summary>
    /// 算法工厂，用于创建和管理算法实例
    /// </summary>
    public static class AlgorithmFactory
    {
        private static readonly Dictionary<string, IAlgorithm> _algorithms = new();

        static AlgorithmFactory()
        {
            // 自动发现并注册所有 IAlgorithm 实现
            RegisterAlgorithms();
        }

        /// <summary>
        /// 注册所有算法
        /// </summary>
        private static void RegisterAlgorithms()
        {
            var algorithmTypes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IAlgorithm).IsAssignableFrom(t));

            foreach (var type in algorithmTypes)
            {
                if (Activator.CreateInstance(type) is IAlgorithm algorithm)
                {
                    _algorithms[algorithm.Name] = algorithm;
                }
            }
        }

        /// <summary>
        /// 获取指定名称的算法实例
        /// </summary>
        /// <param name="algorithmName">算法名称</param>
        /// <returns>算法实例，如果不存在则返回null</returns>
        public static IAlgorithm? GetAlgorithm(string? algorithmName)
        {
            if (string.IsNullOrWhiteSpace(algorithmName))
            {
                return null;
            }

            return _algorithms.TryGetValue(algorithmName, out var algorithm) ? algorithm : null;
        }

        /// <summary>
        /// 获取所有可用的算法
        /// </summary>
        /// <returns>所有算法的列表</returns>
        public static List<IAlgorithm> GetAllAlgorithms()
        {
            return _algorithms.Values.ToList();
        }

        /// <summary>
        /// 检查算法是否存在
        /// </summary>
        /// <param name="algorithmName">算法名称</param>
        /// <returns>如果算法存在返回true，否则返回false</returns>
        public static bool HasAlgorithm(string? algorithmName)
        {
            if (string.IsNullOrWhiteSpace(algorithmName))
            {
                return false;
            }

            return _algorithms.ContainsKey(algorithmName);
        }
    }
}