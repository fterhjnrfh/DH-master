# DLL 算法文件使用指南

## 概述
本项目现在支持通过 .dll 文件加载外部算法。除了已有的 .xtj/.xtjs 文件格式外，您还可以创建和使用 .dll 算法文件。

## 支持的 .dll 算法实现方式

### 方式一：实现 IAlgorithm 接口
创建一个类实现 `AlgorithmModule.IAlgorithm` 接口：

```csharp
using System;
using System.Collections.Generic;
using AlgorithmModule;

public class MyDllAlgorithm : IAlgorithm
{
    public string Name => "MyDllAlgorithm";
    public string Description => "我的自定义算法";
    public string Version => "1.0.0";
    public string Author => "Your Name";

    public Dictionary<string, object> DefaultParameters => new Dictionary<string, object>
    {
        { "param1", 10.0 },
        { "param2", 5 }
    };

    public object Process(object input, IDictionary<string, object>? parameters = null)
    {
        // 实现算法逻辑
        if (!(input is double[] inputArray))
        {
            throw new ArgumentException("Input must be of type double[]");
        }

        // 处理逻辑
        var result = new double[inputArray.Length];
        // ... 算法逻辑 ...
        return result;
    }
}
```

### 方式二：静态方法
创建一个包含静态 `Process` 方法的类，方法签名必须为：
```csharp
public static double[] Process(double[] input, IDictionary<string, object> parameters)
```
或
```csharp
public static double[] Process(double[] input, Dictionary<string, object> parameters)
```

示例：
```csharp
using System;
using System.Collections.Generic;

public static class MyStaticAlgorithm
{
    public static double[] Process(double[] input, IDictionary<string, object> parameters)
    {
        if (input == null || input.Length == 0) return input;
        
        // 从参数获取配置
        int windowSize = 5;
        if (parameters.ContainsKey("windowSize"))
        {
            windowSize = Convert.ToInt32(parameters["windowSize"]);
        }
        
        // 实现算法
        var output = new double[input.Length];
        // ... 算法逻辑 ...
        return output;
    }
}
```

## 编译 DLL

使用 .NET SDK 编译算法：

```bash
# 编译为 DLL
dotnet build --configuration Release

# 或者创建单个 DLL 文件
dotnet build --configuration Release
# DLL 文件位于 bin/Release/netX.X/ 目录下
```

## 使用方法

1. 将编译好的 .dll 文件放置在项目目录中
2. 在应用程序中点击"浏览"按钮选择 .dll 文件
3. 算法将被动态加载并应用到数据处理中

## 注意事项

1. **安全考虑**：只加载来自可信来源的 .dll 文件
2. **依赖项**：确保 .dll 文件的依赖项在运行时环境中可用
3. **错误处理**：算法执行错误会被捕获，不会导致主程序崩溃
4. **性能**：动态加载的 .dll 算法只在首次加载时有额外开销
5. **兼容性**：算法的 `Process` 方法必须返回与输入相同长度的 `double[]` 数组

## 调试

如果 DLL 算法无法加载或运行失败：
1. 检查控制台输出中的错误信息
2. 确认 Process 方法签名正确
3. 确认算法不会抛出异常
4. 检查依赖项是否完整

## 示例

项目中的 `SampleDllAlgorithm.cs` 文件提供了完整的示例实现，展示两种不同的实现方式。