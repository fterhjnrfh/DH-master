using System.Collections.Generic;
using System.Threading.Tasks;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    public interface IFunctionCombinationService
    {
        Task<FunctionCombinationType> DetectFunctionTypeAsync(ControlGroup group, List<ControlInfo> controls);
        Task ActivateFunctionAsync(ControlGroup group, List<ControlInfo> controls);
        Task DeactivateFunctionAsync(string groupId);
        Task UpdateFunctionParametersAsync(string groupId, string parameterId, object value);
        Task<bool> IsSinWaveCombinationAsync(List<ControlInfo> controls);
    }
}