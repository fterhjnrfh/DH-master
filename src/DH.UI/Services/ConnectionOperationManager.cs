using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using NewAvalonia.Models;
using NewAvalonia.Views;


namespace NewAvalonia.Services
{
    public class ConnectionOperationManager
    {
        private readonly Canvas _canvas;
        private readonly ConnectionManager _connectionManager;
        private readonly ConnectionLineManager _lineManager;
        private readonly FunctionCombinationService _functionService;
        private readonly IWorkingLogicService _workingLogicService;
        private readonly Dictionary<string, ControlGroupBorderView> _groupBorders = new();
        private List<ControlInfo> _currentControls = new();

        // 连接状态
        private bool _isConnecting = false;
        private string _sourceControlId = string.Empty;
        private ConnectionPointView? _sourceConnectionPoint;
        private readonly Dictionary<string, ConnectionPointView> _controlConnectionPoints = new();

        public ConnectionOperationManager(Canvas canvas, IWorkingLogicService workingLogicService)
        {
            _canvas = canvas;
            _connectionManager = new ConnectionManager();
            _lineManager = new ConnectionLineManager(canvas);
            _functionService = new FunctionCombinationService();
            _workingLogicService = workingLogicService;

            // 订阅连接管理器事件
            _connectionManager.ConnectionCreated += OnConnectionCreated;
            _connectionManager.ConnectionRemoved += OnConnectionRemoved;
            _connectionManager.GroupCreated += OnGroupCreated;
            _connectionManager.GroupUpdated += OnGroupUpdated;
            _connectionManager.GroupRemoved += OnGroupRemoved;

            // 订阅连接线删除事件
            _lineManager.ConnectionLineDeleted += OnConnectionLineDeleted;
        }

        public void RegisterConnectionPoint(string controlId, ConnectionPointView connectionPoint)
        {
            _controlConnectionPoints[controlId] = connectionPoint;
        }

        public async Task<bool> HandleConnectionPointClick(string controlId, ConnectionPointView connectionPoint, List<ControlInfo> allControls)
        {
            if (!_isConnecting)
            {
                // 开始连接状态
                return StartConnection(controlId, connectionPoint, allControls);
            }
            else if (_sourceControlId == controlId)
            {
                // 点击同一个点，取消连接
                CancelConnection();
                return true;
            }
            else
            {
                // 点击不同的点，完成连接
                return await CompleteConnection(controlId, allControls);
            }
        }

        private bool StartConnection(string controlId, ConnectionPointView connectionPoint, List<ControlInfo> allControls)
        {
            _isConnecting = true;
            _sourceControlId = controlId;
            _sourceConnectionPoint = connectionPoint;

            // 设置连接点状态为红色
            connectionPoint.SetState(ConnectionPointView.ConnectionPointState.Connecting);

            return true;
        }



