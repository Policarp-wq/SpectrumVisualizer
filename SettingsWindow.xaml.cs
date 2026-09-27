using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace TrackPeek;

public partial class SettingsWindow : Window
{
    private readonly VisualizerSettings _settings;
    private readonly Action _apply;
    private bool _ready;

    public SettingsWindow(VisualizerSettings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;
        InitializeComponent();
        RainbowCheck.IsChecked = settings.Rainbow;
        OpacitySlider.Value = Math.Clamp(settings.Opacity * 100, 5, 100);
        SensitivitySlider.Value = Math.Clamp(settings.Sensitivity * 100, 10, 200);
        ShowTimeCheck.IsChecked = settings.ShowTime;
        TimeOpacitySlider.Value = Math.Clamp(settings.TimeOpacity * 100, 20, 100);
        HeightSlider.Value = Math.Clamp(settings.Height, 48, 126);
        TextScaleSlider.Value = Math.Clamp(settings.TextScale * 100, 75, 150);
        TextOpacitySlider.Value = Math.Clamp(settings.TextOpacity * 100, 25, 100);
        AutoStartCheck.IsChecked = StartupManager.IsEnabled;
        _ready = true;
        RefreshControls();
    }

    private void Rainbow_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.Rainbow = RainbowCheck.IsChecked == true;
        Commit();
    }

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.ColorTranslator.FromHtml(_settings.Color)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _settings.Rainbow = false;
        RainbowCheck.IsChecked = false;
        Commit();
    }

    private void ChooseTextColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.ColorTranslator.FromHtml(_settings.TextColor)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.TextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        Commit();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Opacity = OpacitySlider.Value / 100;
        Commit();
    }

    private void Sensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Sensitivity = SensitivitySlider.Value / 100;
        Commit();
    }

    private void ShowTime_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ShowTime = ShowTimeCheck.IsChecked == true;
        Commit();
    }

    private void TimeOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.TimeOpacity = TimeOpacitySlider.Value / 100;
        Commit();
    }

    private void Height_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Height = HeightSlider.Value;
        Commit();
    }

    private void TextScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.TextScale = TextScaleSlider.Value / 100;
        Commit();
    }

    private void TextOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.TextOpacity = TextOpacitySlider.Value / 100;
        Commit();
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enabled = AutoStartCheck.IsChecked == true;
        try
        {
            StartupManager.SetEnabled(enabled);
            _settings.AutoStart = enabled;
            Commit();
        }
        catch (Exception ex)
        {
            AutoStartCheck.IsChecked = StartupManager.IsEnabled;
            System.Windows.MessageBox.Show(this, $"Не удалось изменить автозапуск: {ex.Message}", "TrackPeek", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Commit()
    {
        RefreshControls();
        _settings.Save();
        _apply();
    }

    private void RefreshControls()
    {
        ColorButton.IsEnabled = !(_settings.Rainbow);
        OpacityLabel.Text = $"Заметность: {Math.Round(_settings.Opacity * 100):0}%";
        SensitivityLabel.Text = $"Чувствительность: {Math.Round(_settings.Sensitivity * 100):0}%";
        TimeOpacityLabel.Text = $"Заметность времени: {Math.Round(_settings.TimeOpacity * 100):0}%";
        TimeOpacitySlider.IsEnabled = _settings.ShowTime;
        HeightLabel.Text = $"Высота плашки: {Math.Round(_settings.Height):0} px";
        TextScaleLabel.Text = $"Размер текста: {Math.Round(_settings.TextScale * 100):0}%";
        TextOpacityLabel.Text = $"Заметность текста и кнопок: {Math.Round(_settings.TextOpacity * 100):0}%";
        try { ColorSwatch.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(_settings.Color)!; }
        catch { ColorSwatch.Background = System.Windows.Media.Brushes.MediumAquamarine; }
        try { TextColorSwatch.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(_settings.TextColor)!; }
        catch { TextColorSwatch.Background = System.Windows.Media.Brushes.White; }
    }
}
