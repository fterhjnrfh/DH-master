using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace tdms
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                l("Usage: tdms <file.tdms>");
                return;
            }

            read(System.IO.Path.GetFullPath(args[0]));
            l("按任意键退出");
            Console.ReadKey(true);
        }
        //打开文件获取句柄
        [DllImport("nilibddc.dll", EntryPoint = "DDC_OpenFileEx")]
        public static extern int DDC_OpenFileEx(string filePath, string fileType, int readOnly, ref IntPtr flie);
        //关闭文件
        [DllImport("nilibddc.dll", EntryPoint = "DDC_CloseFile")]
        public static extern int DDC_CloseFile(IntPtr flie);
        //获取文件属性值长度
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetFileStringPropertyLength")]
        public static extern int DDC_GetFileStringPropertyLength(IntPtr flie, string typeName, ref int length);
        //获取文件属性值内容
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetFileProperty")]
        public static extern int DDC_GetFileProperty(IntPtr flie, string property, IntPtr pAddr, int length);
        //获取 Group 个数
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetNumChannelGroups")]
        public static extern int DDC_GetNumChannelGroups(IntPtr flie, ref int groupsNum);
        //获取 Group 数组
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetChannelGroups")]
        public static extern int DDC_GetChannelGroups(IntPtr flie, IntPtr pAddr, int groupsNum);
        //获取 group 属性值长度
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetChannelGroupStringPropertyLength")]
        public static extern int DDC_GetChannelGroupStringPropertyLength(IntPtr group, string property, ref int length);
        //获取 group 属性值内容
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetChannelGroupProperty")]
        public static extern int DDC_GetChannelGroupProperty(IntPtr group, string property, IntPtr pAddr, int length);
        //获取 channel 个数
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetNumChannels")]
        public static extern int DDC_GetNumChannels(IntPtr group, ref int channelsNum);
        //获取 channel 数组
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetChannels")]
        public static extern int DDC_GetChannels(IntPtr group, IntPtr pAddr, int channelsNum);
        //获取 channel 属性值长度
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetChannelStringPropertyLength")]
        public static extern int DDC_GetChannelStringPropertyLength(IntPtr channel, string property, ref int length);
        //获取 channel 属性值内容
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetChannelProperty")]
        public static extern int DDC_GetChannelProperty(IntPtr channel, string property, IntPtr pAddr, int length);
        //获取 channelValue 个数
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetNumDataValues")]
        public static extern int DDC_GetNumDataValues(IntPtr channel, ref int valuesNum);
        //获取 channelValue
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetDataValues")]
        public static extern int DDC_GetDataValues(IntPtr channel, int indexOfFirstValueToGet, int numberOfValuesToGet, IntPtr pAddr);
        //获取 channelValue
        [DllImport("nilibddc.dll", EntryPoint = "DDC_GetDataValuesTimestampComponents")]
        public static extern int DDC_GetDataValuesTimestampComponents(IntPtr channel, int indexOfFirstValueToGet, int numberOfValuesToGet, out int year, out int month, out int day, out int hour, out int minute, out int second, out double millisecond, out int weekday);

        public static void read(string path)
        {
            IntPtr file = new IntPtr(0);
            l(DDC_OpenFileEx(path, "TDMS", 1, ref file));//第二个参数根据文件类型选择，TDMS或TDM
            getProperty(file);
            l(DDC_CloseFile(file));
        }

        public static void getProperty(IntPtr file)
        {
            l("============getProperty");
            int length = 0;
            IntPtr property;
            string propertyName;
            string[] proList = "name,title,author".Split(',');
            for (int i = 0; i < proList.Length; i++)
            {
                l(DDC_GetFileStringPropertyLength(file, proList[i], ref length));
                property = Marshal.AllocHGlobal(length + 1);
                l(DDC_GetFileProperty(file, proList[i], property, length + 1));
                propertyName = Marshal.PtrToStringAnsi(property);
                l(propertyName);
                l(proList[i]+":"+propertyName);
            }
            getGroups(file);
        }

        public static void getGroups(IntPtr file)
        {
            l("============getGroups");
            int numGroups = 0;
            l(DDC_GetNumChannelGroups(file, ref numGroups));
            IntPtr groups = Marshal.AllocHGlobal(numGroups * Marshal.SizeOf(typeof(IntPtr)));
            l(DDC_GetChannelGroups(file, groups, numGroups));
            int offSet = Marshal.SizeOf(typeof(IntPtr));
            for (int i = 0; i < numGroups; i++)
            {
                IntPtr group = (IntPtr)Marshal.PtrToStructure(new IntPtr((int)groups + i * offSet), typeof(IntPtr));
                int length = 0;
                l(DDC_GetChannelGroupStringPropertyLength(group, "name", ref length));
                IntPtr property = Marshal.AllocHGlobal(length + 1);
                l(DDC_GetChannelGroupProperty(group, "name", property, length + 1));
                l(Marshal.PtrToStringAnsi(property));
                getChannels(group);
                Marshal.FreeHGlobal(property);
            }
            Marshal.FreeHGlobal(groups);
        }
        public static void getChannels(IntPtr group)
        {
            l("============getChannels");
            int numChannels = 0;
            l(DDC_GetNumChannels(group, ref numChannels));
            IntPtr channels = Marshal.AllocHGlobal(numChannels * Marshal.SizeOf(typeof(IntPtr)));
            l(DDC_GetChannels(group, channels, numChannels));
            int offSet = Marshal.SizeOf(typeof(IntPtr));
            for (int i = 0; i < numChannels; i++)
            {
                IntPtr channel = (IntPtr)Marshal.PtrToStructure(new IntPtr((int)channels + i * offSet), typeof(IntPtr));
                int length = 0;
                l(DDC_GetChannelStringPropertyLength(channel, "name", ref length));
                IntPtr property = Marshal.AllocHGlobal(length + 1);
                l(DDC_GetChannelProperty(channel, "name", property, length + 1));
                string key = Marshal.PtrToStringAnsi(property);
                l(key+":");
                getChannelValue(channel);//double数据格式
                //getChannelValueDate(channel);//若数据为日期格式，使用该函数获取
                Marshal.FreeHGlobal(property);
            }
            Marshal.FreeHGlobal(channels);
        }
        public static void getChannelValueDate(IntPtr channel)//获取date数据
        {
            int numValues = 0;
            int year, month, day, hour, minute, second, weekday;
            double millisecond;
            l(DDC_GetNumDataValues(channel, ref numValues));
            for (int i = 1; i <= numValues; i++)
            {
                l(DDC_GetDataValuesTimestampComponents(channel, i - 1, 1, out year, out month, out day, out hour, out minute, out second, out millisecond, out weekday));
                l(year + "-" + month + "-" + day + " " + hour + ":" + minute + ":" + second + "." + (int)millisecond);
            }
        }
        public static void getChannelValue(IntPtr channel)//获取double数据
        {
            int numValues = 0;
            l(DDC_GetNumDataValues(channel, ref numValues));
            IntPtr datas = Marshal.AllocHGlobal(numValues * Marshal.SizeOf(typeof(double)));
            l(DDC_GetDataValues(channel, 0, numValues, datas));
            int offSet = Marshal.SizeOf(typeof(double));
            for (int i = 0; i < numValues; i++)
            {
                double data = (double)Marshal.PtrToStructure(new IntPtr((int)datas + i * offSet), typeof(double));
                l(data);
            }
        }

        public static void l(object obj)
        {
            if (!obj.ToString().Equals("0"))
                Console.WriteLine(obj);
        }
    }
}