        private async Task<bool> CompleteConnection(string targetControlId, List<ControlInfo> allControls)
        {
            if (!_isConnecting || string.IsNullOrEmpty(_sourceControlId)) return false;

            try
            {
                // 检查是否是重复连接（取消连接）
                var existingConnections = await _connectionManager.GetConnectionsForControlAsync(_sourceControlId);
                var existingConnection = existingConnections.FirstOrDefault(c => 
                    c.SourceControlId == targetControlId || c.TargetControlId == targetControlId);

                if (existingConnection != null)
                {
                    // 取消现有连接
                    await _connectionManager.RemoveConnectionAsync(existingConnection.Id);
                    
                    // 移除连接线
                    _lineManager.RemovePermanentLine(existingConnection.Id);
                    
                    // 重新检测所有受影响的控件组的功能类型
                    await RedetectAllGroupFunctions(allControls);
                    
                    // 更新连接点状态和边框
                    await UpdateAllConnectionPointStates(allControls);
                    await UpdateControlPositions(allControls);
                    
                    CancelConnection();
                    return true;
                }

                // 验证连接是否允许
                if (!await _connectionManager.ValidateConnectionAsync(_sourceControlId, targetControlId))
                {
                    // 检查具体的失败原因
                    var sourceGroup = await _connectionManager.GetControlGroupAsync(_sourceControlId);
                    var targetGroup = await _connectionManager.GetControlGroupAsync(targetControlId);
                    
                    if (sourceGroup != null && targetGroup != null && sourceGroup != targetGroup)
                    {
                        // 显示警告信息（暂时使用控制台输出）
                        Console.WriteLine("警告: 不能连接属于不同控件组的控件。请在同一个控件组内建立连接，或者先解除现有的控件组连接。");
                        
                        // TODO: 在这里可以添加更好的UI警告提示框
                    }
                    
                    // 重置目标连接点状态
                    if (_controlConnectionPoints.TryGetValue(targetControlId, out var targetConnectionPoint))
                    {
                        targetConnectionPoint.SetConnectingState(false);
                    }
                    
                    CancelConnection();
                    return false;
                }

                // 创建新连接
                var connection = await _connectionManager.CreateConnectionAsync(_sourceControlId, targetControlId);
                if (connection != null)
                {
                    // 创建连接线
                    var sourceControl = allControls.FirstOrDefault(c => c.Id == _sourceControlId);
                    var targetControl = allControls.FirstOrDefault(c => c.Id == targetControlId);
                    if (sourceControl != null && targetControl != null)
                    {
                        var startPoint = _lineManager.CalculateConnectionPoint(sourceControl);
                        var endPoint = _lineManager.CalculateConnectionPoint(targetControl);
                        _lineManager.AddPermanentLine(connection, startPoint, endPoint);
                    }

                    // 更新所有连接点状态
                    await UpdateAllConnectionPointStates(allControls);

                    // 仅更新控件组边框，不自动激活功能
                    // 功能激活现在需要用户手动选择工作逻辑
                    await UpdateControlPositions(allControls);
                    
                    // 通知控件内容已更新
                    ControlsUpdated?.Invoke(allControls);

                    CancelConnection();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"连接操作失败: {ex.Message}");
                
                // 重置目标连接点状态
                if (_controlConnectionPoints.TryGetValue(targetControlId, out var targetConnectionPoint))
                {
                    targetConnectionPoint.SetConnectingState(false);
                }
            }

            // 重置目标连接点状态
            if (_controlConnectionPoints.TryGetValue(targetControlId, out var targetConnectionPoint2))
            {
                targetConnectionPoint2.SetConnectingState(false);
            }
            
            CancelConnection();
            return false;
        }

        public void CancelConnection()
        {
            if (_isConnecting)
            {
                _isConnecting = false;

                // 重置连接点状态
                if (_sourceConnectionPoint != null)
                {
                    // 正确重置连接状态：从Connecting状态恢复到之前的状态
                    _sourceConnectionPoint.SetConnectingState(false);
                }

                _sourceControlId = string.Empty;
                _sourceConnectionPoint = null;
            }
        }

        private async Task UpdateAllConnectionPointStates(List<ControlInfo> allControls)
        {
            foreach (var control in allControls)
            {
                if (_controlConnectionPoints.TryGetValue(control.Id, out var connectionPoint))
                {
                    var connections = await _connectionManager.GetConnectionsForControlAsync(control.Id);
                    bool hasConnections = connections.Any();
                    connectionPoint.SetConnectedState(hasConnections);
                }
            }
        }

        private async Task RedetectAllGroupFunctions(List<ControlInfo> allControls)
        {
            var groups = await _connectionManager.GetAllGroupsAsync();
            foreach (var group in groups)
            {
                // 重新检测功能类型
                var newFunctionType = await _functionService.DetectFunctionTypeAsync(group, allControls);
                
                // 如果功能类型发生变化，更新组
                if (group.FunctionType != newFunctionType)
                {
                    group.FunctionType = newFunctionType;
                    
                    // 如果功能类型变为None，停用功能
                    if (newFunctionType == FunctionCombinationType.None)
                    {
                        await _functionService.DeactivateFunctionAsync(group.Id);
                    }
                    else
                    {
                        // 激活新功能
                        await _functionService.ActivateFunctionAsync(group, allControls);
                    }
                }
            }
        }

