using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NewAvalonia.FlowEditor;

namespace NewAvalonia.FlowEditor
{
    /// <summary>
    /// 特定算法DLL生成器
    /// </summary>
    public class SpecificAlgorithmDllGenerator
    {
        /// <summary>
        /// 生成特定算法的DLL源代码
        /// </summary>
        /// <param name="nodeType">算法节点类型</param>
        /// <param name="parameters">算法参数</param>
        /// <param name="dllName">DLL名称</param>
        /// <returns>生成的C++源代码</returns>
        public static string GenerateSpecificAlgorithmDll(NodeType nodeType, Dictionary<string, object> parameters, string dllName)
        {
            var code = new StringBuilder();
            
            // 包含必要的头文件
            code.AppendLine("#include <vector>");
            code.AppendLine("#include <cmath>");
            code.AppendLine("#include <algorithm>");
            code.AppendLine("#include <cstring>");
            code.AppendLine();
            
            // 根据算法类型生成相应的算法实现
            switch (nodeType)
            {
                case NodeType.Gaussian:
                    GenerateGaussianFilterImplementation(code, parameters);
                    break;
                case NodeType.Median:
                    GenerateMedianFilterImplementation(code, parameters);
                    break;
                case NodeType.MovingAverage:
                    GenerateMovingAverageFilterImplementation(code, parameters);
                    break;
                case NodeType.SignalSmooth:
                    GenerateSignalSmoothImplementation(code, parameters);
                    break;
                default:
                    throw new NotSupportedException($"不支持的算法类型: {nodeType}");
            }
            
            // C风格导出函数
            code.AppendLine("extern \"C\" {");
            code.AppendLine($"    __declspec(dllexport) int {dllName.ToLower()}_process(double* input, int length, double* output) {{");
            code.AppendLine("        if (!input || !output || length <= 0) {");
            code.AppendLine("            return -1; // 错误");
            code.AppendLine("        }");
            code.AppendLine("        ");
            
            // 调用相应的算法函数
            switch (nodeType)
            {
                case NodeType.Gaussian:
                    code.AppendLine("        return gaussian_filter(input, length, output);");
                    break;
                case NodeType.Median:
                    code.AppendLine("        return median_filter(input, length, output);");
                    break;
                case NodeType.MovingAverage:
                    code.AppendLine("        return moving_average_filter(input, length, output);");
                    break;
                case NodeType.SignalSmooth:
                    code.AppendLine("        return signal_smooth(input, length, output);");
                    break;
            }
            
            code.AppendLine("    }");
            code.AppendLine("}");
            
            return code.ToString();
        }
        
        /// <summary>
        /// 生成高斯滤波算法实现
        /// </summary>
        private static void GenerateGaussianFilterImplementation(StringBuilder code, Dictionary<string, object> parameters)
        {
            double sigma = parameters.ContainsKey("Sigma") ? 
                          Convert.ToDouble(parameters["Sigma"]) : 1.0;
            int kernelSize = parameters.ContainsKey("KernelSize") ? 
                           Convert.ToInt32(parameters["KernelSize"]) : 5;
            
            // 确保核大小为奇数
            if (kernelSize % 2 == 0) kernelSize++;
            
            code.AppendLine("// 高斯核生成函数");
            code.AppendLine("std::vector<double> generate_gaussian_kernel(int size, double sigma) {");
            code.AppendLine("    std::vector<double> kernel(size);");
            code.AppendLine("    int center = size / 2;");
            code.AppendLine("    double sum = 0.0;");
            code.AppendLine("    ");
            code.AppendLine("    for (int i = 0; i < size; i++) {");
            code.AppendLine($"        double x = i - center;");
            code.AppendLine($"        kernel[i] = exp(-(x * x) / (2 * {sigma} * {sigma}));");
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
            
            code.AppendLine("// 高斯滤波函数");
            code.AppendLine("int gaussian_filter(double* input, int length, double* output) {");
            code.AppendLine($"    int kernel_size = {kernelSize};");
            code.AppendLine($"    double sigma_val = {sigma};");
            code.AppendLine("    auto kernel = generate_gaussian_kernel(kernel_size, sigma_val);");
            code.AppendLine("    int half_kernel = kernel_size / 2;");
            code.AppendLine("    ");
            code.AppendLine("    for (int i = 0; i < length; i++) {");
            code.AppendLine("        double sum = 0.0;");
            code.AppendLine("        ");
            code.AppendLine("        for (int j = 0; j < kernel_size; j++) {");
            code.AppendLine("            int idx = i - half_kernel + j;");
            code.AppendLine("            ");
            code.AppendLine("            // 边界处理");
            code.AppendLine("            if (idx < 0) idx = -idx;");
            code.AppendLine("            else if (idx >= length) idx = 2 * length - idx - 2;");
            code.AppendLine("            ");
            code.AppendLine("            if (idx >= 0 && idx < length) {");
            code.AppendLine("                sum += input[idx] * kernel[j];");
            code.AppendLine("            }");
            code.AppendLine("        }");
            code.AppendLine("        output[i] = sum;");
            code.AppendLine("    }");
            code.AppendLine("    ");
            code.AppendLine("    return 0; // 成功");
            code.AppendLine("}");
            code.AppendLine();
        }
        
