using System.Reflection;

var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
if (string.IsNullOrWhiteSpace(nugetPackages))
{
    nugetPackages = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nuget",
        "packages");
}

var asmPath = Path.Combine(nugetPackages, "zstdsharp.port", "0.8.1", "lib", "net8.0", "ZstdSharp.dll");
if (!File.Exists(asmPath))
{
    Console.Error.WriteLine($"ZstdSharp.dll not found: {asmPath}");
    return;
}

var asm = Assembly.LoadFrom(asmPath);
foreach (var t in asm.GetTypes().Where(t => t.IsEnum && t.Name.Contains("Parameter", StringComparison.OrdinalIgnoreCase)).OrderBy(t=>t.FullName))
{
    Console.WriteLine(t.FullName);
    foreach (var n in Enum.GetNames(t)) Console.WriteLine("  " + n);
}
