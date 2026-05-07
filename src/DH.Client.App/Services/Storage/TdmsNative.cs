using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace DH.Client.App.Services.Storage
{
    // 轻量封装 NI TDM C DLL（nilibddc.dll）以进行 TDMS 写入
    internal static class TdmsNative
    {
        public const string DllName = "nilibddc.dll";
        private static readonly string[] TdmsDependencyDlls =
        {
            "xerces-c_3_1_usi.dll",
            "Uds.dll",
            "usiPluginTDM.dll",
            "uspTdms.dll",
            "usiEx.dll",
            "tdms_ebd.dll",
            "dacasr.dll",
        };

        public static bool IsAvailable { get; }

        // 添加 Windows DLL 搜索路径配置，帮助 USI 插件解析
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern bool SetDllDirectory(string lpPathName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr AddDllDirectory(string lpPathName);

        static TdmsNative()
        {
            // 预加载 DLL：尝试从应用目录、当前目录以及仓库根目录加载
            TryPreload();
            try
            {
                IntPtr dummy = IntPtr.Zero;
                var err = DDC_CreateFile("", "TDMS", "", "", "", "", ref dummy);
                IsAvailable = true;
            }
            catch
            {
                IsAvailable = false;
            }
        }

        private static void TryPreload()
        {
            // 统一 ddc 路径与依赖查找目录（方法级可见）
            var repoRoot = FindRepoRoot();
            var ddcBin = ResolveDdcBin(repoRoot);
            var dirs = new[]
            {
                ddcBin ?? string.Empty,
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
                repoRoot ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(ddcBin))
            {
                EnsureLocalNativeFiles(ddcBin);
            }
            else
            {
                Console.WriteLine("[TDMS] Warn: 未找到 TDMS DLL 目录，可设置 TDMS_DLL_DIR 环境变量指向 nilibddc.dll 所在路径。");
            }
            try
            {
                // 优先使用用户指定的 64 位 nilibddc.dll 目录
                try
                {
                    if (!string.IsNullOrWhiteSpace(ddcBin))
                    {
                        SetDllDirectory(ddcBin);
                        AddDllDirectory(ddcBin);
                        Console.WriteLine($"[TDMS] SetDllDirectory (ddc): {ddcBin}");
                        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                        Environment.SetEnvironmentVariable("PATH", ddcBin + ";" + path);
                    }
                }
                catch { }
            
                // 设置 DLL 搜索路径
                try
                {
                    if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
                    {
                        Console.WriteLine($"[TDMS] AddDllDirectory: {AppContext.BaseDirectory}");
                        try { AddDllDirectory(AppContext.BaseDirectory); } catch { }
                    }
                    if (!string.IsNullOrWhiteSpace(repoRoot))
                    {
                        Console.WriteLine($"[TDMS] AddDllDirectory: {repoRoot}");
                        try { AddDllDirectory(repoRoot); } catch { }
                    }
                    var cur = Environment.CurrentDirectory;
                    if (!string.IsNullOrWhiteSpace(cur))
                    {
                        Console.WriteLine($"[TDMS] AddDllDirectory: {cur}");
                        try { AddDllDirectory(cur); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[TDMS] Set/AddDllDirectory failed: " + ex.Message);
                }
            
                // USI 插件搜索路径统一到应用目录及其 Shared\\UsiCore\\Plugins
                try
                {
                    var pluginDirs = new[]
                    {
                        AppContext.BaseDirectory,
                        Path.Combine(AppContext.BaseDirectory, "Plugins"),
                        Path.Combine(AppContext.BaseDirectory, "Shared", "UsiCore", "Plugins"),
                    };
                    var existing = string.Join(";", Array.FindAll(pluginDirs, Directory.Exists));
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        Environment.SetEnvironmentVariable("USI_PLUGINSPATH", existing);
                        Console.WriteLine($"[TDMS] USI_PLUGINSPATH={existing}");
                        foreach (var d in existing.Split(';'))
                        {
                            try { AddDllDirectory(d); Console.WriteLine($"[TDMS] AddDllDirectory (plugins): {d}"); } catch { }
                        }
                        try
                        {
                            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                            Environment.SetEnvironmentVariable("PATH", existing + ";" + path);
                        }
                        catch { }
                    }
                    else
                    {
                        Environment.SetEnvironmentVariable("USI_PLUGINSPATH", AppContext.BaseDirectory);
                        Console.WriteLine($"[TDMS] USI_PLUGINSPATH={AppContext.BaseDirectory}");
                        try { AddDllDirectory(AppContext.BaseDirectory); } catch { }
                    }
                }
                catch { }
            
                // USI 日志
                try
                {
                    var logPath = Path.Combine(AppContext.BaseDirectory, "usi.log");
                    Environment.SetEnvironmentVariable("USI_LOGFILE", logPath);
                    Environment.SetEnvironmentVariable("USI_LOG_LEVEL", "5");
                    Environment.SetEnvironmentVariable("USI_CREATE_LOG_FILE", "1");
                    Console.WriteLine($"[TDMS] USI logging to: {logPath}");
                }
                catch { }

                // 准备 USI 数据模型目录（Shared/UsiCore/DataModels 与 DataModels）
                try
                {
                    var dmSharedRoot = Path.Combine(AppContext.BaseDirectory, "Shared", "UsiCore", "DataModels", "USI");
                    var dmAppRoot = Path.Combine(AppContext.BaseDirectory, "DataModels", "USI");
                    Directory.CreateDirectory(dmSharedRoot);
                    Directory.CreateDirectory(dmAppRoot);

                    // 从 NI 安装目录或 DataModels_NI 完整镜像 USI 目录树
                    var pf1 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var pf2 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;
                    var copyRoots = new[]
                    {
                        Path.Combine(pf1, "National Instruments", "Shared", "UsiCore", "DataModels", "USI"),
                        Path.Combine(pf2, "National Instruments", "Shared", "UsiCore", "DataModels", "USI"),
                        Path.Combine(AppContext.BaseDirectory, "DataModels_NI", "USI")
                    };
                    foreach (var root in copyRoots)
                    {
                        if (!Directory.Exists(root)) continue;
                        TryCopyDirectory(root, dmSharedRoot);
                        TryCopyDirectory(root, dmAppRoot);
                        Console.WriteLine($"[TDMS] Mirrored USI from: {root}");
                        break;
                    }

                    // 设置 USI 资源目录到应用本地 DataModels\\USI，核心目录指向 Shared\\UsiCore\\DataModels\\USI
                    Environment.SetEnvironmentVariable("USI_RESOURCEDIR", dmAppRoot);
                    Environment.SetEnvironmentVariable("USICORERESOURCEDIR", dmSharedRoot);
                    Console.WriteLine($"[TDMS] USI resource dir: {dmAppRoot}");
                    Console.WriteLine($"[TDMS] USI core dir: {dmSharedRoot}");

                    // 将包含 *_Version.dll 的目录加入 DLL 搜索路径，便于 USI 动态加载
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(dmAppRoot, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var hasVersionDll = Directory.GetFiles(dir, "*_Version.dll", SearchOption.TopDirectoryOnly).Length > 0;
                                if (hasVersionDll)
                                {
                                    AddDllDirectory(dir);
                                    Console.WriteLine($"[TDMS] AddDllDirectory: {dir}");
                                }
                            }
                            catch { /* ignore per-dir errors */ }
                        }
                    }
                    catch { /* ignore scanning errors */ }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[TDMS] Prepare DataModels failed: " + ex.Message);
                }

                var dmDest = Path.Combine(AppContext.BaseDirectory, "Shared", "UsiCore", "DataModels");
                var dmAppDest = Path.Combine(AppContext.BaseDirectory, "DataModels");
                Directory.CreateDirectory(dmDest);
                Directory.CreateDirectory(dmAppDest);

                string usi10Dest = Path.Combine(dmDest, "USI", "1_0");
                string usi10AppDest = Path.Combine(dmAppDest, "USI", "1_0");

                bool needCopy =
                    !File.Exists(Path.Combine(usi10Dest, "usi_1_0.xsd")) ||
                    !File.Exists(Path.Combine(usi10AppDest, "usi_1_0.xsd"));

                if (needCopy)
                {
                    var pf1 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var pf2 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;
                    var copyRoots = new[]
                    {
                        Path.Combine(pf1, "National Instruments", "Shared", "UsiCore", "DataModels"),
                        Path.Combine(pf2, "National Instruments", "Shared", "UsiCore", "DataModels"),
                    };
                    foreach (var root in copyRoots)
                    {
                        if (!Directory.Exists(root)) continue;
                        var usi10Src = Path.Combine(root, "USI", "1_0");
                        if (Directory.Exists(usi10Src))
                        {
                            TryCopyDirectory(usi10Src, usi10Dest);
                            TryCopyDirectory(usi10Src, usi10AppDest);
                            Console.WriteLine($"[TDMS] Copied USI 1_0 from: {usi10Src}");
                        }
                        if (File.Exists(Path.Combine(usi10Dest, "usi_1_0.xsd")) &&
                            File.Exists(Path.Combine(usi10AppDest, "usi_1_0.xsd")))
                            break;
                    }
                }

                var dmSources = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "DataModels"),
                    Path.Combine(FindRepoRoot() ?? string.Empty, "src", "DH.Client.App", "DataModels"),
                    Path.Combine(FindRepoRoot() ?? string.Empty, "DataModels"),
                    Path.Combine(AppContext.BaseDirectory, "DataModels_NI")
                };
                foreach (var src in dmSources)
                {
                    if (Directory.Exists(src))
                    {
                        TryCopyIfExists(Path.Combine(src, "usi_1_0.xsd"), Path.Combine(usi10Dest, "usi_1_0.xsd"));
                        TryCopyIfExists(Path.Combine(src, "usi_1_0_datatypes.xsd"), Path.Combine(usi10Dest, "usi_1_0_datatypes.xsd"));

                        TryCopyIfExists(Path.Combine(src, "usi_1_0.xsd"), Path.Combine(usi10AppDest, "usi_1_0.xsd"));
                        TryCopyIfExists(Path.Combine(src, "usi_1_0_datatypes.xsd"), Path.Combine(usi10AppDest, "usi_1_0_datatypes.xsd"));
                        Console.WriteLine($"[TDMS] DataModels prepared from: {src}");
                        break;
                    }
                }

                // 提示 USI 在应用目录下的 DataModels 查找资源；核心目录指向 Shared\\UsiCore\\DataModels\\USI
                try
                {
                    Environment.SetEnvironmentVariable("USI_RESOURCEDIR", dmAppDest);
                    Environment.SetEnvironmentVariable("USICORERESOURCEDIR", Path.Combine(dmDest, "USI"));
                    Console.WriteLine($"[TDMS] USI resource dir: {dmAppDest}");
                    Console.WriteLine($"[TDMS] USI core dir: {Path.Combine(dmDest, "USI")}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TDMS] Prepare DataModels failed: " + ex.Message);
            }

            // 预加载 USI 依赖链
            foreach (var dep in TdmsDependencyDlls)
            {
                bool loaded = false;
                foreach (var d in dirs)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var p = Path.Combine(d, dep);
                    Console.WriteLine($"[TDMS] Preload dep: {p}");
                    if (!File.Exists(p)) continue;
                    try { NativeLibrary.Load(p); loaded = true; Console.WriteLine("[TDMS] Dep loaded."); break; }
                    catch (BadImageFormatException bie) { Console.WriteLine("[TDMS] Dep BadImageFormat: " + bie.Message); }
                    catch (DllNotFoundException dne) { Console.WriteLine("[TDMS] Dep DllNotFound: " + dne.Message); }
                    catch (Exception ex) { Console.WriteLine("[TDMS] Dep load failed: " + ex.Message); }
                }
                if (!loaded)
                {
                    Console.WriteLine($"[TDMS] Warn: dependency not found: {dep}");
                }
            }

            // 加载主库 nilibddc.dll（首选指定目录）
            var candidates = new[]
            {
                !string.IsNullOrWhiteSpace(ddcBin) ? Path.Combine(ddcBin, DllName) : null,
                Path.Combine(AppContext.BaseDirectory, DllName),
                Path.Combine(Environment.CurrentDirectory, DllName),
                repoRoot != null ? Path.Combine(repoRoot, DllName) : null
            };
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                Console.WriteLine($"[TDMS] Trying to preload: {c}");
                if (!File.Exists(c)) { Console.WriteLine("[TDMS] Not found."); continue; }
                try
                {
                    NativeLibrary.Load(c);
                    Console.WriteLine("[TDMS] Preload success.");
                    break;
                }
                catch (BadImageFormatException bie)
                {
                    Console.WriteLine("[TDMS] Preload failed (BadImageFormat) — 可能是 32/64 位不匹配: " + bie.Message);
                }
                catch (DllNotFoundException dne)
                {
                    Console.WriteLine("[TDMS] Preload failed (依赖缺失): " + dne.Message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[TDMS] Preload failed: " + ex.Message);
                }
            }
        }

        private static string? ResolveDdcBin(string? repoRoot)
        {
            var env = Environment.GetEnvironmentVariable("TDMS_DLL_DIR");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                var repoCandidate = Path.Combine(repoRoot, "tdms", "TDM C DLL[官方源文件]", "dev", "bin", "64-bit");
                if (Directory.Exists(repoCandidate)) return repoCandidate;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop))
            {
                var desktopCandidate = Path.Combine(desktop, "DH", "tdms", "TDM C DLL[官方源文件]", "dev", "bin", "64-bit");
                if (Directory.Exists(desktopCandidate)) return desktopCandidate;
            }

            return null;
        }

        private static void EnsureLocalNativeFiles(string sourceDir)
        {
            try
            {
                foreach (var dep in TdmsDependencyDlls)
                {
                    TryCopyIfExists(Path.Combine(sourceDir, dep), Path.Combine(AppContext.BaseDirectory, dep));
                }
                TryCopyIfExists(Path.Combine(sourceDir, DllName), Path.Combine(AppContext.BaseDirectory, DllName));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TDMS] EnsureLocalNativeFiles failed: " + ex.Message);
            }
        }

        private static void TryCopyIfExists(string src, string dest)
        {
            try
            {
                if (File.Exists(src))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, true);
                }
            }
            catch { }
        }

        private static void TryCopyDirectory(string srcDir, string destDir)
        {
            try
            {
                if (!Directory.Exists(srcDir)) return;
                Directory.CreateDirectory(destDir);
                foreach (var dir in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcDir, dir);
                    Directory.CreateDirectory(Path.Combine(destDir, rel));
                }
                foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcDir, file);
                    var target = Path.Combine(destDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TDMS] Copy directory failed: " + ex.Message);
            }
        }

        private static string? FindRepoRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var sln = Path.Combine(dir.FullName, "DH.sln");
                    if (File.Exists(sln)) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_CreateFile(
            string filePath,
            string fileType,
            string name,
            string description,
            string title,
            string author,
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

        // 映射 NI DDCDataType
        public enum DDCDataType
        {
            UInt8 = 5,
            Int16 = 2,
            Int32 = 3,
            Float = 9,
            Double = 10,
            String = 23,
            Timestamp = 30,
        }
        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_AddChannel(
            IntPtr group,
            DDCDataType dataType,
            string name,
            string description,
            string unitString,
            ref IntPtr channel);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_SetDataValuesDouble(
            IntPtr channel,
            double[] values,
            uint count);

        // Append variant for streaming data
        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_AppendDataValuesFloat(
            IntPtr channel,
            float[] values,
            uint count);

        // Append variant for streaming data
        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_AppendDataValuesDouble(
            IntPtr channel,
            double[] values,
            uint count);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_CreateFilePropertyString(IntPtr file, string property, string value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_CreateChannelPropertyString(IntPtr channel, string property, string value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_CreateChannelPropertyDouble(IntPtr channel, string property, double value);

        // 读取相关 API（Open/Enumerate/Property Read）
        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_OpenFileEx(string filePath, string fileType, int readOnly, ref IntPtr file);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetNumChannelGroups(IntPtr file, ref int numChannelGroups);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelGroups(IntPtr file, IntPtr[] channelGroups, int numChannelGroups);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetNumChannels(IntPtr group, ref int numChannels);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannels(IntPtr group, IntPtr[] channels, int numChannels);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelGroupStringPropertyLength(IntPtr group, string property, ref int length);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelGroupPropertyString(IntPtr group, string property, StringBuilder value, int valueSize);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelStringPropertyLength(IntPtr channel, string property, ref int length);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyString(IntPtr channel, string property, StringBuilder value, int valueSize);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetNumChannelProperties(IntPtr channel, ref int numProperties);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyNameLengthFromIndex(IntPtr channel, int index, ref int length);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyNameFromIndex(IntPtr channel, int index, StringBuilder propertyName, int propertyNameSize);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyType(IntPtr channel, string property, out DDCDataType dataType);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyUInt8(IntPtr channel, string property, out byte value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyInt16(IntPtr channel, string property, out short value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyInt32(IntPtr channel, string property, out int value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyFloat(IntPtr channel, string property, out float value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern int DDC_GetChannelPropertyDouble(IntPtr channel, string property, out double value);

        [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        private static extern int DDC_GetLibraryErrorDescription(int errorCode, StringBuilder buffer, int bufferLength);
        
        public static string DescribeError(int errorCode)
        {
            try
            {
                var sb = new StringBuilder(512);
                var rc = DDC_GetLibraryErrorDescription(errorCode, sb, sb.Capacity);
                if (rc == 0 && sb.Length > 0) return sb.ToString();
            }
            catch { }
            return string.Empty;
        }
        public static void SaveSimpleTdms(string tdmsPath)
        {
            var samples = new double[1000];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Sin(2 * Math.PI * i / 100);
            }
            SaveSimpleTdms(tdmsPath, samples, "Simple");
        }

        public static void SaveSimpleTdms(string tdmsPath, ReadOnlySpan<double> samples, string? sessionName = null)
        {
            if (!IsAvailable) throw new InvalidOperationException("nilibddc.dll 不可用或加载失败。");
            Directory.CreateDirectory(Path.GetDirectoryName(tdmsPath)!);

            var idx = tdmsPath + "_index";
            try { if (File.Exists(tdmsPath)) File.Delete(tdmsPath); }
            catch { tdmsPath = Path.Combine(Path.GetDirectoryName(tdmsPath)!, $"sample_{DateTime.Now:yyyyMMdd_HHmmss}.tdms"); }
            try { if (File.Exists(idx)) File.Delete(idx); } catch { }

            IntPtr file = IntPtr.Zero;
            IntPtr group = IntPtr.Zero;
            IntPtr channel = IntPtr.Zero;
            string name = Path.GetFileNameWithoutExtension(tdmsPath);

            int err = DDC_CreateFile(tdmsPath, "TDMS", name, "", name, "DH", ref file);
            if (err != 0) throw new IOException($"DDC_CreateFile 失败: {err} {DescribeError(err)}");

            err = DDC_AddChannelGroup(file, "Session", sessionName ?? name, ref group);
            if (err != 0)
            {
                try { if (file != IntPtr.Zero) DDC_CloseFile(file); } catch { }
                throw new IOException($"DDC_AddChannelGroup 失败: {err} {DescribeError(err)}");
            }

            err = DDC_AddChannel(group, DDCDataType.Double, "CH1", "Channel 1", "V", ref channel);
            if (err != 0)
            {
                try { if (file != IntPtr.Zero) { DDC_SaveFile(file); DDC_CloseFile(file); } } catch { }
                throw new IOException($"DDC_AddChannel 失败: {err} {DescribeError(err)}");
            }

            var arr = samples.ToArray();
            err = DDC_SetDataValuesDouble(channel, arr, (uint)arr.Length);
            if (err != 0)
            {
                try { if (file != IntPtr.Zero) { DDC_SaveFile(file); DDC_CloseFile(file); } } catch { }
                throw new IOException($"DDC_SetDataValuesDouble 失败: {err} {DescribeError(err)}");
            }

            try
            {
                DDC_CreateChannelPropertyString(channel, "wf_xname", "Time");
                DDC_CreateChannelPropertyString(channel, "wf_xunit_string", "s");
                DDC_CreateChannelPropertyDouble(channel, "wf_increment", 0.001);
            }
            catch { }

            try { DDC_SaveFile(file); } catch { }
            try { DDC_CloseFile(file); } catch { }
        }
    }
}
