namespace VideoCutCS
{
    public class VideoInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public string VideoCodec { get; set; } = "";
        public long BitRate { get; set; }

        // 音声ストリーム情報（スマートカット時の音声再エンコードに使用）
        public string AudioCodec { get; set; } = "";
        public long AudioBitRate { get; set; }

        public bool IsValid => Width > 0 && Height > 0;
    }
}