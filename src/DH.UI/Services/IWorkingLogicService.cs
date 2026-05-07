using System.Collections.Generic;
using System.Threading.Tasks;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    public interface IWorkingLogicService
    {
        /// <summary>
        /// 获取控件组可用的工作逻辑列表
        /// </summary>
        Task<List<WorkingLogic>> GetAvailableLogicsAsync(ControlGroup group, List<ControlInfo> controls);

        /// <summary>
        /// 选择并激活工作逻辑
        /// </summary>
        Task<bool> SelectAndActivateLogicAsync(string groupId, WorkingLogic selectedLogic, List<ControlInfo> controls);

        /// <summary>
        /// 取消激活工作逻辑
        /// </summary>
        Task DeactivateLogicAsync(string groupId);

        /// <summary>
        /// 获取控件组的激活状态
        /// </summary>
        Task<WorkingLogicSelection?> GetActiveSelectionAsync(string groupId);

        /// <summary>
        /// 检查控件组是否已激活工作逻辑
        /// </summary>
        Task<bool> IsLogicActivatedAsync(string groupId);

        /// <summary>
        /// 获取所有可用的工作逻辑
        /// </summary>
        Task<List<WorkingLogic>> GetAllLogicsAsync();
    }
}