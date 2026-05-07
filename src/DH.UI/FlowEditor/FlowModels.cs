using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace NewAvalonia.FlowEditor
{
    /// <summary>
    /// 流程图节点类型
    /// </summary>
    public enum NodeType
    {
        Start,      // 开始节点
        End,        // 结束节点
        Gaussian,   // 高斯滤波
        Median,     // 中值滤波
        MovingAverage, // 移动平均滤波
        SignalSmooth   // 信号平滑
    }

    /// <summary>
    /// 流程图节点
    /// </summary>
    public class FlowNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public NodeType Type { get; set; }
        public Point Position { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        
        public string Name => Type switch
        {
            NodeType.Start => "开始",
            NodeType.End => "结束",
            NodeType.Gaussian => "高斯滤波",
            NodeType.Median => "中值滤波",
            NodeType.MovingAverage => "移动平均滤波",
            NodeType.SignalSmooth => "信号平滑",
            _ => "未知节点"
        };

        public FlowNode(NodeType type)
        {
            Type = type;
            Parameters = GetDefaultParameters();
        }

        private Dictionary<string, object> GetDefaultParameters()
        {
            return Type switch
            {
                NodeType.Gaussian => new Dictionary<string, object>
                {
                    {"Sigma", 1.0},
                    {"KernelSize", 5}
                },
                NodeType.Median => new Dictionary<string, object>
                {
                    {"WindowSize", 3}
                },
                NodeType.MovingAverage => new Dictionary<string, object>
                {
                    {"WindowSize", 5}
                },
                NodeType.SignalSmooth => new Dictionary<string, object>
                {
                    {"WindowSize", 5},
                    {"Smoothness", 0.5}
                },
                _ => new Dictionary<string, object>()
            };
        }
    }

    /// <summary>
    /// 流程图连接
    /// </summary>
    public class FlowConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FromNodeId { get; set; }
        public string ToNodeId { get; set; }
        
        public FlowConnection(string fromNodeId, string toNodeId)
        {
            FromNodeId = fromNodeId;
            ToNodeId = toNodeId;
        }
    }

    /// <summary>
    /// 流程图模型
    /// </summary>
    public class FlowGraph
    {
        public List<FlowNode> Nodes { get; set; } = new List<FlowNode>();
        public List<FlowConnection> Connections { get; set; } = new List<FlowConnection>();
        
        public List<FlowNode> GetExecutionOrder()
        {
            var orderedNodes = new List<FlowNode>();
            var visited = new HashSet<string>();
            var startNode = Nodes.FirstOrDefault(n => n.Type == NodeType.Start);
            
            if (startNode != null)
            {
                DfsVisit(startNode, visited, orderedNodes);
            }
            
            return orderedNodes;
        }

        private void DfsVisit(FlowNode node, HashSet<string> visited, List<FlowNode> orderedNodes)
        {
            if (visited.Contains(node.Id)) return;
            
            visited.Add(node.Id);
            orderedNodes.Add(node);
            
            var outgoingConnections = Connections.Where(c => c.FromNodeId == node.Id);
            foreach (var connection in outgoingConnections)
            {
                var nextNode = Nodes.FirstOrDefault(n => n.Id == connection.ToNodeId);
                if (nextNode != null && !visited.Contains(nextNode.Id))
                {
                    DfsVisit(nextNode, visited, orderedNodes);
                }
            }
        }
    }
}