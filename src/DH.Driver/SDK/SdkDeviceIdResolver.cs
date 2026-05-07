using System;

namespace DH.Driver.SDK;

/// <summary>
/// Normalizes SDK device identifiers to a canonical 0-based device id used by
/// channel ids, raw capture manifests, TDMS export, and UI device lists.
/// </summary>
public static class SdkDeviceIdResolver
{
    public static int ResolveDeviceId(
        int groupId = -1,
        int machineId = -1,
        int channelDeviceId = -1,
        int deviceIndex = -1)
    {
        if (groupId >= 0)
        {
            return groupId;
        }

        if (channelDeviceId >= 0)
        {
            // Older UI/device-list code sometimes stored a 1-based fallback
            // value here. If it exactly matches deviceIndex + 1, normalize it.
            if (deviceIndex >= 0 && channelDeviceId == deviceIndex + 1)
            {
                return deviceIndex;
            }

            return channelDeviceId;
        }

        if (machineId >= 0)
        {
            return machineId;
        }

        return Math.Max(0, deviceIndex);
    }

    public static int ResolveDeviceId(SdkDeviceInfo sdkDevice)
    {
        ArgumentNullException.ThrowIfNull(sdkDevice);
        return ResolveDeviceId(
            groupId: -1,
            machineId: sdkDevice.MachineId,
            channelDeviceId: sdkDevice.ChannelDeviceId,
            deviceIndex: sdkDevice.DeviceIndex);
    }
}
