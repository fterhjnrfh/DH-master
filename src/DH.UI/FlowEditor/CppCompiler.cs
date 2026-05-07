using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace NewAvalonia.FlowEditor
{
    /// <summary>
    /// C++代码编译器
    /// </summary>
    public class CppCompiler
    {
        /// <summary>
        /// 编译C++代码为动态库
        /// </summary>
        /// <param name="cppSourcePath">C++源文件路径</param>
        /// <param name="outputDllPath">输出DLL路径</param>
        /// <returns>编译是否成功</returns>
        public static async Task<bool> CompileToDll(string cppSourcePath, string outputDllPath)
        {
            try
            {
                // 检查源文件是否存在
                if (!File.Exists(cppSourcePath))
                {
                    Console.WriteLine($"C++源文件不存在: {cppSourcePath}");
                    return false;
                }

                // 获取源文件所在目录
                var sourceDir = Path.GetDirectoryName(cppSourcePath);
                var sourceFileName = Path.GetFileNameWithoutExtension(cppSourcePath);
                
                // 创建编译命令（这里假使用g++，实际应用中可能需要检测可用的编译器）
                var startInfo = new ProcessStartInfo
                {
                    FileName = "g++",
                    Arguments = $"-shared -fPIC -O2 -m64 \"{cppSourcePath}\" -o \"{outputDllPath}\" -std=c++17",
                    WorkingDirectory = sourceDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("C++代码编译成功!");
                        Console.WriteLine(output);
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"C++代码编译失败，退出代码: {process.ExitCode}");
                        Console.WriteLine(error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"编译过程中出现异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 使用Visual Studio编译器编译（Windows）
        /// </summary>
        /// <param name="cppSourcePath">C++源文件路径</param>
        /// <param name="outputDllPath">输出DLL路径</param>
        /// <returns>编译是否成功</returns>
        public static async Task<bool> CompileToDllWithMSVC(string cppSourcePath, string outputDllPath)
        {
            try
            {
                // 检查源文件是否存在
                if (!File.Exists(cppSourcePath))
                {
                    Console.WriteLine($"C++源文件不存在: {cppSourcePath}");
                    return false;
                }

                // 获取源文件所在目录
                var sourceDir = Path.GetDirectoryName(cppSourcePath);
                var sourceFileName = Path.GetFileNameWithoutExtension(cppSourcePath);
                
                // 使用MSVC编译器编译（需要VS环境）
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cl",
                    Arguments = $"/LD \"{cppSourcePath}\" /Fe:\"{outputDllPath}\" /EHsc /std:c++17 /arch:AVX2",
                    WorkingDirectory = sourceDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0 || error.Contains("LINK : warning LNK4044:"))
                    {
                        Console.WriteLine("C++代码编译成功!");
                        Console.WriteLine(output);
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"C++代码编译失败，退出代码: {process.ExitCode}");
                        Console.WriteLine(error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"编译过程中出现异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 自动检测并使用可用的编译器进行编译
        /// </summary>
        /// <param name="cppSourcePath">C++源文件路径</param>
        /// <param name="outputDllPath">输出DLL路径</param>
        /// <returns>编译是否成功</returns>
        public static async Task<bool> CompileToDllAuto(string cppSourcePath, string outputDllPath)
        {
            // 首先尝试使用MSVC (Visual Studio编译器)
            if (await IsCompilerAvailable("cl"))
            {
                Console.WriteLine("检测到Visual Studio编译器，使用MSVC编译...");
                return await CompileToDllWithMSVC(cppSourcePath, outputDllPath);
            }
            // 如果MSVC不可用，则尝试g++
            else if (await IsCompilerAvailable("g++"))
            {
                Console.WriteLine("检测到g++编译器，使用g++编译...");
                return await CompileToDll(cppSourcePath, outputDllPath);
            }
            else
            {
                Console.WriteLine("未找到可用的C++编译器 (需要安装Visual Studio或MinGW-w64)");
                return false;
            }
        }

        /// <summary>
        /// 检查编译器是否可用
        /// </summary>
        /// <param name="compilerName">编译器名称</param>
        /// <returns>编译器是否可用</returns>
        public static async Task<bool> IsCompilerAvailable(string compilerName)
        {
            try
            {
                Console.WriteLine($"正在检测编译器: {compilerName}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = compilerName,
                    Arguments = compilerName == "cl" ? "/?" : "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    
                    Console.WriteLine($"{compilerName} 检测结果 - 退出码: {process.ExitCode}");
                    Console.WriteLine($"{compilerName} 输出: {output}");
                    Console.WriteLine($"{compilerName} 错误: {error}");
                    
                    // 对于 cl 编译器，只要能启动就说明存在
                    if (compilerName == "cl")
                    {
                        return process.ExitCode >= 0; // cl 启动成功（即使显示帮助）
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检测编译器 {compilerName} 时发生异常: {ex.Message}");
                return false;
            }
        }
    }
}