using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// 存储文件无损验证器 —— 写入时记录 SHA-256 指纹，回读时逐通道比对
    /// </summary>
    public static class StorageVerifier
    {
        /// <summary>
        /// 计算 double 数组的 SHA-256 哈希（基于字节级二进制表示，bit-exact）
        /// </summary>
        public static string ComputeHash(double[] data)
        {
            if (data == null || data.Length == 0) return "";
            var bytes = new byte[data.Length * sizeof(double)];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        public static string ComputeHash(float[] data)
        {
            if (data == null || data.Length == 0) return "";
            var hash = SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan()));
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 增量式 SHA-256：在写入过程中持续追加数据块，最终生成整个通道的哈希
        /// </summary>
        public sealed class IncrementalHasher : IDisposable
        {
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private long _totalSamples;

            public long TotalSamples => _totalSamples;

            /// <summary>追加一批样本到哈希计算</summary>
            public void Append(double[] data)
            {
                if (data == null || data.Length == 0) return;
                var bytes = new byte[data.Length * sizeof(double)];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                _hash.AppendData(bytes);
                _totalSamples += data.Length;
            }

            /// <summary>追加一批 float 样本到哈希计算，直接按单精度 TDMS 写入字节计算。</summary>
            public void Append(ReadOnlySpan<float> data)
            {
                if (data.IsEmpty) return;
                _hash.AppendData(MemoryMarshal.AsBytes(data));
                _totalSamples += data.Length;
            }

            private string? _cachedHash;

            /// <summary>获取最终哈希值（可重复调用，结果不变）</summary>
            public string Finalize()
            {
                if (_cachedHash != null) return _cachedHash;
                var hash = _hash.GetHashAndReset();
                _cachedHash = Convert.ToHexString(hash);
                return _cachedHash;
            }

            public void Dispose() => _hash.Dispose();
        }

        /// <summary>
        /// 单通道验证结果
        /// </summary>
        public sealed class ChannelVerifyResult
        {
            public string ChannelName { get; init; } = "";
            public long WrittenSamples { get; init; }
            public long ReadBackSamples { get; init; }
            public string WriteHash { get; init; } = "";
            public string ReadBackHash { get; init; } = "";
            /// <summary>是否有写入时的哈希可比对</summary>
            public bool HasWriteHash => !string.IsNullOrEmpty(WriteHash) && WriteHash != "(读取失败)";
            /// <summary>无损判定：有写入哈希则比对；无写入哈希则仅检查回读是否成功</summary>
            public bool IsLossless => HasWriteHash
                                   ? WriteHash == ReadBackHash && WrittenSamples == ReadBackSamples
                                   : ReadBackSamples > 0 && !string.IsNullOrEmpty(ReadBackHash);
            /// <summary>首个不一致样本的索引（仅逐样本比对时有值）</summary>
            public long FirstMismatchIndex { get; init; } = -1;
            public double MaxAbsoluteError { get; init; }
        }

        /// <summary>
        /// 整个会话的验证结果
        /// </summary>
        public sealed class SessionVerifyResult
        {
            public string FilePath { get; init; } = "";
            public DateTime VerifyTime { get; init; } = DateTime.Now;
            public List<ChannelVerifyResult> Channels { get; init; } = new();
            public bool AllLossless => Channels.Count > 0 && Channels.All(c => c.IsLossless);
            public long TotalWrittenSamples => Channels.Sum(c => c.WrittenSamples);
            public long TotalReadBackSamples => Channels.Sum(c => c.ReadBackSamples);
            public TimeSpan VerifyDuration { get; init; }

            public string Summary
            {
                get
                {
                    var hasAnyWriteHash = Channels.Any(c => c.HasWriteHash);
                    var sb = new StringBuilder();
                    if (AllLossless)
                    {
                        if (hasAnyWriteHash)
                            sb.AppendLine($"✅ 文件无损验证通过 — {Channels.Count} 通道, {TotalWrittenSamples:#,0} 样本全部 bit-exact 一致");
                        else
                            sb.AppendLine($"✅ 文件回读成功 — {Channels.Count} 通道, {TotalReadBackSamples:#,0} 样本（无写入指纹，仅验证可读性）");
                    }
                    else
                    {
                        var failCount = Channels.Count(c => !c.IsLossless);
                        sb.AppendLine($"❌ 文件无损验证失败 — {failCount}/{Channels.Count} 个通道存在差异");
                    }
                    sb.AppendLine($"   文件: {Path.GetFileName(FilePath)}");
                    sb.AppendLine($"   验证耗时: {VerifyDuration.TotalMilliseconds:F1} ms");
                    foreach (var ch in Channels)
                    {
                        var icon = ch.IsLossless ? "✅" : "❌";
                        sb.Append($"   {icon} {ch.ChannelName}: ");
                        if (ch.HasWriteHash)
                            sb.Append($"写入={ch.WrittenSamples:#,0} ");
                        sb.Append($"回读={ch.ReadBackSamples:#,0}");
                        if (!string.IsNullOrEmpty(ch.WriteHash) && ch.WriteHash.Length >= 16)
                            sb.Append($"  SHA256写入={ch.WriteHash[..16]}…");
                        if (!string.IsNullOrEmpty(ch.ReadBackHash) && ch.ReadBackHash.Length >= 16)
                            sb.Append($"  回读={ch.ReadBackHash[..16]}…");
                        if (ch.HasWriteHash && !ch.IsLossless)
                            sb.Append($"  ⚠ 哈希不匹配!");
                        if (!ch.IsLossless && ch.FirstMismatchIndex >= 0)
                            sb.Append($"  首差异@[{ch.FirstMismatchIndex}] 最大误差={ch.MaxAbsoluteError:E3}");
                        sb.AppendLine();
                    }
                    return sb.ToString().TrimEnd();
                }
            }
        }

        /// <summary>
        /// 验证已存储的 TDMS 文件 —— 回读所有通道，与写入时的 SHA-256 指纹比对。
        /// 如果有写入时的哈希，使用哈希快速比对；否则退化为报告回读信息。
        /// </summary>
        /// <param name="filePath">TDMS 文件路径</param>
        /// <param name="writeHashes">写入时记录的通道哈希（通道名 → hash）</param>
        /// <param name="writeSampleCounts">写入时记录的通道样本数（通道名 → count）</param>
        public static SessionVerifyResult Verify(
            string filePath,
            IReadOnlyDictionary<string, string>? writeHashes,
            IReadOnlyDictionary<string, long>? writeSampleCounts)
        {
            var sw = Stopwatch.StartNew();
            var channelResults = new List<ChannelVerifyResult>();

            try
            {
                var map = TdmsReaderUtil.ListGroupsAndChannels(filePath);
                foreach (var kv in map)
                {
                    var groupName = kv.Key;
                    foreach (var channelName in kv.Value)
                    {
                        try
                        {
                            var (readSampleCount, readHash) = TdmsReaderUtil.ReadChannelRawHash(filePath, groupName, channelName);

                            var writeHash = "";
                            long writtenSamples = 0;
                            writeHashes?.TryGetValue(channelName, out writeHash!);
                            writeSampleCounts?.TryGetValue(channelName, out writtenSamples);

                            channelResults.Add(new ChannelVerifyResult
                            {
                                ChannelName = channelName,
                                WrittenSamples = writtenSamples,
                                ReadBackSamples = readSampleCount,
                                WriteHash = writeHash ?? "",
                                ReadBackHash = readHash,
                            });
                        }
                        catch (Exception ex)
                        {
                            channelResults.Add(new ChannelVerifyResult
                            {
                                ChannelName = channelName,
                                WriteHash = "(读取失败)",
                                ReadBackHash = ex.Message,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                channelResults.Add(new ChannelVerifyResult
                {
                    ChannelName = "(文件级错误)",
                    WriteHash = "",
                    ReadBackHash = ex.Message,
                });
            }

            sw.Stop();
            return new SessionVerifyResult
            {
                FilePath = filePath,
                Channels = channelResults,
                VerifyDuration = sw.Elapsed,
            };
        }

        // ═══════════════════════════════════════════════════════════
        //  持久化 SHA-256 清单（.sha256 JSON 文件）
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 持久化的哈希清单，与 TDMS 文件同目录、同名、扩展名 .sha256
        /// </summary>
        public sealed class HashManifest
        {
            [JsonPropertyName("version")]
            public int Version { get; set; } = 1;

            [JsonPropertyName("tdmsFile")]
            public string TdmsFile { get; set; } = "";

            [JsonPropertyName("createdAt")]
            public DateTime CreatedAt { get; set; } = DateTime.Now;

            [JsonPropertyName("channels")]
            public Dictionary<string, ChannelManifestEntry> Channels { get; set; } = new();
        }

        public sealed class ChannelManifestEntry
        {
            [JsonPropertyName("sha256")]
            public string Sha256 { get; set; } = "";

            [JsonPropertyName("sampleCount")]
            public long SampleCount { get; set; }
        }

        /// <summary>
        /// 获取 TDMS 文件对应的 .sha256 清单路径
        /// </summary>
        public static string GetManifestPath(string tdmsFilePath)
            => Path.ChangeExtension(tdmsFilePath, ".sha256");

        /// <summary>
        /// 将写入时的 SHA-256 指纹保存为 .sha256 JSON 文件（与 TDMS 文件同目录同名）
        /// </summary>
        public static void SaveManifest(
            string tdmsFilePath,
            IReadOnlyDictionary<string, string>? writeHashes,
            IReadOnlyDictionary<string, long>? writeSampleCounts)
        {
            if (writeHashes == null || writeHashes.Count == 0) return;

            var manifest = new HashManifest
            {
                TdmsFile = Path.GetFileName(tdmsFilePath),
                CreatedAt = DateTime.Now,
            };

            foreach (var kv in writeHashes)
            {
                long count = 0;
                writeSampleCounts?.TryGetValue(kv.Key, out count);
                manifest.Channels[kv.Key] = new ChannelManifestEntry
                {
                    Sha256 = kv.Value,
                    SampleCount = count,
                };
            }

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            var manifestPath = GetManifestPath(tdmsFilePath);
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
            Console.WriteLine($"[StorageVerifier] 已保存 SHA-256 清单: {manifestPath}");
        }

        /// <summary>
        /// 从 .sha256 JSON 文件加载写入时的 SHA-256 指纹
        /// </summary>
        /// <returns>成功则返回 (hashes, counts)；文件不存在或解析失败返回 (null, null)</returns>
        public static (IReadOnlyDictionary<string, string>? hashes, IReadOnlyDictionary<string, long>? counts) LoadManifest(string tdmsFilePath)
        {
            var manifestPath = GetManifestPath(tdmsFilePath);
            if (!File.Exists(manifestPath))
                return (null, null);

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<HashManifest>(json);
                if (manifest == null || manifest.Channels.Count == 0)
                    return (null, null);

                var hashes = new Dictionary<string, string>();
                var counts = new Dictionary<string, long>();
                foreach (var kv in manifest.Channels)
                {
                    hashes[kv.Key] = kv.Value.Sha256;
                    counts[kv.Key] = kv.Value.SampleCount;
                }

                Console.WriteLine($"[StorageVerifier] 已加载 SHA-256 清单: {manifestPath} ({hashes.Count} 通道)");
                return (hashes, counts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageVerifier] 加载清单失败: {manifestPath} — {ex.Message}");
                return (null, null);
            }
        }
    }
}
