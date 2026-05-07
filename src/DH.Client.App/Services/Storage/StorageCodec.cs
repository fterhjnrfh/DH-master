using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;
using Snappier;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// Storage codec for preprocess + compression packed into TDMS double payloads.
    /// Layout: [signature, compressionType, preprocessType, sampleCount, compressedSize, data...]
    /// </summary>
    internal static class StorageCodec
    {
        internal static readonly byte[] V2Signature = Encoding.ASCII.GetBytes("DHCMPv2!");

        internal sealed class StorageEncodeResult
        {
            public double[]? EncodedSamples { get; init; }

            public int RawBytes { get; init; }

            public int CodecBytes { get; init; }

            public int PayloadBytes { get; init; }
        }

        public static double[]? Encode(double[] samples, CompressionType compression, PreprocessType preprocess, CompressionOptions? options = null)
        {
            return EncodeWithMetrics(samples, compression, preprocess, options).EncodedSamples;
        }

        internal static StorageEncodeResult EncodeWithMetrics(double[] samples, CompressionType compression, PreprocessType preprocess, CompressionOptions? options = null)
        {
            int rawBytesLength = samples.Length * sizeof(double);
            if (compression == CompressionType.None && preprocess == PreprocessType.None)
            {
                return new StorageEncodeResult
                {
                    EncodedSamples = null,
                    RawBytes = rawBytesLength,
                    CodecBytes = rawBytesLength,
                    PayloadBytes = rawBytesLength,
                };
            }

            double[] processed = preprocess != PreprocessType.None
                ? DataPreprocessor.Apply(samples, preprocess)
                : samples;

            byte[] rawBytes = new byte[processed.Length * sizeof(double)];
            Buffer.BlockCopy(processed, 0, rawBytes, 0, rawBytes.Length);

            byte[] compressedBytes;
            int compressedSize;

            if (compression == CompressionType.None)
            {
                compressedBytes = rawBytes;
                compressedSize = rawBytes.Length;
            }
            else
            {
                (compressedBytes, compressedSize) = CompressBytes(rawBytes, compression, options);
            }

            var result = new List<double>();
            double sig = BitConverter.ToDouble(V2Signature, 0);
            result.Add(sig);
            result.Add((double)(int)compression);
            result.Add((double)(int)preprocess);
            result.Add((double)samples.Length);
            result.Add((double)compressedSize);

            int doubleCount = (compressedSize + 7) / 8;
            for (int i = 0; i < doubleCount; i++)
            {
                byte[] temp = new byte[8];
                int bytesToCopy = Math.Min(8, compressedSize - i * 8);
                Buffer.BlockCopy(compressedBytes, i * 8, temp, 0, bytesToCopy);
                result.Add(BitConverter.ToDouble(temp, 0));
            }

            var encodedSamples = result.ToArray();
            return new StorageEncodeResult
            {
                EncodedSamples = encodedSamples,
                RawBytes = rawBytesLength,
                CodecBytes = compressedSize,
                PayloadBytes = encodedSamples.Length * sizeof(double),
            };
        }

        internal static (byte[] bytes, int size) CompressBytes(byte[] rawBytes, CompressionType type, CompressionOptions? options = null)
        {
            var opts = options ?? new CompressionOptions();
            switch (type)
            {
                case CompressionType.LZ4:
                {
                    var level = (LZ4Level)Math.Clamp(opts.LZ4Level, 0, 12);
                    var buf = new byte[LZ4Codec.MaximumOutputSize(rawBytes.Length)];
                    int size = LZ4Codec.Encode(rawBytes, 0, rawBytes.Length, buf, 0, buf.Length, level);
                    return (buf, size);
                }
                case CompressionType.LZ4_HC:
                {
                    var level = (LZ4Level)Math.Clamp(opts.LZ4HCLevel, 3, 12);
                    var buf = new byte[LZ4Codec.MaximumOutputSize(rawBytes.Length)];
                    int size = LZ4Codec.Encode(rawBytes, 0, rawBytes.Length, buf, 0, buf.Length, level);
                    return (buf, size);
                }
                case CompressionType.Zstd:
                {
                    int level = Math.Clamp(opts.ZstdLevel, 1, 22);
                    int windowLog = Math.Clamp(opts.ZstdWindowLog, 10, 31);
                    using var compressor = new Compressor(level);
                    compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
                    var compressed = compressor.Wrap(rawBytes);
                    var bytes = compressed.ToArray();
                    return (bytes, bytes.Length);
                }
                case CompressionType.Brotli:
                {
                    int quality = Math.Clamp(opts.BrotliQuality, 0, 11);
                    int windowBits = Math.Clamp(opts.BrotliWindowBits, 10, 24);
                    using var encoder = new BrotliEncoder(quality, windowBits);
                    using var ms = new MemoryStream();
                    var input = rawBytes.AsSpan();
                    var buffer = new byte[65536];
                    while (true)
                    {
                        var status = encoder.Compress(input, buffer.AsSpan(), out int bytesConsumed, out int bytesWritten, input.Length == 0);
                        if (bytesWritten > 0)
                        {
                            ms.Write(buffer, 0, bytesWritten);
                        }

                        if (bytesConsumed > 0)
                        {
                            input = input.Slice(bytesConsumed);
                        }

                        if (status == OperationStatus.Done)
                        {
                            break;
                        }

                        if (status == OperationStatus.InvalidData)
                        {
                            throw new InvalidOperationException("Brotli compress error");
                        }
                    }

                    while (true)
                    {
                        var status = encoder.Flush(buffer.AsSpan(), out int bytesWritten);
                        if (bytesWritten > 0)
                        {
                            ms.Write(buffer, 0, bytesWritten);
                        }

                        if (status == OperationStatus.Done)
                        {
                            break;
                        }
                    }

                    var bytes = ms.ToArray();
                    return (bytes, bytes.Length);
                }
                case CompressionType.Snappy:
                {
                    var bytes = Snappy.CompressToArray(rawBytes);
                    return (bytes, bytes.Length);
                }
                case CompressionType.Zlib:
                {
                    var level = opts.ZlibLevel switch
                    {
                        0 => CompressionLevel.NoCompression,
                        <= 3 => CompressionLevel.Fastest,
                        <= 6 => CompressionLevel.Optimal,
                        _ => CompressionLevel.SmallestSize,
                    };
                    using var ms = new MemoryStream();
                    using (var zlib = new ZLibStream(ms, level, leaveOpen: true))
                    {
                        zlib.Write(rawBytes, 0, rawBytes.Length);
                    }

                    var bytes = ms.ToArray();
                    return (bytes, bytes.Length);
                }
                case CompressionType.BZip2:
                {
                    int blockSize = Math.Clamp(opts.BZip2BlockSize, 1, 9);
                    using var input = new MemoryStream(rawBytes);
                    using var output = new MemoryStream();
                    ICSharpCode.SharpZipLib.BZip2.BZip2.Compress(input, output, false, blockSize);
                    var bytes = output.ToArray();
                    return (bytes, bytes.Length);
                }
                default:
                    throw new NotSupportedException($"Unsupported compression type: {type}");
            }
        }

        internal static byte[] DecompressBytes(byte[] compressedBytes, int compressedSize, int originalByteSize, CompressionType type)
        {
            switch (type)
            {
                case CompressionType.None:
                    return compressedBytes;

                case CompressionType.LZ4:
                case CompressionType.LZ4_HC:
                {
                    var result = new byte[originalByteSize];
                    int decoded = LZ4Codec.Decode(compressedBytes, 0, compressedSize, result, 0, originalByteSize);
                    if (decoded != originalByteSize)
                    {
                        throw new InvalidDataException($"LZ4 decompressed size mismatch. Expected {originalByteSize}, actual {decoded}");
                    }

                    return result;
                }
                case CompressionType.Zstd:
                {
                    using var decompressor = new Decompressor();
                    var result = decompressor.Unwrap(compressedBytes.AsSpan(0, compressedSize)).ToArray();
                    if (result.Length != originalByteSize)
                    {
                        throw new InvalidDataException($"Zstd decompressed size mismatch. Expected {originalByteSize}, actual {result.Length}");
                    }

                    return result;
                }
                case CompressionType.Brotli:
                {
                    using var ms = new MemoryStream(compressedBytes, 0, compressedSize);
                    using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    brotli.CopyTo(output);
                    var result = output.ToArray();
                    if (result.Length != originalByteSize)
                    {
                        throw new InvalidDataException($"Brotli decompressed size mismatch. Expected {originalByteSize}, actual {result.Length}");
                    }

                    return result;
                }
                case CompressionType.Snappy:
                {
                    var result = Snappy.DecompressToArray(compressedBytes.AsSpan(0, compressedSize));
                    if (result.Length != originalByteSize)
                    {
                        throw new InvalidDataException($"Snappy decompressed size mismatch. Expected {originalByteSize}, actual {result.Length}");
                    }

                    return result;
                }
                case CompressionType.Zlib:
                {
                    using var ms = new MemoryStream(compressedBytes, 0, compressedSize);
                    using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    var result = output.ToArray();
                    if (result.Length != originalByteSize)
                    {
                        throw new InvalidDataException($"Zlib decompressed size mismatch. Expected {originalByteSize}, actual {result.Length}");
                    }

                    return result;
                }
                case CompressionType.BZip2:
                {
                    using var input = new MemoryStream(compressedBytes, 0, compressedSize);
                    using var output = new MemoryStream();
                    ICSharpCode.SharpZipLib.BZip2.BZip2.Decompress(input, output, false);
                    var result = output.ToArray();
                    if (result.Length != originalByteSize)
                    {
                        throw new InvalidDataException($"BZip2 decompressed size mismatch. Expected {originalByteSize}, actual {result.Length}");
                    }

                    return result;
                }
                default:
                    throw new NotSupportedException($"Unsupported compression type: {type}");
            }
        }
    }
}
