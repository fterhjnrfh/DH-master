using System.Collections.Generic;
using System.Linq;

namespace NewAvalonia.Models
{
    /// <summary>
    /// 工作逻辑定义
    /// </summary>
    public class WorkingLogic
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public FunctionCombinationType FunctionType { get; set; }
        public WorkingLogicRule Rule { get; set; } = new WorkingLogicRule();
    }

    /// <summary>
    /// 工作逻辑匹配规则
    /// </summary>
    public class WorkingLogicRule
    {
        public Dictionary<string, int> RequiredControlTypes { get; set; } = new Dictionary<string, int>();
        public int MinControlCount { get; set; } = 1;
        public int MaxControlCount { get; set; } = int.MaxValue;
        
        /// <summary>
        /// 检查控件组是否匹配此规则
        /// </summary>
        public bool IsMatch(List<ControlInfo> controls)
        {
            if (controls.Count < MinControlCount || controls.Count > MaxControlCount)
                return false;

            // 检查控件类型和数量是否匹配
            var controlTypeCounts = controls.GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var requiredType in RequiredControlTypes)
            {
                if (!controlTypeCounts.ContainsKey(requiredType.Key) || 
                    controlTypeCounts[requiredType.Key] != requiredType.Value)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 工作逻辑选择结果
    /// </summary>
    public class WorkingLogicSelection
    {
        public string GroupId { get; set; } = string.Empty;
        public WorkingLogic? SelectedLogic { get; set; }
        public bool IsActivated { get; set; } = false;
    }
}