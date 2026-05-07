using System.Text.Json;
using DH.Contracts.Abstractions;

namespace DH.Configmanage.Providers;

public class JsonConfigProvider : IConfigProvider
{
    private readonly string _path;
    public JsonConfigProvider(string path) => _path = path;

    public T Get<T>(string section) where T : new()
    {
        try
        {
            if (!File.Exists(_path)) return new T();
            using var fs = File.OpenRead(_path);
            var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty(section, out var elem))
                return elem.Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
        }
        catch { /* minimal demo */ }
        return new T();
    }
}