using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool SetDllDirectory(string lpPathName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    private const string DllName = "nilibddc.dll";

    // Match native signatures (stdcall)
    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CreateFile(
        string filePath,
        string fileType,
        string name,
        string description,
        string title,
        string author,
        ref IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "DDC_OpenFileEx")]
    public static extern int DDC_OpenFileEx(
        string filePath,
        string fileType,
        int readOnly,
        ref IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_CloseFile(IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_SaveFile(IntPtr file);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AddChannelGroup(
        IntPtr file,
        string groupName,
        string groupDescription,
        ref IntPtr group);

    public enum DDCDataType
    {
        DDC_UInt8 = 5,
        DDC_Int16 = 2,
        DDC_Int32 = 3,
        DDC_Float = 9,
        DDC_Double = 10,
        DDC_String = 23,
        DDC_Timestamp = 30
    }

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_AddChannel(
        IntPtr group,
        DDCDataType dataType,
        string channelName,
        string description,
        string unitString,
        ref IntPtr channel);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int DDC_SetDataValuesDouble(
        IntPtr channel,
        double[] values,
        UIntPtr count);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr DDC_GetLibraryErrorDescription(int errorCode);

    private static string DescribeError(int errorCode)
    {
        try
        {
            var p = DDC_GetLibraryErrorDescription(errorCode);
            if (p != IntPtr.Zero)
            {
                return Marshal.PtrToStringAnsi(p) ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            return $"(desc failed: {ex.Message})";
        }
        return string.Empty;
    }

    private static string? FindSystemUsiRoot()
    {
        var candidates = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "National Instruments", "Shared", "UsiCore", "DataModels", "USI"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "National Instruments", "Shared", "UsiCore", "DataModels", "USI"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    private static void TryOpen(string path, string type)
    {
        Console.WriteLine($"[Smoke] Try open {type}: {path}");
        IntPtr f = IntPtr.Zero;
        int rc = DDC_OpenFileEx(path, type, 1, ref f);
        Console.WriteLine($"[Smoke] DDC_OpenFileEx({type}): err={rc} {DescribeError(rc)}");
        if (rc == 0 && f != IntPtr.Zero)
        {
            DDC_CloseFile(f);
            Console.WriteLine($"[Smoke] Closed {type}: {path}");
        }
    }

    private static string FindRepoRoot()
    {
        var starts = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var start in starts)
        {
            try
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "DH.sln")))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
            }
        }

        return Environment.CurrentDirectory;
    }

    static void Main()
    {
        var repoRoot = FindRepoRoot();
        var appBin = Path.Combine(repoRoot, "src", "DH.Client.App", "bin", "Debug", "net6.0-windows7.0");
        Console.WriteLine($"[Smoke] AddDllDirectory: {appBin}");
        AddDllDirectory(appBin);
 
        // Prefer official 64-bit nilibddc.dll from specified path
        var ddcBin = Path.Combine(repoRoot, "tdms", "TDM C DLL[官方源文件]", "dev", "bin", "64-bit");
        try
        {
            SetDllDirectory(ddcBin);
            AddDllDirectory(ddcBin);
            Console.WriteLine($"[Smoke] SetDllDirectory (ddc): {ddcBin}");
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", ddcBin + ";" + path);
            Console.WriteLine("[Smoke] PATH prepended with ddcBin");
        }
        catch { }

         // USI logging for deep diagnostics
         var outDir = Path.Combine(repoRoot, "data");
        Directory.CreateDirectory(outDir);
        var logPath = Path.Combine(outDir, "usi_smoke.log");
        Environment.SetEnvironmentVariable("USI_LOGFILE", logPath);
        Environment.SetEnvironmentVariable("USI_LOG_LEVEL", "5");
        Environment.SetEnvironmentVariable("USI_CREATE_LOG_FILE", "1");
        Console.WriteLine($"[Smoke] USI_LOGFILE={logPath}");

        // Also include repo root where USI plugin DLLs are present
        Console.WriteLine($"[Smoke] AddDllDirectory (repo): {repoRoot}");
        AddDllDirectory(repoRoot);
        
        // Configure plugin search paths so USI can discover plugins
        try
        {
            var pf1 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf2 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var sysPlugins1 = Path.Combine(pf1, "National Instruments", "Shared", "UsiCore", "Plugins");
            var sysPlugins2 = Path.Combine(pf2, "National Instruments", "Shared", "UsiCore", "Plugins");

            var pluginDirs = new[]
            {
                appBin,
                Path.Combine(appBin, "Plugins"),
                Path.Combine(appBin, "Shared", "UsiCore", "Plugins"),
                sysPlugins1,
                sysPlugins2,
            };
            var existing = string.Join(";", Array.FindAll(pluginDirs, Directory.Exists));
            if (!string.IsNullOrWhiteSpace(existing))
            {
                Environment.SetEnvironmentVariable("USI_PLUGINSPATH", existing);
                Console.WriteLine($"[Smoke] USI_PLUGINSPATH={existing}");
                foreach (var d in existing.Split(';'))
                {
                    try { AddDllDirectory(d); Console.WriteLine($"[Smoke] AddDllDirectory (plugins): {d}"); } catch {}
                }
                try
                {
                    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    var add = string.Join(";", existing.Split(';'));
                    var newPath = add + ";" + path;
                    Environment.SetEnvironmentVariable("PATH", newPath);
                    Console.WriteLine("[Smoke] PATH prepended with plugin dirs");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Smoke] Failed to prepend PATH: {ex.Message}");
                }
            }
            else
            {
                // Fallback to app bin; many deployments place plugins next to exe
                Environment.SetEnvironmentVariable("USI_PLUGINSPATH", appBin);
                Console.WriteLine($"[Smoke] USI_PLUGINSPATH={appBin}");
                AddDllDirectory(appBin);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Smoke] Set USI_PLUGINSPATH failed: {ex.Message}");
        }
        
        // Explicitly preload USI dependency chain from app bin to reveal missing deps
        var deps = new[] {
            "xerces-c_3_1_usi.dll",
            "Uds.dll",
            "usiPluginTDM.dll",
            "uspTdms.dll",
            "uspTdmXml.dll",
            "usiEx.dll",
            "tdms_ebd.dll",
            "dacasr.dll",
        };
        foreach (var dep in deps)
        {
            var p = Path.Combine(appBin, dep);
            Console.WriteLine($"[Smoke] Preloading {p}");
            if (!File.Exists(p)) { Console.WriteLine("[Smoke] Not found"); continue; }
            try { NativeLibrary.Load(p); Console.WriteLine("[Smoke] Loaded"); }
            catch (Exception ex) { Console.WriteLine($"[Smoke] Failed: {ex.Message}"); }
        }

        // Explicitly load nilibddc.dll from specified 64-bit path to ensure correct version
        try
        {
            var libPath = Path.Combine(ddcBin, DllName);
            Console.WriteLine($"[Smoke] Loading {libPath}");
            NativeLibrary.Load(libPath);
            Console.WriteLine("[Smoke] nilibddc.dll loaded from specified ddcBin.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Smoke] Failed to load nilibddc.dll from ddcBin: {ex.Message}");
        }

        // Prefer full NI-provided USI resources if present
        var usiRootNi = Path.Combine(appBin, "DataModels_NI", "USI");
        var usiRootApp = Path.Combine(appBin, "DataModels", "USI");
        var sysUsi = FindSystemUsiRoot();
        var usiRoot = sysUsi ?? (Directory.Exists(usiRootNi) ? usiRootNi : usiRootApp);
        var usiCoreRoot = sysUsi ?? Path.Combine(appBin, "Shared", "UsiCore", "DataModels", "USI");
        Environment.SetEnvironmentVariable("USI_RESOURCEDIR", usiRoot);
        Environment.SetEnvironmentVariable("USICORERESOURCEDIR", Directory.Exists(usiCoreRoot) ? usiCoreRoot : usiRoot);
        Console.WriteLine($"[Smoke] USI_RESOURCEDIR={usiRoot}");
        Console.WriteLine($"[Smoke] USICORERESOURCEDIR={(Directory.Exists(usiCoreRoot) ? usiCoreRoot : usiRoot)}");
        
        // Make plugin version DLLs discoverable
        var usi1_0 = Path.Combine(usiRoot, "1_0");
        var tdm1_0 = Path.Combine(usiRoot, "TDM", "1_0");
        AddDllDirectory(usi1_0);
        AddDllDirectory(tdm1_0);
        Console.WriteLine($"[Smoke] AddDllDirectory: {usi1_0}");
        Console.WriteLine($"[Smoke] AddDllDirectory: {tdm1_0}");
        
        var coreUsi1_0 = Path.Combine(usiCoreRoot, "1_0");
        var coreTdm1_0 = Path.Combine(usiCoreRoot, "TDM", "1_0");
        if (Directory.Exists(coreUsi1_0)) { AddDllDirectory(coreUsi1_0); Console.WriteLine($"[Smoke] AddDllDirectory: {coreUsi1_0}"); }
        if (Directory.Exists(coreTdm1_0)) { AddDllDirectory(coreTdm1_0); Console.WriteLine($"[Smoke] AddDllDirectory: {coreTdm1_0}"); }
        
        // Make all USI Version.dll directories discoverable and proactively load them
        try
        {
            var versionDlls = Directory.GetFiles(usiRoot, "*_Version.dll", SearchOption.AllDirectories);
            foreach (var v in versionDlls)
            {
                var dir = Path.GetDirectoryName(v)!;
                AddDllDirectory(dir);
                Console.WriteLine($"[Smoke] AddDllDirectory: {dir}");
                try { NativeLibrary.Load(v); Console.WriteLine($"[Smoke] Loaded Version: {v}"); }
                catch (Exception ex) { Console.WriteLine($"[Smoke] Version load failed: {v} -> {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Smoke] Scan Version.dll failed: {ex.Message}");
        }
        
        // Try explicit load of USI version DLL to surface dependency errors
        var versionDll = Path.Combine(usiRoot, "TDM", "1_0", "USI_TDM_1_0_Version.dll");
        Console.WriteLine($"[Smoke] Preloading version: {versionDll}");
        if (File.Exists(versionDll))
        {
            try { NativeLibrary.Load(versionDll); Console.WriteLine("[Smoke] Version DLL loaded"); }
            catch (Exception ex) { Console.WriteLine($"[Smoke] Version load failed: {ex.Message}"); }
        }
        else
        {
            Console.WriteLine("[Smoke] Version DLL not found in USI TDM 1_0 directory");
        }

        var tdmsPath = Path.Combine(outDir, "smoke.tdms");
        var idx = tdmsPath + "_index";
        try { if (File.Exists(tdmsPath)) File.Delete(tdmsPath); } catch { tdmsPath = Path.Combine(outDir, $"smoke_{DateTime.Now:yyyyMMdd_HHmmss}.tdms"); }
        try { if (File.Exists(idx)) File.Delete(idx); } catch { }
        Console.WriteLine($"[Smoke] Creating TDMS: {tdmsPath}");

        IntPtr file = IntPtr.Zero;
        int err = DDC_CreateFile(tdmsPath, "TDMS", "Smoke", "", "Smoke", "DH", ref file);
        Console.WriteLine($"[Smoke] DDC_CreateFile: err={err} {DescribeError(err)}");
        if (err != 0)
        {
            Console.WriteLine("[Smoke] TDMS create failed, attempting TDM...");
            var tdmPath = Path.Combine(outDir, "smoke.tdm");
            try { if (File.Exists(tdmPath)) File.Delete(tdmPath); } catch { tdmPath = Path.Combine(outDir, $"smoke_{DateTime.Now:yyyyMMdd_HHmmss}.tdm"); }
            var tdx = Path.ChangeExtension(tdmPath, ".tdx");
            try { if (File.Exists(tdx)) File.Delete(tdx); } catch { }
            err = DDC_CreateFile(tdmPath, "TDM", "Smoke", "", "Smoke", "DH", ref file);
            Console.WriteLine($"[Smoke] DDC_CreateFile(TDM): err={err} {DescribeError(err)}");
            if (err != 0)
            {
                // 读取测试，验证读链路
                var sess1 = Path.Combine(outDir, "session.tdm");
                var sess2 = Path.Combine(outDir, "session6.tdm");
                if (File.Exists(sess1)) TryOpen(sess1, "TDM"); else Console.WriteLine("[Smoke] Missing: session.tdm");
                if (File.Exists(sess2)) TryOpen(sess2, "TDM"); else Console.WriteLine("[Smoke] Missing: session6.tdm");
                var smokeTdm = Path.Combine(outDir, "smoke.tdm");
                if (File.Exists(smokeTdm)) TryOpen(smokeTdm, "TDM");
                return;
            }
        }

        IntPtr group = IntPtr.Zero;
        err = DDC_AddChannelGroup(file, "Session", "SmokeGroup", ref group);
        Console.WriteLine($"[Smoke] DDC_AddChannelGroup: err={err} {DescribeError(err)}");
        if (err != 0) return;

        IntPtr ch = IntPtr.Zero;
        err = DDC_AddChannel(group, DDCDataType.DDC_Double, "CH1", "Channel 1", "V", ref ch);
        Console.WriteLine($"[Smoke] DDC_AddChannel: err={err} {DescribeError(err)}");
        if (err != 0) return;

        var values = new double[1000];
        for (int i = 0; i < values.Length; i++) values[i] = Math.Sin(i * 0.01);
        err = DDC_SetDataValuesDouble(ch, values, (UIntPtr)values.Length);
        Console.WriteLine($"[Smoke] DDC_SetDataValuesDouble: err={err} {DescribeError(err)}");

        DDC_SaveFile(file);
        DDC_CloseFile(file);
        Console.WriteLine("[Smoke] Done.");
    }
}