        /// <summary>
        /// 生成中值滤波算法实现
        /// </summary>
        private static void GenerateMedianFilterImplementation(StringBuilder code, Dictionary<string, object> parameters)
        {
            int windowSize = parameters.ContainsKey("WindowSize") ? 
                           Convert.ToInt32(parameters["WindowSize"]) : 3;
            
            // 确保窗口大小为奇数
            if (windowSize % 2 == 0) windowSize++;
            
            code.AppendLine("// 中值滤波函数");
            code.AppendLine("int median_filter(double* input, int length, double* output) {");
            code.AppendLine($"    int window_size = {windowSize};");
            code.AppendLine("    int half_window = window_size / 2;");
            code.AppendLine("    std::vector<double> window(window_size);");
            code.AppendLine("    ");
            code.AppendLine("    for (int i = 0; i < length; i++) {");
            code.AppendLine("        // 构建窗口");
            code.AppendLine("        for (int j = 0; j < window_size; j++) {");
            code.AppendLine("            int idx = i - half_window + j;");
            code.AppendLine("            ");
            code.AppendLine("            // 边界处理");
            code.AppendLine("            if (idx < 0) idx = -idx;");
            code.AppendLine("            else if (idx >= length) idx = 2 * length - idx - 2;");
            code.AppendLine("            ");
            code.AppendLine("            if (idx >= 0 && idx < length) {");
            code.AppendLine("                window[j] = input[idx];");
            code.AppendLine("            } else {");
            code.AppendLine("                window[j] = 0.0; // 默认值");
            code.AppendLine("            }");
            code.AppendLine("        }");
            code.AppendLine("        ");
            code.AppendLine("        // 排序并取中值");
            code.AppendLine("        std::sort(window.begin(), window.end());");
            code.AppendLine("        output[i] = window[window_size / 2];");
            code.AppendLine("    }");
            code.AppendLine("    ");
            code.AppendLine("    return 0; // 成功");
            code.AppendLine("}");
            code.AppendLine();
        }
        
        /// <summary>
        /// 生成移动平均滤波算法实现
        /// </summary>
        private static void GenerateMovingAverageFilterImplementation(StringBuilder code, Dictionary<string, object> parameters)
        {
            int windowSize = parameters.ContainsKey("WindowSize") ? 
                           Convert.ToInt32(parameters["WindowSize"]) : 5;
            
            // 确保窗口大小为奇数
            if (windowSize % 2 == 0) windowSize++;
            
            code.AppendLine("// 移动平均滤波函数");
            code.AppendLine("int moving_average_filter(double* input, int length, double* output) {");
            code.AppendLine($"    int window_size = {windowSize};");
            code.AppendLine("    int half_window = window_size / 2;");
            code.AppendLine("    ");
            code.AppendLine("    for (int i = 0; i < length; i++) {");
            code.AppendLine("        double sum = 0.0;");
            code.AppendLine("        int count = 0;");
            code.AppendLine("        ");
            code.AppendLine("        for (int j = -half_window; j <= half_window; j++) {");
            code.AppendLine("            int idx = i + j;");
            code.AppendLine("            ");
            code.AppendLine("            // 边界处理");
            code.AppendLine("            if (idx < 0) idx = -idx;");
            code.AppendLine("            else if (idx >= length) idx = 2 * length - idx - 2;");
            code.AppendLine("            ");
            code.AppendLine("            if (idx >= 0 && idx < length) {");
            code.AppendLine("                sum += input[idx];");
            code.AppendLine("                count++;");
            code.AppendLine("            }");
            code.AppendLine("        }");
            code.AppendLine("        ");
            code.AppendLine("        if (count > 0) {");
            code.AppendLine("            output[i] = sum / count;");
            code.AppendLine("        } else {");
            code.AppendLine("            output[i] = 0.0; // 默认值");
            code.AppendLine("        }");
            code.AppendLine("    }");
            code.AppendLine("    ");
            code.AppendLine("    return 0; // 成功");
            code.AppendLine("}");
            code.AppendLine();
        }
        
