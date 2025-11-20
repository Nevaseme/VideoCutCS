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
        private string _currentFilePath = "";
        private TimeSpan _startTime = TimeSpan.Zero;
        private TimeSpan _endTime = TimeSpan.Zero;
        private bool _isEndSet = false;
        private DispatcherTimer _timer;
        private bool _isEditingTime = false;
        private bool _isUpdatingTimeByCode = false;
        private bool _isEditingStart = false;
        private bool _isEditingEnd = false;
        private bool _isDraggingTimeline = false;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "VideoCutCS - 開発中";
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += Timer_Tick;
        }

        private void LoadVideoFile(StorageFile file)
        {
            if (file == null) return;
            _currentFilePath = file.Path;
            var source = MediaSource.CreateFromStorageFile(file);
            Player.Source = source;
            GuidePanel.Visibility = Visibility.Collapsed;
            this.Title = $"VideoCutCS - {file.Name}";
            _startTime = TimeSpan.Zero;
            _isEndSet = false;
            UpdateLabels();
            StatusText.Text = "動画を読み込みました。";

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
                if (sender.PlaybackState == MediaPlaybackState.Playing)
                    IconPlayPause.Symbol = Symbol.Pause;
                else
                    IconPlayPause.Symbol = Symbol.Play;
            });
        }

        private void Timer_Tick(object? sender, object e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var session = Player.MediaPlayer.PlaybackSession;

            if (!_isDraggingTimeline) UpdatePlayhead(session.Position);

            if (!_isEditingTime)
            {
                _isUpdatingTimeByCode = true;
                TimeBox.Text = session.Position.ToString(@"hh\:mm\:ss");
                _isUpdatingTimeByCode = false;
            }
            DurationText.Text = session.NaturalDuration.ToString(@"hh\:mm\:ss");
        }

        private TimeSpan GetTimeFromX(double x)
        {
            if (TimelineArea.ActualWidth <= 0 || Player.MediaPlayer?.PlaybackSession == null) return TimeSpan.Zero;
            double width = TimelineArea.ActualWidth;
            var duration = Player.MediaPlayer.PlaybackSession.NaturalDuration;
            double clampedX = Math.Clamp(x, 0, width);
            double ratio = clampedX / width;
            double timeMs = duration.TotalMilliseconds * ratio;
            return TimeSpan.FromMilliseconds(timeMs);
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
                if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = time;
                UpdatePlayhead(time);
            }
        }

        private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;

            var point = e.GetCurrentPoint(TimelineArea);
            double currentX = point.Position.X;
            TimeSpan time = GetTimeFromX(currentX);

            // 時間テキスト更新
            HoverTooltipText.Text = time.ToString(@"hh\:mm\:ss\.fff");

            // 位置調整（中央揃え）
            // 初回などActualWidthが0の場合は、おおよその幅(80)で計算
            double tooltipWidth = HoverTooltip.ActualWidth;
            if (tooltipWidth <= 0) tooltipWidth = 80;

            double targetX = currentX - (tooltipWidth / 2);

            // 画面端でツールチップが切れないようにガード（オプション）
            if (targetX < 0) targetX = 0;
            else if (targetX + tooltipWidth > TimelineArea.ActualWidth)
                targetX = TimelineArea.ActualWidth - tooltipWidth;

            HoverTooltipTransform.X = targetX;

            HoverTooltip.Visibility = Visibility.Visible;

            if (_isDraggingTimeline)
            {
                if (Player.MediaPlayer.PlaybackSession.CanSeek) Player.MediaPlayer.PlaybackSession.Position = time;
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
            // ドラッグ中以外は隠す
            if (!_isDraggingTimeline)
            {
                HoverTooltip.Visibility = Visibility.Collapsed;
            }
        }

        private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTimelineLayout();
            if (Player.MediaPlayer?.PlaybackSession != null) UpdatePlayhead(Player.MediaPlayer.PlaybackSession.Position);
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

        private bool TryParseUserTime(string input, out TimeSpan result)
        {
            if (string.IsNullOrWhiteSpace(input)) { result = TimeSpan.Zero; return false; }
            if (input.Split(':').Length == 2) { if (TimeSpan.TryParse("00:" + input, out result)) return true; }
            if (double.TryParse(input, out double seconds)) { result = TimeSpan.FromSeconds(seconds); return true; }
            return TimeSpan.TryParse(input, out result);
        }

        private async void TimeBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _isEditingTime = true;
            if (sender is TextBox tb) { await Task.Delay(20); tb.SelectAll(); }
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
                    MainRoot.Focus(FocusState.Programmatic);
                    _isEditingTime = false;
                }
                else StatusText.Text = "エラー: 時間の形式が正しくありません";
                e.Handled = true;
            }
        }

        private async void StartTime_GotFocus(object sender, RoutedEventArgs e)
        {
            _isEditingStart = true;
            if (sender is TextBox tb) { await Task.Delay(20); tb.SelectAll(); }
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

        private async void EndTime_GotFocus(object sender, RoutedEventArgs e)
        {
            _isEditingEnd = true;
            if (sender is TextBox tb) { await Task.Delay(20); tb.SelectAll(); }
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

        private void BtnSetStart_Click(object sender, RoutedEventArgs e) => SetStartLogic();
        private void BtnSetEnd_Click(object sender, RoutedEventArgs e) => SetEndLogic();

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

        private async void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hWnd);

            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            openPicker.FileTypeFilter.Add(".mp4");
            openPicker.FileTypeFilter.Add(".mkv");
            openPicker.FileTypeFilter.Add(".mov");
            openPicker.FileTypeFilter.Add(".avi");
            openPicker.FileTypeFilter.Add(".ts");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null) LoadVideoFile(file);
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e) => this.Close();

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

        private void TogglePlayPause()
        {
            if (Player.MediaPlayer?.PlaybackSession == null) return;
            var state = Player.MediaPlayer.PlaybackSession.PlaybackState;
            if (state == MediaPlaybackState.Playing) Player.MediaPlayer.Pause();
            else Player.MediaPlayer.Play();
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

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
        private void BtnSnapshot_Click(object sender, RoutedEventArgs e) => SaveSnapshotLogic();

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

        private void Shortcut_SeekBack5s(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SeekRelative(TimeSpan.FromSeconds(-5)); args.Handled = true; }
        private void Shortcut_SeekForward5s(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SeekRelative(TimeSpan.FromSeconds(5)); args.Handled = true; }
        private void Shortcut_SeekBack1s(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SeekRelative(TimeSpan.FromSeconds(-1)); args.Handled = true; }
        private void Shortcut_SeekForward1s(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SeekRelative(TimeSpan.FromSeconds(1)); args.Handled = true; }
        private void Shortcut_SetStart(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SetStartLogic(); args.Handled = true; }
        private void Shortcut_SetEnd(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SetEndLogic(); args.Handled = true; }
        private void Shortcut_PlayPause(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { TogglePlayPause(); args.Handled = true; }
        private void Shortcut_Snapshot(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { SaveSnapshotLogic(); args.Handled = true; }
    }
}