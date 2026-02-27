using System;
using VideoCutCS;

namespace VideoCutCS.Tests;

public class VideoSegmentTests
{
    [Fact]
    public void Duration_IsEndMinusStart()
    {
        var seg = new VideoSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(30) };
        Assert.Equal(TimeSpan.FromSeconds(20), seg.Duration);
    }

    [Fact]
    public void Duration_WhenStartEqualsEnd_IsZero()
    {
        var seg = new VideoSegment { Start = TimeSpan.FromSeconds(15), End = TimeSpan.FromSeconds(15) };
        Assert.Equal(TimeSpan.Zero, seg.Duration);
    }

    [Theory]
    [InlineData(1, "00:00:10", "00:01:00")]
    [InlineData(5, "00:10:00", "01:00:00")]
    public void DisplayString_StartsWithIndex(int index, string start, string end)
    {
        var seg = new VideoSegment { Index = index, Start = TimeSpan.Parse(start), End = TimeSpan.Parse(end) };
        Assert.StartsWith($"{index}:", seg.DisplayString.TrimStart());
    }

    [Fact]
    public void DisplayString_ContainsFormattedTimes()
    {
        var seg = new VideoSegment
        {
            Index = 2,
            Start = TimeSpan.FromSeconds(0),
            End = TimeSpan.FromMinutes(1)
        };
        Assert.Contains("00:00:00", seg.DisplayString);
        Assert.Contains("00:01:00", seg.DisplayString);
    }
}
