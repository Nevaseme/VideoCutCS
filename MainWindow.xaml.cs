using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Media.Core;
using System;
using System.IO;
using System.Diagnostics;
using Windows.System;
using Windows.UI.Core;
using Windows.Media.Playback;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace VideoCutCS
{
    public sealed partial class MainWindow : Window
    {
        #region Fields & Initialization

        private string _currentFilePath = "";
        private List<TimeSpan> _keyframes = new List<TimeSpan>();
        
        // 最適化: エンジンインスタンスを再利用
        private readonly FFmpegEngine _ffmpegEngine = new FFmpegEngine();

        // タイムラインロジック
        private TimeSpan _startTime = TimeSpan.Zero;
        private TimeSpan _endTime = TimeSpan.Zero;
        private bool _isEndSet = false;

        // 最適化: 前回の時間を保持して不要なUI更新を抑制
        private TimeSpan _lastUiUpdatePosition = TimeSpan.MinValue;
        private TimeSpan _lastUiUpdateDuration = TimeSpan.MinValue;

        // UI制御フラグ
        private DispatcherTimer _timer;
        private bool _isEditingTime = false;
        private bool _isUpdatingTimeByCode = false;
        private bool _isEditingStart = false;
        private bool _isEditingEnd = false;
        private bool _isDraggingTimeline = false;
        private bool _skipZoomHandler = false;
        private bool _isEditingZoom = false;
        private bool _isHoveringTimeline = false;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "VideoCutCS";

            InitializeSettings();
            CheckEnvironment();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += Timer_Tick;
        }

        private void InitializeSettings()
        {
            if (ZoomSlider != null)
            {
                ZoomSlider.Minimum = AppSettings.Current.ZoomMin;
                ZoomSlider.Maximum = AppSettings.Current.ZoomMax;
            }

			// 設定の整合性チェック (吸着ONならSmartCutはOFF)
			if (AppSettings.Current.UseSmartCut && AppSettings.Current.SnapToKeyframes)
            {
                AppSettings.Current.UseSmartCut = false;
            }

            if (MenuSmartCut != null) MenuSmartCut.IsChecked = AppSettings.Current.UseSmartCut;
            if (MenuSnapToKeyframe != null) MenuSnapToKeyframe.IsChecked = AppSettings.Current.SnapToKeyframes;
        }

        private async void CheckEnvironment()
        {
            // 最適化: フィールドのインスタンスを使用
            string version = await _ffmpegEngine.GetFFmpegVersionAsync();
            if (version.StartsWith("エラー") || version.StartsWith("コマンドエラー"))
            {
                SetStatus("エラー: FFmpegが正しくインストールされていません。", true);
                BtnCut.IsEnabled = false;
                BtnSnapshot.IsEnabled = false;
            }
        }
        #endregion

        #region File Loading & Player Logic

        private void LoadVideoFile(StorageFile file)
        {
            if (file == null) return;
            
            // 修正: 古いソースを解放してファイルロックを解除
            if (Player.Source is IDisposable oldSource)
            {
                oldSource.Dispose();
            }
            Player.Source = null;

            _currentFilePath = file.Path;

			// 状態リセット
			_keyframes.Clear();
            BtnPrevKeyframe.IsEnabled = false;
            BtnNextKeyframe.IsEnabled = false;
            _startTime = TimeSpan.Zero;
            _isEndSet = false;

            Player.Source = MediaSource.CreateFromStorageFile(file);
            GuidePanel.Visibility = Visibility.Collapsed;
            this.Title = $"VideoCutCS - {file.Name}";

            UpdateLabels();
            SetStatus("読み込み中...");

			// イベントハンドラの多重登録防止
			if (Player.MediaPlayer != null)
            {
                Player.MediaPlayer.MediaOpened -= OnMediaOpened;
                Player.MediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                Player.MediaPlayer.MediaOpened += OnMediaOpened;
                Player.MediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            }

            if (AppSettings.Current.LoadKeyframes) LoadKeyframesAsync(_currentFilePath);
        }

        private async void LoadKeyframesAsync(string path)
        {
            SetStatus("キーフレーム情報を解析中...", isError: false, isWarning: true);

            // 最適化: データの取得・ソート・加工をまとめてバックグラウンドスレッドで実行
            // これにより、キーフレーム数が多い場合のUIフリーズを防ぎます
            var sortedFrames = await Task.Run(async () =>
            {
                var frames = await _ffmpegEngine.GetKeyframesAsync(path);
                return frames.OrderBy(t => t).Distinct().ToList();
            });

            this.DispatcherQueue.TryEnqueue(async () =>
            {
                // 修正: UIスレッドで安全に更新
                _keyframes = sortedFrames;

                if (_keyframes.Count > 0)
                {
                    SetStatus($"キーフレーム数: {_keyframes.Count} 個");
                    StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                    BtnPrevKeyframe.IsEnabled = true;
                    BtnNextKeyframe.IsEnabled = true;
                }
                else
                {
                    SetStatus("キーフレームが見つかりませんでした (0個)", true);
                }

                await Task.Delay(3000);
                if (StatusText.Text.StartsWith("キーフレーム数")) SetStatus("準備完了");
            });
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                _timer.Start();
                _skipZoomHandler = true;
                ZoomSlider.Value = AppSettings.Current.ZoomMin;
                ZoomBox.Text = $"x {AppSettings.Current.ZoomMin:F1}";
                _skipZoomHandler = false;
                UpdateTimelineWidth();
                UpdateLabels();
            });
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                IconPlayPause.Symbol = (sender.PlaybackState == MediaPlaybackState.Playing) ? Symbol.Pause : Symbol.Play;
				// 再生中のみタイマーを動かす
				if (sender.PlaybackState == MediaPlaybackState.Playing) _timer.Start();
                else _timer.Stop();
            });
        }

        private void TogglePlayPause()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var state = Player.MediaPlayer.PlaybackSession.PlaybackState;
            if (state == MediaPlaybackState.Playing) Player.MediaPlayer.Pause();
            else Player.MediaPlayer.Play();
        }

        private void Timer_Tick(object? sender, object e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var session = Player.MediaPlayer.PlaybackSession;
            var currentPos = session.Position;

            if (!_isDraggingTimeline)
            {
                UpdatePlayhead(currentPos);
                if (!AppSettings.Current.PreventAutoScrollOnHover || !_isHoveringTimeline)
                {
                    KeepPlayheadCentered();
                }
            }

            if (!_isEditingTime)
            {
                // 最適化: 秒単位で変化があった場合のみ文字列生成とUI更新を行う
                if ((int)currentPos.TotalSeconds != (int)_lastUiUpdatePosition.TotalSeconds)
                {
                    var posText = currentPos.ToString(@"hh\:mm\:ss");
                    if (TimeBox.Text != posText)
                    {
                        _isUpdatingTimeByCode = true;
                        TimeBox.Text = posText;
                        _isUpdatingTimeByCode = false;
                    }
                    _lastUiUpdatePosition = currentPos;
                }
            }

            // 最適化: Durationの変更チェックも同様に軽量化
            var currentDur = session.NaturalDuration;
            if (currentDur != _lastUiUpdateDuration)
            {
                DurationText.Text = currentDur.ToString(@"hh\:mm\:ss");
                _lastUiUpdateDuration = currentDur;
            }
        }
		#endregion

		#region Timeline & Zoom Logic

		// タイムラインとズームのイベントハンドラ
		private void TimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineWidth();

        private void TimelineScrollViewer_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isHoveringTimeline = true;
        }

        private void TimelineScrollViewer_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isHoveringTimeline = false;
        }

        private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_skipZoomHandler) return;
            if (!_isEditingZoom)
            {
                ZoomBox.Text = $"x {e.NewValue:F1}";
            }
            UpdateTimelineWidth();
        }

        private void UpdateTimelineWidth()
        {
            if (TimelineScrollViewer == null || TimelineArea == null) return;
            double zoom = ZoomSlider.Value;
            double newWidth = TimelineScrollViewer.ActualWidth * zoom;
            TimelineArea.Width = newWidth;
            UpdateSelectionRect();
            if (Player.MediaPlayer?.PlaybackSession != null) UpdatePlayhead(Player.MediaPlayer.PlaybackSession.Position);
        }

        private void UpdateSelectionRect()
        {
            double totalWidth = TimelineArea.Width;
            if (Player.MediaPlayer?.PlaybackSession == null || totalWidth == 0) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
            if (duration <= 0) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            double startSec = _startTime.TotalSeconds;
            double endSec = _isEndSet ? _endTime.TotalSeconds : duration;

            if (startSec <= 0.1 && endSec >= duration - 0.1) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            double startRatio = startSec / duration;
            double durationRatio = (endSec - startSec) / duration;

            SelectionRect.Margin = new Thickness(totalWidth * startRatio, 0, 0, 0);
            SelectionRect.Width = totalWidth * durationRatio;
            SelectionRect.Visibility = Visibility.Visible;
        }

        private void KeepPlayheadCentered()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            double x = GetXFromTime(Player.MediaPlayer.PlaybackSession.Position);
            double target = x - (TimelineScrollViewer.ActualWidth / 2.0);
            if (Math.Abs(TimelineScrollViewer.HorizontalOffset - target) > 8.0)
                TimelineScrollViewer.ChangeView(Math.Max(0, target), null, null);
        }

		// 座標変換系
		private TimeSpan GetTimeFromX(double x)
        {
            if (TimelineArea.ActualWidth <= 0 || Player.MediaPlayer?.PlaybackSession == null) return TimeSpan.Zero;
            double ratio = Math.Clamp(x, 0, TimelineArea.ActualWidth) / TimelineArea.ActualWidth;
            return TimeSpan.FromMilliseconds(Player.MediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds * ratio);
        }

        private double GetXFromTime(TimeSpan time)
        {
            if (TimelineArea.ActualWidth <= 0 || Player.MediaPlayer?.PlaybackSession == null) return 0;
            double ratio = time.TotalMilliseconds / Player.MediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds;
            return TimelineArea.ActualWidth * ratio;
        }

        private void UpdatePlayhead(TimeSpan time)
        {
            PlayheadTransform.X = GetXFromTime(time) - (Playhead.Width / 2);
        }

		// ポインターイベント
		private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            if (e.GetCurrentPoint(TimelineArea).Properties.IsLeftButtonPressed)
            {
                TimelineArea.CapturePointer(e.Pointer);
                _isDraggingTimeline = true;
                var time = GetTimeFromX(e.GetCurrentPoint(TimelineArea).Position.X);
                if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = time;
                UpdatePlayhead(time);
            }
        }

        private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var point = e.GetCurrentPoint(TimelineArea);
            var time = GetTimeFromX(point.Position.X);

			// ツールチップ
			HoverTooltipText.Text = time.ToString(@"hh\:mm\:ss\.fff");
            var screenPoint = TimelineArea.TransformToVisual(TimelineContainer).TransformPoint(point.Position);
            HoverTooltipTransform.X = Math.Clamp(screenPoint.X - (HoverTooltip.ActualWidth / 2), 0, TimelineContainer.ActualWidth - HoverTooltip.ActualWidth);
            HoverTooltip.Visibility = Visibility.Visible;

            if (_isDraggingTimeline)
            {
                if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = time;
                UpdatePlayhead(time);
            }
        }

        private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e) { TimelineArea.ReleasePointerCapture(e.Pointer); _isDraggingTimeline = false; }
        private void Timeline_PointerExited(object sender, PointerRoutedEventArgs e) { if (!_isDraggingTimeline) HoverTooltip.Visibility = Visibility.Collapsed; }

		// マウスホイール処理 (ズーム/シーク)
		private void MainRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleWheel(e);
        private void Timeline_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleWheel(e);
        private void Player_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleWheel(e);

        private void HandleWheel(PointerRoutedEventArgs e)
        {
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;

            if (shift)
            {
				// ズーム
				double step = AppSettings.Current.ZoomStep * (delta > 0 ? 1 : -1);
                ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + step, ZoomSlider.Minimum, ZoomSlider.Maximum);
                e.Handled = true;
            }
            else if (Player.MediaPlayer?.PlaybackSession != null)
            {
				// シーク
				double step = AppSettings.Current.SeekStepNormal;
                if (ZoomSlider.Value >= AppSettings.Current.ZoomThresholdHigh) step = AppSettings.Current.SeekStepHigh;
                else if (ZoomSlider.Value >= AppSettings.Current.ZoomThresholdMedium) step = AppSettings.Current.SeekStepMedium;

                bool forward = AppSettings.Current.InvertMouseWheelSeek ? delta < 0 : delta > 0;
                SeekRelative(TimeSpan.FromSeconds(forward ? step : -step));
                e.Handled = true;
            }
        }
        #endregion

        #region Actions & Operations (Cut, Snapshot, Menus)

        private TimeSpan GetSnapTime(TimeSpan target)
        {
            if (!AppSettings.Current.SnapToKeyframes || _keyframes.Count == 0) return target;
			// 二分探索で最近傍フレームを検索
			int i = _keyframes.BinarySearch(target);
            if (i >= 0) return _keyframes[i];
            i = ~i; // 挿入位置（直後の要素）のインデックス
			if (i <= 0) return _keyframes[0];
            if (i >= _keyframes.Count) return _keyframes[^1];
            var before = _keyframes[i - 1];
            var after = _keyframes[i];
            return (target - before) <= (after - target) ? before : after;
        }

        private void SetStartLogic()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var t = Player.MediaPlayer.PlaybackSession.Position;
            _startTime = GetSnapTime(t);
            if (Math.Abs((_startTime - t).TotalMilliseconds) > 1) Player.MediaPlayer.PlaybackSession.Position = _startTime;
            UpdateLabels();
        }

        private void SetEndLogic()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var t = Player.MediaPlayer.PlaybackSession.Position;
            _endTime = GetSnapTime(t);
            if (Math.Abs((_endTime - t).TotalMilliseconds) > 1) Player.MediaPlayer.PlaybackSession.Position = _endTime;
            _isEndSet = true;
            UpdateLabels();
        }

        private void BtnSetStart_Click(object sender, RoutedEventArgs e) => SetStartLogic();
        private void BtnSetEnd_Click(object sender, RoutedEventArgs e) => SetEndLogic();

        private async void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;

            TimeSpan finalEnd = _isEndSet ? _endTime : Player.MediaPlayer.PlaybackSession.NaturalDuration;
            if (_startTime >= finalEnd) { SetStatus("エラー: 開始時間が終了時間より後です。", true); return; }

            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeChoices.Add("MP4 Video", new List<string> { ".mp4" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_currentFilePath) + (AppSettings.Current.UseSmartCut ? "_smart" : "_cut");

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                // 修正: 入力ファイルと出力ファイルが同じ場合のチェック
                if (string.Equals(file.Path, _currentFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("エラー: 入力ファイルと同じ場所に上書き保存はできません。", true);
                    return;
                }

                BtnCut.IsEnabled = false;
                
                try
                {
                    string result;

                    if (AppSettings.Current.UseSmartCut)
                    {
                        SetStatus("スマートカット処理中... (時間がかかります)", false, true);
                        result = await Task.Run(() => _ffmpegEngine.SmartCutVideoAsync(_currentFilePath, file.Path, _startTime, finalEnd, _keyframes));
                    }
                    else
                    {
                        SetStatus("高速カット処理中...");
                        result = await _ffmpegEngine.CutVideoSimpleAsync(_currentFilePath, file.Path, _startTime, finalEnd);
                    }

                    SetStatus("処理が完了しました！");
                    StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                    Debug.WriteLine(result);
                }
                catch (Exception ex)
                {
                    SetStatus($"エラーが発生しました: {ex.Message}", true);
                }
                finally
                {
                    // 修正: エラー発生時でもボタンを再度有効化する
                    BtnCut.IsEnabled = true;
                }
            }
        }

		// トグル系ロジック
		private void MenuSmartCut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item)
            {
                AppSettings.Current.UseSmartCut = item.IsChecked;
                if (item.IsChecked)
                {
                    AppSettings.Current.SnapToKeyframes = false;
                    if (MenuSnapToKeyframe != null) MenuSnapToKeyframe.IsChecked = false;
                    SetStatus("SmartCut: ON (スナップOFF)");
                }
                else SetStatus("SmartCut: OFF");
            }
        }

        private void MenuSnapToKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item)
            {
                AppSettings.Current.SnapToKeyframes = item.IsChecked;
                if (item.IsChecked)
                {
                    AppSettings.Current.UseSmartCut = false;
                    if (MenuSmartCut != null) MenuSmartCut.IsChecked = false;
                    SetStatus("スナップ: ON (SmartCut OFF)");
                }
                else SetStatus("スナップ: OFF");
            }
        }

		// その他メニュー・ボタン
		private void BtnSnapshot_Click(object sender, RoutedEventArgs e) => SaveSnapshotLogic();
        private async void SaveSnapshotLogic()
        {
            if (string.IsNullOrEmpty(_currentFilePath) || Player.MediaPlayer?.PlaybackSession == null) return;
            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
            picker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_currentFilePath)}_{Player.MediaPlayer.PlaybackSession.Position:hh-mm-ss}";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                SetStatus("画像を保存中...");
                // 最適化: フィールドのインスタンスを使用
                await _ffmpegEngine.SaveSnapshotAsync(_currentFilePath, file.Path, Player.MediaPlayer.PlaybackSession.Position);
                SetStatus("画像を保存しました！");
            }
        }

        private async void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".mp4"); picker.FileTypeFilter.Add(".mov"); picker.FileTypeFilter.Add(".ts");
            var file = await picker.PickSingleFileAsync();
            if (file != null) LoadVideoFile(file);
        }
        private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
        private void BtnPrevKeyframe_Click(object sender, RoutedEventArgs e) => SeekKeyframe(false);
        private void BtnNextKeyframe_Click(object sender, RoutedEventArgs e) => SeekKeyframe(true);

        private void SeekKeyframe(bool next)
        {
            if (Player.MediaPlayer?.PlaybackSession == null || _keyframes.Count == 0) return;
            var cur = Player.MediaPlayer.PlaybackSession.Position;
            var probe = cur + (next ? TimeSpan.FromMilliseconds(100) : -TimeSpan.FromMilliseconds(100));
            int i = _keyframes.BinarySearch(probe);
            if (i < 0) i = ~i;

            int idx = next ? Math.Min(i, _keyframes.Count - 1)
                           : Math.Max(i - 1, 0);

            var target = _keyframes[idx];
            if ((next && target > cur) || (!next && target < cur))
                Player.MediaPlayer.PlaybackSession.Position = target;
        }
        #endregion

        #region Utility & Events (Shortcuts, D&D, TextBoxes)
        private void SeekRelative(TimeSpan amount)
        {
            if (Player.MediaPlayer?.PlaybackSession != null && Player.MediaPlayer.PlaybackSession.CanSeek)
            {
                var s = Player.MediaPlayer.PlaybackSession;
                s.Position = TimeSpan.FromTicks(Math.Clamp((s.Position + amount).Ticks, 0, s.NaturalDuration.Ticks));
                UpdatePlayhead(s.Position);
            }
        }

        private void SetStatus(string msg, bool isError = false, bool isWarning = false)
        {
            StatusText.Text = msg;
            if (isError) StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            else if (isWarning) StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            else StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        }

        private void UpdateLabels()
        {
            if (!_isEditingStart)
            {
                var startText = $"{_startTime:hh\\:mm\\:ss\\.fff}";
                if (TextStartTime.Text != startText) TextStartTime.Text = startText;
            }
            if (!_isEditingEnd && Player.MediaPlayer?.PlaybackSession != null)
            {
                var endText = $"{(_isEndSet ? _endTime : Player.MediaPlayer.PlaybackSession.NaturalDuration):hh\\:mm\\:ss\\.fff}";
                if (TextEndTime.Text != endText) TextEndTime.Text = endText;
            }

            if (Player.MediaPlayer?.PlaybackSession != null)
            {
                var d = (_isEndSet ? _endTime : Player.MediaPlayer.PlaybackSession.NaturalDuration) - _startTime;
                var durationText = $"(長さ: {Math.Max(0, d.TotalSeconds):F3}s)";
                if (TextDuration.Text != durationText) TextDuration.Text = durationText;
            }
            UpdateSelectionRect();
        }

		// テキストボックス関連
		private void Shared_TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (object.ReferenceEquals(sender, TimeBox)) _isEditingTime = true;
            else if (object.ReferenceEquals(sender, TextStartTime)) _isEditingStart = true;
            else if (object.ReferenceEquals(sender, TextEndTime)) _isEditingEnd = true;
            else if (object.ReferenceEquals(sender, ZoomBox)) _isEditingZoom = true;

            if (sender is TextBox tb) tb.SelectAll();
        }

        private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUpdatingTimeByCode && TimeBox.FocusState != FocusState.Unfocused) _isEditingTime = true;
        }

        private void HandleTimeInput(TextBox tb, Action<TimeSpan> onSuccess)
        {
            if (TryParseUserTime(tb.Text, out TimeSpan t)) { onSuccess(t); MainRoot.Focus(FocusState.Programmatic); UpdateLabels(); }
            else SetStatus("無効な時間形式です", true);
        }
        private void TimeBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) HandleTimeInput(TimeBox, t => { if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = t; _isEditingTime = false; }); }
        private void StartTime_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) HandleTimeInput(TextStartTime, t => { _startTime = t; _isEditingStart = false; }); }
        private void EndTime_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) HandleTimeInput(TextEndTime, t => { _endTime = t; _isEndSet = true; _isEditingEnd = false; }); }

        private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => _isEditingTime = false;
        private void StartTime_LostFocus(object sender, RoutedEventArgs e) { _isEditingStart = false; UpdateLabels(); }
        private void EndTime_LostFocus(object sender, RoutedEventArgs e) { _isEditingEnd = false; UpdateLabels(); }

        private void ZoomBox_LostFocus(object sender, RoutedEventArgs e) => _isEditingZoom = false;
        private void ZoomBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) ApplyZoomFromText(); }

        private void ApplyZoomFromText()
        {
            if (ZoomBox == null || ZoomSlider == null) return;
            string input = ZoomBox.Text.Replace("x", "").Replace(" ", "").Trim();

            if (double.TryParse(input, out double result))
            {
                ZoomSlider.Value = Math.Clamp(result, ZoomSlider.Minimum, ZoomSlider.Maximum);
            }
            else
            {
                SetStatus("エラー: 有効な倍率を入力してください", true);
                ZoomBox.Text = $"x {ZoomSlider.Value:F1}";
            }
            _isEditingZoom = false;
            MainRoot.Focus(FocusState.Programmatic);
        }

		// ショートカット処理
		private void Player_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

            if (e.Key == VirtualKey.Left)
            {
                SeekRelative(TimeSpan.FromSeconds(ctrl ? -1 : -5));
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Right)
            {
                SeekRelative(TimeSpan.FromSeconds(ctrl ? 1 : 5));
                e.Handled = true;
            }
        }

        private void Shortcut_PlayPause(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { TogglePlayPause(); a.Handled = true; }
        private void Shortcut_Snapshot(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SaveSnapshotLogic(); a.Handled = true; }
        private void Shortcut_SeekBack5s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(-5)); a.Handled = true; }
        private void Shortcut_SeekForward5s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(5)); a.Handled = true; }
        private void Shortcut_SeekBack1s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(-1)); a.Handled = true; }
        private void Shortcut_SeekForward1s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(1)); a.Handled = true; }
        private void Shortcut_SetStart(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SetStartLogic(); a.Handled = true; }
        private void Shortcut_SetEnd(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SetEndLogic(); a.Handled = true; }
        private void Shortcut_PrevKeyframe(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekKeyframe(false); a.Handled = true; }
        private void Shortcut_NextKeyframe(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekKeyframe(true); a.Handled = true; }

        private void MainRoot_DragOver(object sender, DragEventArgs e) { e.AcceptedOperation = DataPackageOperation.Copy; }
        private async void MainRoot_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file) LoadVideoFile(file);
            }
        }

        private bool TryParseUserTime(string input, out TimeSpan result)
        {
            if (string.IsNullOrWhiteSpace(input)) { result = TimeSpan.Zero; return false; }
            if (!input.Contains(":") && double.TryParse(input, out double s)) { result = TimeSpan.FromSeconds(s); return true; }
            return TimeSpan.TryParse(input.Contains(":") && input.Split(':').Length == 2 ? "00:" + input : input, out result);
        }
        #endregion
    }
}