        /// <summary>
        /// 生成信号平滑算法实现
        /// </summary>
        private static void GenerateSignalSmoothImplementation(StringBuilder code, Dictionary<string, object> parameters)
        {
            int windowSize = parameters.ContainsKey("WindowSize") ? 
                           Convert.ToInt32(parameters["WindowSize"]) : 5;
            double smoothness = parameters.ContainsKey("Smoothness") ? 
                             Convert.ToDouble(parameters["Smoothness"]) : 0.5;
            
            // 确保窗口大小为奇数
            if (windowSize % 2 == 0) windowSize++;
            
            code.AppendLine("// 信号平滑函数");
            code.AppendLine("int signal_smooth(double* input, int length, double* output) {");
            code.AppendLine($"    int window_size = {windowSize};");
            code.AppendLine($"    double smooth_factor = {smoothness};");
            code.AppendLine("    int half_window = window_size / 2;");
            code.AppendLine("    ");
            code.AppendLine("    for (int i = 0; i < length; i++) {");
            code.AppendLine("        double sum = 0.0;");
            code.AppendLine("        double weight_sum = 0.0;");
            code.AppendLine("        ");
            code.AppendLine("        for (int j = 0; j < window_size; j++) {");
            code.AppendLine("            int idx = i - half_window + j;");
            code.AppendLine("            double distance = abs(j - half_window) / (double)half_window;");
            code.AppendLine("            double weight = 1.0 - (smooth_factor * distance);");
            code.AppendLine("            ");
            code.AppendLine("            // 边界处理");
            code.AppendLine("            if (idx < 0) idx = -idx;");
            code.AppendLine("            else if (idx >= length) idx = 2 * length - idx - 2;");
            code.AppendLine("            ");
            code.AppendLine("            if (idx >= 0 && idx < length) {");
            code.AppendLine("                sum += input[idx] * weight;");
            code.AppendLine("                weight_sum += weight;");
            code.AppendLine("            }");
            code.AppendLine("        }");
            code.AppendLine("        ");
            code.AppendLine("        if (weight_sum > 0) {");
            code.AppendLine("            output[i] = sum / weight_sum;");
            code.AppendLine("        } else {");
            code.AppendLine("            output[i] = input[i]; // 保持原值");
            code.AppendLine("        }");
            code.AppendLine("    }");
            code.AppendLine("    ");
            code.AppendLine("    return 0; // 成功");
            code.AppendLine("}");
            code.AppendLine();
        }
        
        /// <summary>
        /// 保存特定算法的DLL项目
        /// </summary>
        /// <param name="nodeType">算法节点类型</param>
        /// <param name="parameters">算法参数</param>
        /// <param name="outputPath">输出路径</param>
        /// <param name="dllName">DLL名称</param>
        public static void SaveSpecificAlgorithmDll(NodeType nodeType, Dictionary<string, object> parameters, string outputPath, string dllName)
        {
            // 确保输出目录存在
            Directory.CreateDirectory(outputPath);
            
            // 生成源文件
            string cppSource = GenerateSpecificAlgorithmDll(nodeType, parameters, dllName);
            string headerSource = GenerateSpecificAlgorithmHeader(dllName);
            
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
        
        /// <summary>
        /// 生成特定算法的头文件
        /// </summary>
        /// <param name="dllName">DLL名称</param>
        /// <returns>头文件内容</returns>
        private static string GenerateSpecificAlgorithmHeader(string dllName)
        {
            var header = new StringBuilder();
            header.AppendLine($"#ifndef {dllName.ToUpper()}_H");
            header.AppendLine($"#define {dllName.ToUpper()}_H");
            header.AppendLine();
            header.AppendLine("#ifdef __cplusplus");
            header.AppendLine("extern \"C\" {");
            header.AppendLine("#endif");
            header.AppendLine();
            header.AppendLine($"int {dllName.ToLower()}_process(double* input, int length, double* output);");
            header.AppendLine();
            header.AppendLine("#ifdef __cplusplus");
            header.AppendLine("}");
            header.AppendLine("#endif");
            header.AppendLine();
            header.AppendLine($"#endif // {dllName.ToUpper()}_H");
            
            return header.ToString();
        }
    }
}