using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    public class FunctionCombinationService : IFunctionCombinationService
    {
        private readonly Dictionary<string, FunctionCombinationType> _activeFunctions = new();

        public async Task<FunctionCombinationType> DetectFunctionTypeAsync(ControlGroup group, List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var groupControls = controls.Where(c => group.ControlIds.Contains(c.Id)).ToList();

            // 检测模拟信号源组合：1个SimulatedSignalSourceControl
            if (await IsSimulatedSignalSourceCombinationAsync(groupControls))
            {
                return FunctionCombinationType.SimulatedSignalSource;
            }

            // 检测正弦波组合：2个TextBox + 1个DisplayControl或1个DisplayControl2
            if (await IsSinWaveCombinationAsync(groupControls))
            {
                return FunctionCombinationType.SinWaveGenerator;
            }

            // 检测方波组合：2个TextBox + 1个DisplayControl或1个DisplayControl2
            if (await IsSquareWaveCombinationAsync(groupControls))
            {
                return FunctionCombinationType.SquareWaveGenerator;
            }

            return FunctionCombinationType.None;
        }

        public async Task<bool> IsSinWaveCombinationAsync(List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var textBoxes = controls.Where(c => c.Type == "TextBox").ToList();
            var displays = controls.Where(c => c.Type == "DisplayControl" || c.Type == "DisplayControl2").ToList();

            return textBoxes.Count == 2 && displays.Count == 1;
        }

        public async Task<bool> IsSquareWaveCombinationAsync(List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var textBoxes = controls.Where(c => c.Type == "TextBox").ToList();
            var displays = controls.Where(c => c.Type == "DisplayControl" || c.Type == "DisplayControl2").ToList();

            return textBoxes.Count == 2 && displays.Count == 1;
        }

        public async Task<bool> IsSimulatedSignalSourceCombinationAsync(List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var simulatedSignalSources = controls.Where(c => c.Type == "SimulatedSignalSourceControl").ToList();

            return simulatedSignalSources.Count == 1;
        }

        public async Task ActivateFunctionAsync(ControlGroup group, List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var functionType = await DetectFunctionTypeAsync(group, controls);
            if (functionType == FunctionCombinationType.None) return;

            // 更新组的功能类型
            group.FunctionType = functionType;
            
            // 记录激活的功能
            _activeFunctions[group.Id] = functionType;

            // 根据功能类型执行相应的激活逻辑
            switch (functionType)
            {
                case FunctionCombinationType.SinWaveGenerator:
                    await ActivateSinWaveFunctionAsync(group, controls);
                    break;
                case FunctionCombinationType.SquareWaveGenerator:
                    await ActivateSquareWaveFunctionAsync(group, controls);
                    break;
                case FunctionCombinationType.SimulatedSignalSource:
                    await ActivateSimulatedSignalSourceFunctionAsync(group, controls);
                    break;
            }
        }

        public async Task DeactivateFunctionAsync(string groupId)
        {
            await Task.CompletedTask;

            if (_activeFunctions.ContainsKey(groupId))
            {
                var functionType = _activeFunctions[groupId];
                
                // 根据功能类型执行相应的停用逻辑
                switch (functionType)
                {
                    case FunctionCombinationType.SinWaveGenerator:
                        await DeactivateSinWaveFunctionAsync(groupId);
                        break;
                    case FunctionCombinationType.SquareWaveGenerator:
                        await DeactivateSquareWaveFunctionAsync(groupId);
                        break;
                    case FunctionCombinationType.SimulatedSignalSource:
                        await DeactivateSimulatedSignalSourceFunctionAsync(groupId);
                        break;
                }

                _activeFunctions.Remove(groupId);
            }
        }

        public async Task UpdateFunctionParametersAsync(string groupId, string parameterId, object value)
        {
            await Task.CompletedTask;

            if (!_activeFunctions.ContainsKey(groupId)) return;

            var functionType = _activeFunctions[groupId];

            // 根据功能类型更新参数
            switch (functionType)
            {
                case FunctionCombinationType.SinWaveGenerator:
                    await UpdateSinWaveParametersAsync(groupId, parameterId, value);
                    break;
                case FunctionCombinationType.SquareWaveGenerator:
                    await UpdateSquareWaveParametersAsync(groupId, parameterId, value);
                    break;
            }
        }

        private async Task ActivateSinWaveFunctionAsync(ControlGroup group, List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var groupControls = controls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var textBoxes = groupControls.Where(c => c.Type == "TextBox").OrderBy(c => c.Id).ToList();
            var displays = groupControls.Where(c => c.Type == "DisplayControl" || c.Type == "DisplayControl2").ToList();

            if (textBoxes.Count == 2 && displays.Count == 1)
            {
                // 为正弦波组合中的输入框分配角色名称和默认值
                // 按照ID排序确保一致的角色分配
                textBoxes[0].Name = "波幅";
                textBoxes[0].Content = "25";
                textBoxes[1].Name = "频率";
                textBoxes[1].Content = "1.0";
            }
        }

        private async Task ActivateSquareWaveFunctionAsync(ControlGroup group, List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var groupControls = controls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var textBoxes = groupControls.Where(c => c.Type == "TextBox").OrderBy(c => c.Id).ToList();
            var displays = groupControls.Where(c => c.Type == "DisplayControl" || c.Type == "DisplayControl2").ToList();

            if (textBoxes.Count == 2 && displays.Count == 1)
            {
                // 为方波组合中的输入框分配角色名称和默认值
                // 按照ID排序确保一致的角色分配
                textBoxes[0].Name = "波幅";
                textBoxes[0].Content = "25";
                textBoxes[1].Name = "频率";
                textBoxes[1].Content = "1.0";
            }
        }

        private async Task DeactivateSinWaveFunctionAsync(string groupId)
        {
            await Task.CompletedTask;
            // 这里可以添加清理逻辑，比如重置控件名称等
        }

        private async Task DeactivateSquareWaveFunctionAsync(string groupId)
        {
            await Task.CompletedTask;
            // 这里可以添加清理逻辑，比如重置控件名称等
        }

        private async Task UpdateSinWaveParametersAsync(string groupId, string parameterId, object value)
        {
            await Task.CompletedTask;
            // 这里可以添加参数更新逻辑
            // 在运行时绑定阶段会实现具体的参数传递
        }

        private async Task UpdateSquareWaveParametersAsync(string groupId, string parameterId, object value)
        {
            await Task.CompletedTask;
            // 这里可以添加参数更新逻辑
            // 在运行时绑定阶段会实现具体的参数传递
        }

        private async Task ActivateSimulatedSignalSourceFunctionAsync(ControlGroup group, List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var groupControls = controls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var simulatedSignalSources = groupControls.Where(c => c.Type == "SimulatedSignalSourceControl").ToList();

            if (simulatedSignalSources.Count == 1)
            {
                // 为模拟信号源设置名称
                simulatedSignalSources[0].Name = "信号源(算法处理)";
            }
        }

        private async Task DeactivateSimulatedSignalSourceFunctionAsync(string groupId)
        {
            await Task.CompletedTask;
            // 这里可以添加清理逻辑，比如重置控件名称等
        }

        private async Task UpdateSimulatedSignalSourceParametersAsync(string groupId, string parameterId, object value)
        {
            await Task.CompletedTask;
            // 这里可以添加参数更新逻辑
            // 在运行时绑定阶段会实现具体的参数传递
        }
    }
}