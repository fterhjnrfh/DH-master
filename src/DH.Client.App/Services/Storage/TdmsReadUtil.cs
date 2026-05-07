using System;
using System.Collections.Generic;
using System.Linq;

namespace DH.Client.App.Services.Storage
{
    public static class TdmsReadUtil
    {
        public static IReadOnlyDictionary<string, (string Group, long Count, string DataType)> Enumerate(string path)
        {
            try
            {
                using var tdms = new NationalInstruments.Tdms.File(path);
                tdms.Open();
                var list = new Dictionary<string, (string Group, long Count, string DataType)>();
                foreach (var group in tdms)
                {
                    foreach (var channel in group)
                    {
                        list[$"{group.Name}/{channel.Name}"] = (group.Name, channel.DataCount, channel.DataType.ToString());
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, (string, long, string)>
                {
                    {"ERROR", (ex.GetType().Name + ": " + ex.Message, 0, "-")}
                };
            }
        }

        public static double[] ReadChannelDouble(string path, string groupName, string channelName, int maxCount = 0)
        {
            using var tdms = new NationalInstruments.Tdms.File(path);
            tdms.Open();
            var group = tdms.Groups[groupName];
            var channel = group.Channels[channelName];
            var data = channel.GetData<double>();
            if (maxCount > 0) return data.Take(maxCount).ToArray();
            return data.ToArray();
        }
    }
}