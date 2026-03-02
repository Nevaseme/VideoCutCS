using VideoCutCS;

namespace VideoCutCS.Tests;

public class FFmpegEngineTests
{
    private readonly FFmpegEngine _engine = new FFmpegEngine();

    [Fact]
    public void ParseVideoInfo_ParsesWidthAndHeight()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("width=1920\nheight=1080", info);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
    }

    [Fact]
    public void ParseVideoInfo_ParsesCodecAndBitrate()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("codec_name=h264\nbit_rate=4000000", info);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(4000000L, info.BitRate);
    }

    [Fact]
    public void ParseVideoInfo_ParsesFrameRate_Integer()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("r_frame_rate=30/1", info);
        Assert.Equal(30.0, info.FrameRate);
    }

    [Fact]
    public void ParseVideoInfo_ParsesFrameRate_Fractional()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("r_frame_rate=60000/1001", info);
        Assert.Equal(60000.0 / 1001.0, info.FrameRate, precision: 6);
    }

    [Fact]
    public void ParseVideoInfo_EmptyInput_LeavesDefaults()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("", info);
        Assert.Equal(0, info.Width);
        Assert.Equal(0, info.Height);
        Assert.False(info.IsValid);
    }

    [Fact]
    public void ParseVideoInfo_CompleteInput_IsValid()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo(
            "width=1280\nheight=720\ncodec_name=h264\nbit_rate=2000000\nr_frame_rate=60/1",
            info);
        Assert.True(info.IsValid);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(2000000L, info.BitRate);
        Assert.Equal(60.0, info.FrameRate);
    }

    [Fact]
    public void ParseVideoInfo_InvalidLine_IsIgnored()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("width=abc\nheight=720", info);
        Assert.Equal(720, info.Height);
    }

    [Fact]
    public async Task BatchCutAndMergeAsync_EmptySegments_ReturnsError()
    {
        var result = await _engine.BatchCutAndMergeAsync(
            "input.mp4", "output.mp4", Array.Empty<VideoSegment>());
        Assert.StartsWith("エラー", result);
    }

    // --- 音声情報パースのテスト ---

    [Fact]
    public void ParseAudioInfo_ParsesCodecAndBitrate()
    {
        var info = new VideoInfo();
        _engine.ParseAudioInfo("codec_name=aac\nbit_rate=192000", info);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(192000L, info.AudioBitRate);
    }

    [Fact]
    public void ParseAudioInfo_EmptyInput_LeavesDefaults()
    {
        var info = new VideoInfo();
        _engine.ParseAudioInfo("", info);
        Assert.Equal("", info.AudioCodec);
        Assert.Equal(0L, info.AudioBitRate);
    }

    [Fact]
    public void ParseAudioInfo_DoesNotOverwriteVideoFields()
    {
        var info = new VideoInfo();
        _engine.ParseVideoInfo("codec_name=hevc\nbit_rate=8000000", info);
        _engine.ParseAudioInfo("codec_name=aac\nbit_rate=320000", info);

        Assert.Equal("hevc", info.VideoCodec);
        Assert.Equal(8000000L, info.BitRate);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(320000L, info.AudioBitRate);
    }

    // --- コーデック正規化のテスト ---

    [Theory]
    [InlineData("h264", "h264")]
    [InlineData("avc", "h264")]
    [InlineData("avc1", "h264")]
    [InlineData("hevc", "hevc")]
    [InlineData("h265", "hevc")]
    [InlineData("hev1", "hevc")]
    [InlineData("hvc1", "hevc")]
    [InlineData("av1", "av1")]
    [InlineData("av01", "av1")]
    [InlineData("vp9", "h264")]
    [InlineData("", "h264")]
    [InlineData("HEVC", "hevc")]
    public void NormalizeCodecFamily_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, FFmpegEngine.NormalizeCodecFamily(input));
    }
}
