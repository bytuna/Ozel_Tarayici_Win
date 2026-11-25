using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OzelTarayici;

public partial class ReminderAlertWindow : Window
{
    private readonly Action<int>? _onSnoozeMinutes;
    private readonly Action? _onCompleted;
    private readonly DispatcherTimer _autoCloseTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private bool _isClosing;

    public ReminderAlertWindow(string message, DateTime dueTime, Action<int>? onSnoozeMinutes, Action? onCompleted = null)
    {
        InitializeComponent();
        _onSnoozeMinutes = onSnoozeMinutes;
        _onCompleted = onCompleted;

        MessageText.Text = message;
        TimeText.Text = $"Planlanan zaman: {dueTime:dd.MM.yyyy HH:mm}";

        if (onSnoozeMinutes is null)
        {
            BtnSnooze5.Visibility = Visibility.Collapsed;
            BtnSnooze15.Visibility = Visibility.Collapsed;
            ActionGrid.Columns = 1;
        }

        Loaded += OnWindowLoaded;
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;
        Closed += OnWindowClosed;
        _autoCloseTimer.Tick += OnAutoCloseTick;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12;
        if (!_isClosing) _autoCloseTimer.Start();
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e) => _autoCloseTimer.Stop();

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isClosing) _autoCloseTimer.Start();
    }

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        _autoCloseTimer.Stop();
        _isClosing = true;
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _autoCloseTimer.Stop();
        _autoCloseTimer.Tick -= OnAutoCloseTick;
        Loaded -= OnWindowLoaded;
        MouseEnter -= OnWindowMouseEnter;
        MouseLeave -= OnWindowMouseLeave;
        Closed -= OnWindowClosed;
    }

    private void BtnSnooze5_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        _autoCloseTimer.Stop();
        _onSnoozeMinutes?.Invoke(5);
        Close();
    }

    private void BtnSnooze15_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        _autoCloseTimer.Stop();
        _onSnoozeMinutes?.Invoke(15);
        Close();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        _autoCloseTimer.Stop();
        _onCompleted?.Invoke();
        Close();
    }

    private void BtnDismiss_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        _autoCloseTimer.Stop();
        Close();
    }
}
