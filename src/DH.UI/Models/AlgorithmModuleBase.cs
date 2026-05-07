using System.Collections.Generic;

namespace NewAvalonia.Models
{
    /// <summary>
    /// 算法模块基类
    /// </summary>
    public abstract class AlgorithmModuleBase : IAlgorithmModule
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        
        public abstract double[] Process(double[] inputData);
        
        public virtual Dictionary<string, object> GetParameters()
        {
            return new Dictionary<string, object>();
        }
        
        public virtual void SetParameters(Dictionary<string, object> parameters)
        {
            // 默认实现：不设置任何参数
        }
    }
}