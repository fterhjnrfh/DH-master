using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    public interface IConnectionManager
    {
        event EventHandler<ControlConnection>? ConnectionCreated;
        event EventHandler<ControlConnection>? ConnectionRemoved;
        event EventHandler<ControlGroup>? GroupCreated;
        event EventHandler<ControlGroup>? GroupUpdated;
        event EventHandler<ControlGroup>? GroupRemoved;
        
        Task<ControlConnection?> CreateConnectionAsync(string sourceControlId, string targetControlId);
        Task RemoveConnectionAsync(string connectionId);
        Task<ControlGroup?> GetControlGroupAsync(string controlId);
        Task<List<ControlGroup>> GetAllGroupsAsync();
        Task<ControlGroup?> GetControlGroupByIdAsync(string groupId);
        Task<bool> ValidateConnectionAsync(string sourceControlId, string targetControlId);
        Task<List<ControlConnection>> GetConnectionsAsync();
        Task<List<ControlConnection>> GetConnectionsForControlAsync(string controlId);
        Task UpdateGroupBoundingBoxAsync(string groupId, List<ControlInfo> controls);
    }
}