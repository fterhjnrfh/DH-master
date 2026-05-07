// NativeDllInterface.cs
using System;
using System.Runtime.InteropServices;

namespace NewAvalonia
{
    public static class NativeDllInterface
    {
        // 定义与 C++ DLL 中 gaussianfilter_process 函数匹配的委托
        // int gaussianfilter_process(double* input, int length, double* output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] // Cdecl 是 extern "C" 的默认调用约定
        public delegate int GaussianFilterProcessDelegate(
            [In] double[] input, // [In] 表示数据从 C# 传入 DLL
            int length,
            [In, Out] double[] output // [In, Out] 表示数据可被 DLL 修改后传出
        );

        // 用于动态加载所需函数的 P/Invoke 声明 (kernel32.dll)
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hReservedNull, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

        public const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    }
}