        public async Task UpdateControlPositions(List<ControlInfo> allControls)
        {
            // 更新所有连接线的位置
            var connections = await _connectionManager.GetConnectionsAsync();
            foreach (var connection in connections)
            {
                var sourceControl = allControls.FirstOrDefault(c => c.Id == connection.SourceControlId);
                var targetControl = allControls.FirstOrDefault(c => c.Id == connection.TargetControlId);

                if (sourceControl != null && targetControl != null)
                {
                    var startPoint = _lineManager.CalculateConnectionPoint(sourceControl);
                    var endPoint = _lineManager.CalculateConnectionPoint(targetControl);
                    _lineManager.UpdatePermanentLine(connection.Id, startPoint, endPoint);
                }
            }

            // 更新所有控件组边框的位置和功能类型
            var groups = await _connectionManager.GetAllGroupsAsync();
            foreach (var group in groups)
            {
                var groupControls = allControls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
                if (groupControls.Any())
                {
                    var bounds = ConnectionManager.CalculateGroupBoundingBox(groupControls);
                    group.BoundingBox = bounds;

                    if (_groupBorders.TryGetValue(group.Id, out var borderView))
                    {
                        borderView.SetBounds(bounds);
                        borderView.SetFunctionType(group.FunctionType); // 确保功能类型是最新的
                        borderView.UpdatePosition(ControlGroupBorderView.CalculatePosition(bounds));
                    }
                }
            }
        }

        public bool IsConnecting => _isConnecting;

        public event System.Action<List<ControlInfo>>? ControlsUpdated;

        public async Task<List<ControlGroup>> GetAllControlGroupsAsync()
        {
            return await _connectionManager.GetAllGroupsAsync();
        }

        public async Task HandleControlDeletionAsync(string deletedControlId, List<ControlInfo> allControls)
        {
            try
            {
                // 1. 先获取与该控件相关的所有连接
                var connections = await _connectionManager.GetConnectionsForControlAsync(deletedControlId);
                var connectionsToRemove = connections.ToList(); // 创建副本，避免在迭代时修改集合

                // 2. 逐个取消连接（这会触发正常的连接取消流程）
                foreach (var connection in connectionsToRemove)
                {
                    // 使用正常的连接移除流程，这会触发所有相关的事件和更新
                    await _connectionManager.RemoveConnectionAsync(connection.Id);
                    
                    // 移除连接线
                    _lineManager.RemovePermanentLine(connection.Id);
                    
                    // 重新检测所有受影响的控件组的功能类型
                    await RedetectAllGroupFunctions(allControls);
                    
                    // 更新连接点状态和边框
                    await UpdateAllConnectionPointStates(allControls);
                    await UpdateControlPositions(allControls);
                }

                // 3. 移除连接点注册
                _controlConnectionPoints.Remove(deletedControlId);

                // 4. 最终更新（确保所有状态都是最新的）
                await UpdateAllConnectionPointStates(allControls);
                await UpdateControlPositions(allControls);

                // 5. 通知控件内容已更新
                ControlsUpdated?.Invoke(allControls);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理控件删除时发生错误: {ex.Message}");
            }
        }

        // 事件处理
        private async void OnConnectionCreated(object? sender, ControlConnection connection)
        {
            // 连接创建后的处理已在CompleteConnection中完成
            await Task.CompletedTask;
        }

        private async void OnConnectionRemoved(object? sender, ControlConnection connection)
        {
            _lineManager.RemovePermanentLine(connection.Id);
            await Task.CompletedTask;
        }

        private async void OnGroupCreated(object? sender, ControlGroup group)
        {
            await CreateGroupBorder(group);
        }

        private async void OnGroupUpdated(object? sender, ControlGroup group)
        {
            await UpdateGroupBorder(group);
        }

        private async void OnGroupRemoved(object? sender, ControlGroup group)
        {
            await RemoveGroupBorder(group);
        }

