using VideoCutCS;

namespace VideoCutCS.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Current_ReturnsSameInstance()
    {
        Assert.Same(AppSettings.Current, AppSettings.Current);
    }

    [Fact]
    public void NewInstance_HasCorrectZoomDefaults()
    {
        var s = new AppSettings();
        Assert.Equal(1.0, s.ZoomMin);
        Assert.Equal(100.0, s.ZoomMax);
        Assert.Equal(0.5, s.ZoomStep);
        Assert.Equal(10.0, s.ZoomThresholdMedium);
        Assert.Equal(50.0, s.ZoomThresholdHigh);
    }

    [Fact]
    public void NewInstance_HasCorrectSeekDefaults()
    {
        var s = new AppSettings();
        Assert.Equal(10.0, s.SeekStepNormal);
        Assert.Equal(1.0, s.SeekStepMedium);
        Assert.Equal(0.1, s.SeekStepHigh);
        Assert.False(s.InvertMouseWheelSeek);
        Assert.True(s.PreventAutoScrollOnHover);
    }

    [Fact]
    public void NewInstance_HasCorrectFeatureDefaults()
    {
        var s = new AppSettings();
        Assert.True(s.LoadKeyframes);
        Assert.False(s.UseSmartCut);
    }

    [Fact]
    public void Settings_CanBeModified()
    {
        var s = new AppSettings();
        s.ZoomMin = 2.0;
        s.UseSmartCut = true;
        Assert.Equal(2.0, s.ZoomMin);
        Assert.True(s.UseSmartCut);
    }
}
