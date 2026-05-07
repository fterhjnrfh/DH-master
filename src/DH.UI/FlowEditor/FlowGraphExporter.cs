using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NewAvalonia.FlowEditor;

namespace NewAvalonia.FlowEditor
{
    /// <summary>
    /// 流程图导出器
    /// </summary>
    public class FlowGraphExporter
    {
        /// <summary>
        /// 生成C++源代码
        /// </summary>
        /// <param name="graph">流程图</param>
        /// <param name="functionName">函数名称</param>
        /// <returns>C++源代码</returns>
        public static string GenerateCppSource(FlowGraph graph, string functionName = "process_signal")
        {
            var executionOrder = graph.GetExecutionOrder();
            var code = new StringBuilder();
            
            // 包含必要的头文件
            code.AppendLine("#include <vector>");
            code.AppendLine("#include <cmath>");
            code.AppendLine("#include <algorithm>");
            code.AppendLine("#include <cstring>");
            code.AppendLine();
            
            // 高斯核生成函数
            code.AppendLine("std::vector<double> generate_gaussian_kernel(int size, double sigma) {");
            code.AppendLine("    std::vector<double> kernel(size);");
            code.AppendLine("    int center = size / 2;");
            code.AppendLine("    double sum = 0.0;");
            code.AppendLine("    ");
            code.AppendLine("    for (int i = 0; i < size; i++) {");
            code.AppendLine("        double x = i - center;");
            code.AppendLine("        kernel[i] = exp(-(x * x) / (2 * sigma * sigma));");
            code.AppendLine("        sum += kernel[i];");
            code.AppendLine("    }");
            code.AppendLine("    ");
            code.AppendLine("    // 归一化");
            code.AppendLine("    for (int i = 0; i < size; i++) {");
            code.AppendLine("        kernel[i] /= sum;");
            code.AppendLine("    }");
            code.AppendLine("    ");
            code.AppendLine("    return kernel;");
            code.AppendLine("}");
            code.AppendLine();
            
            // C风格导出函数
            code.AppendLine("extern \"C\" {");
            code.AppendLine($"    __declspec(dllexport) int {functionName}(double* input, int length, double* output) {{");
            code.AppendLine("        if (!input || !output || length <= 0) {");
            code.AppendLine("            return -1; // 错误");
            code.AppendLine("        }");
            code.AppendLine("        ");
            code.AppendLine("        // 创建临时数组用于中间处理");
            code.AppendLine("        std::vector<double> temp_input(input, input + length);");
            code.AppendLine("        std::vector<double> temp_output(length);");
            code.AppendLine("        std::vector<double> swap_temp;");
            code.AppendLine("        ");
            
            // 按流程图顺序生成处理代码
            int stepCounter = 0;
            foreach (var node in executionOrder)
            {
                if (node.Type == NodeType.Start || node.Type == NodeType.End)
                    continue;
                    
                code.AppendLine($"        // 步骤 {++stepCounter}: {node.Name}");
                
                switch (node.Type)
                {
                    case NodeType.Gaussian:
                        double sigma = node.Parameters.ContainsKey("Sigma") ? 
                                      Convert.ToDouble(node.Parameters["Sigma"]) : 1.0;
                        int kernelSize = node.Parameters.ContainsKey("KernelSize") ? 
                                       Convert.ToInt32(node.Parameters["KernelSize"]) : 5;
                        
                        // 确保核大小为奇数
                        if (kernelSize % 2 == 0) kernelSize++;
                        
                        code.AppendLine("        {");
                        code.AppendLine($"            int kernel_size = {kernelSize};");
                        code.AppendLine($"            double sigma_val = {sigma};");
                        code.AppendLine("            auto kernel = generate_gaussian_kernel(kernel_size, sigma_val);");
                        code.AppendLine("            int half_kernel = kernel_size / 2;");
                        code.AppendLine("            ");
                        code.AppendLine("            for (int i = 0; i < length; i++) {");
                        code.AppendLine("                double sum = 0.0;");
                        code.AppendLine("                ");
                        code.AppendLine("                for (int j = 0; j < kernel_size; j++) {");
                        code.AppendLine("                    int idx = i - half_kernel + j;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    // 边界处理");
                        code.AppendLine("                    if (idx < 0) idx = -idx;");
                        code.AppendLine("                    else if (idx >= length) idx = 2 * length - idx - 2;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    if (idx >= 0 && idx < length) {");
                        code.AppendLine("                        sum += temp_input[idx] * kernel[j];");
                        code.AppendLine("                    }");
                        code.AppendLine("                }");
                        code.AppendLine("                temp_output[i] = sum;");
                        code.AppendLine("            }");
                        code.AppendLine("            ");
                        code.AppendLine("            // 交换输入输出数组");
                        code.AppendLine("            swap_temp = temp_input;");
                        code.AppendLine("            temp_input = temp_output;");
                        code.AppendLine("            temp_output = swap_temp;");
                        code.AppendLine("        }");
                        code.AppendLine("        ");
                        break;
                        
                    case NodeType.Median:
                        int medWindowSize = node.Parameters.ContainsKey("WindowSize") ? 
                                         Convert.ToInt32(node.Parameters["WindowSize"]) : 3;
                        
                        // 确保窗口大小为奇数
                        if (medWindowSize % 2 == 0) medWindowSize++;
                        
                        code.AppendLine("        {");
                        code.AppendLine($"            int window_size = {medWindowSize};");
                        code.AppendLine("            int half_window = window_size / 2;");
                        code.AppendLine("            std::vector<double> window(window_size);");
                        code.AppendLine("            ");
                        code.AppendLine("            for (int i = 0; i < length; i++) {");
                        code.AppendLine("                // 构建窗口");
                        code.AppendLine("                for (int j = 0; j < window_size; j++) {");
                        code.AppendLine("                    int idx = i - half_window + j;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    // 边界处理");
                        code.AppendLine("                    if (idx < 0) idx = -idx;");
                        code.AppendLine("                    else if (idx >= length) idx = 2 * length - idx - 2;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    if (idx >= 0 && idx < length) {");
                        code.AppendLine("                        window[j] = temp_input[idx];");
                        code.AppendLine("                    } else {");
                        code.AppendLine("                        window[j] = 0.0; // 默认值");
                        code.AppendLine("                    }");
                        code.AppendLine("                }");
                        code.AppendLine("                ");
                        code.AppendLine("                // 排序并取中值");
                        code.AppendLine("                std::sort(window.begin(), window.end());");
                        code.AppendLine("                temp_output[i] = window[window_size / 2];");
                        code.AppendLine("            }");
                        code.AppendLine("            ");
                        code.AppendLine("            // 交换输入输出数组");
                        code.AppendLine("            swap_temp = temp_input;");
                        code.AppendLine("            temp_input = temp_output;");
                        code.AppendLine("            temp_output = swap_temp;");
                        code.AppendLine("        }");
                        code.AppendLine("        ");
                        break;
                        
                    case NodeType.MovingAverage:
                        int maWindowSize = node.Parameters.ContainsKey("WindowSize") ? 
                                         Convert.ToInt32(node.Parameters["WindowSize"]) : 5;
                        
                        // 确保窗口大小为奇数
                        if (maWindowSize % 2 == 0) maWindowSize++;
                        
                        code.AppendLine("        {");
                        code.AppendLine($"            int window_size = {maWindowSize};");
                        code.AppendLine("            int half_window = window_size / 2;");
                        code.AppendLine("            ");
                        code.AppendLine("            for (int i = 0; i < length; i++) {");
                        code.AppendLine("                double sum = 0.0;");
                        code.AppendLine("                int count = 0;");
                        code.AppendLine("                ");
                        code.AppendLine("                for (int j = -half_window; j <= half_window; j++) {");
                        code.AppendLine("                    int idx = i + j;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    // 边界处理");
                        code.AppendLine("                    if (idx < 0) idx = -idx;");
                        code.AppendLine("                    else if (idx >= length) idx = 2 * length - idx - 2;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    if (idx >= 0 && idx < length) {");
                        code.AppendLine("                        sum += temp_input[idx];");
                        code.AppendLine("                        count++;");
                        code.AppendLine("                    }");
                        code.AppendLine("                }");
                        code.AppendLine("                ");
                        code.AppendLine("                if (count > 0) {");
                        code.AppendLine("                    temp_output[i] = sum / count;");
                        code.AppendLine("                } else {");
                        code.AppendLine("                    temp_output[i] = 0.0; // 默认值");
                        code.AppendLine("                }");
                        code.AppendLine("            }");
                        code.AppendLine("            ");
                        code.AppendLine("            // 交换输入输出数组");
                        code.AppendLine("            swap_temp = temp_input;");
                        code.AppendLine("            temp_input = temp_output;");
                        code.AppendLine("            temp_output = swap_temp;");
                        code.AppendLine("        }");
                        code.AppendLine("        ");
                        break;
                        
                    case NodeType.SignalSmooth:
                        int smoothWindowSize = node.Parameters.ContainsKey("WindowSize") ? 
                                             Convert.ToInt32(node.Parameters["WindowSize"]) : 5;
                        double smoothness = node.Parameters.ContainsKey("Smoothness") ? 
                                          Convert.ToDouble(node.Parameters["Smoothness"]) : 0.5;
                        
                        // 确保窗口大小为奇数
                        if (smoothWindowSize % 2 == 0) smoothWindowSize++;
                        
                        code.AppendLine("        {");
                        code.AppendLine($"            int window_size = {smoothWindowSize};");
                        code.AppendLine($"            double smooth_factor = {smoothness};");
                        code.AppendLine("            int half_window = window_size / 2;");
                        code.AppendLine("            ");
                        code.AppendLine("            for (int i = 0; i < length; i++) {");
                        code.AppendLine("                double sum = 0.0;");
                        code.AppendLine("                double weight_sum = 0.0;");
                        code.AppendLine("                ");
                        code.AppendLine("                for (int j = 0; j < window_size; j++) {");
                        code.AppendLine("                    int idx = i - half_window + j;");
                        code.AppendLine("                    double distance = abs(j - half_window) / (double)half_window;");
                        code.AppendLine("                    double weight = 1.0 - (smooth_factor * distance);");
                        code.AppendLine("                    ");
                        code.AppendLine("                    // 边界处理");
                        code.AppendLine("                    if (idx < 0) idx = -idx;");
                        code.AppendLine("                    else if (idx >= length) idx = 2 * length - idx - 2;");
                        code.AppendLine("                    ");
                        code.AppendLine("                    if (idx >= 0 && idx < length) {");
                        code.AppendLine("                        sum += temp_input[idx] * weight;");
                        code.AppendLine("                        weight_sum += weight;");
                        code.AppendLine("                    }");
                        code.AppendLine("                }");
                        code.AppendLine("                ");
                        code.AppendLine("                if (weight_sum > 0) {");
                        code.AppendLine("                    temp_output[i] = sum / weight_sum;");
                        code.AppendLine("                } else {");
                        code.AppendLine("                    temp_output[i] = temp_input[i]; // 保持原值");
                        code.AppendLine("                }");
                        code.AppendLine("            }");
                        code.AppendLine("            ");
                        code.AppendLine("            // 交换输入输出数组");
                        code.AppendLine("            swap_temp = temp_input;");
                        code.AppendLine("            temp_input = temp_output;");
                        code.AppendLine("            temp_output = swap_temp;");
                        code.AppendLine("        }");
                        code.AppendLine("        ");
                        break;
                }
            }
            
            // 最后将结果复制到输出数组
            code.AppendLine("        // 将最终结果复制到输出数组");
            code.AppendLine("        for (int i = 0; i < length; i++) {");
            code.AppendLine("            output[i] = temp_input[i];");
            code.AppendLine("        }");
            code.AppendLine("        ");
            code.AppendLine("        return 0; // 成功");
            code.AppendLine("    }");
            code.AppendLine("}");
            
            return code.ToString();
        }
        
