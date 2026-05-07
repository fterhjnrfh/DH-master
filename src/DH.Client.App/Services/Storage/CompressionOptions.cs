using System;

namespace DH.Client.App.Services.Storage;

public sealed class CompressionOptions
{
    public int LZ4Level { get; set; } = 0;

    public int LZ4HCLevel { get; set; } = 12;

    public int ZstdLevel { get; set; } = 3;

    public int ZstdWindowLog { get; set; } = 23;

    public int ZlibLevel { get; set; } = 6;

    public int BZip2BlockSize { get; set; } = 9;

    public int BrotliQuality { get; set; } = 4;

    public int BrotliWindowBits { get; set; } = 22;

    public CompressionOptions Clone()
        => new()
        {
            LZ4Level = LZ4Level,
            LZ4HCLevel = LZ4HCLevel,
            ZstdLevel = ZstdLevel,
            ZstdWindowLog = ZstdWindowLog,
            ZlibLevel = ZlibLevel,
            BZip2BlockSize = BZip2BlockSize,
            BrotliQuality = BrotliQuality,
            BrotliWindowBits = BrotliWindowBits,
        };

    public void Normalize()
    {
        LZ4Level = Math.Clamp(LZ4Level, 0, 12);
        LZ4HCLevel = Math.Clamp(LZ4HCLevel, 3, 12);
        ZstdLevel = Math.Clamp(ZstdLevel, 1, 22);
        ZstdWindowLog = Math.Clamp(ZstdWindowLog, 10, 31);
        ZlibLevel = Math.Clamp(ZlibLevel, 0, 9);
        BZip2BlockSize = Math.Clamp(BZip2BlockSize, 1, 9);
        BrotliQuality = Math.Clamp(BrotliQuality, 0, 11);
        BrotliWindowBits = Math.Clamp(BrotliWindowBits, 10, 24);
    }
}
