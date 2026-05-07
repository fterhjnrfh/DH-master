using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StorageThroughputProbe;

internal static class Program
{
    private const int FloatBytes = sizeof(float);
    private const int HeaderBytes = 4096;

    private static int Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            options = options.WithResolvedOutputDirectory();
            Directory.CreateDirectory(options.OutputDirectory);

            var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var runDirectory = Path.Combine(options.OutputDirectory, $"storage-throughput-{runId}");
            Directory.CreateDirectory(runDirectory);

            using var log = new StreamWriter(
                new FileStream(Path.Combine(runDirectory, "storage-throughput.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            WriteLine(log, $"RunDirectory={runDirectory}");
            WriteLine(log, $"Mode={options.Mode}");
            WriteLine(log, $"Sources={options.Sources}");
            WriteLine(log, $"ChannelsPerSource={options.Channels}");
            WriteLine(log, $"SampleRateHz={options.SampleRateHz}");
            WriteLine(log, $"Seconds={options.Seconds}");
            WriteLine(log, $"ChunkMilliseconds={options.ChunkMilliseconds}");
            WriteLine(log, $"SegmentSeconds={options.SegmentSeconds}");
            WriteLine(log, $"FileBufferBytes={options.FileBufferBytes}");
            WriteLine(log, $"Preallocate={options.Preallocate}");
            WriteLine(log, $"FlushToDisk={options.FlushToDisk}");

            var expectedBytes = options.ExpectedPayloadBytes;
            WriteLine(log, $"ExpectedPayloadBytes={expectedBytes}");
            WriteLine(log, $"RequiredPayloadMBps={ToMiB(expectedBytes / options.Seconds):F1}");

            var payload = CreatePayload(options.SourceChunkPayloadBytes);
            var total = options.Mode switch
            {
                ProbeMode.RawSingleStream => RunRawSingleStream(options, runDirectory, log, payload),
                ProbeMode.RawSourceSegment => RunRawSourceSegment(options, runDirectory, log, payload),
                ProbeMode.RawSourceSegmentContiguous => RunRawSourceSegmentContiguous(options, runDirectory, log, payload),
                ProbeMode.RawSegmentContainer => RunRawSegmentContainer(options, runDirectory, log, payload),
                _ => throw new InvalidOperationException($"Unsupported mode: {options.Mode}")
            };

            WriteLine(log, $"TotalPayloadBytes={total.PayloadBytes}");
            WriteLine(log, $"TotalElapsedMs={total.Elapsed.TotalMilliseconds:F3}");
            WriteLine(log, $"PayloadMBps={ToMiB(total.PayloadBytes / Math.Max(total.Elapsed.TotalSeconds, 0.001)):F1}");
            WriteLine(log, "Result=Completed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static ProbeResult RunRawSingleStream(ProbeOptions options, string runDirectory, TextWriter log, byte[] payload)
    {
        var path = Path.Combine(runDirectory, "raw-single-stream.bin");
        var chunkCount = options.TotalChunkCount;
        var totalPayloadBytes = 0L;
        var stopwatch = Stopwatch.StartNew();

        using var stream = CreateFileStream(path, options);
        WriteHeader(stream, "DHRAW1", options, sourceIndex: -1, segmentIndex: -1, payloadBytes: options.ExpectedPayloadBytes);

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            for (var source = 0; source < options.Sources; source++)
            {
                stream.Write(payload, 0, payload.Length);
                totalPayloadBytes += payload.Length;
            }

            if ((chunk + 1) % options.ChunksPerSecond == 0)
            {
                var seconds = (chunk + 1) / options.ChunksPerSecond;
                WriteLine(log, $"Progress:second={seconds},payloadBytes={totalPayloadBytes},elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3},payloadMBps={ToMiB(totalPayloadBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)):F1}");
            }
        }

        var flushStopwatch = Stopwatch.StartNew();
        stream.Flush(options.FlushToDisk);
        flushStopwatch.Stop();
        stopwatch.Stop();

        WriteLine(log, $"SingleStream:flushMs={flushStopwatch.Elapsed.TotalMilliseconds:F3}");
        return new ProbeResult(totalPayloadBytes, stopwatch.Elapsed);
    }

    private static ProbeResult RunRawSourceSegment(ProbeOptions options, string runDirectory, TextWriter log, byte[] payload)
    {
        var totalPayloadBytes = 0L;
        var totalStopwatch = Stopwatch.StartNew();
        var segmentCount = (int)Math.Ceiling(options.Seconds / options.SegmentSeconds);

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentStartSecond = segment * options.SegmentSeconds;
            var segmentSeconds = Math.Min(options.SegmentSeconds, options.Seconds - segmentStartSecond);
            var segmentChunks = checked((int)Math.Round(segmentSeconds * options.ChunksPerSecond));
            var segmentPayloadBytesPerSource = (long)segmentChunks * payload.Length;
            var segmentDirectory = Path.Combine(runDirectory, $"segment-{segment:D5}");
            Directory.CreateDirectory(segmentDirectory);

            var segmentStopwatch = Stopwatch.StartNew();
            var streams = new FileStream[options.Sources];
            try
            {
                for (var source = 0; source < options.Sources; source++)
                {
                    var sourcePath = Path.Combine(segmentDirectory, $"source-{source:D3}.dhraw");
                    streams[source] = CreateFileStream(sourcePath, options);
                    if (options.Preallocate)
                    {
                        streams[source].SetLength(HeaderBytes + segmentPayloadBytesPerSource);
                    }

                    WriteHeader(streams[source], "DHSEG1", options, source, segment, segmentPayloadBytesPerSource);
                }

                for (var chunk = 0; chunk < segmentChunks; chunk++)
                {
                    for (var source = 0; source < options.Sources; source++)
                    {
                        streams[source].Write(payload, 0, payload.Length);
                        totalPayloadBytes += payload.Length;
                    }
                }

                var flushStopwatch = Stopwatch.StartNew();
                for (var source = 0; source < options.Sources; source++)
                {
                    streams[source].Flush(options.FlushToDisk);
                }

                flushStopwatch.Stop();
                segmentStopwatch.Stop();

                var segmentPayloadBytes = segmentPayloadBytesPerSource * options.Sources;
                WriteLine(log, $"Segment:{segment}:seconds={segmentSeconds:F3},payloadBytes={segmentPayloadBytes},elapsedMs={segmentStopwatch.Elapsed.TotalMilliseconds:F3},flushMs={flushStopwatch.Elapsed.TotalMilliseconds:F3},payloadMBps={ToMiB(segmentPayloadBytes / Math.Max(segmentStopwatch.Elapsed.TotalSeconds, 0.001)):F1}");
            }
            finally
            {
                foreach (var stream in streams)
                {
                    stream?.Dispose();
                }
            }
        }

        totalStopwatch.Stop();
        return new ProbeResult(totalPayloadBytes, totalStopwatch.Elapsed);
    }

    private static ProbeResult RunRawSourceSegmentContiguous(ProbeOptions options, string runDirectory, TextWriter log, byte[] payload)
    {
        var totalPayloadBytes = 0L;
        var totalStopwatch = Stopwatch.StartNew();
        var segmentCount = (int)Math.Ceiling(options.Seconds / options.SegmentSeconds);

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentStartSecond = segment * options.SegmentSeconds;
            var segmentSeconds = Math.Min(options.SegmentSeconds, options.Seconds - segmentStartSecond);
            var segmentChunks = checked((int)Math.Round(segmentSeconds * options.ChunksPerSecond));
            var segmentPayloadBytesPerSource = (long)segmentChunks * payload.Length;
            var segmentDirectory = Path.Combine(runDirectory, $"segment-{segment:D5}");
            Directory.CreateDirectory(segmentDirectory);

            var segmentStopwatch = Stopwatch.StartNew();
            for (var source = 0; source < options.Sources; source++)
            {
                var sourcePath = Path.Combine(segmentDirectory, $"source-{source:D3}.dhraw");
                using var stream = CreateFileStream(sourcePath, options);
                if (options.Preallocate)
                {
                    stream.SetLength(HeaderBytes + segmentPayloadBytesPerSource);
                }

                WriteHeader(stream, "DHSRC1", options, source, segment, segmentPayloadBytesPerSource);
                for (var chunk = 0; chunk < segmentChunks; chunk++)
                {
                    stream.Write(payload, 0, payload.Length);
                    totalPayloadBytes += payload.Length;
                }

                stream.Flush(options.FlushToDisk);
            }

            segmentStopwatch.Stop();

            var segmentPayloadBytes = segmentPayloadBytesPerSource * options.Sources;
            WriteLine(log, $"SourceContiguousSegment:{segment}:seconds={segmentSeconds:F3},payloadBytes={segmentPayloadBytes},elapsedMs={segmentStopwatch.Elapsed.TotalMilliseconds:F3},payloadMBps={ToMiB(segmentPayloadBytes / Math.Max(segmentStopwatch.Elapsed.TotalSeconds, 0.001)):F1}");
        }

        totalStopwatch.Stop();
        return new ProbeResult(totalPayloadBytes, totalStopwatch.Elapsed);
    }

    private static ProbeResult RunRawSegmentContainer(ProbeOptions options, string runDirectory, TextWriter log, byte[] payload)
    {
        var totalPayloadBytes = 0L;
        var totalStopwatch = Stopwatch.StartNew();
        var segmentCount = (int)Math.Ceiling(options.Seconds / options.SegmentSeconds);

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentStartSecond = segment * options.SegmentSeconds;
            var segmentSeconds = Math.Min(options.SegmentSeconds, options.Seconds - segmentStartSecond);
            var segmentChunks = checked((int)Math.Round(segmentSeconds * options.ChunksPerSecond));
            var segmentPayloadBytesPerSource = (long)segmentChunks * payload.Length;
            var segmentPayloadBytes = segmentPayloadBytesPerSource * options.Sources;
            var segmentPath = Path.Combine(runDirectory, $"segment-{segment:D5}.dhraw");

            var segmentStopwatch = Stopwatch.StartNew();
            using (var stream = CreateFileStream(segmentPath, options))
            {
                if (options.Preallocate)
                {
                    stream.SetLength(HeaderBytes + segmentPayloadBytes);
                }

                WriteHeader(stream, "DHCON1", options, sourceIndex: -1, segment, segmentPayloadBytes);
                for (var source = 0; source < options.Sources; source++)
                {
                    for (var chunk = 0; chunk < segmentChunks; chunk++)
                    {
                        stream.Write(payload, 0, payload.Length);
                        totalPayloadBytes += payload.Length;
                    }
                }

                stream.Flush(options.FlushToDisk);
            }

            segmentStopwatch.Stop();
            WriteLine(log, $"SegmentContainer:{segment}:seconds={segmentSeconds:F3},payloadBytes={segmentPayloadBytes},elapsedMs={segmentStopwatch.Elapsed.TotalMilliseconds:F3},payloadMBps={ToMiB(segmentPayloadBytes / Math.Max(segmentStopwatch.Elapsed.TotalSeconds, 0.001)):F1}");
        }

        totalStopwatch.Stop();
        return new ProbeResult(totalPayloadBytes, totalStopwatch.Elapsed);
    }