        /// <summary>
        /// 生成C++头文件
        /// </summary>
        /// <param name="functionName">函数名称</param>
        /// <returns>C++头文件内容</returns>
        public static string GenerateCppHeader(string functionName = "process_signal")
        {
            var header = new StringBuilder();
            header.AppendLine("#ifndef FLOW_SIGNAL_PROCESSOR_H");
            header.AppendLine("#define FLOW_SIGNAL_PROCESSOR_H");
            header.AppendLine();
            header.AppendLine("#ifdef __cplusplus");
            header.AppendLine("extern \"C\" {");
            header.AppendLine("#endif");
            header.AppendLine();
            header.AppendLine($"int {functionName}(double* input, int length, double* output);");
            header.AppendLine();
            header.AppendLine("#ifdef __cplusplus");
            header.AppendLine("}");
            header.AppendLine("#endif");
            header.AppendLine();
            header.AppendLine("#endif // FLOW_SIGNAL_PROCESSOR_H");
            
            return header.ToString();
        }
        
        /// <summary>
        /// 保存为C++项目文件
        /// </summary>
        /// <param name="graph">流程图</param>
        /// <param name="outputPath">输出路径</param>
        /// <param name="dllName">DLL名称</param>
        public static void SaveAsCppProject(FlowGraph graph, string outputPath, string dllName = "FlowProcessor")
        {
            // 确保输出目录存在
            Directory.CreateDirectory(outputPath);
            
            // 生成源文件
            string cppSource = GenerateCppSource(graph, $"{dllName}_process");
            string headerSource = GenerateCppHeader($"{dllName}_process");
            
            // 写入文件
            File.WriteAllText(Path.Combine(outputPath, $"{dllName}.cpp"), cppSource);
            File.WriteAllText(Path.Combine(outputPath, $"{dllName}.h"), headerSource);
            
            // 生成CMakeLists.txt
            var cmakeContent = $"cmake_minimum_required(VERSION 3.15)\n";
            cmakeContent += $"project({dllName})\n\n";
            cmakeContent += $"set(CMAKE_CXX_STANDARD 17)\n\n";
            cmakeContent += $"add_library({dllName} SHARED\n";
            cmakeContent += $"    {dllName}.cpp\n";
            cmakeContent += $")\n";
            
            File.WriteAllText(Path.Combine(outputPath, "CMakeLists.txt"), cmakeContent);
        }
    }
}