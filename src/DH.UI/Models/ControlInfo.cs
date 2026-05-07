using System;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Models
{
    public class ControlInfo
    {
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public string Content { get; set; } = string.Empty;
        public string ForegroundColor { get; set; } = "Black"; // 添加文字颜色属性，默认为黑色
        
        // 新增连接系统相关属性
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string GroupId { get; set; } = string.Empty;
        
        // 图像处理器专用属性
        public string SelectedAlgorithm { get; set; } = string.Empty; // 选中的算法名称
        public string AlgorithmType { get; set; } = "Built-in"; // 算法类型：Built-in（内置）或 External（外部）
        public string AlgorithmFilePath { get; set; } = string.Empty; // 外部算法文件路径
        public string AlgorithmParameters { get; set; } = string.Empty; // 算法参数（JSON格式）
        
        // ViewModel属性，用于访问控件的数据上下文
        public object? ViewModel { get; set; }
    }
}