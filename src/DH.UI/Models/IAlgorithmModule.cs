using System.Collections.Generic;

namespace NewAvalonia.Models
{
    /// <summary>
    /// 算法模块接口规范
    /// </summary>
    public interface IAlgorithmModule
    {
        /// <summary>
        /// 算法模块名称
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// 算法模块描述
        /// </summary>
        string Description { get; }
        
        /// <summary>
        /// 处理信号数据
        /// </summary>
        /// <param name="inputData">输入信号数据</param>
        /// <returns>处理后的信号数据</returns>
        double[] Process(double[] inputData);
        
        /// <summary>
        /// 获取算法参数
        /// </summary>
        /// <returns>算法参数列表</returns>
        Dictionary<string, object> GetParameters();
        
        /// <summary>
        /// 设置算法参数
        /// </summary>
        /// <param name="parameters">参数字典</param>
        void SetParameters(Dictionary<string, object> parameters);
    }
}