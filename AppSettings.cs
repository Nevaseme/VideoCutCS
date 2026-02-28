using System;

namespace VideoCutCS
{
    public class AppSettings
    {
        // ★修正: Null許容型に変更して警告(CS8618)を回避
        private static AppSettings? _current;
        public static AppSettings Current => _current ??= new AppSettings();

        // --- 表示・ズーム ---
        public double ZoomMin { get; set; } = 1.0;
        public double ZoomMax { get; set; } = 100.0;
        public double ZoomStep { get; set; } = 0.5;
        public double ZoomThresholdMedium { get; set; } = 10.0;
        public double ZoomThresholdHigh { get; set; } = 50.0;

        // --- 操作・シーク ---
        public double SeekStepNormal { get; set; } = 10.0;
        public double SeekStepMedium { get; set; } = 1.0;
        public double SeekStepHigh { get; set; } = 0.1;
        public bool InvertMouseWheelSeek { get; set; } = false;
        public bool PreventAutoScrollOnHover { get; set; } = true;

        // --- カット・解析機能 ---
        public bool LoadKeyframes { get; set; } = true;

        // スマートカットOFF時はキーフレーム吸着で高速カット（デフォルト）
        public bool UseSmartCut { get; set; } = false;

        // --- ハードウェアアクセラレーション ---
        public bool UseHardwareAccel { get; set; } = false;
    }
}