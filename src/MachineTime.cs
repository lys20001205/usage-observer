using System;
using System.Globalization;
using Microsoft.Win32;

namespace CodexUsageTray
{
    internal static class MachineTime
    {
        static MachineTime()
        {
            // Windows broadcasts this event when the clock or time zone changes.
            // Re-read the machine's zone without changing any system settings.
            SystemEvents.TimeChanged+=delegate{TimeZoneInfo.ClearCachedData();};
        }
        internal static DateTimeOffset InZone(DateTimeOffset value,TimeZoneInfo zone)
        {return TimeZoneInfo.ConvertTime(value,zone);}
        public static DateTimeOffset Local(DateTimeOffset value)
        {return InZone(value,TimeZoneInfo.Local);}
        internal static string ZoneLabel(DateTimeOffset value,TimeZoneInfo zone)
        {return "本机时间 UTC"+InZone(value,zone).ToString("zzz",CultureInfo.InvariantCulture);}
        public static string Label(DateTimeOffset value)
        {return ZoneLabel(value,TimeZoneInfo.Local);}
        public static string Format(DateTimeOffset value,string format)
        {return Local(value).ToString(format,CultureInfo.InvariantCulture);}
        public static string Describe(DateTimeOffset value)
        {return Format(value,"MM-dd HH:mm")+" "+Label(value);}
        public static string DescribeFull(DateTimeOffset value)
        {return Format(value,"MM-dd HH:mm:ss")+" "+Label(value);}
        public static string Timestamp(string value)
        {DateTimeOffset time;return DateTimeOffset.TryParse(value,out time)?DescribeFull(time):value;}
    }
}
