using System.Collections.Generic;

namespace AlgorithmModule
{
    /// <summary>
    /// 算法接口定义
    /// </summary>
    public interface IAlgorithm
    {
        /// <summary>
        /// 算法名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 算法描述
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 算法版本
        /// </summary>
        string Version { get; }

        /// <summary>
        /// 算法作者
        /// </summary>
        string Author { get; }

        /// <summary>
        /// 默认参数
        /// </summary>
        Dictionary<string, object> DefaultParameters { get; }

        /// <summary>
        /// 处理方法
        /// </summary>
        /// <param name="input">输入数据</param>
        /// <param name="parameters">算法参数</param>
        /// <returns>处理后的数据</returns>
        object Process(object input, IDictionary<string, object>? parameters = null);
    }
}