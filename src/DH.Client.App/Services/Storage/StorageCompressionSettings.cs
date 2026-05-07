using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DH.Client.App.Services.Storage;

public sealed class StorageCompressionSettings
{
    public const string DefaultFileName = "storage.compression.json";
    private const string ConfigPathEnvironmentVariable = "DH_STORAGE_COMPRESSION_CONFIG";

    public bool Enabled { get; set; }

    public CompressionType Algorithm { get; set; } = CompressionType.Zstd;

    public PreprocessType Preprocess { get; set; } = PreprocessType.None;

    public CompressionOptions Options { get; set; } = new();

    public StorageCompressionSettings Clone()
        => new()
        {
            Enabled = Enabled,
            Algorithm = Algorithm,
            Preprocess = Preprocess,
            Options = Options.Clone(),
        };

    public void Normalize()
    {
        if (!Enum.IsDefined(typeof(CompressionType), Algorithm))
        {
            Algorithm = CompressionType.Zstd;
        }

        if (!Enum.IsDefined(typeof(PreprocessType), Preprocess))
        {
            Preprocess = PreprocessType.None;
        }

        Options ??= new CompressionOptions();
        Options.Normalize();
    }

    public string Describe()
    {
        if (!Enabled)
        {
            return "disabled";
        }

        string preprocess = Preprocess == PreprocessType.None ? "none" : Preprocess.ToString();
        string algorithm = Algorithm == CompressionType.None ? "none" : Algorithm.ToString();
        return $"{preprocess}+{algorithm} {DescribeAlgorithmParameters()}";
    }

    public static string ResolveConfigPath(string basePath)
    {
        string? overridePath = Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Path.Combine(Path.GetFullPath(basePath), DefaultFileName);
    }

    public static bool TryLoad(string basePath, out StorageCompressionSettings settings, out string configPath, out string error)
    {
        configPath = ResolveConfigPath(basePath);
        settings = new StorageCompressionSettings();
        error = string.Empty;

        if (!File.Exists(configPath))
        {
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter());

            string json = File.ReadAllText(configPath);
            settings = JsonSerializer.Deserialize<StorageCompressionSettings>(json, options) ?? new StorageCompressionSettings();
            settings.Normalize();
            return true;
        }
        catch (Exception ex)
        {
            settings = new StorageCompressionSettings();
            error = ex.Message;
            return false;
        }
    }

    public static void WriteSnapshot(string path, StorageCompressionSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(path, JsonSerializer.Serialize(settings, options));
    }

    private string DescribeAlgorithmParameters()
        => Algorithm switch
        {
            CompressionType.LZ4 => $"lz4Level={Options.LZ4Level}",
            CompressionType.LZ4_HC => $"lz4HCLevel={Options.LZ4HCLevel}",
            CompressionType.Zstd => $"zstdLevel={Options.ZstdLevel},zstdWindowLog={Options.ZstdWindowLog}",
            CompressionType.Zlib => $"zlibLevel={Options.ZlibLevel}",
            CompressionType.BZip2 => $"bzip2BlockSize={Options.BZip2BlockSize}",
            CompressionType.Snappy => "snappyDefault",
            CompressionType.None => "raw-preprocess",
            _ => "default",
        };
}
