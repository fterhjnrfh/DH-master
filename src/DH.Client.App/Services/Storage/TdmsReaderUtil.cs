using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NationalInstruments.Tdms;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace DH.Client.App.Services.Storage
{
    public static class TdmsReaderUtil
    {
        // 固定8字节签名：用于可靠标识压缩批次
        private static readonly byte[] Lz4Signature = Encoding.ASCII.GetBytes("DHLZ4v1!");
        private static readonly byte[] ZstdSignature = Encoding.ASCII.GetBytes("DHZSTv1!");
        private static readonly byte[] BrotliSignature = Encoding.ASCII.GetBytes("DHBROv1!");
        // v2 统一签名（支持所有压缩算法 + 预处理）
        private static readonly byte[] V2Signature = StorageCodec.V2Signature;
        /// <summary>
        /// Read double data from a TDMS file channel using TDMSReader package (NationalInstruments.Tdms).
        /// Supports common data types by converting to double.
        /// </summary>
        /// <param name="filePath">TDMS file path.</param>
        /// <param name="groupName">Group name containing the channel.</param>
        /// <param name="channelName">Channel name to read.</param>
        /// <returns>Array of double values from the channel.</returns>
        public static double[] ReadChannelData(string filePath, string groupName, string channelName)
        {
            using var tdms = new NationalInstruments.Tdms.File(filePath);
            tdms.Open();
            if (!tdms.Groups.TryGetValue(groupName, out var group))
                throw new InvalidOperationException($"Group '{groupName}' not found.");
            if (!group.Channels.TryGetValue(channelName, out var channel))
                throw new InvalidOperationException($"Channel '{channelName}' not found in group '{groupName}'.");

            double[] result;
            try
            {
                var d = channel.GetData<double>();
                result = d?.ToArray() ?? Array.Empty<double>();
            }
            catch
            {
                try
                {
                    var f = channel.GetData<float>();
                    result = f?.Select(x => (double)x).ToArray() ?? Array.Empty<double>();
                }
                catch
                {
                    try
                    {
                        var i = channel.GetData<int>();
                        result = i?.Select(x => (double)x).ToArray() ?? Array.Empty<double>();
                    }
                    catch
                    {
                        try
                        {
                            var s = channel.GetData<short>();
                            result = s?.Select(x => (double)x).ToArray() ?? Array.Empty<double>();
                        }
                        catch
                        {
                            try
                            {
                                var b = channel.GetData<byte>();
                                result = b?.Select(x => (double)x).ToArray() ?? Array.Empty<double>();
                            }
                            catch
                            {
                                result = Array.Empty<double>();
                            }
                        }
                    }
                }
            }

            // 检测并解压缩 LZ4 压缩的数据
            result = DecompressIfNeeded(result);

            return result;
        }

        public static (long sampleCount, string hash) ReadChannelRawHash(string filePath, string groupName, string channelName)
        {
            using var tdms = new NationalInstruments.Tdms.File(filePath);
            tdms.Open();
            if (!tdms.Groups.TryGetValue(groupName, out var group))
                throw new InvalidOperationException($"Group '{groupName}' not found.");
            if (!group.Channels.TryGetValue(channelName, out var channel))
                throw new InvalidOperationException($"Channel '{channelName}' not found in group '{groupName}'.");

            try
            {
                var floats = channel.GetData<float>()?.ToArray() ?? Array.Empty<float>();
                return (floats.Length, StorageVerifier.ComputeHash(floats));
            }
            catch
            {
                var doubles = channel.GetData<double>()?.ToArray() ?? Array.Empty<double>();
                return (doubles.Length, StorageVerifier.ComputeHash(doubles));
            }
        }
        
        /// <summary>
        /// 检测并解压缩压缩的数据。
        /// 支持 v2 格式（统一签名 DHCMPv2!，含压缩类型 + 预处理类型）和
        /// v1 格式（LZ4/Zstd/Brotli 独立签名，无预处理）。
        /// </summary>
        private static double[] DecompressIfNeeded(double[] data)
        {
            if (data == null || data.Length < 3)
                return data;
            
            var decompressed = new List<double>();
            int offset = 0;
            bool anySignature = false;

            while (offset < data.Length)
            {
                byte[] headerBytes = BitConverter.GetBytes(data[offset]);

                // ─── 优先检测 v2 统一签名 ───
                if (headerBytes.SequenceEqual(V2Signature) || headerBytes.Reverse().SequenceEqual(V2Signature))
                {
                    // v2 格式：[signature, compressionType, preprocessType, sampleCount, compressedSize, data...]
                    if (offset + 5 > data.Length) { anySignature = false; break; }
                    
                    var compType = (CompressionType)(int)data[offset + 1];
                    var prepType = (PreprocessType)(int)data[offset + 2];
                    int originalSampleCount = (int)data[offset + 3];
                    int compressedByteSize = (int)data[offset + 4];
                    
                    if (originalSampleCount <= 0 || compressedByteSize <= 0) { anySignature = false; break; }
                    
                    int doubleCount = (compressedByteSize + 7) / 8;
                    if (offset + 5 + doubleCount > data.Length) { anySignature = false; break; }
                    
                    try
                    {
                        // 提取压缩字节
                        var compressedBytes = new byte[doubleCount * 8]; // 分配足够空间
                        for (int i = 0; i < doubleCount; i++)
                        {
                            byte[] db = BitConverter.GetBytes(data[offset + 5 + i]);
                            Buffer.BlockCopy(db, 0, compressedBytes, i * 8, 8);
                        }
                        
                        int originalByteSize = originalSampleCount * sizeof(double);
                        
                        // 使用 StorageCodec 解压缩
                        byte[] decompressedBytes = StorageCodec.DecompressBytes(
                            compressedBytes, compressedByteSize, originalByteSize, compType);
                        
                        // 转回 double 数组
                        var batch = new double[originalSampleCount];
                        Buffer.BlockCopy(decompressedBytes, 0, batch, 0, originalByteSize);
                        
                        // 逆预处理
                        if (prepType != PreprocessType.None)
                            batch = DataPreprocessor.Reverse(batch, prepType);
                        
                        decompressed.AddRange(batch);
                        offset += 5 + doubleCount;
                        anySignature = true;
                    }
                    catch
                    {
                        anySignature = false;
                        break;
                    }
                    continue;
                }
                
                // ─── v1 签名检测（向后兼容） ───
                CompressionType detectedType = CompressionType.None;
                if (headerBytes.SequenceEqual(Lz4Signature) || headerBytes.Reverse().SequenceEqual(Lz4Signature))
                    detectedType = CompressionType.LZ4;
                else if (headerBytes.SequenceEqual(ZstdSignature) || headerBytes.Reverse().SequenceEqual(ZstdSignature))
                    detectedType = CompressionType.Zstd;
                else if (headerBytes.SequenceEqual(BrotliSignature) || headerBytes.Reverse().SequenceEqual(BrotliSignature))
                    detectedType = CompressionType.Brotli;

                if (detectedType == CompressionType.None) break;
                anySignature = true;

                // v1 格式：[signature, sampleCount, compressedSize, data...]
                if (offset + 3 > data.Length) { anySignature = false; break; }
                int v1SampleCount = (int)data[offset + 1];
                int v1CompressedSize = (int)data[offset + 2];
                if (v1SampleCount <= 0 || v1CompressedSize <= 0) { anySignature = false; break; }

                int v1DoubleCount = (v1CompressedSize + 7) / 8;
                if (offset + 3 + v1DoubleCount > data.Length) { anySignature = false; break; }

                try
                {
                    var compressedBytes = new byte[v1DoubleCount * 8];
                    for (int i = 0; i < v1DoubleCount; i++)
                    {
                        byte[] db = BitConverter.GetBytes(data[offset + 3 + i]);
                        Buffer.BlockCopy(db, 0, compressedBytes, i * 8, 8);
                    }
                    
                    int v1OriginalByteSize = v1SampleCount * sizeof(double);
                    
                    // 使用 StorageCodec 解压缩（v1 无预处理）
                    byte[] decompressedBytes = StorageCodec.DecompressBytes(
                        compressedBytes, v1CompressedSize, v1OriginalByteSize, detectedType);
                    
                    var batch = new double[v1SampleCount];
                    Buffer.BlockCopy(decompressedBytes, 0, batch, 0, v1OriginalByteSize);
                    decompressed.AddRange(batch);
                    offset += 3 + v1DoubleCount;
                }
                catch
                {
                    anySignature = false;
                    break;
                }
            }
            
            if (anySignature) return decompressed.ToArray();

            // 回退尝试旧格式（整段必须严格匹配，否则返回原始数据）
            decompressed.Clear();
            offset = 0;
            bool matchedOld = false;
            while (offset < data.Length)
            {
                if (offset + 2 > data.Length) { matchedOld = false; break; }
                int originalSampleCount = (int)data[offset];
                int compressedByteSize = (int)data[offset + 1];
                if (originalSampleCount <= 0 || compressedByteSize <= 0) { matchedOld = false; break; }
                int doubleCount = (compressedByteSize + 7) / 8;
                int expected = 2 + doubleCount;
                if (offset + expected > data.Length) { matchedOld = false; break; }
                try
                {
                    var compressedBytes = new byte[compressedByteSize];
                    for (int i = 0; i < doubleCount; i++)
                    {
                        byte[] db = BitConverter.GetBytes(data[offset + 2 + i]);
                        int bytesToCopy = Math.Min(8, compressedByteSize - i * 8);
                        Buffer.BlockCopy(db, 0, compressedBytes, i * 8, bytesToCopy);
                    }
                    int originalByteSize = originalSampleCount * sizeof(double);
                    var decompressedBytes = new byte[originalByteSize];
                    int decompressedSize = LZ4Codec.Decode(
                        compressedBytes, 0, compressedByteSize,
                        decompressedBytes, 0, originalByteSize);
                    if (decompressedSize != originalByteSize) { matchedOld = false; break; }
                    var batch = new double[originalSampleCount];
                    Buffer.BlockCopy(decompressedBytes, 0, batch, 0, originalByteSize);
                    decompressed.AddRange(batch);
                    offset += expected;
                    matchedOld = true;
                }
                catch
                {
                    matchedOld = false;
                    break;
                }
            }
            if (matchedOld && offset == data.Length) return decompressed.ToArray();
            return data;
        }

        /// <summary>
        /// Enumerate TDMS structure: groups and channels.
        /// </summary>
        public static IReadOnlyDictionary<string, string[]> ListGroupsAndChannels(string filePath)
        {
            using var tdms = new NationalInstruments.Tdms.File(filePath);
            tdms.Open();
            var dict = new Dictionary<string, string[]>();
            foreach (var kv in tdms.Groups)
            {
                var group = kv.Value;
                var names = group.Channels.Select(kv2 => kv2.Value.Name).ToArray();
                dict[group.Name] = names;
            }
            return dict;
        }

        /// <summary>
        /// Read all properties for a specific channel using nilibddc.dll (native API).
        /// Returns a dictionary of property name to value (typed when possible).
        /// </summary>
        public static IReadOnlyDictionary<string, object> ReadChannelProperties(string filePath, string groupName, string channelName)
        {
            var props = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            IntPtr file = IntPtr.Zero;
            int err = TdmsNative.DDC_OpenFileEx(filePath, "TDMS", 1, ref file);
            if (err != 0 || file == IntPtr.Zero)
                throw new InvalidOperationException($"DDC_OpenFileEx failed: {err} {TdmsNative.DescribeError(err)}");

            try
            {
                // Locate group by name
                int numGroups = 0;
                err = TdmsNative.DDC_GetNumChannelGroups(file, ref numGroups);
                if (err != 0) throw new InvalidOperationException($"DDC_GetNumChannelGroups failed: {err} {TdmsNative.DescribeError(err)}");
                var groups = new IntPtr[numGroups];
                err = TdmsNative.DDC_GetChannelGroups(file, groups, numGroups);
                if (err != 0) throw new InvalidOperationException($"DDC_GetChannelGroups failed: {err} {TdmsNative.DescribeError(err)}");

                IntPtr targetGroup = IntPtr.Zero;
                for (int gi = 0; gi < numGroups; gi++)
                {
                    var g = groups[gi];
                    int len = 0;
                    if (TdmsNative.DDC_GetChannelGroupStringPropertyLength(g, "name", ref len) >= 0 && len >= 0)
                    {
                        var sb = new StringBuilder(len + 1);
                        if (TdmsNative.DDC_GetChannelGroupPropertyString(g, "name", sb, sb.Capacity) == 0)
                        {
                            var name = sb.ToString();
                            if (string.Equals(name, groupName, StringComparison.Ordinal))
                            {
                                targetGroup = g;
                                break;
                            }
                        }
                    }
                }
                if (targetGroup == IntPtr.Zero)
                    throw new InvalidOperationException($"Group '{groupName}' not found.");

                // Locate channel by name
                int numChannels = 0;
                err = TdmsNative.DDC_GetNumChannels(targetGroup, ref numChannels);
                if (err != 0) throw new InvalidOperationException($"DDC_GetNumChannels failed: {err} {TdmsNative.DescribeError(err)}");
                var channels = new IntPtr[numChannels];
                err = TdmsNative.DDC_GetChannels(targetGroup, channels, numChannels);
                if (err != 0) throw new InvalidOperationException($"DDC_GetChannels failed: {err} {TdmsNative.DescribeError(err)}");

                IntPtr targetChannel = IntPtr.Zero;
                for (int ci = 0; ci < numChannels; ci++)
                {
                    var ch = channels[ci];
                    int len = 0;
                    if (TdmsNative.DDC_GetChannelStringPropertyLength(ch, "name", ref len) >= 0 && len >= 0)
                    {
                        var sb = new StringBuilder(len + 1);
                        if (TdmsNative.DDC_GetChannelPropertyString(ch, "name", sb, sb.Capacity) == 0)
                        {
                            var name = sb.ToString();
                            if (string.Equals(name, channelName, StringComparison.Ordinal))
                            {
                                targetChannel = ch;
                                break;
                            }
                        }
                    }
                }
                if (targetChannel == IntPtr.Zero)
                    throw new InvalidOperationException($"Channel '{channelName}' not found.");

                // Enumerate all properties
                int numProps = 0;
                err = TdmsNative.DDC_GetNumChannelProperties(targetChannel, ref numProps);
                if (err != 0) throw new InvalidOperationException($"DDC_GetNumChannelProperties failed: {err} {TdmsNative.DescribeError(err)}");

                for (int i = 0; i < numProps; i++)
                {
                    int nameLen = 0;
                    if (TdmsNative.DDC_GetChannelPropertyNameLengthFromIndex(targetChannel, i, ref nameLen) != 0 || nameLen <= 0)
                        continue;
                    var nameSb = new StringBuilder(nameLen + 1);
                    if (TdmsNative.DDC_GetChannelPropertyNameFromIndex(targetChannel, i, nameSb, nameSb.Capacity) != 0)
                        continue;
                    var propName = nameSb.ToString();
                    if (string.IsNullOrWhiteSpace(propName)) continue;

                    // Get property type
                    if (TdmsNative.DDC_GetChannelPropertyType(targetChannel, propName, out var dt) != 0)
                        continue;

                    try
                    {
                        switch (dt)
                        {
                            case TdmsNative.DDCDataType.String:
                                int slen = 0;
                                if (TdmsNative.DDC_GetChannelStringPropertyLength(targetChannel, propName, ref slen) >= 0 && slen >= 0)
                                {
                                    var sb = new StringBuilder(slen + 1);
                                    if (TdmsNative.DDC_GetChannelPropertyString(targetChannel, propName, sb, sb.Capacity) == 0)
                                        props[propName] = sb.ToString();
                                }
                                break;
                            case TdmsNative.DDCDataType.Double:
                                if (TdmsNative.DDC_GetChannelPropertyDouble(targetChannel, propName, out var dv) == 0)
                                    props[propName] = dv;
                                break;
                            case TdmsNative.DDCDataType.Float:
                                if (TdmsNative.DDC_GetChannelPropertyFloat(targetChannel, propName, out var fv) == 0)
                                    props[propName] = fv;
                                break;
                            case TdmsNative.DDCDataType.Int32:
                                if (TdmsNative.DDC_GetChannelPropertyInt32(targetChannel, propName, out var iv) == 0)
                                    props[propName] = iv;
                                break;
                            case TdmsNative.DDCDataType.Int16:
                                if (TdmsNative.DDC_GetChannelPropertyInt16(targetChannel, propName, out var i16) == 0)
                                    props[propName] = i16;
                                break;
                            case TdmsNative.DDCDataType.UInt8:
                                if (TdmsNative.DDC_GetChannelPropertyUInt8(targetChannel, propName, out var u8) == 0)
                                    props[propName] = u8;
                                break;
                            default:
                                // Other types (e.g., Timestamp) not handled here
                                break;
                        }
                    }
                    catch
                    {
                        // Ignore individual property read failures
                    }
                }

                return props;
            }
            finally
            {
                try { if (file != IntPtr.Zero) TdmsNative.DDC_CloseFile(file); } catch { }
            }
        }
    }
}
