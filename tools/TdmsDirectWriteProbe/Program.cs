using System.Diagnostics;
using System.Globalization;
using System.Text;
using DH.Client.App.Services.Storage;

namespace TdmsDirectWriteProbe;

internal static class Program
{
    private const int FloatBytes = sizeof(float);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Any(static arg => string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)))
            {
                PrintUsage();
                return 0;
            }

            ProbeOptions options = ProbeOptions.Parse(args).WithResolvedOutputDirectory();
            Directory.CreateDirectory(options.OutputDirectory);

            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string runDirectory = Path.Combine(options.OutputDirectory, $"tdms-direct-write-{runId}");
            Directory.CreateDirectory(runDirectory);

            using var log = new StreamWriter(
                new FileStream(Path.Combine(runDirectory, "tdms-direct-write.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            WriteLine(log, $"RunDirectory={runDirectory}");
            WriteLine(log, $"TdmsAvailable={TdmsSourceSegmentFileWriter.IsAvailable}");
            WriteLine(log, $"Sources={options.Sources}");
            WriteLine(log, $"ChannelsPerSource={options.Channels}");
            WriteLine(log, $"SampleRateHz={options.SampleRateHz}");
            WriteLine(log, $"Seconds={options.Seconds}");
            WriteLine(log, $"SegmentSeconds={options.SegmentSeconds}");
            WriteLine(log, $"ParallelSources={options.ParallelSources}");
            WriteLine(log, $"Writer={options.Writer}");
            WriteLine(log, $"ValidateFirstRead={options.ValidateFirstRead}");
            WriteLine(log, $"ExpectedPayloadBytes={options.ExpectedPayloadBytes}");
            WriteLine(log, $"RequiredPayloadMiBps={ToMiB(options.ExpectedPayloadBytes / options.Seconds):F1}");

            if (options.Writer == TdmsSegmentWriterKind.Ddc && !TdmsSourceSegmentFileWriter.IsAvailable)
            {
                WriteLine(log, "Result=Failed:TDMS library is not available");
                return 2;
            }

            ProbeResult result = RunProbe(options, runDirectory, log);
            WriteLine(log, $"TotalPayloadBytes={result.PayloadBytes}");
            WriteLine(log, $"TotalFileBytes={result.FileBytes}");
            WriteLine(log, $"TotalElapsedMs={result.Elapsed.TotalMilliseconds:F3}");
            WriteLine(log, $"PayloadMiBps={ToMiB(result.PayloadBytes / Math.Max(result.Elapsed.TotalSeconds, 0.001)):F1}");
            WriteLine(log, "Result=Completed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static ProbeResult RunProbe(ProbeOptions options, string runDirectory, TextWriter log)
    {
        float[][] payloads = CreateChannelPayloads(options.Channels, options.SamplesPerSegment);
        var totalStopwatch = Stopwatch.StartNew();
        long totalPayloadBytes = 0;
        long totalFileBytes = 0;
        int segmentCount = (int)Math.Ceiling(options.Seconds / options.SegmentSeconds);

        for (int segment = 0; segment < segmentCount; segment++)
        {
            double segmentStartSecond = segment * options.SegmentSeconds;
            double segmentSeconds = Math.Min(options.SegmentSeconds, options.Seconds - segmentStartSecond);
            int samplesPerChannel = checked((int)Math.Round(segmentSeconds * options.SampleRateHz));
            string segmentDirectory = Path.Combine(runDirectory, $"segment-{segment:D5}");
            Directory.CreateDirectory(segmentDirectory);

            var segmentStopwatch = Stopwatch.StartNew();
            var results = options.ParallelSources
                ? Enumerable.Range(0, options.Sources).AsParallel().AsOrdered().Select(source => WriteSourceSegment(options, segmentDirectory, segment, source, samplesPerChannel, payloads)).ToArray()
                : Enumerable.Range(0, options.Sources).Select(source => WriteSourceSegment(options, segmentDirectory, segment, source, samplesPerChannel, payloads)).ToArray();
            segmentStopwatch.Stop();

            long segmentPayloadBytes = results.Sum(static result => result.PayloadBytes);
            long segmentFileBytes = results.Sum(static result => result.FileBytes);
            totalPayloadBytes += segmentPayloadBytes;
            totalFileBytes += segmentFileBytes;

            double appendMs = results.Sum(static result => result.AppendElapsed.TotalMilliseconds);
            double saveMs = results.Sum(static result => result.SaveElapsed.TotalMilliseconds);
            double closeMs = results.Sum(static result => result.CloseElapsed.TotalMilliseconds);
            WriteLine(log, $"Segment:{segment}:seconds={segmentSeconds:F3},files={results.Length},payloadBytes={segmentPayloadBytes},fileBytes={segmentFileBytes},elapsedMs={segmentStopwatch.Elapsed.TotalMilliseconds:F3},appendMsSum={appendMs:F3},saveMsSum={saveMs:F3},closeMsSum={closeMs:F3},payloadMiBps={ToMiB(segmentPayloadBytes / Math.Max(segmentStopwatch.Elapsed.TotalSeconds, 0.001)):F1}");

            if (segment == 0 && options.ValidateFirstRead && results.Length > 0)
            {
                ValidateFirstRead(options, results[0], log);
            }
        }

        totalStopwatch.Stop();
        return new ProbeResult(totalPayloadBytes, totalFileBytes, totalStopwatch.Elapsed);
    }

    private static void ValidateFirstRead(ProbeOptions options, TdmsSourceSegmentWriteResult result, TextWriter log)
    {
        string groupName = $"source_{result.SourceId:D4}";
        string channelName = $"AI{result.SourceId * options.Channels:D4}";
        var stopwatch = Stopwatch.StartNew();
        double[] samples = TdmsReaderUtil.ReadChannelData(result.FilePath, groupName, channelName);
        stopwatch.Stop();

        if (samples.Length != result.SamplesPerChannel)
        {
            throw new InvalidDataException($"First-read validation failed: channel={channelName}, expected={result.SamplesPerChannel}, actual={samples.Length}.");
        }

        WriteLine(log, $"ValidateFirstRead=Passed:file={Path.GetFileName(result.FilePath)},group={groupName},channel={channelName},samples={samples.Length},elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}");
    }

    private static TdmsSourceSegmentWriteResult WriteSourceSegment(
        ProbeOptions options,
        string segmentDirectory,
        int segment,
        int source,
        int samplesPerChannel,
        float[][] payloads)
    {
        if (samplesPerChannel > payloads[0].Length)
        {
            throw new InvalidOperationException($"Segment requires {samplesPerChannel} samples but payload buffer has {payloads[0].Length}.");
        }

        string sourcePath = Path.Combine(segmentDirectory, $"source_{source:D4}_seg{segment:D6}.tdms");
        int channelBase = source * options.Channels;
        int[] channelIds = Enumerable.Range(0, options.Channels)
            .Select(channel => channelBase + channel)
            .ToArray();

        var writer = new TdmsSourceSegmentFileWriter();
        var request = new TdmsSourceSegmentWriteRequest
        {
            FilePath = sourcePath,
            SourceId = source,
            SegmentIndex = segment,
            SampleRateHz = options.SampleRateHz,
            SamplesPerChannel = samplesPerChannel,
            ChannelIds = channelIds,
            GetSamples = channelId => payloads[channelId - channelBase],
            Overwrite = true
        };

        return options.Writer == TdmsSegmentWriterKind.Manual
            ? new ManualTdmsSourceSegmentFileWriter().Write(request)
            : writer.Write(request);
    }

    private static float[][] CreateChannelPayloads(int channels, int samplesPerChannel)
    {
        var payloads = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            var payload = new float[samplesPerChannel];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (float)Math.Sin((i + channel) * 0.001);
            }

            payloads[channel] = payload;
        }

        return payloads;
    }

    private static void WriteLine(TextWriter log, string message)
    {
        Console.WriteLine(message);
        log.WriteLine(message);
    }

    private static void PrintUsage()
        => Console.WriteLine("TdmsDirectWriteProbe --output <dir> --sources 10 --channels 16 --sample-rate 1000000 --seconds 10 --segment-seconds 2 --parallel-sources false --writer ddc|manual");

    private static double ToMiB(double bytes) => bytes / (1024d * 1024d);

    private sealed record ProbeResult(long PayloadBytes, long FileBytes, TimeSpan Elapsed);

    private sealed class ProbeOptions
    {
        public string OutputDirectory { get; private init; } = Path.Combine(".", "data", "tdms-direct-probe");
        public int Sources { get; private init; } = 10;
        public int Channels { get; private init; } = 16;
        public double SampleRateHz { get; private init; } = 1_000_000;
        public double Seconds { get; private init; } = 10;
        public double SegmentSeconds { get; private init; } = 2;
        public bool ParallelSources { get; private init; }
        public TdmsSegmentWriterKind Writer { get; private init; } = TdmsSegmentWriterKind.Ddc;
        public bool ValidateFirstRead { get; private init; }
        public int SamplesPerSegment => checked((int)Math.Round(SampleRateHz * SegmentSeconds));
        public long ExpectedPayloadBytes => checked((long)Math.Round(Sources * Channels * SampleRateHz * Seconds * FloatBytes));

        public ProbeOptions WithResolvedOutputDirectory()
        {
            return new ProbeOptions
            {
                OutputDirectory = Path.GetFullPath(OutputDirectory),
                Sources = Sources,
                Channels = Channels,
                SampleRateHz = SampleRateHz,
                Seconds = Seconds,
                SegmentSeconds = SegmentSeconds,
                ParallelSources = ParallelSources,
                Writer = Writer,
                ValidateFirstRead = ValidateFirstRead
            };
        }

        public static ProbeOptions Parse(string[] args)
        {
            var options = new ProbeOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next()
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {arg}.");
                    }

                    return args[++i];
                }

                options = arg switch
                {
                    "--output" => options.WithOutput(Next()),
                    "--sources" => options.WithSources(ParseInt(Next(), arg)),
                    "--channels" => options.WithChannels(ParseInt(Next(), arg)),
                    "--sample-rate" => options.WithSampleRate(ParseDouble(Next(), arg)),
                    "--seconds" => options.WithSeconds(ParseDouble(Next(), arg)),
                    "--segment-seconds" => options.WithSegmentSeconds(ParseDouble(Next(), arg)),
                    "--parallel-sources" => options.WithParallelSources(ParseBool(Next(), arg)),
                    "--writer" => options.WithWriter(ParseWriter(Next(), arg)),
                    "--validate-first-read" => options.WithValidateFirstRead(ParseBool(Next(), arg)),
                    _ => throw new ArgumentException($"Unknown argument: {arg}")
                };
            }

            options.Validate();
            return options;
        }

        private void Validate()
        {
            if (Sources <= 0) throw new ArgumentOutOfRangeException(nameof(Sources));
            if (Channels <= 0) throw new ArgumentOutOfRangeException(nameof(Channels));
            if (SampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRateHz));
            if (Seconds <= 0) throw new ArgumentOutOfRangeException(nameof(Seconds));
            if (SegmentSeconds <= 0 || SegmentSeconds > Seconds) throw new ArgumentOutOfRangeException(nameof(SegmentSeconds));
        }

        private ProbeOptions WithOutput(string value) => new() { OutputDirectory = value, Sources = Sources, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithSources(int value) => new() { OutputDirectory = OutputDirectory, Sources = value, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithChannels(int value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = value, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithSampleRate(double value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = Channels, SampleRateHz = value, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithSeconds(double value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = value, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithSegmentSeconds(double value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = value, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithParallelSources(bool value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = value, Writer = Writer, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithWriter(TdmsSegmentWriterKind value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = value, ValidateFirstRead = ValidateFirstRead };
        private ProbeOptions WithValidateFirstRead(bool value) => new() { OutputDirectory = OutputDirectory, Sources = Sources, Channels = Channels, SampleRateHz = SampleRateHz, Seconds = Seconds, SegmentSeconds = SegmentSeconds, ParallelSources = ParallelSources, Writer = Writer, ValidateFirstRead = value };
    }

    private static int ParseInt(string value, string name)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {name}: {value}");

    private static double ParseDouble(string value, string name)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {name}: {value}");

    private static bool ParseBool(string value, string name)
        => bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ArgumentException($"Invalid bool for {name}: {value}");

    private static TdmsSegmentWriterKind ParseWriter(string value, string name)
        => value.ToLowerInvariant() switch
        {
            "ddc" => TdmsSegmentWriterKind.Ddc,
            "manual" => TdmsSegmentWriterKind.Manual,
            _ => throw new ArgumentException($"Invalid writer for {name}: {value}")
        };

    private enum TdmsSegmentWriterKind
    {
        Ddc,
        Manual
    }
}
