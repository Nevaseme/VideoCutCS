using System;

namespace VideoCutCS
{
    /// <summary>
    /// アプリケーション全体の設定値を管理するクラス
    /// 将来的にJSONファイル等への保存・読み込みをここで行います。
    /// </summary>
    public class AppSettings
    {
        // シングルトンパターン（どこからでも AppSettings.Current でアクセス可能にする）
        private static AppSettings _current;
        public static AppSettings Current => _current ??= new AppSettings();

        // --- ズーム関連設定 ---
        public double ZoomMin { get; set; } = 1.0;
        public double ZoomMax { get; set; } = 100.0;

        // ズーム倍率の閾値（これを超えるとシーク幅が変わる）
        public double ZoomThresholdMedium { get; set; } = 10.0;
        public double ZoomThresholdHigh { get; set; } = 50.0;

        // --- シーク（時間移動）設定 ---
        // 通常時の移動秒数
        public double SeekStepNormal { get; set; } = 10.0;
        // 中ズーム時の移動秒数
        public double SeekStepMedium { get; set; } = 1.0;
        // 高ズーム時の移動秒数
        public double SeekStepHigh { get; set; } = 0.1;

        // --- 操作設定 ---
        // マウスホイールの回転方向を反転するか (trueなら 手前に回して進む)
        public bool InvertMouseWheelSeek { get; set; } = false;

        // ズーム時の感度（1回のホイールでどれくらい倍率を変えるか）
        public double ZoomStep { get; set; } = 0.5;
    }
}