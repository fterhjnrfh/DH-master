// 用于抑制重复特性警告的文件
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Compiler", "CS0579", Justification = "在.NET 9.0中由于SDK自动生成特性导致的重复特性", Scope = "module")]