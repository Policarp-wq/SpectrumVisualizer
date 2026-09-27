using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;

namespace TrackPeek;

public partial class MainWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly Forms.NotifyIcon _tray;
    private readonly VisualizerSettings _settings = VisualizerSettings.Load();
    private SettingsWindow? _settingsWindow;
    private AudioSpectrum? _spectrum;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _timeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly double[] _displayLevels = new double[AudioSpectrum.BandCount];
    private readonly Border[][] _segments = new Border[AudioSpectrum.BandCount][];
    private readonly SolidColorBrush[] _columnBrushes = new SolidColorBrush[AudioSpectrum.BandCount];
    private DateTime _lastSpectrumRestart = DateTime.MinValue;
    private bool _positionReady;
    private double _rainbowPhase;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += Window_SizeChanged;
        CreateSpectrumBackdrop();
        Height = Math.Clamp(_settings.Height, 48, 126);
        ApplySettings();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "TrackPeek",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Показать / скрыть", null, (_, _) => Dispatcher.Invoke(() => Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible));
        _tray.ContextMenuStrip.Items.Add("Настроить эквалайзер…", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _tray.ContextMenuStrip.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(Close));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible);
        _meterTimer.Tick += MeterTimer_Tick;
        _recoveryTimer.Tick += RecoveryTimer_Tick;
        _timeTimer.Tick += (_, _) => UpdateTimeDisplay();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left is double left && _settings.Top is double top)
        {
            Left = left;
            Top = top;
        }
        else PositionNearTaskbar();
        _positionReady = true;
        MaintainTopmost();
        RestartSpectrum();
        _meterTimer.Start();
        _recoveryTimer.Start();
        _timeTimer.Start();
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        await ConnectMediaManagerAsync();
    }

    private async Task ConnectMediaManagerAsync()
    {
        try
        {
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                _manager.SessionsChanged -= Manager_SessionsChanged;
            }
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            _manager.SessionsChanged += Manager_SessionsChanged;
            await SelectSessionAsync();
        }
        catch
        {
            TrackTitle.Text = "TrackPeek";
            TrackArtist.Text = "Медиасеанс недоступен";
        }
    }

    private void PositionNearTaskbar()
    {
        var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        Left = area.Right * scaleX - Width - 16;
        Top = area.Bottom * scaleY - Height - 10;
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => Dispatcher.InvokeAsync(SelectSessionAsync);

    private void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => Dispatcher.InvokeAsync(SelectSessionAsync);

    private async Task SelectSessionAsync()
    {
        if (_manager is null) return;
        var current = _manager.GetCurrentSession();
        var next = current?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? current
            : _manager.GetSessions().FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? current;
        if (!ReferenceEquals(_session, next))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= Session_Changed;
                _session.PlaybackInfoChanged -= Session_Changed;
                _session.TimelinePropertiesChanged -= Session_TimelineChanged;
            }
            _session = next;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += Session_Changed;
                _session.PlaybackInfoChanged += Session_Changed;
                _session.TimelinePropertiesChanged += Session_TimelineChanged;
            }
        }
        await RefreshAsync();
        UpdateTimeDisplay();
    }

    private void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args)
        => Dispatcher.InvokeAsync(RefreshAsync);

    private void Session_TimelineChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => Dispatcher.InvokeAsync(UpdateTimeDisplay);

    private async Task RefreshAsync()
    {
        if (_session is null)
        {
            TrackTitle.Text = "TrackPeek";
            TrackArtist.Text = "Включи музыку в Spotify";
            Artwork.Source = null;
            TimeText.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var media = await _session.TryGetMediaPropertiesAsync();
            TrackTitle.Text = string.IsNullOrWhiteSpace(media.Title) ? "Без названия" : media.Title;
            TrackArtist.Text = string.IsNullOrWhiteSpace(media.Artist) ? _session.SourceAppUserModelId : media.Artist;
            PlayGlyph.Data = Geometry.Parse(_session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? "M3,2 L6,2 L6,14 L3,14 Z M10,2 L13,2 L13,14 L10,14 Z"
                : "M4,2 L13,8 L4,14 Z");
            Artwork.Source = null;
            if (media.Thumbnail is not null)
            {
                using IRandomAccessStreamWithContentType stream = await media.Thumbnail.OpenReadAsync();
                using var memory = new MemoryStream();
                using var input = stream.GetInputStreamAt(0);
                var buffer = new Windows.Storage.Streams.Buffer(8192);
                while (true)
                {
                    var read = await input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
                    if (read.Length == 0) break;
                    var bytes = new byte[read.Length];
                    Windows.Storage.Streams.DataReader.FromBuffer(read).ReadBytes(bytes);
                    await memory.WriteAsync(bytes);
                }
                memory.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = memory;
                image.EndInit();
                image.Freeze();
                Artwork.Source = image;
            }
            ArtworkFallback.Visibility = Artwork.Source is null ? Visibility.Visible : Visibility.Collapsed;
            UpdateTimeDisplay();
        }
        catch
        {
            TrackArtist.Text = "Не удалось прочитать трек";
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null) await _session.TrySkipPreviousAsync();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null) await _session.TryTogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null) await _session.TrySkipNextAsync();
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        var levels = _spectrum?.ReadLevels() ?? new double[AudioSpectrum.BandCount];
        if (_settings.Rainbow)
        {
            _rainbowPhase = (_rainbowPhase + 0.0025) % 1;
            for (var i = 0; i < _columnBrushes.Length; i++)
                _columnBrushes[i].Color = HsvColor((i / (double)_columnBrushes.Length + _rainbowPhase) % 1);
        }
        for (var i = 0; i < _segments.Length; i++)
        {
            _displayLevels[i] = Math.Max(levels[i], _displayLevels[i] * 0.84);
            var lit = (int)Math.Ceiling(_displayLevels[i] * _segments[i].Length);
            for (var segment = 0; segment < _segments[i].Length; segment++)
                _segments[i][segment].Visibility = segment < lit ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void CreateSpectrumBackdrop()
    {
        for (var column = 0; column < _segments.Length; column++)
        {
            _columnBrushes[column] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(94, 225, 171));
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var segments = new Border[8];
            for (var level = segments.Length - 1; level >= 0; level--)
            {
                segments[level] = new Border
                {
                    Width = 8,
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(0, 1.5, 0, 1.5),
                    Background = _columnBrushes[column],
                    Visibility = Visibility.Hidden
                };
                stack.Children.Add(segments[level]);
            }
            _segments[column] = segments;
            SpectrumBackdrop.Children.Add(stack);
        }
        UpdateSpectrumSegmentSizing();
    }

    private void ApplySettings()
    {
        var previousHeight = Height;
        var targetHeight = Math.Clamp(_settings.Height, 48, 126);
        if (IsLoaded && Math.Abs(targetHeight - previousHeight) > 0.1)
            Top -= targetHeight - previousHeight;
        Height = targetHeight;
        SpectrumBackdrop.Opacity = Math.Clamp(_settings.Opacity, 0.05, 1);
        TimeText.Opacity = Math.Clamp(_settings.TimeOpacity * _settings.TextOpacity, 0.05, 1);
        var scale = Math.Clamp(_settings.TextScale, 0.75, 1.5);
        TrackTitle.FontSize = 11 * scale;
        TrackArtist.FontSize = 9 * scale;
        TimeText.FontSize = 9 * scale;
        foreach (var button in new[] { PreviousButton, PlayButton, NextButton })
        {
            button.FontSize = 12 * scale;
            button.Foreground = ParseTextBrush();
            button.Opacity = Math.Clamp(_settings.TextOpacity, 0.25, 1);
        }
        TrackTitle.Foreground = ParseTextBrush();
        TrackArtist.Foreground = ParseTextBrush();
        TimeText.Foreground = ParseTextBrush();
        ArtworkFallback.Foreground = ParseTextBrush();
        TrackTitle.Opacity = Math.Clamp(_settings.TextOpacity, 0.25, 1);
        TrackArtist.Opacity = Math.Clamp(_settings.TextOpacity * 0.78, 0.2, 1);
        if (!_settings.ShowTime) TimeText.Visibility = Visibility.Collapsed;
        else UpdateTimeDisplay();
        if (_spectrum is not null) _spectrum.Sensitivity = Math.Clamp(_settings.Sensitivity, 0.1, 2);
        if (_settings.Rainbow) return;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_settings.Color)!;
            foreach (var brush in _columnBrushes) brush.Color = color;
        }
        catch { /* Keep the last valid color. */ }
    }

    private System.Windows.Media.Brush ParseTextBrush()
    {
        try { return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(_settings.TextColor)!; }
        catch { return System.Windows.Media.Brushes.White; }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateSpectrumSegmentSizing();

    private void UpdateSpectrumSegmentSizing()
    {
        if (ActualHeight <= 0) return;
        var slotHeight = Math.Max(3, (ActualHeight - 22) / 8);
        var segmentHeight = Math.Clamp(slotHeight * 0.44, 1.8, 4.5);
        var verticalMargin = Math.Max(0.5, (slotHeight - segmentHeight) / 2);
        foreach (var column in _segments)
        {
            if (column is null) continue;
            foreach (var segment in column)
                segment.Height = segmentHeight;
            foreach (var child in SpectrumBackdrop.Children)
            {
                if (child is StackPanel stack && stack.Children.Count == column.Length && ReferenceEquals(stack.Children[0], column[^1]))
                {
                    foreach (Border segment in stack.Children)
                        segment.Margin = new Thickness(0, verticalMargin, 0, verticalMargin);
                    break;
                }
            }
        }
    }

    private static System.Windows.Media.Color HsvColor(double hue)
    {
        var h = hue * 6;
        var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x)
        };
        return System.Windows.Media.Color.FromRgb((byte)(r * 195 + 60), (byte)(g * 195 + 60), (byte)(b * 195 + 60));
    }

    private void UpdateTimeDisplay()
    {
        if (!_settings.ShowTime || _session is null) { TimeText.Visibility = Visibility.Collapsed; return; }
        try
        {
            var timeline = _session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            {
                TimeText.Visibility = Visibility.Collapsed;
                return;
            }
            var position = timeline.Position - timeline.StartTime;
            if (_session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                position += DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, duration.Ticks));
            TimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
            TimeText.Visibility = Visibility.Visible;
        }
        catch { TimeText.Visibility = Visibility.Collapsed; }
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void RestartSpectrum()
    {
        _lastSpectrumRestart = DateTime.UtcNow;
        try { _spectrum?.Dispose(); } catch { }
        _spectrum = null;
        try
        {
            _spectrum = new AudioSpectrum { Sensitivity = Math.Clamp(_settings.Sensitivity, 0.1, 2) };
        }
        catch { }
    }

    private void RecoveryTimer_Tick(object? sender, EventArgs e)
    {
        MaintainTopmost();
        try
        {
            var changedDevice = _spectrum is not null && _spectrum.DeviceId != AudioSpectrum.CurrentOutputDeviceId();
            var playing = _session?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var stalled = playing && _spectrum is not null && DateTime.UtcNow - _spectrum.LastAudioUtc > TimeSpan.FromSeconds(12);
            if ((changedDevice || stalled || _spectrum?.IsStopped == true || _spectrum is null)
                && DateTime.UtcNow - _lastSpectrumRestart > TimeSpan.FromSeconds(12)) RestartSpectrum();
        }
        catch { /* Retry on the next timer tick. */ }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(MaintainTopmost), DispatcherPriority.Background);

    private void MaintainTopmost()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || WindowState == WindowState.Minimized) return;
        Topmost = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(ResumeAfterSleepAsync);
    }

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) Dispatcher.InvokeAsync(ResumeAfterSleepAsync);
    }

    private async Task ResumeAfterSleepAsync()
    {
        await Task.Delay(1500);
        if (!IsLoaded) return;
        RestartSpectrum();
        await ConnectMediaManagerAsync();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        try
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings) { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        catch (Exception ex)
        {
            _settingsWindow = null;
            System.Windows.MessageBox.Show(this, $"Не получилось открыть настройки: {ex.Message}", "TrackPeek", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (!_positionReady) return;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        _meterTimer.Stop();
        _recoveryTimer.Stop();
        _timeTimer.Stop();
        _spectrum?.Dispose();
        _settingsWindow?.Close();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