        private async void OnConnectionLineDeleted(string connectionId)
        {
            await _connectionManager.RemoveConnectionAsync(connectionId);
        }

        private async Task CreateGroupBorder(ControlGroup group)
        {
            var borderView = new ControlGroupBorderView()
            {
                GroupId = group.Id
            };

            borderView.SetBounds(group.BoundingBox);
            borderView.SetFunctionType(group.FunctionType);
            borderView.UpdatePosition(ControlGroupBorderView.CalculatePosition(group.BoundingBox));
            
            // 设置工作逻辑服务依赖项
            // 注意：这里需要获取所有控件信息，可能需要从外部传入
            // 暂时先设置基本依赖，稍后在需要时补充控件信息
            
            // 订阅工作逻辑选择事件
            borderView.WorkingLogicSelected += OnWorkingLogicSelected;

            _groupBorders[group.Id] = borderView;
            _canvas.Children.Add(borderView);

            await Task.CompletedTask;
        }

        private async Task UpdateGroupBorder(ControlGroup group)
        {
            if (_groupBorders.TryGetValue(group.Id, out var borderView))
            {
                borderView.SetBounds(group.BoundingBox);
                borderView.SetFunctionType(group.FunctionType);
                borderView.UpdatePosition(ControlGroupBorderView.CalculatePosition(group.BoundingBox));
            }

            await Task.CompletedTask;
        }

        private async Task RemoveGroupBorder(ControlGroup group)
        {
            if (_groupBorders.TryGetValue(group.Id, out var borderView))
            {
                // 取消订阅事件
                borderView.WorkingLogicSelected -= OnWorkingLogicSelected;
                _canvas.Children.Remove(borderView);
                _groupBorders.Remove(group.Id);
            }

            await Task.CompletedTask;
        }
        
        /// <summary>
        /// 设置控件组边框的依赖项
        /// </summary>
        public async Task SetGroupBorderDependencies(List<ControlInfo> allControls)
        {
            // 更新当前控件信息列表
            _currentControls = allControls;
            
            var groups = await _connectionManager.GetAllGroupsAsync();
            foreach (var group in groups)
            {
                if (_groupBorders.TryGetValue(group.Id, out var borderView))
                {
                    borderView.SetDependencies(_workingLogicService, group, allControls);
                }
            }
        }
        
        /// <summary>
        /// 处理工作逻辑选择事件
        /// </summary>
        private async void OnWorkingLogicSelected(string groupId, WorkingLogic? selectedLogic)
        {
            try
            {
                var group = await _connectionManager.GetControlGroupByIdAsync(groupId);
                if (group == null) return;
                
                if (selectedLogic != null)
                {
                    // 获取控件组的控件信息
                    var controls = group.ControlIds.Select(id => _currentControls.FirstOrDefault(c => c.Id == id))
                                       .Where(c => c != null).Cast<ControlInfo>().ToList();
                    
                    // 激活选定的工作逻辑
                    await _workingLogicService.SelectAndActivateLogicAsync(groupId, selectedLogic, controls);
                    
                    // 更新控件组的功能类型
                    group.FunctionType = selectedLogic.FunctionType;
                    
                    // 更新边框显示
                    if (_groupBorders.TryGetValue(groupId, out var borderView))
                    {
                        borderView.SetFunctionType(group.FunctionType);
                    }
                    
                    // 通知控件内容已更新
                    // 这里需要获取最新的控件列表，可能需要从外部传入或存储
                    // ControlsUpdated?.Invoke(allControls);
                }
                else
                {
                    // 取消激活
                    await _workingLogicService.DeactivateLogicAsync(groupId);
                    
                    // 重置功能类型
                    group.FunctionType = FunctionCombinationType.None;
                    
                    // 更新边框显示
                    if (_groupBorders.TryGetValue(groupId, out var borderView))
                    {
                        borderView.SetFunctionType(group.FunctionType);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理工作逻辑选择时发生错误: {ex.Message}");
            }
        }
    }
}