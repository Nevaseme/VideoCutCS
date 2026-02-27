using VideoCutCS;

namespace VideoCutCS.Tests;

public class VideoInfoTests
{
    [Fact]
    public void IsValid_WhenWidthAndHeightPositive_ReturnsTrue()
    {
        var info = new VideoInfo { Width = 1920, Height = 1080 };
        Assert.True(info.IsValid);
    }

    [Fact]
    public void IsValid_WhenWidthZero_ReturnsFalse()
    {
        var info = new VideoInfo { Width = 0, Height = 1080 };
        Assert.False(info.IsValid);
    }

    [Fact]
    public void IsValid_WhenHeightZero_ReturnsFalse()
    {
        var info = new VideoInfo { Width = 1920, Height = 0 };
        Assert.False(info.IsValid);
    }

    [Fact]
    public void IsValid_Default_ReturnsFalse()
    {
        var info = new VideoInfo();
        Assert.False(info.IsValid);
    }

    [Fact]
    public void DefaultVideoCodec_IsEmptyString()
    {
        var info = new VideoInfo();
        Assert.Equal("", info.VideoCodec);
    }
}