    private static FileStream CreateFileStream(string path, ProbeOptions options)
    {
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            options.FileBufferBytes,
            FileOptions.SequentialScan);
    }

    private static void WriteHeader(Stream stream, string magic, ProbeOptions options, int sourceIndex, int segmentIndex, long payloadBytes)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        Encoding.ASCII.GetBytes(magic, header);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], options.Sources);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], options.Channels);
        BinaryPrimitives.WriteDoubleLittleEndian(header[24..], options.SampleRateHz);
        BinaryPrimitives.WriteDoubleLittleEndian(header[32..], options.ChunkMilliseconds);
        BinaryPrimitives.WriteDoubleLittleEndian(header[40..], options.SegmentSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..], sourceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header[52..], segmentIndex);
        BinaryPrimitives.WriteInt64LittleEndian(header[56..], payloadBytes);
        stream.Write(header);
    }

    private static byte[] CreatePayload(int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        for (var offset = 0; offset < payload.Length; offset += FloatBytes)
        {
            var sampleIndex = offset / FloatBytes;
            var value = sampleIndex % 2048;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, FloatBytes), value);
        }

        return payload;
    }

    private static void WriteLine(TextWriter log, string message)
    {
        Console.WriteLine(message);
        log.WriteLine($"{DateTime.Now:O} {message}");
    }

    private static double ToMiB(double bytes) => bytes / 1024.0 / 1024.0;

    private readonly record struct ProbeResult(long PayloadBytes, TimeSpan Elapsed);

    private enum ProbeMode
    {
        RawSingleStream,
        RawSourceSegment,
        RawSourceSegmentContiguous,
        RawSegmentContainer
    }

    private sealed class ProbeOptions
    {
        public string OutputDirectory { get; private init; } = @"data\storage-probe";
        public ProbeMode Mode { get; private init; } = ProbeMode.RawSourceSegment;
        public int Sources { get; private init; } = 10;
        public int Channels { get; private init; } = 16;
        public double SampleRateHz { get; private init; } = 1_000_000;
        public double Seconds { get; private init; } = 20;
        public double ChunkMilliseconds { get; private init; } = 100;
        public double SegmentSeconds { get; private init; } = 2;
        public int FileBufferBytes { get; private init; } = 4 * 1024 * 1024;
        public bool Preallocate { get; private init; }
        public bool FlushToDisk { get; private init; }

        public int SamplesPerChunkPerChannel => checked((int)Math.Round(SampleRateHz * ChunkMilliseconds / 1000.0));
        public int SourceChunkPayloadBytes => checked(SamplesPerChunkPerChannel * Channels * FloatBytes);
        public int ChunksPerSecond => checked((int)Math.Round(1000.0 / ChunkMilliseconds));
        public int TotalChunkCount => checked((int)Math.Round(Seconds * ChunksPerSecond));
        public long ExpectedPayloadBytes => checked((long)Sources * Channels * (long)Math.Round(SampleRateHz * Seconds) * FloatBytes);

        public static ProbeOptions Parse(string[] args)
        {
            if (args.Any(static arg => arg is "-h" or "--help" or "/?"))
            {
                PrintHelp();
                Environment.Exit(0);
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unknown argument: {arg}");
                }

                var key = arg[2..];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values[key] = "true";
                }
                else
                {
                    values[key] = args[++i];
                }
            }

            var options = new ProbeOptions
            {
                OutputDirectory = GetString(values, "output", @"data\storage-probe"),
                Mode = ParseMode(GetString(values, "mode", "raw-source-segment")),
                Sources = GetInt(values, "sources", 10),
                Channels = GetInt(values, "channels", 16),
                SampleRateHz = GetDouble(values, "sample-rate", 1_000_000),
                Seconds = GetDouble(values, "seconds", 20),
                ChunkMilliseconds = GetDouble(values, "chunk-ms", 100),
                SegmentSeconds = GetDouble(values, "segment-seconds", 2),
                FileBufferBytes = GetInt(values, "file-buffer-mb", 4) * 1024 * 1024,
                Preallocate = GetBool(values, "preallocate", false),
                FlushToDisk = GetBool(values, "flush-to-disk", false)
            };

            options.Validate();
            return options;
        }

        public ProbeOptions WithResolvedOutputDirectory()
        {
            return new ProbeOptions
            {
                OutputDirectory = ResolveOutputDirectory(OutputDirectory),
                Mode = Mode,
                Sources = Sources,
                Channels = Channels,
                SampleRateHz = SampleRateHz,
                Seconds = Seconds,
                ChunkMilliseconds = ChunkMilliseconds,
                SegmentSeconds = SegmentSeconds,
                FileBufferBytes = FileBufferBytes,
                Preallocate = Preallocate,
                FlushToDisk = FlushToDisk
            };
        }

        private void Validate()
        {
            if (Sources <= 0) throw new ArgumentOutOfRangeException(nameof(Sources));
            if (Channels <= 0) throw new ArgumentOutOfRangeException(nameof(Channels));
            if (SampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRateHz));
            if (Seconds <= 0) throw new ArgumentOutOfRangeException(nameof(Seconds));
            if (ChunkMilliseconds <= 0 || ChunkMilliseconds > 1000) throw new ArgumentOutOfRangeException(nameof(ChunkMilliseconds));
            if (SegmentSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(SegmentSeconds));
            if (FileBufferBytes <= 0) throw new ArgumentOutOfRangeException(nameof(FileBufferBytes));
            if (Math.Abs(Math.Round(1000.0 / ChunkMilliseconds) - (1000.0 / ChunkMilliseconds)) > 0.0001)
            {
                throw new ArgumentException("--chunk-ms must divide 1000 cleanly.");
            }
        }

        private static ProbeMode ParseMode(string value)
        {
            return value.ToLowerInvariant() switch
            {
                "raw-single-stream" => ProbeMode.RawSingleStream,
                "raw-source-segment" => ProbeMode.RawSourceSegment,
                "raw-source-segment-contiguous" => ProbeMode.RawSourceSegmentContiguous,
                "raw-segment-container" => ProbeMode.RawSegmentContainer,
                _ => throw new ArgumentException($"Unknown --mode '{value}'. Use raw-single-stream, raw-source-segment, raw-source-segment-contiguous, or raw-segment-container.")
            };
        }

        private static string GetString(IReadOnlyDictionary<string, string> values, string key, string defaultValue)
        {
            return values.TryGetValue(key, out var value) ? value : defaultValue;
        }

        private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
        {
            return values.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : defaultValue;
        }

        private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
        {
            return values.TryGetValue(key, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : defaultValue;
        }

        private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
        {
            return values.TryGetValue(key, out var value) ? bool.Parse(value) : defaultValue;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(
                "StorageThroughputProbe\n\n" +
                "Options:\n" +
                "  --output <dir>               Default: data\\storage-probe\n" +
                "  --mode <mode>                raw-single-stream | raw-source-segment | raw-source-segment-contiguous | raw-segment-container\n" +
                "  --sources <n>                Default: 10\n" +
                "  --channels <n>               Default: 16\n" +
                "  --sample-rate <hz>           Default: 1000000\n" +
                "  --seconds <n>                Default: 20\n" +
                "  --chunk-ms <n>               Default: 100\n" +
                "  --segment-seconds <n>        Default: 2\n" +
                "  --file-buffer-mb <n>         Default: 4\n" +
                "  --preallocate <true|false>   Default: false\n" +
                "  --flush-to-disk <true|false> Default: false");
        }

        private static string ResolveOutputDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Path.Combine(FindRepoRoot(), "data", "storage-probe");
            }

            string normalized = path.Trim();
            string slashNormalized = normalized.Replace('/', '\\');
            if (slashNormalized.Equals("data\\storage-probe", StringComparison.OrdinalIgnoreCase)
                || slashNormalized.Equals(".\\data\\storage-probe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(FindRepoRoot(), "data", "storage-probe");
            }

            if (Path.IsPathRooted(normalized) || IsWindowsAbsolutePath(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            return Path.GetFullPath(Path.Combine(FindRepoRoot(), normalized));
        }

        private static bool IsWindowsAbsolutePath(string path)
        {
            return path.Length >= 3
                && char.IsLetter(path[0])
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/');
        }

        private static string FindRepoRoot()
        {
            var starts = new[]
            {
                AppContext.BaseDirectory,
                Environment.CurrentDirectory
            };

            foreach (string start in starts)
            {
                try
                {
                    var dir = new DirectoryInfo(start);
                    while (dir != null)
                    {
                        if (File.Exists(Path.Combine(dir.FullName, "DH.sln")))
                        {
                            return dir.FullName;
                        }

                        dir = dir.Parent;
                    }
                }
                catch
                {
                }
            }

            return Environment.CurrentDirectory;
        }
    }
}
