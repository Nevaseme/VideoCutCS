using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

namespace VideoCutCS
{
    public sealed partial class MainWindow : Window
    {
        // --- フィールド: 状態管理 ---
        private string _currentFilePath = "";
        private TimeSpan _startTime = TimeSpan.Zero;
        private TimeSpan _endTime = TimeSpan.Zero;
        private bool _isEndSet = false;

        // --- フィールド: UI制御フラグ ---
        private DispatcherTimer _timer;
        private bool _isEditingTime = false;        // 現在時間の手動入力中か
        private bool _isUpdatingTimeByCode = false; // コードによるテキスト更新中か
        private bool _isEditingStart = false;       // 開始時間の編集中か
        private bool _isEditingEnd = false;         // 終了時間の編集中か
        private bool _isDraggingTimeline = false;   // タイムラインドラッグ中か

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "VideoCutCS - 開発中";

            // タイマー設定 (約30fps)
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += Timer_Tick;
        }

        // ====================================================
        // ファイル読み込み・プレイヤー制御
        // ====================================================

        private void LoadVideoFile(StorageFile file)
        {
            if (file == null) return;
            _currentFilePath = file.Path;

            var source = MediaSource.CreateFromStorageFile(file);
            Player.Source = source;
            GuidePanel.Visibility = Visibility.Collapsed;
            this.Title = $"VideoCutCS - {file.Name}";

            // 状態リセット
            _startTime = TimeSpan.Zero;
            _isEndSet = false;
            UpdateLabels();
            StatusText.Text = "動画を読み込みました。";

            // イベント二重登録防止のための解除と再登録
            if (Player.MediaPlayer != null)
            {
                Player.MediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                Player.MediaPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                Player.MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
                Player.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            }
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                _timer.Start();
                UpdateTimelineLayout();
                UpdateLabels();
            });
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                IconPlayPause.Symbol = (sender.PlaybackState == MediaPlaybackState.Playing)
                    ? Symbol.Pause
                    : Symbol.Play;
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

            // 再生ヘッドの更新 (ドラッグ中は更新しない)
            if (!_isDraggingTimeline) UpdatePlayhead(session.Position);

            // 現在時間テキストの更新 (編集中は更新しない)
            if (!_isEditingTime)
            {
                _isUpdatingTimeByCode = true;
                TimeBox.Text = session.Position.ToString(@"hh\:mm\:ss");
                _isUpdatingTimeByCode = false;
            }
            DurationText.Text = session.NaturalDuration.ToString(@"hh\:mm\:ss");
        }

        // ====================================================
        // タイムライン・ツールチップ制御
        // ====================================================

        private TimeSpan GetTimeFromX(double x)
        {
            if (TimelineArea.ActualWidth <= 0 || Player.MediaPlayer?.PlaybackSession == null) return TimeSpan.Zero;

            double width = TimelineArea.ActualWidth;
            var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration;

            double clampedX = Math.Clamp(x, 0, width);
            double ratio = clampedX / width;

            return TimeSpan.FromMilliseconds(duration.TotalMilliseconds * ratio);
        }

        private double GetXFromTime(TimeSpan time)
        {
            if (TimelineArea.ActualWidth <= 0 || Player.MediaPlayer?.PlaybackSession == null) return 0;

            var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration;
            if (duration.TotalMilliseconds <= 0) return 0;

            double ratio = time.TotalMilliseconds / duration.TotalMilliseconds;
            return TimelineArea.ActualWidth * ratio;
        }

        private void UpdatePlayhead(TimeSpan time)
        {
            double x = GetXFromTime(time);
            PlayheadTransform.X = x - (Playhead.Width / 2);
        }

        private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var point = e.GetCurrentPoint(TimelineArea);

            if (point.Properties.IsLeftButtonPressed)
            {
                TimelineArea.CapturePointer(e.Pointer);
                _isDraggingTimeline = true;

                TimeSpan time = GetTimeFromX(point.Position.X);
                if (Player.MediaPlayer.PlaybackSession.CanSeek)
                    Player.MediaPlayer.PlaybackSession.Position = time;

                UpdatePlayhead(time);
            }
        }

        private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;

            var point = e.GetCurrentPoint(TimelineArea);
            double currentX = point.Position.X;
            TimeSpan time = GetTimeFromX(currentX);

            // 1. 時間テキスト更新
            HoverTooltipText.Text = time.ToString(@"hh\:mm\:ss\.fff");

            // 2. ツールチップの位置計算（中央揃え＆画面外防止）
            double tooltipWidth = HoverTooltip.ActualWidth > 0 ? HoverTooltip.ActualWidth : 80;
            double targetX = currentX - (tooltipWidth / 2);

            if (targetX < 0) targetX = 0;
            else if (targetX + tooltipWidth > TimelineArea.ActualWidth)
                targetX = TimelineArea.ActualWidth - tooltipWidth;

            HoverTooltipTransform.X = targetX;
            HoverTooltip.Visibility = Visibility.Visible;

            // 3. ドラッグ中のシーク処理
            if (_isDraggingTimeline)
            {
                if (Player.MediaPlayer.PlaybackSession.CanSeek)
                    Player.MediaPlayer.PlaybackSession.Position = time;
                UpdatePlayhead(time);
            }
        }

        private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            TimelineArea.ReleasePointerCapture(e.Pointer);
            _isDraggingTimeline = false;
        }

        private void Timeline_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingTimeline) HoverTooltip.Visibility = Visibility.Collapsed;
        }

        private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTimelineLayout();
            if (Player.MediaPlayer?.PlaybackSession != null)
                UpdatePlayhead(Player.MediaPlayer.PlaybackSession.Position);
        }

        private void UpdateTimelineLayout() => UpdateSelectionRect();

        private void UpdateSelectionRect()
        {
            if (Player.MediaPlayer?.PlaybackSession == null || TimelineArea.ActualWidth == 0)
            {
                SelectionRect.Visibility = Visibility.Collapsed;
                return;
            }

            var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
            if (duration <= 0) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            double startSec = _startTime.TotalSeconds;
            double endSec = _isEndSet ? _endTime.TotalSeconds : duration;

            // 全選択状態ならバーを消す（見やすくするため）
            bool isFullRange = (startSec <= 0.1 && endSec >= duration - 0.1);
            if (isFullRange) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            double totalWidth = TimelineArea.ActualWidth;
            double startRatio = startSec / duration;
            double durationRatio = (endSec - startSec) / duration;

            if (durationRatio < 0) { SelectionRect.Visibility = Visibility.Collapsed; return; }

            SelectionRect.Margin = new Thickness(totalWidth * startRatio, 0, 0, 0);
            SelectionRect.Width = totalWidth * durationRatio;
            SelectionRect.Visibility = Visibility.Visible;
        }

        // ====================================================
        // テキストボックス入力制御 (整理・共通化済み)
        // ====================================================

        private bool TryParseUserTime(string input, out TimeSpan result)
        {
            if (string.IsNullOrWhiteSpace(input)) { result = TimeSpan.Zero; return false; }
            // "30:05" のような分:秒入力をサポート
            if (input.Split(':').Length == 2) { if (TimeSpan.TryParse("00:" + input, out result)) return true; }
            // 秒数のみの入力をサポート
            if (double.TryParse(input, out double seconds)) { result = TimeSpan.FromSeconds(seconds); return true; }
            // 標準的な解析
            return TimeSpan.TryParse(input, out result);
        }

        // ★共通イベントハンドラ: フォーカス時に全選択
        private async void Shared_TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender == TimeBox) _isEditingTime = true;
            else if (sender == TextStartTime) _isEditingStart = true;
            else if (sender == TextEndTime) _isEditingEnd = true;

            if (sender is TextBox tb)
            {
                // WinUIのフォーカス挙動のタイミング問題を回避するためのウェイト
                await Task.Delay(20);
                tb.SelectAll();
            }
        }

        private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUpdatingTimeByCode && TimeBox.FocusState != FocusState.Unfocused) _isEditingTime = true;
        }

        private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _isEditingTime = false;
            if (Player.MediaPlayer?.PlaybackSession != null)
            {
                _isUpdatingTimeByCode = true;
                TimeBox.Text = Player.MediaPlayer.PlaybackSession.Position.ToString(@"hh\:mm\:ss");
                _isUpdatingTimeByCode = false;
            }
        }

        private void TimeBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                if (TryParseUserTime(TimeBox.Text, out TimeSpan newTime))
                {
                    if (Player.MediaPlayer?.PlaybackSession != null && Player.MediaPlayer.PlaybackSession.CanSeek)
                        Player.MediaPlayer.PlaybackSession.Position = newTime;

                    MainRoot.Focus(FocusState.Programmatic); // フォーカスを外す
                    _isEditingTime = false;
                }
                else StatusText.Text = "エラー: 時間の形式が正しくありません";
                e.Handled = true;
            }
        }

        private void StartTime_LostFocus(object sender, RoutedEventArgs e) { _isEditingStart = false; UpdateLabels(); }

        private void StartTime_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                if (TryParseUserTime(TextStartTime.Text, out TimeSpan newTime))
                {
                    _startTime = newTime;
                    _isEditingStart = false;
                    UpdateLabels();
                    StatusText.Text = "開始位置を更新しました。";
                    MainRoot.Focus(FocusState.Programmatic);
                }
                else StatusText.Text = "エラー: 開始時間の形式が正しくありません";
                e.Handled = true;
            }
        }

        private void EndTime_LostFocus(object sender, RoutedEventArgs e) { _isEditingEnd = false; UpdateLabels(); }

        private void EndTime_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                if (TryParseUserTime(TextEndTime.Text, out TimeSpan newTime))
                {
                    _endTime = newTime;
                    _isEndSet = true;
                    _isEditingEnd = false;
                    UpdateLabels();
                    StatusText.Text = "終了位置を更新しました。";
                    MainRoot.Focus(FocusState.Programmatic);
                }
                else StatusText.Text = "エラー: 終了時間の形式が正しくありません";
                e.Handled = true;
            }
        }

        // ====================================================
        // ボタン操作・ショートカット・FFmpeg連携
        // ====================================================

        private void SetStartLogic()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            _startTime = Player.MediaPlayer.PlaybackSession.Position;
            UpdateLabels();
        }

        private void SetEndLogic()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            _endTime = Player.MediaPlayer.PlaybackSession.Position;
            _isEndSet = true;
            UpdateLabels();
        }

        private void UpdateLabels()
        {
            // 編集中でない場合のみテキストを更新
            if (!_isEditingStart) TextStartTime.Text = $"{_startTime:hh\\:mm\\:ss\\.fff}";

            if (!_isEditingEnd)
            {
                if (Player.MediaPlayer?.PlaybackSession != null)
                {
                    var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration;
                    var endToShow = _isEndSet ? _endTime : duration;
                    TextEndTime.Text = $"{endToShow:hh\\:mm\\:ss\\.fff}";
                }
                else TextEndTime.Text = "00:00:00.000";
            }

            if (Player.MediaPlayer?.PlaybackSession != null)
            {
                var total = Player.MediaPlayer.PlaybackSession.NaturalDuration;
                var end = _isEndSet ? _endTime : total;
                var diff = end - _startTime;
                if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
                TextDuration.Text = $"(長さ: {diff:hh\\:mm\\:ss\\.fff})";
            }
            UpdateSelectionRect();
        }

        private async void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;

            TimeSpan finalEndTime = _isEndSet ? _endTime : Player.MediaPlayer.PlaybackSession.NaturalDuration;
            if (_startTime >= finalEndTime) { StatusText.Text = "エラー: 開始時間が終了時間より後です。"; return; }

            var savePicker = new FileSavePicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

            savePicker.SuggestedStartLocation = PickerLocationId.Desktop;
            savePicker.FileTypeChoices.Add("MP4 Video", new System.Collections.Generic.List<string>() { ".mp4" });
            savePicker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath) + "_cut";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                StatusText.Text = "カット処理中...";
                BtnCut.IsEnabled = false;

                var engine = new FFmpegEngine();
                string log = await engine.CutVideoAsync(_currentFilePath, file.Path, _startTime, finalEndTime);

                StatusText.Text = "完了しました！";
                BtnCut.IsEnabled = true;
                Debug.WriteLine(log);
            }
        }

        private async void SaveSnapshotLogic()
        {
            if (string.IsNullOrEmpty(_currentFilePath) || Player.MediaPlayer?.PlaybackSession == null) return;

            TimeSpan currentPosition = Player.MediaPlayer.PlaybackSession.Position;

            var savePicker = new FileSavePicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

            savePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            savePicker.FileTypeChoices.Add("PNG Image", new System.Collections.Generic.List<string>() { ".png" });

            string timestamp = currentPosition.ToString(@"hh\-mm\-ss");
            string originalName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
            savePicker.SuggestedFileName = $"{originalName}_{timestamp}";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                StatusText.Text = "画像を保存中...";
                BtnSnapshot.IsEnabled = false;

                var engine = new FFmpegEngine();
                string log = await engine.SaveSnapshotAsync(_currentFilePath, file.Path, currentPosition);

                StatusText.Text = "画像を保存しました！";
                BtnSnapshot.IsEnabled = true;
                Debug.WriteLine(log);
            }
        }

        private async void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hWnd);

            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            // 対応拡張子の追加
            foreach (var ext in new[] { ".mp4", ".mkv", ".mov", ".avi", ".ts" })
                openPicker.FileTypeFilter.Add(ext);

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null) LoadVideoFile(file);
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e) => this.Close();

        // ドラッグ＆ドロップ
        private void MainRoot_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "動画を読み込む";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
            }
            else e.AcceptedOperation = DataPackageOperation.None;
        }

        private async void MainRoot_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file) LoadVideoFile(file);
            }
        }

        // キーボード操作・ショートカット
        private void SeekRelative(TimeSpan amount)
        {
            if (Player.MediaPlayer?.PlaybackSession != null && Player.MediaPlayer.PlaybackSession.CanSeek)
            {
                var current = Player.MediaPlayer.PlaybackSession.Position;
                var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration;
                var newPos = current + amount;

                if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
                if (newPos > duration) newPos = duration;

                Player.MediaPlayer.PlaybackSession.Position = newPos;
                if (!_isDraggingTimeline) UpdatePlayhead(newPos);
            }
        }

        private void Player_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool isCtrlPressed = (ctrlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (e.Key == VirtualKey.Left) { SeekRelative(TimeSpan.FromSeconds(isCtrlPressed ? -1 : -5)); e.Handled = true; }
            else if (e.Key == VirtualKey.Right) { SeekRelative(TimeSpan.FromSeconds(isCtrlPressed ? 1 : 5)); e.Handled = true; }
        }

        private void Player_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool isCtrlPressed = (ctrlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            int baseSeconds = isCtrlPressed ? 1 : 10;

            if (delta < 0) SeekRelative(TimeSpan.FromSeconds(-baseSeconds));
            else if (delta > 0) SeekRelative(TimeSpan.FromSeconds(baseSeconds));
            e.Handled = true;
        }

        // イベントハンドラの紐づけ (XAMLから参照)
        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
        private void BtnSnapshot_Click(object sender, RoutedEventArgs e) => SaveSnapshotLogic();
        private void BtnSetStart_Click(object sender, RoutedEventArgs e) => SetStartLogic();
        private void BtnSetEnd_Click(object sender, RoutedEventArgs e) => SetEndLogic();

        // KeyboardAccelerator
        private void Shortcut_SeekBack5s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(-5)); a.Handled = true; }
        private void Shortcut_SeekForward5s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(5)); a.Handled = true; }
        private void Shortcut_SeekBack1s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(-1)); a.Handled = true; }
        private void Shortcut_SeekForward1s(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SeekRelative(TimeSpan.FromSeconds(1)); a.Handled = true; }
        private void Shortcut_SetStart(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SetStartLogic(); a.Handled = true; }
        private void Shortcut_SetEnd(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SetEndLogic(); a.Handled = true; }
        private void Shortcut_PlayPause(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { TogglePlayPause(); a.Handled = true; }
        private void Shortcut_Snapshot(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { SaveSnapshotLogic(); a.Handled = true; }
    }
}