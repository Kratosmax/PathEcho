using System.Windows;
using System.Windows.Controls;
using PathEcho.Core.Models;
using Forms = System.Windows.Forms;

namespace PathEcho.Controls;

public partial class BackupNotificationEditor : System.Windows.Controls.UserControl
{
    private BackupNotificationOffsets _offsets = new();
    private BackupNotificationPosition _activePosition = BackupNotificationPosition.BottomRight;
    private bool _loading;

    public BackupNotificationEditor()
    {
        InitializeComponent();
        ThemeBox.ItemsSource = new[]
        {
            new Choice<BackupNotificationTheme>("深色", BackupNotificationTheme.Dark),
            new Choice<BackupNotificationTheme>("浅色", BackupNotificationTheme.Light),
        };
        PositionBox.ItemsSource = new[]
        {
            new Choice<BackupNotificationPosition>("右下角", BackupNotificationPosition.BottomRight),
            new Choice<BackupNotificationPosition>("右上角", BackupNotificationPosition.TopRight),
            new Choice<BackupNotificationPosition>("左下角", BackupNotificationPosition.BottomLeft),
            new Choice<BackupNotificationPosition>("左上角", BackupNotificationPosition.TopLeft),
        };
        MonitorBox.ItemsSource = Forms.Screen.AllScreens
            .Select((screen, index) => new Choice<int>(
                $"{index + 1} · {screen.DeviceName}{(screen.Primary ? "（主显示器）" : string.Empty)}",
                index))
            .ToArray();
        LoadSettings(new BackupNotificationSettings());
    }

    public event EventHandler<BackupNotificationSettings>? PreviewRequested;

    public void LoadSettings(BackupNotificationSettings settings)
    {
        _loading = true;
        _offsets = settings.Offsets ?? new BackupNotificationOffsets();
        SelectValue(ThemeBox, settings.Theme);
        SelectValue(PositionBox, settings.Position);
        _activePosition = SelectedValue(PositionBox, BackupNotificationPosition.BottomRight);
        SelectValue(MonitorBox, Math.Clamp(settings.MonitorIndex, 0, Math.Max(0, MonitorBox.Items.Count - 1)));
        LoadActiveOffset();
        ErrorText.Text = string.Empty;
        _loading = false;
    }

    public BackupNotificationSettings GetSettings()
    {
        SaveActiveOffset();
        return new BackupNotificationSettings
        {
            Theme = SelectedValue(ThemeBox, BackupNotificationTheme.Dark),
            MonitorIndex = SelectedValue(MonitorBox, 0),
            Position = SelectedValue(PositionBox, BackupNotificationPosition.BottomRight),
            Offsets = _offsets,
        };
    }

    public string CaptureState() => string.Join('|',
        ThemeBox.SelectedIndex,
        MonitorBox.SelectedIndex,
        PositionBox.SelectedIndex,
        OffsetXBox.Text,
        OffsetYBox.Text,
        SerializeStoredOffsets());

    private void OnPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PositionBox.SelectedItem is not Choice<BackupNotificationPosition> selected)
        {
            return;
        }

        try
        {
            SaveActiveOffset();
            _activePosition = selected.Value;
            LoadActiveOffset();
            ErrorText.Text = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            _loading = true;
            SelectValue(PositionBox, _activePosition);
            _loading = false;
            ErrorText.Text = exception.Message;
        }
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = GetSettings();
            ErrorText.Text = string.Empty;
            PreviewRequested?.Invoke(this, settings);
        }
        catch (InvalidOperationException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnResetCurrent(object sender, RoutedEventArgs e)
    {
        OffsetXBox.Text = "0";
        OffsetYBox.Text = "0";
        SaveActiveOffset();
        ErrorText.Text = string.Empty;
    }

    private void SaveActiveOffset()
    {
        var x = ParseOffset(OffsetXBox.Text, "水平微调");
        var y = ParseOffset(OffsetYBox.Text, "垂直微调");
        _offsets = _offsets.With(_activePosition, new BackupNotificationOffset { X = x, Y = y });
    }

    private void LoadActiveOffset()
    {
        var offset = _offsets.Get(_activePosition);
        OffsetXBox.Text = offset.X.ToString();
        OffsetYBox.Text = offset.Y.ToString();
    }

    private static int ParseOffset(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed is < -5000 or > 5000)
        {
            throw new InvalidOperationException($"{name}必须是 -5000 到 5000 之间的整数。");
        }

        return parsed;
    }

    private string SerializeStoredOffsets() => string.Join(';', Enum.GetValues<BackupNotificationPosition>()
        .Select(position =>
        {
            var offset = _offsets.Get(position);
            return $"{position}:{offset.X},{offset.Y}";
        }));

    private static void SelectValue<T>(System.Windows.Controls.ComboBox comboBox, T value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<Choice<T>>()
            .FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, value))
            ?? comboBox.Items.Cast<Choice<T>>().FirstOrDefault();
    }

    private static T SelectedValue<T>(System.Windows.Controls.ComboBox comboBox, T fallback) =>
        comboBox.SelectedItem is Choice<T> selected ? selected.Value : fallback;

    private sealed record Choice<T>(string Label, T Value);
}
