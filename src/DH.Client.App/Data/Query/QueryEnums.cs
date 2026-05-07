namespace DH.Client.App.Data.Query;

public enum PreviewLevel
{
    L0 = 0,
    L1 = 1,
    L2 = 2,
    L3 = 3,
    L4 = 4
}

public enum BuildState
{
    Ready = 0,
    Building = 1,
    Degraded = 2,
    Missing = 3
}

public enum TimeAxisKind
{
    DeviceTime = 0,
    HostTime = 1,
    SampleIndexMappedTime = 2
}
