using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    public class WorkingLogicService : IWorkingLogicService
    {
        private readonly List<WorkingLogic> _availableLogics;
        private readonly Dictionary<string, WorkingLogicSelection> _activeSelections = new();

        public WorkingLogicService()
        {
            _availableLogics = InitializeBuiltInLogics();
        }

        /// <summary>
        /// 初始化内置工作逻辑规则
        /// </summary>
        private List<WorkingLogic> InitializeBuiltInLogics()
        {
            return new List<WorkingLogic>
            {
                new WorkingLogic
                {
                    Id = "sinwave_generator",
                    Name = "正弦波绘制器",
                    Description = "使用2个文本框输入波幅和频率参数，1个显示控件绘制正弦波波形",
                    FunctionType = FunctionCombinationType.SinWaveGenerator,
                    Rule = new WorkingLogicRule
                    {
                        RequiredControlTypes = new Dictionary<string, int>
                        {
                            { "TextBox", 2 },
                            { "DisplayControl", 1 }
                        },
                        MinControlCount = 3,
                        MaxControlCount = 3
                    }
                },
                new WorkingLogic
                {
                    Id = "sinwave_generator_gl",
                    Name = "正弦波绘制器-OpenGL",
                    Description = "使用2个文本框输入波幅和频率参数，1个显示控件2绘制正弦波波形（OpenGL版本）",
                    FunctionType = FunctionCombinationType.SinWaveGenerator,
                    Rule = new WorkingLogicRule
                    {
                        RequiredControlTypes = new Dictionary<string, int>
                        {
                            { "TextBox", 2 },
                            { "DisplayControl2", 1 }
                        },
                        MinControlCount = 3,
                        MaxControlCount = 3
                    }
                },
                new WorkingLogic
                {
                    Id = "squarewave_generator",
                    Name = "方波绘制器",
                    Description = "使用2个文本框输入波幅和频率参数，1个显示控件绘制方波波形",
                    FunctionType = FunctionCombinationType.SquareWaveGenerator,
                    Rule = new WorkingLogicRule
                    {
                        RequiredControlTypes = new Dictionary<string, int>
                        {
                            { "TextBox", 2 },
                            { "DisplayControl", 1 }
                        },
                        MinControlCount = 3,
                        MaxControlCount = 3
                    }
                },
                new WorkingLogic
                {
                    Id = "squarewave_generator_gl",
                    Name = "方波绘制器-OpenGL",
                    Description = "使用2个文本框输入波幅和频率参数，1个显示控件2绘制方波波形（OpenGL版本）",
                    FunctionType = FunctionCombinationType.SquareWaveGenerator,
                    Rule = new WorkingLogicRule
                    {
                        RequiredControlTypes = new Dictionary<string, int>
                        {
                            { "TextBox", 2 },
                            { "DisplayControl2", 1 }
                        },
                        MinControlCount = 3,
                        MaxControlCount = 3
                    }
                },
                new WorkingLogic
                {
                    Id = "simulated_signal_source",
                    Name = "模拟信号源算法处理",
                    Description = "使用1个模拟信号源控件，对信号进行算法处理",
                    FunctionType = FunctionCombinationType.SimulatedSignalSource,
                    Rule = new WorkingLogicRule
                    {
                        RequiredControlTypes = new Dictionary<string, int>
                        {
                            { "SimulatedSignalSourceControl", 1 }
                        },
                        MinControlCount = 1,
                        MaxControlCount = 1
                    }
                }
                // 未来可以在这里添加更多内置逻辑
            };
        }

        /// <summary>
        /// 获取控件组可用的工作逻辑列表
        /// </summary>
        public async Task<List<WorkingLogic>> GetAvailableLogicsAsync(ControlGroup group, List<ControlInfo> controls)
        {
            await Task.CompletedTask;

            var groupControls = controls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var availableLogics = new List<WorkingLogic>();

            foreach (var logic in _availableLogics)
            {
                if (logic.Rule.IsMatch(groupControls))
                {
                    availableLogics.Add(logic);
                }
            }

            return availableLogics;
        }

        /// <summary>
        /// 选择并激活工作逻辑
        /// </summary>
        public async Task<bool> SelectAndActivateLogicAsync(string groupId, WorkingLogic selectedLogic, List<ControlInfo> controls)
        {
            try
            {
                var selection = new WorkingLogicSelection
                {
                    GroupId = groupId,
                    SelectedLogic = selectedLogic,
                    IsActivated = true
                };

                _activeSelections[groupId] = selection;
                
                // 激活功能组合逻辑，设置输入框名称和默认值
                var functionService = new FunctionCombinationService();
                // 创建临时控件组对象用于激活功能
                var controlIds = controls.Select(c => c.Id).ToList();
                var tempGroup = new ControlGroup { Id = groupId, FunctionType = selectedLogic.FunctionType, ControlIds = controlIds };
                await functionService.ActivateFunctionAsync(tempGroup, controls);
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取消激活工作逻辑
        /// </summary>
        public async Task DeactivateLogicAsync(string groupId)
        {
            await Task.CompletedTask;

            if (_activeSelections.ContainsKey(groupId))
            {
                _activeSelections[groupId].IsActivated = false;
                _activeSelections.Remove(groupId);
            }
        }

        /// <summary>
        /// 获取控件组的激活状态
        /// </summary>
        public async Task<WorkingLogicSelection?> GetActiveSelectionAsync(string groupId)
        {
            await Task.CompletedTask;
            return _activeSelections.TryGetValue(groupId, out var selection) ? selection : null;
        }

        /// <summary>
        /// 检查控件组是否已激活工作逻辑
        /// </summary>
        public async Task<bool> IsLogicActivatedAsync(string groupId)
        {
            await Task.CompletedTask;
            return _activeSelections.ContainsKey(groupId) && _activeSelections[groupId].IsActivated;
        }

        /// <summary>
        /// 获取所有可用的工作逻辑
        /// </summary>
        public async Task<List<WorkingLogic>> GetAllLogicsAsync()
        {
            await Task.CompletedTask;
            return _availableLogics.ToList();
        }
    }
}