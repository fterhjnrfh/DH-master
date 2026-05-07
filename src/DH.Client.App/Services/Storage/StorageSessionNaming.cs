using System;
using System.IO;
using System.Text;

namespace DH.Client.App.Services.Storage;

internal static class StorageSessionNaming
{
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "session";
        }

        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        string safe = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
    }

    public static string CreateUniqueSessionFolder(string basePath, string sessionName, out string safeSessionName)
    {
        safeSessionName = SanitizeName(sessionName);
        Directory.CreateDirectory(basePath);

        string candidate = Path.Combine(basePath, safeSessionName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        for (int i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(basePath, $"{safeSessionName}_{i:D3}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        string fallbackName = $"{safeSessionName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        safeSessionName = fallbackName;
        return Path.Combine(basePath, fallbackName);
    }
}
