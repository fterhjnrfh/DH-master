using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DH.Client.App.Services.Storage;

internal sealed class TdmsSourceStreamFileWriter : IDisposable
{
    private readonly Dictionary<int, IntPtr> _channels = new();
    private readonly int _sourceId;
    private readonly double _sampleRateHz;
    private readonly string _filePath;
    private IntPtr _file = IntPtr.Zero;
    private IntPtr _group = IntPtr.Zero;
    private bool _closed;

    public TdmsSourceStreamFileWriter(string rawFolder, int sourceId, double sampleRateHz, IReadOnlyCollection<int> channelIds)
    {
        if (!TdmsNative.IsAvailable)
        {
            throw new InvalidOperationException("TDMS library is not available. Please place nilibddc.dll in the app directory or PATH.");
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "Sample rate must be positive.");
        }

        _sourceId = sourceId;
        _sampleRateHz = sampleRateHz;
        Directory.CreateDirectory(rawFolder);
        _filePath = Path.Combine(rawFolder, $"source_{sourceId:D4}.tdms");
        TryDelete(_filePath);
        TryDelete(_filePath + "_index");

        string fileName = Path.GetFileNameWithoutExtension(_filePath);
        int err = TdmsNative.DDC_CreateFile(_filePath, "TDMS", SanitizeAscii(fileName), "", SanitizeAscii(fileName), "DH", ref _file);
        ThrowIfError(err, "DDC_CreateFile");

        string groupName = $"source_{sourceId:D4}";
        err = TdmsNative.DDC_AddChannelGroup(_file, groupName, groupName, ref _group);
        ThrowIfError(err, "DDC_AddChannelGroup");

        CreateFileMetadata();
        foreach (int channelId in channelIds.Distinct().OrderBy(static id => id))
        {
            EnsureChannel(channelId);
        }
    }

    public string FilePath => _filePath;

    public int ChannelCount => _channels.Count;

    public TimeSpan Append(int channelId, float[] samples, int sampleCount)
    {
        if (_closed)
        {
            throw new ObjectDisposedException(nameof(TdmsSourceStreamFileWriter));
        }

        if (sampleCount <= 0)
        {
            return TimeSpan.Zero;
        }

        if (samples.Length < sampleCount)
        {
            throw new ArgumentException($"Channel {channelId} buffer has {samples.Length} samples, expected at least {sampleCount}.", nameof(samples));
        }

        IntPtr channel = EnsureChannel(channelId);
        var stopwatch = Stopwatch.StartNew();
        int err = TdmsNative.DDC_AppendDataValuesFloat(channel, samples, (uint)sampleCount);
        stopwatch.Stop();
        ThrowIfError(err, $"DDC_AppendDataValuesFloat channel={channelId}");
        return stopwatch.Elapsed;
    }

    public TdmsSourceStreamCloseResult Close()
    {
        if (_closed)
        {
            return BuildCloseResult(TimeSpan.Zero, TimeSpan.Zero);
        }

        var saveElapsed = TimeSpan.Zero;
        var closeElapsed = TimeSpan.Zero;
        try
        {
            if (_file != IntPtr.Zero)
            {
                var saveStopwatch = Stopwatch.StartNew();
                int err = TdmsNative.DDC_SaveFile(_file);
                saveStopwatch.Stop();
                saveElapsed = saveStopwatch.Elapsed;
                ThrowIfError(err, "DDC_SaveFile");

                var closeStopwatch = Stopwatch.StartNew();
                err = TdmsNative.DDC_CloseFile(_file);
                closeStopwatch.Stop();
                closeElapsed = closeStopwatch.Elapsed;
                ThrowIfError(err, "DDC_CloseFile");
                _file = IntPtr.Zero;
            }
        }
        finally
        {
            _group = IntPtr.Zero;
            _channels.Clear();
            _closed = true;
        }

        return BuildCloseResult(saveElapsed, closeElapsed);
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            Close();
        }
        catch
        {
            CloseFileQuietly();
        }
    }

    private TdmsSourceStreamCloseResult BuildCloseResult(TimeSpan saveElapsed, TimeSpan closeElapsed)
    {
        long fileBytes = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
        return new TdmsSourceStreamCloseResult(_filePath, _sourceId, fileBytes, saveElapsed, closeElapsed);
    }

    private IntPtr EnsureChannel(int channelId)
    {
        if (_channels.TryGetValue(channelId, out IntPtr existing))
        {
            return existing;
        }

        IntPtr channel = IntPtr.Zero;
        string channelName = SanitizeAscii($"AI{channelId:D4}");
        int err = TdmsNative.DDC_AddChannel(_group, TdmsNative.DDCDataType.Float, channelName, $"Channel {channelId}", "V", ref channel);
        ThrowIfError(err, $"DDC_AddChannel channel={channelId}");
        CreateChannelMetadata(channel, channelId);
        _channels[channelId] = channel;
        return channel;
    }

    private void CreateFileMetadata()
    {
        TryCreateFileProperty("dh_storage_format", "tdms-source-stream-v1");
        TryCreateFileProperty("dh_source_id", _sourceId.ToString(CultureInfo.InvariantCulture));
        TryCreateFileProperty("dh_sample_rate_hz", _sampleRateHz.ToString("R", CultureInfo.InvariantCulture));
    }

    private void CreateChannelMetadata(IntPtr channel, int channelId)
    {
        TdmsNative.DDC_CreateChannelPropertyString(channel, "wf_xname", "Time");
        TdmsNative.DDC_CreateChannelPropertyString(channel, "wf_xunit_string", "s");
        TdmsNative.DDC_CreateChannelPropertyDouble(channel, "wf_increment", 1.0 / _sampleRateHz);
        TdmsNative.DDC_CreateChannelPropertyDouble(channel, "wf_start_offset", 0.0);
        TdmsNative.DDC_CreateChannelPropertyString(channel, "dh_channel_id", channelId.ToString(CultureInfo.InvariantCulture));
    }

    private void TryCreateFileProperty(string property, string value)
    {
        try
        {
            TdmsNative.DDC_CreateFilePropertyString(_file, property, value);
        }
        catch
        {
        }
    }

    private void CloseFileQuietly()
    {
        try
        {
            if (_file != IntPtr.Zero)
            {
                TdmsNative.DDC_CloseFile(_file);
            }
        }
        catch
        {
        }
        finally
        {
            _file = IntPtr.Zero;
            _group = IntPtr.Zero;
            _channels.Clear();
            _closed = true;
        }
    }

    private void ThrowIfError(int err, string operation)
    {
        if (err != 0)
        {
            throw new IOException($"{operation} failed for {_filePath}: {err} {TdmsNative.DescribeError(err)}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeAscii(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "session";
        }

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch >= 32 && ch <= 126 && ch != '/' && ch != '\\' && ch != ':' && ch != '*' && ch != '?' && ch != '"' && ch != '<' && ch != '>' && ch != '|')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        string safe = sb.ToString().Trim('_', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
    }
}

internal sealed record TdmsSourceStreamCloseResult(
    string FilePath,
    int SourceId,
    long FileBytes,
    TimeSpan SaveElapsed,
    TimeSpan CloseElapsed);
