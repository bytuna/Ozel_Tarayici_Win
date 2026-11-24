using System.Windows;
using System.Windows.Threading;

namespace OzelTarayici;

public partial class ReminderAlertWindow : Window
{
    private readonly Action<int>? _onSnoozeMinutes;
    private readonly Action? _onCompleted;
    private readonly DispatcherTimer _autoCloseTimer = new() { Interval = TimeSpan.FromSeconds(45) };

    public ReminderAlertWindow(string message, DateTime dueTime, Action<int>? onSnoozeMinutes, Action? onCompleted = null)
    {
        InitializeComponent();
        _onSnoozeMinutes = onSnoozeMinutes;
        _onCompleted = onCompleted;

        MessageText.Text = message;
        TimeText.Text = $"Planlanan zaman: {dueTime:dd.MM.yyyy HH:mm}";

        Loaded += (_, _) =>
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 12;
            Top = area.Bottom - ActualHeight - 12;
            _autoCloseTimer.Start();
        };

        MouseEnter += (_, _) => _autoCloseTimer.Stop();
        MouseLeave += (_, _) => _autoCloseTimer.Start();
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Close();
        };
    }

    private void BtnSnooze5_Click(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        _onSnoozeMinutes?.Invoke(5);
        Close();
    }

    private void BtnSnooze15_Click(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        _onSnoozeMinutes?.Invoke(15);
        Close();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        _onCompleted?.Invoke();
        Close();
    }
}
