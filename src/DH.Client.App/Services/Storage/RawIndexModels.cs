using System;
using System.Collections.Generic;

namespace DH.Client.App.Services.Storage;

internal sealed class RawIndexChannelFileEntry
{
    public int ChannelId { get; init; }

    public string RelativeFilePath { get; init; } = string.Empty;

    public long EntryCount { get; set; }

    public long StartSampleIndex { get; set; } = -1;

    public long EndSampleIndex { get; set; } = -1;
}

internal sealed class RawIndexManifest
{
    public int Version { get; init; } = 1;

    public string CaptureFilePath { get; init; } = string.Empty;

    public double SampleRateHz { get; init; }

    public DateTime GeneratedAtUtc { get; init; }

    public IReadOnlyList<RawIndexChannelFileEntry> Channels { get; init; } = Array.Empty<RawIndexChannelFileEntry>();
}
