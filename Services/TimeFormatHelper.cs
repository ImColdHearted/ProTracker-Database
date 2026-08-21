using System;
using System.Collections.Generic;
using System.Text;

namespace Foot_Tracker.Services
{
    public static class TimeFormatHelper
    {
        public static string FormatElapsed(TimeSpan time)
        {
            if (time.TotalDays >= 1)
            {
                return $"{(int)time.TotalDays}:{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
            }

            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
    }
}