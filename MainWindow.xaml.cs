using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Media.Core;
using System;
using System.Diagnostics;
using Windows.System;
using Windows.UI.Core;
using Windows.Media.Playback;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VideoCutCS
{
    public sealed partial class MainWindow : Window
    {
        #region Fields & Initialization

        private string _currentFilePath = "";
        private List<TimeSpan> _keyframes = new();
        private readonly FFmpegEngine _ffmpegEngine = new();

        private readonly SolidColorBrush _brushNormal  = new(Microsoft.UI.Colors.Gray);
        private readonly SolidColorBrush _brushError   = new(Microsoft.UI.Colors.Red);
        private readonly SolidColorBrush _brushWarning = new(Microsoft.UI.Colors.Orange);
        private readonly SolidColorBrush _brushSuccess = new(Microsoft.UI.Colors.Green);

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
        private bool _isEditingSpeed = false;
        private bool _skipSpeedHandler = false;

        // ラグ低減: ドラッグシークのスロットリング (50ms間隔)
        private DateTime _lastDragSeekTime = DateTime.MinValue;
        private const double DragSeekThrottleMs = 50.0;

        // キーフレーム解析のキャンセル管理
        private CancellationTokenSource? _keyframeCts;

        // バッチカットのキャンセル管理
        private CancellationTokenSource? _batchCts;

        // HWエンコーダーが利用可能かどうか（CheckEnvironment で設定）
        private bool _hwEncoderAvailable = false;

        // セグメントリスト
        private readonly ObservableCollection<VideoSegment> _segments = new();
        private readonly List<Rectangle> _segmentRects = new();
        private VideoSegment? _hoveredSegment = null;

        // 無音防止: 再生再開後に速度を適用するタイマー (80ms)
        private DispatcherTimer _playResumeTimer = null!;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "VideoCutCS";

            SegmentListView.ItemsSource = _segments;
            _segments.CollectionChanged += (_, _) => { UpdateSegmentUI(); UpdateSegmentRects(); };

            // セグメントリストアイテムのホバーイベントを登録
            SegmentListView.ContainerContentChanging += (s, args) =>
            {
                if (args.ItemContainer is ListViewItem item)
                {
                    item.PointerEntered -= OnSegmentItemPointerEntered;
                    item.PointerExited -= OnSegmentItemPointerExited;
                    item.PointerEntered += OnSegmentItemPointerEntered;
                    item.PointerExited += OnSegmentItemPointerExited;
                }
            };

            InitializeSettings();
            CheckEnvironment();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += Timer_Tick;

            _playResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _playResumeTimer.Tick += (s, _) =>
            {
                _playResumeTimer.Stop();
                if (Player.MediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
                    Player.MediaPlayer.PlaybackSession.PlaybackRate = SpeedSlider.Value;
            };

            this.Closed += (_, _) =>
            {
                _timer.Stop();
                _playResumeTimer.Stop();
                _keyframeCts?.Cancel();
                _keyframeCts?.Dispose();
                _batchCts?.Cancel();
                _batchCts?.Dispose();
            };
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
            if (MenuHardwareAccel != null) MenuHardwareAccel.IsChecked = AppSettings.Current.UseHardwareAccel;
        }

        private async void CheckEnvironment()
        {
            string version = await _ffmpegEngine.GetFFmpegVersionAsync();
            if (version.StartsWith("エラー") || version.StartsWith("コマンドエラー"))
            {
                SetStatus("エラー: FFmpegが正しくインストールされていません。", true);
                BtnCut.IsEnabled = false;
                BtnSnapshot.IsEnabled = false;
                return;
            }

            string? hwEncoder = await _ffmpegEngine.DetectHardwareEncoderAsync();
            if (MenuHardwareAccel != null)
            {
                if (hwEncoder != null)
                {
                    _hwEncoderAvailable = true;
                    MenuHardwareAccel.Text = $"ハードウェアエンコード ({hwEncoder})";
                    MenuHardwareAccel.IsEnabled = AppSettings.Current.UseSmartCut;
                }
                else
                {
                    MenuHardwareAccel.Text = "ハードウェアエンコード (非対応)";
                    MenuHardwareAccel.IsEnabled = false;
                    if (AppSettings.Current.UseHardwareAccel)
                    {
                        AppSettings.Current.UseHardwareAccel = false;
                        MenuHardwareAccel.IsChecked = false;
                    }
                }
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
			_segments.Clear();
			BtnPrevKeyframe.IsEnabled = false;
			BtnNextKeyframe.IsEnabled = false;
			_startTime = TimeSpan.Zero;
			_isEndSet = false;
			_skipSpeedHandler = true;
			SpeedSlider.Value = 1.0;
			SpeedBox.Text = "x 1.0";
			_skipSpeedHandler = false;

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

            if (AppSettings.Current.LoadKeyframes)
            {
                _keyframeCts?.Cancel();
                _keyframeCts?.Dispose();
                _keyframeCts = new CancellationTokenSource();
                LoadKeyframesAsync(_currentFilePath, _keyframeCts.Token);
            }
        }

        private async void LoadKeyframesAsync(string path, CancellationToken cancellationToken = default)
        {
            SetStatus("キーフレーム情報を解析中...", isError: false, isWarning: true);

            try
            {
                var frames = await _ffmpegEngine.GetKeyframesAsync(path, cancellationToken);
                _keyframes = frames.Distinct().ToList();

                if (_keyframes.Count > 0)
                {
                    SetStatus($"キーフレーム数: {_keyframes.Count} 個");
                    StatusText.Foreground = _brushSuccess;
                    BtnPrevKeyframe.IsEnabled = true;
                    BtnNextKeyframe.IsEnabled = true;
                }
                else
                {
                    SetStatus("キーフレームが見つかりませんでした (0個)", true);
                }

                await Task.Delay(3000, cancellationToken);
                if (StatusText.Text.StartsWith("キーフレーム数")) SetStatus("準備完了");
            }
            catch (OperationCanceledException)
            {
                // 新しいファイルが読み込まれたためキャンセル。何もしない。
            }
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
                sender.PlaybackSession.PlaybackRate = SpeedSlider.Value;
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
            var session = Player.MediaPlayer.PlaybackSession;
            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                // 無音防止: 停止前にレートを1.0にリセット
                session.PlaybackRate = 1.0;
                Player.MediaPlayer.Pause();
            }
            else
            {
                Player.MediaPlayer.Play();
                // 無音防止: オーディオパイプライン初期化を待ってから速度を適用
                _playResumeTimer.Stop();
                _playResumeTimer.Start();
            }
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
            UpdateSegmentRects();
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

            // 開始 >= 終了の不正状態では非表示にして例外を防ぐ
            if (durationRatio <= 0) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            SelectionRect.Margin = new Thickness(totalWidth * startRatio, 0, 0, 0);
            SelectionRect.Width = totalWidth * durationRatio;
            SelectionRect.Visibility = Visibility.Visible;
        }

        private void UpdateSegmentRects()
        {
            foreach (var r in _segmentRects)
                TimelineArea.Children.Remove(r);
            _segmentRects.Clear();

            if (Player.MediaPlayer?.PlaybackSession == null) return;
            double totalWidth = TimelineArea.Width;
            double duration = Player.MediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
            if (duration <= 0 || totalWidth <= 0) return;

            var activeSeg = _hoveredSegment ?? SegmentListView.SelectedItem as VideoSegment;

            foreach (var seg in _segments)
            {
                double startRatio = seg.Start.TotalSeconds / duration;
                double widthRatio = (seg.End - seg.Start).TotalSeconds / duration;
                if (widthRatio <= 0) continue;

                bool isActive = seg == activeSeg;
                var rect = new Rectangle
                {
                    Width = totalWidth * widthRatio,
                    Height = 6,
                    RadiusX = 3,
                    RadiusY = 3,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(totalWidth * startRatio, 0, 0, 0),
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                    Opacity = isActive ? 0.8 : 0.4,
                    IsHitTestVisible = false
                };
                TimelineArea.Children.Insert(TimelineArea.Children.IndexOf(TimelineBackground) + 1, rect);
                _segmentRects.Add(rect);
            }
        }

        private void OnSegmentItemPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ListViewItem item && item.Content is VideoSegment seg)
            {
                _hoveredSegment = seg;
                UpdateSegmentRects();
            }
        }

        private void OnSegmentItemPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoveredSegment = null;
            UpdateSegmentRects();
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
			var point = e.GetCurrentPoint(TimelineArea);
			if (point.Properties.IsLeftButtonPressed)
			{
				TimelineArea.CapturePointer(e.Pointer);
				_isDraggingTimeline = true;
				var time = GetTimeFromX(point.Position.X);
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
                // スロットリング: 50ms以上経過した場合のみシーク (プレイヘッドは常に更新)
                var now = DateTime.UtcNow;
                if ((now - _lastDragSeekTime).TotalMilliseconds >= DragSeekThrottleMs)
                {
                    if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = time;
                    _lastDragSeekTime = now;
                }
                UpdatePlayhead(time);
            }
        }

        private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            TimelineArea.ReleasePointerCapture(e.Pointer);
            _isDraggingTimeline = false;
            // スロットリングで最終位置が未反映の場合があるため、リリース時に確実にシーク
            if (Player.MediaPlayer?.PlaybackSession?.CanSeek == true)
            {
                var session = Player.MediaPlayer.PlaybackSession;
                var time = GetTimeFromX(e.GetCurrentPoint(TimelineArea).Position.X);
                session.Position = time;
                UpdatePlayhead(time);
                // 一時停止中はTimeBoxを手動更新
                if (!_isEditingTime && session.PlaybackState != MediaPlaybackState.Playing)
                {
                    _isUpdatingTimeByCode = true;
                    TimeBox.Text = time.ToString(@"hh\:mm\:ss");
                    _isUpdatingTimeByCode = false;
                }
            }
        }
        private void Timeline_PointerExited(object sender, PointerRoutedEventArgs e) { if (!_isDraggingTimeline) HoverTooltip.Visibility = Visibility.Collapsed; }

		// マウスホイール処理 (ズーム/シーク)
		private void MainRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleWheel(e);
        private void Timeline_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleWheel(e);
        private void Player_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandleWheel(e);

        private void HandleWheel(PointerRoutedEventArgs e)
        {
            // WinUI 3 では e.Handled = true でも親の XAML ハンドラが発火するため、
            // 子要素 (ListView の ScrollViewer や SegmentPanel_PointerWheelChanged) が
            // 処理済みのイベントに対してシーク/ズームが二重実行されないようガードする
            if (e.Handled) return;

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
			return TimeHelper.GetNearestKeyframe(target, _keyframes);
		}

        private void SetStartLogic()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var t = Player.MediaPlayer.PlaybackSession.Position;
            _startTime = GetSnapTime(t);
            if (Math.Abs((_startTime - t).TotalMilliseconds) > 1) Player.MediaPlayer.PlaybackSession.Position = _startTime;
            // 開始時刻が終了時刻以降になった場合は終了時刻をリセット
            if (_isEndSet && _startTime >= _endTime)
                _isEndSet = false;
            UpdateLabels();
        }

        private void SetEndLogic()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var t = Player.MediaPlayer.PlaybackSession.Position;
            _endTime = GetSnapTime(t);
            if (Math.Abs((_endTime - t).TotalMilliseconds) > 1) Player.MediaPlayer.PlaybackSession.Position = _endTime;
            // 終了時刻が開始時刻以前の場合は拒否
            if (_endTime <= _startTime) { SetStatus("エラー: 終了時間は開始時間より後に設定してください。", true); return; }
            _isEndSet = true;
            UpdateLabels();
        }

        private void BtnSetStart_Click(object sender, RoutedEventArgs e) => SetStartLogic();
        private void BtnSetEnd_Click(object sender, RoutedEventArgs e) => SetEndLogic();

        private async void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;
            if (Player.MediaPlayer?.PlaybackSession == null) return;

            TimeSpan finalEnd = _isEndSet ? _endTime : Player.MediaPlayer.PlaybackSession.NaturalDuration;
            if (_startTime >= finalEnd) { SetStatus("エラー: 開始時間が終了時間より後です。", true); return; }

            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeChoices.Add("MP4 Video", new List<string> { ".mp4" });
            picker.FileTypeChoices.Add("MPEG-TS Video", new List<string> { ".ts" });
            picker.FileTypeChoices.Add("MOV Video", new List<string> { ".mov" });
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
                        result = await _ffmpegEngine.SmartCutVideoAsync(_currentFilePath, file.Path, _startTime, finalEnd, _keyframes);
                    }
                    else
                    {
                        SetStatus("高速カット処理中...");
                        result = await _ffmpegEngine.CutVideoSimpleAsync(_currentFilePath, file.Path, _startTime, finalEnd);
                    }

                    SetStatus("処理が完了しました！");
                    StatusText.Foreground = _brushSuccess;
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
					if (MenuHardwareAccel != null) MenuHardwareAccel.IsEnabled = _hwEncoderAvailable;
					SetStatus("スマートカット: ON");
				}
				else
				{
					if (MenuHardwareAccel != null) MenuHardwareAccel.IsEnabled = false;
					SetStatus("スマートカット: OFF");
				}
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
                    if (MenuHardwareAccel != null) MenuHardwareAccel.IsEnabled = false;
                    SetStatus("キーフレーム吸着: ON");
                }
                else SetStatus("キーフレーム吸着: OFF");
            }
        }

        private void MenuHardwareAccel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item)
            {
                AppSettings.Current.UseHardwareAccel = item.IsChecked;
                SetStatus(item.IsChecked ? "ハードウェアエンコード: ON" : "ハードウェアエンコード: OFF");
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

            var baseName = Path.GetFileNameWithoutExtension(_currentFilePath);
            var timestamp = Player.MediaPlayer.PlaybackSession.Position.ToString(@"hh\-mm\-ss");
            picker.SuggestedFileName = $"{baseName}_{timestamp}";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                SetStatus("画像を保存中...");
                try
                {
                    await _ffmpegEngine.SaveSnapshotAsync(_currentFilePath, file.Path, Player.MediaPlayer.PlaybackSession.Position);
                    SetStatus("画像を保存しました！");
                    StatusText.Foreground = _brushSuccess;
                }
                catch (Exception ex)
                {
                    SetStatus($"画像の保存に失敗しました: {ex.Message}", true);
                }
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
            var session = Player.MediaPlayer.PlaybackSession;
            var cur = session.Position;
            var probe = cur + (next ? TimeSpan.FromMilliseconds(100) : -TimeSpan.FromMilliseconds(100));
            int i = _keyframes.BinarySearch(probe);
            if (i < 0) i = ~i;

            int idx = next ? Math.Min(i, _keyframes.Count - 1)
                           : Math.Max(i - 1, 0);

            var target = _keyframes[idx];
            if ((next && target > cur) || (!next && target < cur))
            {
                session.Position = target;
                UpdatePlayhead(target);
                if (!_isEditingTime && session.PlaybackState != MediaPlaybackState.Playing)
                {
                    _isUpdatingTimeByCode = true;
                    TimeBox.Text = target.ToString(@"hh\:mm\:ss");
                    _isUpdatingTimeByCode = false;
                }
            }
        }

        // セグメント管理
        private void UpdateSegmentUI()
        {
            bool hasSegments = _segments.Count > 0;
            SegmentExpander.Text = $"セグメントリスト ({_segments.Count} 件)";
            BtnRemoveSegment.IsEnabled = hasSegments && SegmentListView.SelectedItem != null;
            BtnBatchCut.IsEnabled = hasSegments && !string.IsNullOrEmpty(_currentFilePath);
        }

        private void SegmentListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnRemoveSegment.IsEnabled = SegmentListView.SelectedItem != null;
            UpdateSegmentRects();
        }

        private void SegmentListView_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Delete) BtnRemoveSegment_Click(sender, e);
        }

        private void BtnAddSegment_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || Player.MediaPlayer?.PlaybackSession == null) return;
            TimeSpan end = _isEndSet ? _endTime : Player.MediaPlayer.PlaybackSession.NaturalDuration;
            if (_startTime >= end) { SetStatus("エラー: 開始が終了より後です。", true); return; }

            _segments.Add(new VideoSegment { Start = _startTime, End = end, Index = _segments.Count + 1 });
            SetStatus($"セグメント追加: {_segments.Count} 件");
            StatusText.Foreground = _brushSuccess;
        }

        private void BtnRemoveSegment_Click(object sender, RoutedEventArgs e)
        {
            if (SegmentListView.SelectedItem is not VideoSegment seg) return;
            int removedIndex = _segments.IndexOf(seg);
            _segments.Remove(seg);
            // 削除後に Index を振り直す
            for (int i = 0; i < _segments.Count; i++) _segments[i].Index = i + 1;
            // 削除後に隣接するセグメントを自動選択
            if (_segments.Count > 0)
                SegmentListView.SelectedIndex = Math.Min(removedIndex, _segments.Count - 1);
        }

        private async void BtnBatchCut_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || _segments.Count == 0) return;

            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeChoices.Add("MP4 Video", new List<string> { ".mp4" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_currentFilePath) + "_batch";

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            if (string.Equals(file.Path, _currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("エラー: 入力ファイルと同じ場所に上書き保存はできません。", true);
                return;
            }

            BtnBatchCut.IsEnabled = false;
            BtnCut.IsEnabled = false;

            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _batchCts = new CancellationTokenSource();

            try
            {
                int segCount = _segments.Count;
                var progress = new Progress<(int current, int total)>(p =>
                    SetStatus($"バッチカット処理中... ({p.current}/{p.total})", false, true));

                string result = await _ffmpegEngine.BatchCutAndMergeAsync(
                    _currentFilePath, file.Path, _segments.ToList(), progress, _batchCts.Token);

                SetStatus($"バッチカット完了！ ({segCount} セグメント)");
                StatusText.Foreground = _brushSuccess;
                Debug.WriteLine(result);
            }
            catch (OperationCanceledException)
            {
                SetStatus("バッチカットがキャンセルされました。");
            }
            catch (Exception ex)
            {
                SetStatus($"エラーが発生しました: {ex.Message}", true);
            }
            finally
            {
                BtnBatchCut.IsEnabled = _segments.Count > 0;
                BtnCut.IsEnabled = true;
            }
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
                // 一時停止中はタイマーが止まるのでTimeBoxを手動更新
                if (!_isEditingTime && s.PlaybackState != MediaPlaybackState.Playing)
                {
                    _isUpdatingTimeByCode = true;
                    TimeBox.Text = s.Position.ToString(@"hh\:mm\:ss");
                    _isUpdatingTimeByCode = false;
                }
            }
        }

        private void SetStatus(string msg, bool isError = false, bool isWarning = false)
        {
            StatusText.Text = msg;
            StatusText.Foreground = isError ? _brushError : isWarning ? _brushWarning : _brushNormal;
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
			else if (object.ReferenceEquals(sender, SpeedBox)) _isEditingSpeed = true;

			if (sender is TextBox tb) tb.SelectAll();
		}

        private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUpdatingTimeByCode && TimeBox.FocusState != FocusState.Unfocused) _isEditingTime = true;
        }

        private void HandleTimeInput(TextBox tb, Action<TimeSpan> onSuccess)
        {
            if (TimeHelper.TryParseUserTime(tb.Text, out TimeSpan t)) { onSuccess(t); MainRoot.Focus(FocusState.Programmatic); UpdateLabels(); }
            else SetStatus("無効な時間形式です", true);
        }
        private void TimeBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) HandleTimeInput(TimeBox, t => { if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = t; _isEditingTime = false; }); }
        private void StartTime_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) HandleTimeInput(TextStartTime, t => { _startTime = t; if (_isEndSet && _startTime >= _endTime) _isEndSet = false; _isEditingStart = false; }); }
        private void EndTime_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) HandleTimeInput(TextEndTime, t => { if (t <= _startTime) { SetStatus("エラー: 終了時間は開始時間より後に設定してください。", true); return; } _endTime = t; _isEndSet = true; _isEditingEnd = false; }); }

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

		// ショートカット処理 (パッケージで一本化)
		private void MainRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (_isEditingTime || _isEditingStart || _isEditingEnd || _isEditingZoom || _isEditingSpeed) return;

			var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var alt  = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);

			switch (e.Key)
			{
				case VirtualKey.Left  when alt: SeekKeyframe(false); e.Handled = true; break;
				case VirtualKey.Right when alt: SeekKeyframe(true);  e.Handled = true; break;
				case VirtualKey.Left:  SeekRelative(TimeSpan.FromSeconds(ctrl ? -1 : -5)); e.Handled = true; break;
				case VirtualKey.Right: SeekRelative(TimeSpan.FromSeconds(ctrl ? 1 : 5));   e.Handled = true; break;
				case VirtualKey.Space: TogglePlayPause();   e.Handled = true; break;
				case VirtualKey.I:     SetStartLogic();     e.Handled = true; break;
				case VirtualKey.F:     SetEndLogic();       e.Handled = true; break;
				case VirtualKey.S:     SaveSnapshotLogic(); e.Handled = true; break;
			}
		}

		// 速度スライダー
		private void SpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
		{
			if (_skipSpeedHandler) return;
			if (!_isEditingSpeed) SpeedBox.Text = $"x {e.NewValue:F1}";
			// 無音防止: 再生中のみ即時適用。一時停止中は再生開始時にタイマーで適用する
			if (Player.MediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
				Player.MediaPlayer.PlaybackSession.PlaybackRate = e.NewValue;
		}

		private void SpeedBox_LostFocus(object sender, RoutedEventArgs e)
		{
			_isEditingSpeed = false;
			SpeedBox.Text = $"x {SpeedSlider.Value:F1}";
		}
		private void SpeedBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) ApplySpeedFromText(); }

		private void ZoomSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
		{
			var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
			bool increase = AppSettings.Current.InvertMouseWheelSeek ? delta < 0 : delta > 0;
			ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + AppSettings.Current.ZoomStep * (increase ? 1 : -1), ZoomSlider.Minimum, ZoomSlider.Maximum);
			e.Handled = true;
		}

		private void SpeedSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
		{
			var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
			bool increase = AppSettings.Current.InvertMouseWheelSeek ? delta < 0 : delta > 0;
			SpeedSlider.Value = Math.Clamp(SpeedSlider.Value + SpeedSlider.StepFrequency * (increase ? 1 : -1), SpeedSlider.Minimum, SpeedSlider.Maximum);
			e.Handled = true;
		}

		private void ApplySpeedFromText()
		{
			string input = SpeedBox.Text.Replace("x", "").Replace(" ", "").Trim();
			if (double.TryParse(input, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
			{
				if (val < SpeedSlider.Minimum || val > SpeedSlider.Maximum)
				{
					SetStatus($"エラー: 速度は {SpeedSlider.Minimum:F1}x ～ {SpeedSlider.Maximum:F1}x の範囲で指定してください", true);
					SpeedBox.Text = $"x {SpeedSlider.Value:F1}";
				}
				else
				{
					SpeedSlider.Value = val;
				}
			}
			else
			{
				SetStatus("エラー: 有効な速度を入力してください", true);
				SpeedBox.Text = $"x {SpeedSlider.Value:F1}";
			}
			_isEditingSpeed = false;
			MainRoot.Focus(FocusState.Programmatic);
		}

        private void SegmentPanelGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // ListView が *行 の境界を超えてボタンを覆わないよう MaxHeight を動的設定
            double headerHeight = SegmentExpander.ActualHeight + SegmentExpander.Margin.Bottom;
            double buttonsHeight = SegmentButtonsGrid.ActualHeight + SegmentButtonsGrid.Margin.Top;
            SegmentListView.MaxHeight = Math.Max(0, e.NewSize.Height - headerHeight - buttonsHeight);
        }

        private void SegmentPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // セグメントパネル上のホイールイベントがMainRootに伝播してシーク操作と誤判定されるのを防ぐ
            e.Handled = true;
        }

        private void MainRoot_DragOver(object sender, DragEventArgs e) { e.AcceptedOperation = DataPackageOperation.Copy; }
        private async void MainRoot_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file) LoadVideoFile(file);
            }
        }

        #endregion
    }
}