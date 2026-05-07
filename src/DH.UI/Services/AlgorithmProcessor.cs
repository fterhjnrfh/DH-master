using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OxyPlot;
using AlgorithmModule;

namespace NewAvalonia.Services
{
    public class AlgorithmProcessor
    {
        public class AlgorithmDefinition
        {
            public string AlgorithmName { get; set; } = "";
            public string AlgorithmType { get; set; } = "";
            public string Description { get; set; } = "";
            public string Version { get; set; } = "";
            public Dictionary<string, AlgorithmParameter> Parameters { get; set; } = new();
            public AlgorithmImplementation Implementation { get; set; } = new();
            // 原始JSON（调试用，可选）
            public string? RawJson { get; set; }
        }

        public class AlgorithmParameter
        {
            public string Type { get; set; } = "";
            public object? Default { get; set; }
                = new();
            public object? Min { get; set; } = new();
            public object? Max { get; set; } = new();
            public string Description { get; set; } = "";
        }

        public class AlgorithmImplementation
        {
            public string Method { get; set; } = "";
            public string Class { get; set; } = "";
            // 新增：脚本型实现兼容当前 .xtj 结构（algorithm: { type, code }）
            public string Type { get; set; } = ""; // 例如："csharp"
            public string Code { get; set; } = ""; // 例如：包含 Process 方法的代码
        }

        // 代码缓存（按 code 文本缓存已编译的委托）
        private static readonly Dictionary<string, Func<double[], IDictionary<string, object>, double[]>> s_codeCache
            = new(StringComparer.Ordinal);

        /// <summary>
        /// 加载.xtj算法文件（兼容两种结构：内置结构 和 示例结构）
        /// </summary>
        // 最近一次错误信息（供上层弹窗展示）
        public string? LastError { get; private set; }
        public void ClearLastError() => LastError = null;

        public async Task<AlgorithmDefinition?> LoadAlgorithmAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    LastError = $"算法文件不存在: {filePath}";
                    Console.WriteLine(LastError);
                    return null;
                }

                var ext = Path.GetExtension(filePath)?.ToLowerInvariant();

