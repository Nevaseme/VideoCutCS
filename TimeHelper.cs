using System;
using System.Collections.Generic;

namespace VideoCutCS
{
    internal static class TimeHelper
    {
        internal static bool TryParseUserTime(string input, out TimeSpan result)
        {
            if (string.IsNullOrWhiteSpace(input)) { result = TimeSpan.Zero; return false; }
            if (!input.Contains(":") && double.TryParse(input, out double s)) { result = TimeSpan.FromSeconds(s); return true; }
            return TimeSpan.TryParse(input.Contains(":") && input.Split(':').Length == 2 ? "00:" + input : input, out result);
        }

        internal static TimeSpan GetNearestKeyframe(TimeSpan target, List<TimeSpan> keyframes)
        {
            int i = keyframes.BinarySearch(target);
            if (i >= 0) return keyframes[i];
            i = ~i;
            if (i <= 0) return keyframes[0];
            if (i >= keyframes.Count) return keyframes[^1];
            var before = keyframes[i - 1];
            var after = keyframes[i];
            return (target - before) <= (after - target) ? before : after;
        }
    }
}
