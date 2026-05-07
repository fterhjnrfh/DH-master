using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    public class ConnectionManager : IConnectionManager
    {
        private readonly List<ControlConnection> _connections = new();
        private readonly List<ControlGroup> _groups = new();

        public event EventHandler<ControlConnection>? ConnectionCreated;
        public event EventHandler<ControlConnection>? ConnectionRemoved;
        public event EventHandler<ControlGroup>? GroupCreated;
        public event EventHandler<ControlGroup>? GroupUpdated;
        public event EventHandler<ControlGroup>? GroupRemoved;

        public async Task<ControlConnection?> CreateConnectionAsync(string sourceControlId, string targetControlId)
        {
            // 验证连接
            if (!await ValidateConnectionAsync(sourceControlId, targetControlId))
            {
                return null;
            }

            // 创建连接
            var connection = new ControlConnection
            {
                SourceControlId = sourceControlId,
                TargetControlId = targetControlId,
                SourcePointId = $"{sourceControlId}_point",
                TargetPointId = $"{targetControlId}_point"
            };

            _connections.Add(connection);

            // 更新或创建控件组
            await UpdateControlGroupsAsync(connection);

            // 触发事件
            ConnectionCreated?.Invoke(this, connection);

            return connection;
        }

        public async Task RemoveConnectionAsync(string connectionId)
        {
            var connection = _connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection == null) return;

            _connections.Remove(connection);

            // 更新控件组
            await UpdateControlGroupsAfterRemovalAsync(connection);

            // 触发事件
            ConnectionRemoved?.Invoke(this, connection);
        }

        public async Task<ControlGroup?> GetControlGroupAsync(string controlId)
        {
            await Task.CompletedTask;
            return _groups.FirstOrDefault(g => g.ControlIds.Contains(controlId));
        }

        public async Task<List<ControlGroup>> GetAllGroupsAsync()
        {
            await Task.CompletedTask;
            return new List<ControlGroup>(_groups);
        }

        public async Task<ControlGroup?> GetControlGroupByIdAsync(string groupId)
        {
            await Task.CompletedTask;
            return _groups.FirstOrDefault(g => g.Id == groupId);
        }

        public async Task<bool> ValidateConnectionAsync(string sourceControlId, string targetControlId)
        {
            await Task.CompletedTask;

            // 防止自连接
            if (sourceControlId == targetControlId)
            {
                return false;
            }

            // 防止重复连接
            var existingConnection = _connections.FirstOrDefault(c =>
                (c.SourceControlId == sourceControlId && c.TargetControlId == targetControlId) ||
                (c.SourceControlId == targetControlId && c.TargetControlId == sourceControlId));

            if (existingConnection != null)
            {
                return false;
            }

            // 检查是否属于不同的控件组
            var sourceGroup = _groups.FirstOrDefault(g => g.ControlIds.Contains(sourceControlId));
            var targetGroup = _groups.FirstOrDefault(g => g.ControlIds.Contains(targetControlId));

            // 如果两个控件都已经属于不同的控件组，则禁止连接
            if (sourceGroup != null && targetGroup != null && sourceGroup != targetGroup)
            {
                return false;
            }

            return true;
        }

        public async Task<List<ControlConnection>> GetConnectionsAsync()
        {
            await Task.CompletedTask;
            return new List<ControlConnection>(_connections);
        }

        public async Task<List<ControlConnection>> GetConnectionsForControlAsync(string controlId)
        {
            await Task.CompletedTask;
            return _connections.Where(c => 
                c.SourceControlId == controlId || c.TargetControlId == controlId).ToList();
        }

        public async Task UpdateGroupBoundingBoxAsync(string groupId, List<ControlInfo> controls)
        {
            await Task.CompletedTask;
            
            var group = _groups.FirstOrDefault(g => g.Id == groupId);
            if (group == null) return;

            var groupControls = controls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            if (!groupControls.Any()) return;

            group.BoundingBox = CalculateGroupBoundingBox(groupControls);
            GroupUpdated?.Invoke(this, group);
        }

        public static Avalonia.Rect CalculateGroupBoundingBox(List<ControlInfo> controls)
        {
            if (!controls.Any()) return new Avalonia.Rect();

            double minX = controls.Min(c => c.Left);
            double minY = controls.Min(c => c.Top);
            double maxX = controls.Max(c => c.Left + c.Width);
            double maxY = controls.Max(c => c.Top + c.Height);

            // 添加边距
            const double margin = 10;
            return new Avalonia.Rect(
                minX - margin,
                minY - margin,
                maxX - minX + 2 * margin,
                maxY - minY + 2 * margin
            );
        }

        private async Task UpdateControlGroupsAsync(ControlConnection newConnection)
        {
            await Task.CompletedTask;

            var sourceGroup = _groups.FirstOrDefault(g => g.ControlIds.Contains(newConnection.SourceControlId));
            var targetGroup = _groups.FirstOrDefault(g => g.ControlIds.Contains(newConnection.TargetControlId));

            if (sourceGroup == null && targetGroup == null)
            {
                // 创建新组，确保控件ID按字典序排列
                var newGroup = new ControlGroup();
                var controlIds = new List<string> { newConnection.SourceControlId, newConnection.TargetControlId };
                controlIds.Sort(); // 按字典序排序，确保顺序一致
                newGroup.ControlIds.AddRange(controlIds);
                newGroup.Connections.Add(newConnection);
                
                _groups.Add(newGroup);
                GroupCreated?.Invoke(this, newGroup);
            }
            else if (sourceGroup != null && targetGroup == null)
            {
                // 将目标控件添加到源组
                sourceGroup.ControlIds.Add(newConnection.TargetControlId);
                // 重新排序以保持一致性
                sourceGroup.ControlIds.Sort();
                sourceGroup.Connections.Add(newConnection);
                GroupUpdated?.Invoke(this, sourceGroup);
            }
            else if (sourceGroup == null && targetGroup != null)
            {
                // 将源控件添加到目标组
                targetGroup.ControlIds.Add(newConnection.SourceControlId);
                // 重新排序以保持一致性
                targetGroup.ControlIds.Sort();
                targetGroup.Connections.Add(newConnection);
                GroupUpdated?.Invoke(this, targetGroup);
            }
            else if (sourceGroup != null && targetGroup != null && sourceGroup != targetGroup)
            {
                // 合并两个组
                sourceGroup.ControlIds.AddRange(targetGroup.ControlIds);
                sourceGroup.Connections.AddRange(targetGroup.Connections);
                sourceGroup.Connections.Add(newConnection);
                
                _groups.Remove(targetGroup);
                GroupUpdated?.Invoke(this, sourceGroup);
            }
            else if (sourceGroup != null && targetGroup != null && sourceGroup == targetGroup)
            {
                // 同一组内的连接
                sourceGroup.Connections.Add(newConnection);
                GroupUpdated?.Invoke(this, sourceGroup);
            }
        }

        private async Task UpdateControlGroupsAfterRemovalAsync(ControlConnection removedConnection)
        {
            await Task.CompletedTask;

            var affectedGroup = _groups.FirstOrDefault(g => g.Connections.Any(c => c.Id == removedConnection.Id));
            if (affectedGroup == null) return;

            // 移除连接
            affectedGroup.Connections.RemoveAll(c => c.Id == removedConnection.Id);

            // 检查组是否需要拆分
            if (affectedGroup.Connections.Count == 0)
            {
                // 没有连接了，移除组
                _groups.Remove(affectedGroup);
                // 触发组删除事件
                GroupRemoved?.Invoke(this, affectedGroup);
            }
            else
            {
                // 检查控件是否仍然连接
                var connectedControlIds = new HashSet<string>();
                foreach (var connection in affectedGroup.Connections)
                {
                    connectedControlIds.Add(connection.SourceControlId);
                    connectedControlIds.Add(connection.TargetControlId);
                }

                // 移除不再连接的控件
                affectedGroup.ControlIds.RemoveAll(id => !connectedControlIds.Contains(id));
                
                GroupUpdated?.Invoke(this, affectedGroup);
            }
        }



        private List<ControlGroup> SplitGroupIfNeeded(ControlGroup originalGroup)
        {
            var result = new List<ControlGroup>();
            var processedControlIds = new HashSet<string>();

            foreach (var controlId in originalGroup.ControlIds)
            {
                if (processedControlIds.Contains(controlId)) continue;

                // 找到与此控件连接的所有控件
                var connectedControlIds = new HashSet<string> { controlId };
                var queue = new Queue<string>();
                queue.Enqueue(controlId);

                while (queue.Count > 0)
                {
                    var currentControlId = queue.Dequeue();
                    var relatedConnections = originalGroup.Connections.Where(c =>
                        c.SourceControlId == currentControlId || c.TargetControlId == currentControlId);

                    foreach (var connection in relatedConnections)
                    {
                        var otherControlId = connection.SourceControlId == currentControlId
                            ? connection.TargetControlId
                            : connection.SourceControlId;

                        if (!connectedControlIds.Contains(otherControlId))
                        {
                            connectedControlIds.Add(otherControlId);
                            queue.Enqueue(otherControlId);
                        }
                    }
                }

                // 创建子组
                if (connectedControlIds.Count > 1)
                {
                    var sortedControlIds = connectedControlIds.ToList();
                    sortedControlIds.Sort(); // 确保控件ID按字典序排列
                    var subGroup = new ControlGroup
                    {
                        ControlIds = sortedControlIds,
                        Connections = originalGroup.Connections.Where(c =>
                            connectedControlIds.Contains(c.SourceControlId) &&
                            connectedControlIds.Contains(c.TargetControlId)).ToList()
                    };
                    result.Add(subGroup);
                }

                // 标记这些控件为已处理
                foreach (var id in connectedControlIds)
                {
                    processedControlIds.Add(id);
                }
            }

            return result.Count > 0 ? result : new List<ControlGroup> { originalGroup };
        }
    }
}