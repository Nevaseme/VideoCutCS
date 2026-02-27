using System;
using System.Collections.Generic;
using VideoCutCS;

namespace VideoCutCS.Tests;

public class TimeHelperTests
{
    [Theory]
    [InlineData("", false, 0)]
    [InlineData("   ", false, 0)]
    [InlineData("10", true, 10)]
    [InlineData("1.5", true, 1.5)]
    [InlineData("1:30", true, 90)]
    [InlineData("01:30:00", true, 5400)]
    [InlineData("abc", false, 0)]
    public void TryParseUserTime_ReturnsExpected(string input, bool expectedOk, double expectedSeconds)
    {
        bool ok = TimeHelper.TryParseUserTime(input, out TimeSpan result);
        Assert.Equal(expectedOk, ok);
        if (expectedOk) Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Fact]
    public void GetNearestKeyframe_ExactMatch_ReturnsSelf()
    {
        var kfs = new List<TimeSpan> { TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        Assert.Equal(TimeSpan.FromSeconds(5), TimeHelper.GetNearestKeyframe(TimeSpan.FromSeconds(5), kfs));
    }

    [Fact]
    public void GetNearestKeyframe_CloserToPrevious_ReturnsPrevious()
    {
        var kfs = new List<TimeSpan> { TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        // 6s: distance to 5=1, distance to 10=4 → returns 5
        Assert.Equal(TimeSpan.FromSeconds(5), TimeHelper.GetNearestKeyframe(TimeSpan.FromSeconds(6), kfs));
    }

    [Fact]
    public void GetNearestKeyframe_CloserToNext_ReturnsNext()
    {
        var kfs = new List<TimeSpan> { TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        // 9s: distance to 5=4, distance to 10=1 → returns 10
        Assert.Equal(TimeSpan.FromSeconds(10), TimeHelper.GetNearestKeyframe(TimeSpan.FromSeconds(9), kfs));
    }

    [Fact]
    public void GetNearestKeyframe_BeforeFirst_ReturnsFirst()
    {
        var kfs = new List<TimeSpan> { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        Assert.Equal(TimeSpan.FromSeconds(5), TimeHelper.GetNearestKeyframe(TimeSpan.FromSeconds(0), kfs));
    }

    [Fact]
    public void GetNearestKeyframe_AfterLast_ReturnsLast()
    {
        var kfs = new List<TimeSpan> { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        Assert.Equal(TimeSpan.FromSeconds(10), TimeHelper.GetNearestKeyframe(TimeSpan.FromSeconds(20), kfs));
    }

    [Fact]
    public void GetNearestKeyframe_Equidistant_ReturnsPrevious()
    {
        var kfs = new List<TimeSpan> { TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        // 7.5s: distance to 5 = distance to 10 → tie → returns 5 (before)
        Assert.Equal(TimeSpan.FromSeconds(5), TimeHelper.GetNearestKeyframe(TimeSpan.FromSeconds(7.5), kfs));
    }
}