                // 检查是否为 .dll 文件
                if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    return await LoadAlgorithmFromDllAsync(filePath);
                }

                // 其他文件类型（.xtj, .xtjs）按原逻辑处理
                var jsonContent = await File.ReadAllTextAsync(filePath);

                // 根据扩展名判断是否为加密文件
                if (string.Equals(ext, ".xtjs", StringComparison.Ordinal))
                {
                    var decrypted = XtjCrypto.TryDecryptIfEncrypted(jsonContent, out var decErr);
                    if (decrypted == null)
                    {
                        LastError = decErr ?? "算法文件解密失败";
                        Console.WriteLine(LastError);
                        return null;
                    }
                    // 若返回与原文一致，说明内容并非加密格式，但扩展名为 .xtjs，视为格式错误
                    if (decrypted == jsonContent)
                    {
                        LastError = "文件扩展名为 .xtjs，但内容不是加密格式";
                        Console.WriteLine(LastError);
                        return null;
                    }
                    jsonContent = decrypted;
                }
                // .xtj 按明文 JSON 处理；其他扩展名暂按明文处理

                var def = ParseAlgorithmJson(jsonContent);
                if (def == null)
                {
                    if (string.IsNullOrEmpty(LastError)) LastError = "解析算法文件失败";
                    return null;
                }
                def.RawJson = jsonContent;
                return def;
            }
            catch (Exception ex)
            {
                LastError = $"读取算法文件失败: {ex.Message}";
                Console.WriteLine(LastError);
                return null;
            }
        }

        private AlgorithmDefinition? ParseAlgorithmJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var def = new AlgorithmDefinition();

                // 兼容字段：AlgorithmName/name
                def.AlgorithmName = GetString(root, "AlgorithmName") ?? GetString(root, "name") ?? string.Empty;
                def.Description = GetString(root, "Description") ?? GetString(root, "description") ?? string.Empty;
                def.Version = GetString(root, "Version") ?? GetString(root, "version") ?? string.Empty;
                def.AlgorithmType = GetString(root, "AlgorithmType") ?? string.Empty; // 内置分发依据（可为空）

                // Parameters（两种结构都一致：parameters 节点）
                if (root.TryGetProperty("Parameters", out var parametersNode) || root.TryGetProperty("parameters", out parametersNode))
                {
                    foreach (var prop in parametersNode.EnumerateObject())
                    {
                        var p = new AlgorithmParameter();
                        var v = prop.Value;
                        p.Type = GetString(v, "Type") ?? GetString(v, "type") ?? string.Empty;
                        p.Default = GetJsonElementValue(v, "Default") ?? GetJsonElementValue(v, "default") ?? new object();
                        p.Min = GetJsonElementValue(v, "Min") ?? GetJsonElementValue(v, "min") ?? new object();
                        p.Max = GetJsonElementValue(v, "Max") ?? GetJsonElementValue(v, "max") ?? new object();
                        p.Description = GetString(v, "Description") ?? GetString(v, "description") ?? string.Empty;
                        def.Parameters[prop.Name] = p;
                    }
                }

                // Implementation（两种可能：Implementation 或 algorithm）
                if (root.TryGetProperty("Implementation", out var implNode))
                {
                    def.Implementation.Method = GetString(implNode, "Method") ?? string.Empty;
                    def.Implementation.Class = GetString(implNode, "Class") ?? string.Empty;
                    def.Implementation.Type = GetString(implNode, "Type") ?? string.Empty;
                    def.Implementation.Code = GetString(implNode, "Code") ?? string.Empty;
                }
                if (root.TryGetProperty("algorithm", out var algoNode))
                {
                    def.Implementation.Type = GetString(algoNode, "type") ?? def.Implementation.Type;
                    def.Implementation.Code = GetString(algoNode, "code") ?? def.Implementation.Code;
                }

                return def;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析算法JSON失败: {ex.Message}");
                return null;
            }
        }

        private static string? GetString(JsonElement parent, string name)
        {
            if (parent.TryGetProperty(name, out var v))
            {
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            }
            return null;
        }

        private static object? GetJsonElementValue(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var i64) ? (object)i64 : (v.TryGetDouble(out var d) ? d : null),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        /// <summary>
        /// 从 .dll 文件加载算法定义
        /// </summary>
        private async Task<AlgorithmDefinition?> LoadAlgorithmFromDllAsync(string filePath)
        {
            try
            {
                await Task.Yield(); // 保证方法真正异步以避免同步上下文阻塞
                Console.WriteLine($"\n=== 开始加载 DLL: {filePath} ===");
                
                // 首先尝试加载为托管 DLL（实现 IAlgorithm 接口或有 Process 方法）
                try
                {
                    var assembly = Assembly.LoadFrom(filePath);
                    var algorithmTypes = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && 
                               typeof(IAlgorithm).IsAssignableFrom(t));

                    if (!algorithmTypes.Any())
                    {
                        // 查找具有 Process 方法的托管类型
                        var processorTypes = assembly.GetTypes()
                            .Where(t => t.IsClass && !t.IsAbstract && 
                                   HasProcessMethod(t));

                        if (processorTypes.Any())
                        {
                            var processorType = processorTypes.First();
                            return CreateAlgorithmDefinitionFromType(processorType);
                        }
                    }
                    else
                    {
                        // 发现托管的 IAlgorithm 实现
                        var algorithmType = algorithmTypes.First();
                        if (Activator.CreateInstance(algorithmType) is not IAlgorithm algorithmInstance)
                        {
                            Console.WriteLine($"无法创建算法实例: {algorithmType.FullName}");
                            return null;
                        }

                        var def = new AlgorithmDefinition
                        {
                            AlgorithmName = algorithmInstance.Name ?? string.Empty,
                            Description = algorithmInstance.Description ?? string.Empty,
                            Version = algorithmInstance.Version ?? string.Empty,
                            AlgorithmType = algorithmInstance.GetType().AssemblyQualifiedName
                                ?? algorithmInstance.Name
                                ?? string.Empty,
                            Parameters = new Dictionary<string, AlgorithmParameter>(),
                            Implementation = new AlgorithmImplementation
                            {
                                Method = "DllLoad",
                                Class = algorithmType.FullName
                                    ?? algorithmType.Name
                                    ?? string.Empty,
                                Type = "dll_managed",
                                Code = filePath // 存储 DLL 路径
                            }
                        };

                        // 添加默认参数
                        foreach (var param in algorithmInstance.DefaultParameters)
                        {
                            var parameterValue = param.Value;
                            var parameterType = parameterValue?.GetType().Name ?? "Object";
                            def.Parameters[param.Key] = new AlgorithmParameter
                            {
                                Type = parameterType,
                                Default = parameterValue,
                                Min = parameterValue,
                                Max = parameterValue,
                                Description = param.Key
                            };
                        }

                        Console.WriteLine($"=== 成功加载托管 DLL: {filePath} ===");
                        return def;
                    }
                }
                catch (BadImageFormatException)
                {
                    // 如果是 BadImageFormatException，说明这是一个非托管 DLL
                    Console.WriteLine($"检测到非托管 DLL: {filePath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"加载为托管 DLL 时出错 ({filePath}): {ex.Message}");
                    // 如果加载为托管 DLL 失败，继续尝试非托管 DLL
                }

                // 如果托管加载失败，尝试加载为非托管 DLL（C 风格导出）
                // 检查常见的 C 风格导出函数
                string[] possibleFunctionNames = { 
                    "gaussianfilter_process", 
                    "medianfilter_process", 
                    "movingaveragefilter_process", 
                    "signalsmooth_process",
                    "ProcessSignal", // 通用处理函数名
                    "process" // 通用处理函数名
                };

                Console.WriteLine($"检查非托管 DLL 导出函数: {filePath}");
                
                // 检查预定义的函数名
                foreach (string functionName in possibleFunctionNames)
                {
                    Console.WriteLine($"尝试函数: {functionName}");
                    
                    if (TryCheckUnmanagedExportFunction(filePath, functionName, out var def))
                    {
                        Console.WriteLine($"=== 成功加载非托管 DLL: {filePath} ===");
                        return def;
                    }
                }
                
                // 检查动态算法组合函数名模式
                Console.WriteLine("尝试算法组合函数名模式...");
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                
                // 尝试构建基于文件名的函数名模式（如 "GaussianFilter_SignalSmooth_process"）
                string dynamicFunctionName = fileNameWithoutExtension.ToLower() + "_process";
                Console.WriteLine($"尝试动态函数: {dynamicFunctionName}");
                
                if (TryCheckUnmanagedExportFunction(filePath, dynamicFunctionName, out var dynamicDef))
                {
                    Console.WriteLine($"=== 成功加载非托管 DLL 通过动态函数名: {filePath} ===");
                    return dynamicDef;
                }
                
                // 尝试其他可能的组合模式
                string[] algorithmSuffixes = { "_process", "process", "_Process", "Process" };
                foreach (string suffix in algorithmSuffixes)
                {
                    string tryFunctionName = fileNameWithoutExtension + suffix;
                    Console.WriteLine($"尝试动态函数: {tryFunctionName}");
                    
                    if (TryCheckUnmanagedExportFunction(filePath, tryFunctionName, out var tryDef))
                    {
                        Console.WriteLine($"=== 成功加载非托管 DLL 通过动态函数名: {filePath} ===");
                        return tryDef;
                    }
                }

                LastError = "DLL 文件中未找到实现 IAlgorithm 接口、具有正确 Process 方法的类，或支持的 C 风格导出函数";
                Console.WriteLine(LastError);
                Console.WriteLine($"=== 加载 DLL 失败: {filePath} ===");
                return null;
            }
            catch (Exception ex)
            {
                LastError = $"加载 DLL 算法文件失败: {ex.Message}";
                Console.WriteLine(LastError);
                Console.WriteLine($"=== 加载 DLL 失败: {filePath} ===");
                return null;
            }
        }

        /// <summary>
        /// 检查非托管 DLL 是否包含特定的 C 风格导出函数
        /// </summary>
        private bool HasCStyleExport(string dllPath, string functionName)
        {
            try
            {
                Console.WriteLine($"尝试加载 DLL: {dllPath}");
                // 使用 P/Invoke 调用 kernel32.dll 中的 GetProcAddress 来检查函数是否存在
                IntPtr hModule = LoadLibrary(dllPath);
                if (hModule == IntPtr.Zero)
                {
                    Console.WriteLine($"无法加载 DLL: {dllPath}");
                    return false;
                }

                Console.WriteLine($"DLL 加载成功，尝试获取函数: {functionName}");
                IntPtr functionPtr = GetProcAddress(hModule, functionName);
                bool hasExport = functionPtr != IntPtr.Zero;
                Console.WriteLine($"GetProcAddress 返回: {(hasExport ? "成功" : "失败")}, 指针值: {functionPtr}");

                FreeLibrary(hModule);
                return hasExport;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查 DLL 导出函数时出错: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查非托管 DLL 的导出函数并返回算法定义（如果找到）
        /// </summary>
        private bool TryCheckUnmanagedExportFunction(string dllPath, string functionName, out AlgorithmDefinition? algorithmDefinition)
        {
            algorithmDefinition = null;
            
            try
            {
                Console.WriteLine($"尝试加载 DLL: {dllPath}");
                IntPtr hModule = LoadLibrary(dllPath);
                if (hModule == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    Console.WriteLine($"无法加载 DLL: {dllPath}，错误代码: {errorCode}");
                    return false;
                }

                Console.WriteLine($"DLL 加载成功，尝试获取函数: {functionName}");
                IntPtr functionPtr = GetProcAddress(hModule, functionName);
                bool hasExport = functionPtr != IntPtr.Zero;
                Console.WriteLine($"GetProcAddress 返回: {(hasExport ? "成功" : "失败")}, 指针值: {functionPtr}");

                FreeLibrary(hModule);

                if (hasExport)
                {
                    algorithmDefinition = new AlgorithmDefinition
                    {
                        AlgorithmName = $"{functionName} 算法",
                        Description = $"从非托管 DLL 加载的算法: {Path.GetFileName(dllPath)} - 函数: {functionName}",
                        Version = "1.0.0",
                        AlgorithmType = "dll_unmanaged",
                        Parameters = new Dictionary<string, AlgorithmParameter>(),
                        Implementation = new AlgorithmImplementation
                        {
                            Method = functionName,
                            Class = dllPath, // 存储 DLL 路径
                            Type = "dll_unmanaged",
                            Code = dllPath // 存储 DLL 路径
                        }
                    };
                    Console.WriteLine($"找到函数: {functionName}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查 DLL 导出函数时出错: {ex.Message}");
                return false;
            }
        }

        // P/Invoke 声明用于检查 DLL 导出
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        /// <summary>
        /// 检查类型是否有正确的 Process 方法
        /// </summary>
        private static bool HasProcessMethod(Type type)
        {
            var method = type.GetMethod("Process", 
                BindingFlags.Public | BindingFlags.Static,
                new Type[] { typeof(double[]), typeof(IDictionary<string, object>) });

            if (method != null && method.ReturnType == typeof(double[]))
                return true;

            // 也检查其他可能的重载
            method = type.GetMethod("Process",
                BindingFlags.Public | BindingFlags.Static,
                new Type[] { typeof(double[]), typeof(Dictionary<string, object>) });

            return method != null && method.ReturnType == typeof(double[]);
        }

        /// <summary>
        /// 从类型创建算法定义（当没有 IAlgorithm 接口时）
        /// </summary>
        private AlgorithmDefinition CreateAlgorithmDefinitionFromType(Type processorType)
        {
            var def = new AlgorithmDefinition
            {
                AlgorithmName = processorType.Name,
                Description = $"从 DLL 加载的算法: {processorType.Name}",
                Version = "1.0.0",
                AlgorithmType = "dll",
                Parameters = new Dictionary<string, AlgorithmParameter>(),
                Implementation = new AlgorithmImplementation
                {
                    Method = "Process",
                    Class = processorType.FullName ?? processorType.Name,
                    Type = "dll",
                    Code = processorType.Assembly.Location
                }
            };

            // 这里可以尝试从特性或其他方式获取参数信息
            // 暂时设置为空参数或从方法参数推断

            return def;
        }

        /// <summary>
        /// 应用算法到数据点
        /// </summary>
        public List<DataPoint> ApplyAlgorithm(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters = null)
        {
            if (algorithm == null || inputData == null || inputData.Count == 0)
                return inputData ?? new List<DataPoint>();

            try
            {
                // 优先：脚本型实现（兼容当前 .xtj 的 algorithm: { type: "csharp", code: "..." }）
                if (!string.IsNullOrWhiteSpace(algorithm.Implementation?.Type) &&
                    algorithm.Implementation.Type.Equals("csharp", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(algorithm.Implementation.Code))
                {
                    return ApplyCSharpScript(inputData, algorithm, parameters);
                }

                // DLL 类型算法
                if (!string.IsNullOrWhiteSpace(algorithm.Implementation?.Type) &&
                    (algorithm.Implementation.Type.Equals("dll", StringComparison.OrdinalIgnoreCase) ||
                     algorithm.Implementation.Type.Equals("dll_managed", StringComparison.OrdinalIgnoreCase) ||
                     algorithm.Implementation.Type.Equals("dll_unmanaged", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(algorithm.Implementation.Code))
                {
                    return ApplyDllAlgorithm(inputData, algorithm, parameters);
                }

                // 其次：内置算法类型分发
                switch ((algorithm.AlgorithmType ?? string.Empty).ToLowerInvariant())
                {
                    case "movingaverage":
                        return ApplyMovingAverageFilter(inputData, algorithm, parameters);

                    case "gaussian":
                        return ApplyGaussianFilter(inputData, algorithm, parameters);

                    default:
                        Console.WriteLine($"未知的算法类型: {algorithm.AlgorithmType}");
                        return inputData;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"应用算法时发生错误: {ex.Message}");
                return inputData;
            }
        }

        private List<DataPoint> ApplyMovingAverageFilter(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters)
        {
            // 获取参数
            int windowSize = GetParameterValue<int>(algorithm, parameters, "windowSize", 5);
            double strength = GetParameterValue<double>(algorithm, parameters, "strength", 1.0);

            var smoothedPoints = new List<DataPoint>();
            int halfWindow = Math.Max(1, windowSize) / 2;

            for (int i = 0; i < inputData.Count; i++)
            {
                double sum = 0;
                int count = 0;

                // 计算窗口内的平均值
                for (int j = Math.Max(0, i - halfWindow); j <= Math.Min(inputData.Count - 1, i + halfWindow); j++)
                {
                    sum += inputData[j].Y;
                    count++;
                }

                double smoothedY = count > 0 ? sum / count : inputData[i].Y;

                // 应用强度参数（原始值和平滑值的混合）
                double finalY = inputData[i].Y * (1 - strength) + smoothedY * strength;

                smoothedPoints.Add(new DataPoint(inputData[i].X, finalY));
            }

            return smoothedPoints;
        }

        private List<DataPoint> ApplyGaussianFilter(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters)
        {
            // 简单的高斯滤波实现
            int windowSize = GetParameterValue<int>(algorithm, parameters, "windowSize", 5);
            double sigma = GetParameterValue<double>(algorithm, parameters, "sigma", 1.0);

            var smoothedPoints = new List<DataPoint>();
            int halfWindow = Math.Max(1, windowSize) / 2;

            // 生成高斯权重
            var weights = new double[Math.Max(1, windowSize)];
            double weightSum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                int offset = i - halfWindow;
                weights[i] = Math.Exp(-(offset * offset) / (2 * sigma * sigma));
                weightSum += weights[i];
            }

            // 归一化权重
            if (weightSum > 0)
            {
                for (int i = 0; i < weights.Length; i++)
                {
                    weights[i] /= weightSum;
                }
            }

            // 应用高斯滤波
            for (int i = 0; i < inputData.Count; i++)
            {
                double weightedSum = 0;
                double totalWeight = 0;

                for (int j = 0; j < weights.Length; j++)
                {
                    int dataIndex = i - halfWindow + j;
                    if (dataIndex >= 0 && dataIndex < inputData.Count)
                    {
                        weightedSum += inputData[dataIndex].Y * weights[j];
                        totalWeight += weights[j];
                    }
                }

                double smoothedY = totalWeight > 0 ? weightedSum / totalWeight : inputData[i].Y;
                smoothedPoints.Add(new DataPoint(inputData[i].X, smoothedY));
            }

            return smoothedPoints;
        }

        private List<DataPoint> ApplyCSharpScript(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters)
        {
            // 组装参数字典：以算法默认参数为基础，叠加调用时参数
            IDictionary<string, object> paramDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in algorithm.Parameters)
            {
                if (kv.Value.Default is { } defaultObj)
                {
                    paramDict[kv.Key] = defaultObj;
                }
                else
                {
                    paramDict.Remove(kv.Key);
                }
            }
            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    if (kv.Value is { } valueObj)
                    {
                        paramDict[kv.Key] = valueObj;
                    }
                    else
                    {
                        paramDict.Remove(kv.Key);
                    }
                }
            }

            // 准备输入数组（仅 Y 值参与运算）
            var input = new double[inputData.Count];
            for (int i = 0; i < inputData.Count; i++) input[i] = inputData[i].Y;

            AlgorithmImplementation? implementation = algorithm.Implementation;
            if (implementation == null)
            {
                LastError = "算法定义缺少脚本";
                Console.WriteLine(LastError);
                return inputData;
            }

            var code = implementation.Code ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code)) { LastError = "算法脚本为空"; return inputData; }

            Func<double[], IDictionary<string, object>, double[]>? fn = GetOrCompileDelegate(code);
            if (fn == null)
            {
                LastError = "外部算法编译失败，已回退原始数据";
                Console.WriteLine(LastError);
                return inputData;
            }

            double[] output;
            try
            {
                output = fn(input, paramDict);
            }
            catch (Exception ex)
            {
                LastError = $"外部算法执行失败: {ex.Message}";
                Console.WriteLine(LastError);
                return inputData;
            }

            if (output == null || output.Length != input.Length)
            {
                LastError = "外部算法输出无效（为空或长度不匹配），已回退原始数据";
                Console.WriteLine(LastError);
                return inputData;
            }

            var result = new List<DataPoint>(inputData.Count);
            for (int i = 0; i < inputData.Count; i++)
            {
                result.Add(new DataPoint(inputData[i].X, output[i]));
            }
            return result;
        }

        private Func<double[], IDictionary<string, object>, double[]>? GetOrCompileDelegate_Deprecated(string userMethodCode)
        {
            if (s_codeCache.TryGetValue(userMethodCode, out var cached))
                return cached;

            try
            {
                // 包装用户方法到可编译的类中（旧实现，已弃用）
                // 修复：避免与下方同名方法冲突，改为委托到内部实现
                return GetOrCompileDelegateInternal(userMethodCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"构建源码失败: {ex.Message}");
                return null;
            }
        }

        private Func<double[], IDictionary<string, object>, double[]>? CompileDelegate(string fullSource)
        {
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(fullSource);
                var references = new List<MetadataReference>
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Runtime.GCSettings).Assembly.Location),
                };
                var compilation = CSharpCompilation.Create(
                    "UserAlgo_" + Guid.NewGuid().ToString("N"),
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);
                if (!emitResult.Success)
                {
                    var errors = string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                    Console.WriteLine($"外部算法编译错误:\n{errors}");
                    return null;
                }

                ms.Position = 0;
                var asm = Assembly.Load(ms.ToArray());
                var type = asm.GetType("__UserAlgo__.Entry");
                MethodInfo? method = null;
                if (type != null)
                {
                    method = type.GetMethod("Process", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(double[]), typeof(IDictionary<string, object>) })
                             ?? type.GetMethod("Process", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(double[]), typeof(Dictionary<string, object>) });
                }
                if (method == null)
                {
                    foreach (var t in asm.GetTypes())
                    {
                        method = t.GetMethod("Process", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(double[]), typeof(IDictionary<string, object>) })
                              ?? t.GetMethod("Process", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(double[]), typeof(Dictionary<string, object>) });
                        if (method != null) break;
                    }
                }
                if (method == null)
                {
                    LastError = "外部算法加载失败：未找到符合签名的 Process 方法";
                    Console.WriteLine(LastError);
                    return null;
                }

                Func<double[], IDictionary<string, object>, double[]> del = (double[] input, IDictionary<string, object> p) =>
                {
                    var args = method.GetParameters();
                    if (args.Length == 2 && args[1].ParameterType == typeof(Dictionary<string, object>))
                    {
                        var dict = p is Dictionary<string, object> d ? d : new Dictionary<string, object>(p);
                        return (double[])method.Invoke(null, new object[] { input, dict })!;
                    }
                    else
                    {
                        return (double[])method.Invoke(null, new object[] { input, p })!;
                    }
                };

                s_codeCache[GetCacheKeyFromSource(fullSource)] = del;
                return del;
            }
            catch (Exception ex)
            {
                LastError = $"外部算法动态编译失败: {ex.Message}";
                Console.WriteLine(LastError);
                return null;
            }
        }

        private static string GetCacheKeyFromSource(string src) => src; // 简化：以完整源码作为键

        private Func<double[], IDictionary<string, object>, double[]>? GetOrCompileDelegateInternal(string userMethodCode)
        {
            // 生成完整源码
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("namespace __UserAlgo__ { public static class Entry { ");
            sb.AppendLine(userMethodCode);
            sb.AppendLine("} }");
            var fullSource = sb.ToString();
            return CompileDelegate(fullSource);
        }

        private Func<double[], IDictionary<string, object>, double[]>? GetOrCompileDelegate(string userMethodCode)
        {
            if (s_codeCache.TryGetValue(userMethodCode, out var cached))
                return cached;

            var compiled = GetOrCompileDelegateInternal(userMethodCode);
            if (compiled != null)
            {
                s_codeCache[userMethodCode] = compiled;
            }
            return compiled;
        }

        private T GetParameterValue<T>(AlgorithmDefinition algorithm, Dictionary<string, object>? parameters, string paramName, T defaultValue)
        {
            try
            {
                // 首先尝试从传入的参数中获取
                if (parameters != null && parameters.TryGetValue(paramName, out var providedValue) && providedValue is { } nonNullProvided)
                {
                    return (T)Convert.ChangeType(nonNullProvided, typeof(T));
                }

                // 然后尝试从算法定义的默认参数中获取
                if (algorithm.Parameters.TryGetValue(paramName, out var param) && param.Default is { } nonNullDefault)
                {
                    return (T)Convert.ChangeType(nonNullDefault, typeof(T));
                }

                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private List<DataPoint> ApplyDllAlgorithm(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters = null)
        {
            try
            {
                AlgorithmImplementation? implementation = algorithm.Implementation;
                if (implementation == null)
                {
                    LastError = "算法定义缺少实现类型";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                // 区分托管 DLL 和非托管 DLL
                string? implementationType = implementation.Type;
                if (string.Equals(implementationType, "dll_unmanaged", StringComparison.OrdinalIgnoreCase))
                {
                    return ApplyUnmanagedDllAlgorithm(inputData, algorithm, parameters)!;
                }

                return ApplyManagedDllAlgorithm(inputData, algorithm, parameters)!;
            }
            catch (Exception ex)
            {
                LastError = $"DLL算法执行失败: {ex.Message}";
                Console.WriteLine(LastError);
                return inputData;
            }
        }

        private List<DataPoint> ApplyManagedDllAlgorithm(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters = null)
        {
            try
            {
                // 组装参数字典：以算法默认参数为基础，叠加调用时参数
                IDictionary<string, object> paramDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in algorithm.Parameters)
                {
                    if (kv.Value.Default is { } defaultObj)
                    {
                        paramDict[kv.Key] = defaultObj;
                    }
                    else
                    {
                        paramDict.Remove(kv.Key);
                    }
                }
                if (parameters != null)
                {
                    foreach (var kv in parameters)
                    {
                        if (kv.Value is { } valueObj)
                        {
                            paramDict[kv.Key] = valueObj;
                        }
                        else
                        {
                            paramDict.Remove(kv.Key);
                        }
                    }
                }

                // 准备输入数组（仅 Y 值参与运算）
                var input = new double[inputData.Count];
                for (int i = 0; i < inputData.Count; i++) input[i] = inputData[i].Y;

                // 加载 DLL 和类型
                AlgorithmImplementation? implementation = algorithm.Implementation;
                if (implementation == null)
                {
                    LastError = "算法定义缺少实现信息";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                string? dllPath = implementation.Code;
                string? className = implementation.Class;
                if (string.IsNullOrWhiteSpace(dllPath) || string.IsNullOrWhiteSpace(className))
                {
                    LastError = "算法定义缺少 DLL 信息";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                var assembly = Assembly.LoadFrom(dllPath);
                var type = assembly.GetType(className);
                if (type == null)
                {
                    LastError = "在 DLL 中未找到指定的算法类";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                // 尝试调用 Process 方法
                var processMethod = type.GetMethod("Process",
                    BindingFlags.Public | BindingFlags.Static,
                    new Type[] { typeof(double[]), typeof(IDictionary<string, object>) });

                if (processMethod == null)
                {
                    processMethod = type.GetMethod("Process",
                        BindingFlags.Public | BindingFlags.Static,
                        new Type[] { typeof(double[]), typeof(Dictionary<string, object>) });
                }

                if (processMethod == null || processMethod.ReturnType != typeof(double[]))
                {
                    LastError = "未找到符合要求的 Process 方法";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                // 调用算法
                object? invocationResult;
                if (processMethod.GetParameters()[1].ParameterType == typeof(Dictionary<string, object>))
                {
                    var dictParam = paramDict as Dictionary<string, object> ?? new Dictionary<string, object>(paramDict);
                    invocationResult = processMethod.Invoke(null, new object[] { input, dictParam });
                }
                else
                {
                    invocationResult = processMethod.Invoke(null, new object[] { input, paramDict });
                }

                if (invocationResult is not double[] output)
                {
                    LastError = "DLL算法输出无效（类型错误或为空），已回退原始数据";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                if (output == null || output.Length != input.Length)
                {
                    LastError = "DLL算法输出无效（为空或长度不匹配），已回退原始数据";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                var result = new List<DataPoint>(inputData.Count);
                for (int i = 0; i < inputData.Count; i++)
                {
                    result.Add(new DataPoint(inputData[i].X, output[i]));
                }
                return result;
            }
            catch (Exception ex)
            {
                LastError = $"托管DLL算法执行失败: {ex.Message}";
                Console.WriteLine(LastError);
                return inputData;
            }
        }

        private List<DataPoint> ApplyUnmanagedDllAlgorithm(List<DataPoint> inputData, AlgorithmDefinition algorithm, Dictionary<string, object>? parameters = null)
        {
            try
            {
                // 准备输入数组（仅 Y 值参与运算）
                var input = new double[inputData.Count];
                for (int i = 0; i < inputData.Count; i++) input[i] = inputData[i].Y;

                // 准备输出数组
                var output = new double[inputData.Count];

                // 获取 DLL 路径和函数名
                AlgorithmImplementation? implementation = algorithm.Implementation;
                if (implementation == null)
                {
                    LastError = "算法定义缺少 DLL 函数信息";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                string? dllPath = implementation.Code;
                string? functionName = implementation.Method;

                if (string.IsNullOrWhiteSpace(dllPath))
                {
                    LastError = "算法定义缺少 DLL 路径";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                if (string.IsNullOrWhiteSpace(functionName))
                {
                    LastError = "算法定义缺少 DLL 函数名称";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                // 加载 DLL 并获取函数指针
                IntPtr hModule = LoadLibrary(dllPath);
                if (hModule == IntPtr.Zero)
                {
                    LastError = "无法加载非托管 DLL";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                IntPtr functionPtr = GetProcAddress(hModule, functionName);
                if (functionPtr == IntPtr.Zero)
                {
                    FreeLibrary(hModule);
                    LastError = "在非托管 DLL 中未找到指定函数";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                // 将函数指针转换为委托
                var processor = Marshal.GetDelegateForFunctionPointer<GaussianFilterProcessDelegate>(functionPtr);

                // 调用非托管函数
                int result = processor(input, input.Length, output);

                // 卸载 DLL
                FreeLibrary(hModule);

                if (result != 0) // 假设 0 是成功，非零是错误
                {
                    LastError = $"非托管 DLL 算法执行失败，返回码: {result}";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                // 检查输出长度是否与输入匹配
                if (output.Length != input.Length)
                {
                    LastError = "非托管 DLL 算法输出长度与输入不匹配，已回退原始数据";
                    Console.WriteLine(LastError);
                    return inputData;
                }

                var resultDataPoints = new List<DataPoint>(inputData.Count);
                for (int i = 0; i < inputData.Count; i++)
                {
                    resultDataPoints.Add(new DataPoint(inputData[i].X, output[i]));
                }
                return resultDataPoints;
            }
            catch (Exception ex)
            {
                LastError = $"非托管 DLL 算法执行失败: {ex.Message}";
                Console.WriteLine(LastError);
                return inputData;
            }
        }

        // 委托定义，用于调用非托管 DLL 函数
        [UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate int GaussianFilterProcessDelegate(
            [In] double[] input,
            int length,
            [In, Out] double[] output
        );
    }
}