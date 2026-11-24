using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using OzelTarayici.Models;
using OzelTarayici.Services;
using WinForms = System.Windows.Forms;

namespace OzelTarayici;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BrowserTabItem> _tabs = new();
    private readonly ObservableCollection<BrowserTabItem> _incognitoTabs = new();
    private readonly ObservableCollection<BookmarkItem> _bookmarks = new();
    private readonly ObservableCollection<ReminderItem> _reminders = new();
    private readonly ObservableCollection<StickyNoteItem> _notes = new();
    private readonly ObservableCollection<SavedPasswordItem> _filteredPasswords = new();
    private readonly ObservableCollection<HistoryEntry> _filteredHistory = new();
    private readonly ObservableCollection<DownloadRecord> _downloads = new();
    private readonly ObservableCollection<SidebarAppItem> _sidebarApps = new();
    private readonly Stack<SavedTabState> _closedTabs = new();
    private readonly ObservableCollection<TabGroup> _groups = new();

    private sealed record CommandPaletteEntry(string Label, Action Execute)
    {
        public override string ToString() => Label;
    }

    private List<CommandPaletteEntry> _commandEntries = new();

    private AppSessionData _sessionData = new();
    private BrowserTabItem? _activeTab;
    private BrowserTabItem? _splitTab;
    private CoreWebView2Environment? _defaultEnv;
    private PrayerTimesInfo? _todayPrayerTimes;
    private List<LocationSearchResult> _locationSearchResults = new();
    private CancellationTokenSource? _suggestCts;
    private Point _noteDragStartPoint;
    private Point _tabDragStartPoint;
    private SavedPasswordItem? _pendingCapturedPassword;
    private SavedPasswordItem? _editingPasswordItem;
    private StickyNoteItem? _editingNoteItem;
    private TabGroup? _editingGroup;
    private Action<string>? _masterPwdCallback;
    private string _lastTranslateSource = string.Empty;
    private string _currentTranslateTargetLang = "tr";
    private byte[]? _lastQrPngBytes;
    private bool _isSidebarWebInitialized;
    private string _currentSidebarAppUrl = string.Empty;
    private string _selectedGroupColorHex = "#00B37E";

    private string? _lastDownloadPath;
    private string _lastNotifiedPrayer = string.Empty;
    private bool _isInitializingSession = true;
    private bool _isGeminiInitialized;
    private bool _suppressUrlTextChanged;
    private bool _suppressHomeTextChanged;
    private bool _forceExit;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WinForms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        GroupsItemsControl.ItemsSource = _groups;
        IncognitoTabsItemsControl.ItemsSource = _incognitoTabs;
        BookmarksItemsControl.ItemsSource = _bookmarks;
        RemindersItemsControl.ItemsSource = _reminders;
        StickyNotesItemsControl.ItemsSource = _notes;
        PasswordsItemsControl.ItemsSource = _filteredPasswords;
        HistoryItemsControl.ItemsSource = _filteredHistory;
        DownloadsItemsControl.ItemsSource = _downloads;
        SidebarAppsItemsControl.ItemsSource = _sidebarApps;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Application.Current.SessionEnding += (_, _) => SaveCurrentSessionState();
        SizeChanged += (_, _) => UpdateDynamicTabWidths();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;

        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();

        RemDatePicker.SelectedDate = DateTime.Today;
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Tarayıcıyı Göster", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Çıkış", null, (_, _) =>
            {
                _forceExit = true;
                Dispatcher.BeginInvoke(Close);
            });

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = iconStream is not null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "Özel Tarayıcı",
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch
        {
            // İkon yüklenemezse sessiz geç
        }
    }

    private void RestoreFromTray()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SessionStorageService.CleanupIncognitoFolder();
        _sessionData = SessionStorageService.Load();
        foreach (var g in _sessionData.Groups) _groups.Add(g);

        SyncCollectionsFromSession();

        ChkAdBlock.IsChecked = _sessionData.IsAdBlockEnabled;
        ChkFingerprint.IsChecked = _sessionData.IsAntiFingerprintEnabled;
        ChkBlockCookies.IsChecked = _sessionData.BlockAllCookies;
        ChkRememberTabs.IsChecked = _sessionData.RememberTabs;
        ChkAutoSleepTabs.IsChecked = _sessionData.AutoSleepInactiveTabs;
        ChkMinimizeToTray.IsChecked = _sessionData.MinimizeToTrayOnClose;
        ChkRunOnStartup.IsChecked = _sessionData.RunOnWindowsStartup;
        ApplyTheme(_sessionData.IsDarkTheme);
        UpdateSidebarPinnedState();
        UpdateClockAndReminders();

        _defaultEnv = await CoreWebView2Environment.CreateAsync(
            null,
            SessionStorageService.GetUserDataFolder(isIncognito: false));

        if (_sessionData.RememberTabs && _sessionData.Tabs.Count > 0)
        {
            foreach (var saved in _sessionData.Tabs)
            {
                var created = await CreateTabAsync(saved.Url, saved.GroupId, isIncognito: false, switchTo: false);
                created.IsPinned = saved.IsPinned;
                if (!string.IsNullOrWhiteSpace(saved.Title))
                {
                    created.Title = saved.Title;
                }
            }
            SortTabsByGroup();
            RefreshTabBarVisibility();
            var idx = Math.Clamp(_sessionData.ActiveTabIndex, 0, _tabs.Count - 1);
            SwitchToTab(_tabs[idx]);
        }
        else
        {
            await CreateTabAsync("homepage", "general", isIncognito: false, switchTo: true);
        }

        _isInitializingSession = false;
        _ = LoadSidebarFaviconsAsync();
        await RefreshWeatherAndPrayerAsync();
    }

    private void SyncCollectionsFromSession()
    {
        _bookmarks.Clear();
        foreach (var b in _sessionData.Bookmarks) _bookmarks.Add(b);

        _reminders.Clear();
        foreach (var r in _sessionData.Reminders) _reminders.Add(r);

        _notes.Clear();
        foreach (var n in _sessionData.StickyNotes) _notes.Add(n);

        RefreshFilteredPasswords();
        RefreshFilteredHistory();
        _downloads.Clear();
        foreach (var dl in _sessionData.Downloads) _downloads.Add(dl);
        _sidebarApps.Clear();
        foreach (var app in _sessionData.SidebarApps) _sidebarApps.Add(app);
        WeatherLocationText.Text = $"📍 {_sessionData.Location.DisplayName}";
    }

    private void RefreshFilteredHistory()
    {
        var query = HistorySearchBox?.Text?.Trim() ?? string.Empty;
        _filteredHistory.Clear();
        foreach (var h in _sessionData.History)
        {
            if (string.IsNullOrEmpty(query) ||
                h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                h.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _filteredHistory.Add(h);
            }
        }
    }

    private void RefreshFilteredPasswords()
    {
        var query = PwdSearchBox?.Text?.Trim() ?? string.Empty;
        _filteredPasswords.Clear();
        foreach (var pwd in _sessionData.SavedPasswords)
        {
            if (string.IsNullOrEmpty(query) ||
                pwd.Site.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                pwd.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _filteredPasswords.Add(pwd);
            }
        }
        if (PwdCountBadgeText is not null)
        {
            PwdCountBadgeText.Text = _sessionData.SavedPasswords.Count.ToString();
        }
    }

    private void BtnTogglePasswordVault_Click(object sender, RoutedEventArgs e)
    {
        var open = PasswordVaultBody.Visibility != Visibility.Visible;
        PasswordVaultBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PwdVaultChevron.Text = open ? "▼" : "▶";
    }

    private void ClockTimer_Tick(object? sender, EventArgs e) => UpdateClockAndReminders();

    private void UpdateClockAndReminders()
    {
        var now = DateTime.Now;
        ClockTimeText.Text = now.ToString("HH:mm:ss");
        ClockDateText.Text = now.ToString("dd MMMM yyyy, dddd", new CultureInfo("tr-TR"));

        UpdatePrayerCountdown(now);
        CheckDueReminders(now);
        if (now.Second == 0) CheckAutoSleepTabs(now);
    }

    private void CheckAutoSleepTabs(DateTime now)
    {
        if (ChkAutoSleepTabs.IsChecked != true) return;
        foreach (var tab in _tabs.ToList())
        {
            if (tab == _activeTab || tab == _splitTab || tab.IsPinned || tab.IsHome || tab.IsSleeping || tab.IsPlayingAudio)
                continue;
            if ((now - tab.LastActiveAt).TotalMinutes >= 15)
            {
                SleepTab(tab);
            }
        }
    }

    private void UpdatePrayerCountdown(DateTime now)
    {
        if (_todayPrayerTimes is null) return;

        var slots = new (string Name, string Time, Border Chip)[]
        {
            ("İmsak", _todayPrayerTimes.Imsak, ChipImsak),
            ("Güneş", _todayPrayerTimes.Gunesh, ChipGunes),
            ("Öğle", _todayPrayerTimes.Ogle, ChipOgle),
            ("İkindi", _todayPrayerTimes.Ikindi, ChipIkindi),
            ("Akşam", _todayPrayerTimes.Aksam, ChipAksam),
            ("Yatsı", _todayPrayerTimes.Yatsi, ChipYatsi)
        };

        var defaultChipBg = (Brush)Resources["SurfaceBgBrush"];
        var defaultChipBorder = (Brush)Resources["BorderBrushSubtle"];
        var defaultText = (Brush)Resources["TextPrimaryBrush"];
        var activeBg = (Brush)Resources["AccentBrush"];

        foreach (var (_, _, chip) in slots)
        {
            chip.Background = defaultChipBg;
            chip.BorderBrush = defaultChipBorder;
            TextBlock.SetForeground(chip, defaultText);
        }

        foreach (var (name, timeStr, chip) in slots)
        {
            if (!TimeSpan.TryParse(timeStr, out var ts)) continue;
            var target = now.Date.Add(ts);
            var diff = target - now;

            if (diff.TotalSeconds >= -5 && diff.TotalSeconds <= 5)
            {
                var key = $"{now:yyyyMMdd}-{name}";
                if (_lastNotifiedPrayer != key)
                {
                    _lastNotifiedPrayer = key;
                    PrayerToastTitle.Text = $"{name} Vakti Girdi";
                    PrayerToastDesc.Text = $"{_sessionData.Location.DisplayName} için vakit ({timeStr})";
                    PrayerToast.Visibility = Visibility.Visible;

                    var prayerAlert = new ReminderAlertWindow(
                        $"🕌 {name} Vakti Girdi — {_sessionData.Location.DisplayName} ({timeStr})",
                        now,
                        null,
                        null);
                    prayerAlert.Show();
                }
            }

            if (diff.TotalSeconds > 0)
            {
                chip.Background = activeBg;
                chip.BorderBrush = activeBg;
                TextBlock.SetForeground(chip, Brushes.White);
                NextPrayerCountdownText.Text = $"{name}: {diff.Hours:D2}:{diff.Minutes:D2}:{diff.Seconds:D2}";
                return;
            }
        }

        NextPrayerCountdownText.Text = "Yarın İmsak";
    }

    private void CheckDueReminders(DateTime now)
    {
        var changed = false;
        foreach (var rem in _reminders.ToList())
        {
            if (!rem.IsTriggered && !rem.IsCompleted && rem.DueTime <= now)
            {
                rem.IsTriggered = true;
                changed = true;

                var alert = new ReminderAlertWindow(
                    rem.Text,
                    rem.DueTime,
                    snoozeMinutes =>
                    {
                        rem.DueTime = DateTime.Now.AddMinutes(snoozeMinutes);
                        rem.IsTriggered = false;
                        RefreshRemindersList();
                        SaveCurrentSessionState();
                    },
                    onCompleted: () =>
                    {
                        rem.IsCompleted = true;
                        SaveCurrentSessionState();
                    });
                alert.Show();
            }
        }
        if (changed)
        {
            RefreshRemindersList();
            SaveCurrentSessionState();
        }
    }

    private void RefreshRemindersList()
    {
        var items = _reminders.ToList();
        _reminders.Clear();
        foreach (var item in items) _reminders.Add(item);
    }

    private async Task RefreshWeatherAndPrayerAsync()
    {
        WeatherLocationText.Text = $"📍 {_sessionData.Location.DisplayName}";

        var weatherTask = DashboardApiService.FetchWeatherAsync(_sessionData.Location.Latitude, _sessionData.Location.Longitude);
        var prayerTask = DashboardApiService.FetchPrayerTimesAsync(_sessionData.Location.Latitude, _sessionData.Location.Longitude);

        await Task.WhenAll(weatherTask, prayerTask);

        var weather = weatherTask.Result;
        if (weather is not null)
        {
            WeatherIconText.Text = weather.Icon;
            WeatherTempText.Text = weather.TemperatureText;
            WeatherDescText.Text = weather.Description;
        }
        else
        {
            WeatherDescText.Text = "Alınamadı";
        }

        _todayPrayerTimes = prayerTask.Result;
        if (_todayPrayerTimes is not null)
        {
            PrayerImsakText.Text = _todayPrayerTimes.Imsak;
            PrayerGunesText.Text = _todayPrayerTimes.Gunesh;
            PrayerOgleText.Text = _todayPrayerTimes.Ogle;
            PrayerIkindiText.Text = _todayPrayerTimes.Ikindi;
            PrayerAksamText.Text = _todayPrayerTimes.Aksam;
            PrayerYatsiText.Text = _todayPrayerTimes.Yatsi;
            UpdatePrayerCountdown(DateTime.Now);
        }
    }

    private async Task<BrowserTabItem> CreateTabAsync(
        string initialUrl = "homepage",
        string groupId = "general",
        bool isIncognito = false,
        bool switchTo = true)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId) ?? _groups.First();
        var tab = new BrowserTabItem
        {
            Url = initialUrl,
            IsHome = string.Equals(initialUrl, "homepage", StringComparison.OrdinalIgnoreCase),
            Title = isIncognito ? "Gizli Sekme" : "Ana Sayfa",
            IsIncognito = isIncognito,
            GroupId = group.Id,
            GroupColorHex = group.ColorHex
        };

        var webView = new WebView2 { Visibility = Visibility.Collapsed };
        tab.WebView = webView;
        Grid.SetColumn(webView, 0);
        WebAreaGrid.Children.Add(webView);
        _tabs.Add(tab);
        if (isIncognito)
        {
            _incognitoTabs.Add(tab);
        }
        else
        {
            group.Tabs.Add(tab);
            SortTabsByGroup();
        }
        RefreshTabBarVisibility();

        if (switchTo) SwitchToTab(tab);

        await InitializeWebViewForTabAsync(tab);

        if (!tab.IsHome && Uri.TryCreate(NormalizeUrl(initialUrl), UriKind.Absolute, out var uri))
        {
            webView.CoreWebView2?.Navigate(uri.ToString());
        }

        if (!_isInitializingSession) SaveCurrentSessionState();
        return tab;
    }

    private async Task InitializeWebViewForTabAsync(BrowserTabItem tab)
    {
        if (tab.WebView is null) return;

        var env = tab.IsIncognito
            ? await CoreWebView2Environment.CreateAsync(null, SessionStorageService.GetUserDataFolder(true))
            : _defaultEnv ?? await CoreWebView2Environment.CreateAsync(null, SessionStorageService.GetUserDataFolder(false));

        if (tab.IsIncognito)
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await tab.WebView.EnsureCoreWebView2Async(env, options);
        }
        else
        {
            await tab.WebView.EnsureCoreWebView2Async(env);
        }

        var core = tab.WebView.CoreWebView2;
        if (core is null) return;

        core.WebMessageReceived += (_, msgArgs) =>
        {
            var msg = msgArgs.TryGetWebMessageAsString();
            if (msg == "__nav_back")
            {
                Dispatcher.BeginInvoke(() => BtnBack_Click(this, new RoutedEventArgs()));
            }
            else if (msg == "__nav_forward")
            {
                Dispatcher.BeginInvoke(() => BtnForward_Click(this, new RoutedEventArgs()));
            }
            else if (msg == "__shortcut_h")
            {
                Dispatcher.BeginInvoke(() => OpenHistoryDownloadsDrawer(showDownloads: false));
            }
            else if (msg == "__shortcut_j")
            {
                Dispatcher.BeginInvoke(() => OpenHistoryDownloadsDrawer(showDownloads: true));
            }
            else if (msg == "__shortcut_k")
            {
                Dispatcher.BeginInvoke(OpenCommandPalette);
            }
            else if (msg == "__shortcut_t")
            {
                Dispatcher.BeginInvoke(async () => await CreateTabAsync("homepage", _activeTab?.GroupId ?? "general", false, true));
            }
            else if (msg == "__shortcut_w")
            {
                Dispatcher.BeginInvoke(() => { if (_activeTab is not null) CloseTab(_activeTab); });
            }
            else if (msg == "__shortcut_l")
            {
                Dispatcher.BeginInvoke(() => { UrlTextBox.Focus(); UrlTextBox.SelectAll(); });
            }
            else if (msg == "__shortcut_p")
            {
                Dispatcher.BeginInvoke(PrintActiveTab);
            }
            else if (!tab.IsIncognito && msg.StartsWith("{", StringComparison.Ordinal))
            {
                Dispatcher.BeginInvoke(() => HandleWebJsonMessage(msg));
            }
        };

        await WebViewPrivacyService.ConfigureCoreWebViewAsync(
            core,
            () => ChkAdBlock.IsChecked == true,
            () => ChkBlockCookies.IsChecked == true,
            () => ChkFingerprint.IsChecked == true);

        core.DocumentTitleChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!tab.IsHome && !string.IsNullOrWhiteSpace(core.DocumentTitle))
                {
                    tab.Title = core.DocumentTitle;
                    if (!_isInitializingSession) SaveCurrentSessionState();
                }
            });
        };

        core.NavigationStarting += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                tab.IsLoading = true;
                if (tab == _activeTab) LoadingBadge.Visibility = Visibility.Visible;
            });
        };

        core.NavigationCompleted += async (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                tab.IsLoading = false;
                tab.CanGoBack = core.CanGoBack;
                tab.CanGoForward = core.CanGoForward;
                if (!tab.IsHome)
                {
                    tab.Url = core.Source;
                    if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
                    {
                        tab.Title = core.DocumentTitle;
                    }
                    if (!tab.IsIncognito && !string.IsNullOrWhiteSpace(tab.Url) && tab.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        RecordHistory(tab.Title, tab.Url);
                    }
                }
                if (tab == _activeTab)
                {
                    LoadingBadge.Visibility = Visibility.Collapsed;
                    UpdateAddressAndNavState();
                }
                if (!_isInitializingSession) SaveCurrentSessionState();
            });

            await TryAutofillMatchingPasswordAsync(core);
        };

        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream is null || stream.Length == 0) return;

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                Dispatcher.Invoke(() =>
                {
                    if (tab.IsHome)
                    {
                        tab.FaviconSource = null;
                        return;
                    }
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    tab.FaviconSource = bmp;
                });
            }
            catch { }
        };

        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            Dispatcher.Invoke(() => tab.IsPlayingAudio = core.IsDocumentPlayingAudio);
        };

        core.SourceChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!tab.IsHome)
                {
                    tab.Url = core.Source;
                    if (tab == _activeTab) SetAddressBoxText(tab.Url);
                }
            });
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            var targetUrl = args.Uri;
            Dispatcher.Invoke(async () =>
            {
                if (!string.IsNullOrWhiteSpace(targetUrl))
                {
                    await CreateTabAsync(targetUrl, tab.GroupId, tab.IsIncognito, switchTo: true);
                }
            });
        };

        core.IsMutedChanged += (_, _) => Dispatcher.Invoke(() => tab.IsMuted = core.IsMuted);

        core.ContextMenuRequested += (_, args) => AttachRichContextMenu(core, args);

        core.DownloadStarting += (_, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                var op = args.DownloadOperation;
                _lastDownloadPath = args.ResultFilePath;

                DownloadFileNameText.Text = Path.GetFileName(args.ResultFilePath);
                DownloadProgressBar.Value = 0;
                DownloadPercentText.Text = "%0";
                BtnOpenDownloadFolder.Visibility = Visibility.Collapsed;
                DownloadPill.Visibility = Visibility.Visible;

                op.BytesReceivedChanged += (_, _) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var total = (long)(op.TotalBytesToReceive ?? 0);
                        var received = op.BytesReceived;
                        var percent = total > 0 ? (int)Math.Round((double)received * 100 / total) : 0;
                        DownloadProgressBar.Value = percent;
                        DownloadPercentText.Text = $"%{percent}";
                    });
                };

                op.StateChanged += (_, _) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (op.State == CoreWebView2DownloadState.Completed)
                        {
                            DownloadProgressBar.Value = 100;
                            DownloadPercentText.Text = "Tamam";
                            BtnOpenDownloadFolder.Visibility = Visibility.Visible;

                            var rec = new DownloadRecord
                            {
                                FileName = Path.GetFileName(args.ResultFilePath),
                                SavePath = args.ResultFilePath,
                                TotalBytes = (long)(op.TotalBytesToReceive ?? 0),
                                DownloadedAt = DateTime.Now,
                                State = "Tamamlandı"
                            };
                            _sessionData.Downloads.Insert(0, rec);
                            if (_sessionData.Downloads.Count > 200) _sessionData.Downloads.RemoveAt(_sessionData.Downloads.Count - 1);
                            _downloads.Insert(0, rec);
                            SaveCurrentSessionState();
                        }
                    });
                };
            });
        };
    }

    private async Task TryAutofillMatchingPasswordAsync(CoreWebView2 core)
    {
        var currentUrl = core.Source ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentUrl) || _sessionData.SavedPasswords.Count == 0) return;

        var match = _sessionData.SavedPasswords.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.Site) &&
            currentUrl.Contains(p.Site.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match is null) return;
        await InjectCredentialsAsync(core, match);
    }

    private static async Task InjectCredentialsAsync(CoreWebView2 core, SavedPasswordItem item)
    {
        var userJson = JsonSerializer.Serialize(item.Username);
        var passJson = JsonSerializer.Serialize(item.Password);
        var script = $$"""
            (() => {
              const passInput = document.querySelector('input[type="password"]');
              if (!passInput) return;
              const form = passInput.form || document;
              const userInput = form.querySelector('input[type="email"], input[type="text"], input[name*="user"], input[name*="mail"]');
              if (userInput && !userInput.value) {
                userInput.value = {{userJson}};
                userInput.dispatchEvent(new Event('input', { bubbles: true }));
              }
              if (!passInput.value) {
                passInput.value = {{passJson}};
                passInput.dispatchEvent(new Event('input', { bubbles: true }));
              }
            })();
            """;
        try { await core.ExecuteScriptAsync(script); } catch { }
    }

    private async void SwitchToTab(BrowserTabItem tab)
    {
        if (_activeTab is not null) _activeTab.IsSelected = false;
        _activeTab = tab;
        _activeTab.IsSelected = true;
        _activeTab.LastActiveAt = DateTime.Now;

        if (_activeTab.IsSleeping)
        {
            await WakeTabAsync(_activeTab);
        }

        RefreshWebViewHosts();
        RefreshTabBarVisibility();
        UpdateAddressAndNavState();
    }

    private void SleepTab(BrowserTabItem tab)
    {
        if (tab.IsHome || tab.IsSleeping || tab.IsPinned) return;
        tab.IsSleeping = true;
        tab.IsPlayingAudio = false;
        if (tab.WebView is not null)
        {
            if (tab.WebView.Parent is Grid parent) parent.Children.Remove(tab.WebView);
            tab.WebView.Dispose();
            tab.WebView = null;
        }
    }

    private async Task WakeTabAsync(BrowserTabItem tab)
    {
        if (!tab.IsSleeping) return;
        tab.IsSleeping = false;
        var webView = new WebView2 { Visibility = Visibility.Collapsed };
        tab.WebView = webView;
        Grid.SetColumn(webView, 0);
        WebAreaGrid.Children.Add(webView);
        await InitializeWebViewForTabAsync(tab);
        if (!tab.IsHome && Uri.TryCreate(NormalizeUrl(tab.Url), UriKind.Absolute, out var uri))
        {
            webView.CoreWebView2?.Navigate(uri.ToString());
        }
    }

    private void RecordHistory(string title, string url)
    {
        var last = _sessionData.History.FirstOrDefault();
        if (last is not null && string.Equals(last.Url, url, StringComparison.OrdinalIgnoreCase) &&
            (DateTime.Now - last.VisitedAt).TotalSeconds < 10)
        {
            last.Title = title;
            return;
        }

        var entry = new HistoryEntry { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url, VisitedAt = DateTime.Now };
        _sessionData.History.Insert(0, entry);
        if (_sessionData.History.Count > 500) _sessionData.History.RemoveAt(_sessionData.History.Count - 1);
        RefreshFilteredHistory();
    }

    private void RefreshWebViewHosts()
    {
        foreach (var t in _tabs)
        {
            if (t.WebView is not null) t.WebView.Visibility = Visibility.Collapsed;
        }

        if (_activeTab is null) return;

        ApplyActiveTabVisualMode(_activeTab.IsIncognito);

        if (_activeTab.IsHome)
        {
            if (_activeTab.IsIncognito)
            {
                HomePageOverlay.Visibility = Visibility.Collapsed;
                IncognitoHomeOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                IncognitoHomeOverlay.Visibility = Visibility.Collapsed;
                HomePageOverlay.Visibility = Visibility.Visible;
            }
        }
        else
        {
            HomePageOverlay.Visibility = Visibility.Collapsed;
            IncognitoHomeOverlay.Visibility = Visibility.Collapsed;
            if (_activeTab.WebView is not null)
            {
                Grid.SetColumn(_activeTab.WebView, 0);
                _activeTab.WebView.Visibility = Visibility.Visible;
            }
        }

        if (_splitTab is not null && _splitTab != _activeTab && _splitTab.WebView is not null)
        {
            if (_splitTab.IsHome)
            {
                _splitTab.IsHome = false;
                _splitTab.Url = "https://www.google.com";
                _splitTab.Title = "Google";
                _splitTab.WebView.CoreWebView2?.Navigate("https://www.google.com");
            }
            Grid.SetColumn(_splitTab.WebView, 2);
            _splitTab.WebView.Visibility = Visibility.Visible;
            SplitColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitDivider.Visibility = Visibility.Visible;
        }
        else
        {
            SplitColumn.Width = new GridLength(0);
            SplitDivider.Visibility = Visibility.Collapsed;
        }
        WebAreaGrid.UpdateLayout();
    }

    private void SetAddressBoxText(string text)
    {
        _suppressUrlTextChanged = true;
        UrlTextBox.Text = text;
        _suppressUrlTextChanged = false;
    }

    private void SetHomeSearchBoxText(string text)
    {
        _suppressHomeTextChanged = true;
        HomeSearchBox.Text = text;
        _suppressHomeTextChanged = false;
    }

    private void UpdateAddressAndNavState()
    {
        if (_activeTab is null) return;
        SetAddressBoxText(_activeTab.IsHome ? string.Empty : _activeTab.Url);
        BtnBack.IsEnabled = _activeTab.CanGoBack || !_activeTab.IsHome;
        BtnForward.IsEnabled = _activeTab.CanGoForward;
        LoadingBadge.Visibility = _activeTab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CloseTab(BrowserTabItem tab)
    {
        if (tab.IsPinned) return;
        if (!tab.IsIncognito)
        {
            _closedTabs.Push(new SavedTabState
            {
                Url = tab.Url,
                Title = tab.Title,
                IsHome = tab.IsHome,
                GroupId = tab.GroupId
            });
            BtnRestoreTab.Visibility = Visibility.Visible;
        }

        if (_splitTab == tab)
        {
            _splitTab.IsSplitTarget = false;
            _splitTab = null;
        }

        var idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _incognitoTabs.Remove(tab);
        foreach (var g in _groups) g.Tabs.Remove(tab);

        if (tab.WebView is not null)
        {
            if (tab.WebView.Parent is Grid parent) parent.Children.Remove(tab.WebView);
            tab.WebView.Dispose();
        }

        if (_tabs.Count == 0)
        {
            await CreateTabAsync("homepage", "general", isIncognito: false, switchTo: true);
            return;
        }

        if (_activeTab == tab)
        {
            SwitchToTab(_tabs[Math.Clamp(idx - 1, 0, _tabs.Count - 1)]);
        }
        else
        {
            RefreshWebViewHosts();
        }
        if (!_isInitializingSession) SaveCurrentSessionState();
    }

    private void NavigateActiveTab(string input)
    {
        SuggestionsPopup.IsOpen = false;
        if (_activeTab is null) return;
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        if (string.Equals(trimmed, "homepage", StringComparison.OrdinalIgnoreCase))
        {
            _activeTab.IsHome = true;
            _activeTab.Url = "homepage";
            _activeTab.FaviconSource = null;
            _activeTab.IsPlayingAudio = false;
            _activeTab.Title = _activeTab.IsIncognito ? "Gizli Sekme" : "Ana Sayfa";
            RefreshWebViewHosts();
            UpdateAddressAndNavState();
            return;
        }

        var targetUrl = NormalizeUrl(trimmed);
        _activeTab.IsHome = false;
        _activeTab.Url = targetUrl;
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedUri) && !string.IsNullOrWhiteSpace(parsedUri.Host))
        {
            _activeTab.Title = parsedUri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        RefreshWebViewHosts();

        _activeTab.WebView?.CoreWebView2?.Navigate(targetUrl);
    }

    private static string NormalizeUrl(string input)
    {
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        if (input.Contains('.') && !input.Contains(' '))
        {
            return "https://" + input;
        }

        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    private void ApplyActiveTabVisualMode(bool isIncognito)
    {
        IncognitoBadge.Visibility = isIncognito ? Visibility.Visible : Visibility.Collapsed;
        if (isIncognito)
        {
            Resources["AppBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16131C"));
            Resources["BarBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#211C2B"));
            Resources["SurfaceBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D263B"));
            Resources["HoverBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D3352"));
            Resources["BorderBrushSubtle"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43385C"));
            Resources["TextPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3E8FF"));
            Resources["TextSecondaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C4B5FD"));
            Resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A855F7"));
        }
        else
        {
            ApplyTheme(_sessionData.IsDarkTheme);
        }
    }

    private void ApplyTheme(bool isDark)
    {
        _sessionData.IsDarkTheme = isDark;
        if (isDark)
        {
            Resources["AppBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#121214"));
            Resources["BarBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#18181B"));
            Resources["SurfaceBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#242429"));
            Resources["HoverBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34343D"));
            Resources["BorderBrushSubtle"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E2E36"));
            Resources["TextPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F4F5"));
            Resources["TextSecondaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA"));
            Resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00B37E"));
            Resources["ClockDisplayBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#050608"));
            Resources["ClockLedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFAA"));
            Resources["ClockLedColor"] = (Color)ColorConverter.ConvertFromString("#00FFAA");
        }
        else
        {
            Resources["AppBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F4F7"));
            Resources["BarBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
            Resources["SurfaceBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
            Resources["HoverBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
            Resources["BorderBrushSubtle"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
            Resources["TextPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
            Resources["TextSecondaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
            Resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
            Resources["ClockDisplayBgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
            Resources["ClockLedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
            Resources["ClockLedColor"] = (Color)ColorConverter.ConvertFromString("#38BDF8");
        }
        UpdatePrayerCountdown(DateTime.Now);
    }

    private void ComponentDispatcher_ThreadFilterMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_XBUTTONDOWN = 0x020B;
        const int WM_APPCOMMAND = 0x0319;
        const int APPCOMMAND_BROWSER_BACKWARD = 1;
        const int APPCOMMAND_BROWSER_FORWARD = 2;

        if (msg.message == WM_KEYDOWN && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var vk = (int)msg.wParam;
            if (vk == 'H')
            {
                handled = true;
                Dispatcher.BeginInvoke(() => OpenHistoryDownloadsDrawer(showDownloads: false));
                return;
            }
            if (vk == 'J')
            {
                handled = true;
                Dispatcher.BeginInvoke(() => OpenHistoryDownloadsDrawer(showDownloads: true));
                return;
            }
            if (vk == 'K')
            {
                handled = true;
                Dispatcher.BeginInvoke(OpenCommandPalette);
                return;
            }
            if (vk == 'P')
            {
                handled = true;
                Dispatcher.BeginInvoke(PrintActiveTab);
                return;
            }
        }

        if (msg.message == WM_XBUTTONDOWN)
        {
            var xButton = (int)((msg.wParam.ToInt64() >> 16) & 0xFFFF);
            if (xButton == 1)
            {
                handled = true;
                Dispatcher.BeginInvoke(() => BtnBack_Click(this, new RoutedEventArgs()));
            }
            else if (xButton == 2)
            {
                handled = true;
                Dispatcher.BeginInvoke(() => BtnForward_Click(this, new RoutedEventArgs()));
            }
        }
        else if (msg.message == WM_APPCOMMAND)
        {
            var cmd = (int)((msg.lParam.ToInt64() >> 16) & 0x0FFF);
            if (cmd == APPCOMMAND_BROWSER_BACKWARD)
            {
                handled = true;
                Dispatcher.BeginInvoke(() => BtnBack_Click(this, new RoutedEventArgs()));
            }
            else if (cmd == APPCOMMAND_BROWSER_FORWARD)
            {
                handled = true;
                Dispatcher.BeginInvoke(() => BtnForward_Click(this, new RoutedEventArgs()));
            }
        }
    }

    // --- ADRES ÇUBUĞU VE ANA SAYFA ARAMA ÖNERİLERİ ---

    private async Task<List<string>> BuildCombinedSuggestionsAsync(string text)
    {
        var list = new List<string>();
        foreach (var bm in _bookmarks)
        {
            if (bm.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                bm.Url.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(bm.Url);
            }
        }

        var google = await DashboardApiService.FetchSearchSuggestionsAsync(text);
        foreach (var item in google)
        {
            if (!list.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(item);
            }
        }
        return list.Take(8).ToList();
    }

    private async void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUrlTextChanged || !UrlTextBox.IsFocused) return;
        var text = UrlTextBox.Text.Trim();
        if (text.Length < 2 || text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            SuggestionsPopup.IsOpen = false;
            return;
        }

        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var token = _suggestCts.Token;

        try
        {
            await Task.Delay(180, token);
            var suggestions = await BuildCombinedSuggestionsAsync(text);
            if (token.IsCancellationRequested) return;

            if (suggestions.Count > 0 && UrlTextBox.IsFocused)
            {
                SuggestionsListBox.ItemsSource = suggestions;
                SuggestionsPopup.IsOpen = true;
            }
            else
            {
                SuggestionsPopup.IsOpen = false;
            }
        }
        catch (TaskCanceledException) { }
    }

    private async void HomeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHomeTextChanged || !HomeSearchBox.IsFocused) return;
        var text = HomeSearchBox.Text.Trim();
        if (text.Length < 2)
        {
            HomeSuggestionsPopup.IsOpen = false;
            return;
        }

        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var token = _suggestCts.Token;

        try
        {
            await Task.Delay(180, token);
            var suggestions = await BuildCombinedSuggestionsAsync(text);
            if (token.IsCancellationRequested) return;

            if (suggestions.Count > 0 && HomeSearchBox.IsFocused)
            {
                HomeSuggestionsListBox.ItemsSource = suggestions;
                HomeSuggestionsPopup.IsOpen = true;
            }
            else
            {
                HomeSuggestionsPopup.IsOpen = false;
            }
        }
        catch (TaskCanceledException) { }
    }

    private void HomeSuggestionsListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (HomeSuggestionsListBox.SelectedItem is string selected)
        {
            HomeSuggestionsPopup.IsOpen = false;
            HomeSearchBox.Clear();
            NavigateActiveTab(selected);
        }
    }

    private void HomeSuggestionsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && HomeSuggestionsListBox.SelectedItem is string selected)
        {
            HomeSuggestionsPopup.IsOpen = false;
            HomeSearchBox.Clear();
            NavigateActiveTab(selected);
        }
        else if (e.Key == Key.Tab && HomeSuggestionsListBox.SelectedItem is string tabSel)
        {
            e.Handled = true;
            HomeSearchBox.Text = tabSel;
            HomeSearchBox.CaretIndex = tabSel.Length;
            HomeSuggestionsPopup.IsOpen = false;
            HomeSearchBox.Focus();
        }
    }

    private void HomeSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!HomeSuggestionsListBox.IsKeyboardFocusWithin)
            {
                HomeSuggestionsPopup.IsOpen = false;
            }
        }));
    }

    private void SuggestionsListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionsListBox.SelectedItem is string selected)
        {
            NavigateActiveTab(selected);
        }
    }

    private void SuggestionsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionsListBox.SelectedItem is string selected)
        {
            NavigateActiveTab(selected);
        }
        else if (e.Key == Key.Tab && SuggestionsListBox.SelectedItem is string tabSel)
        {
            e.Handled = true;
            SetAddressBoxText(tabSel);
            UrlTextBox.CaretIndex = tabSel.Length;
            SuggestionsPopup.IsOpen = false;
            UrlTextBox.Focus();
        }
    }

    private void UrlTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!SuggestionsListBox.IsKeyboardFocusWithin)
            {
                SuggestionsPopup.IsOpen = false;
            }
        }));
    }

    // --- GEMİNİ ASİSTAN & SAYFA ÖZETLEME ---

    private async Task EnsureGeminiPanelOpenAsync()
    {
        GeminiDrawer.Visibility = Visibility.Visible;
        if (_isGeminiInitialized) return;
        _isGeminiInitialized = true;

        var env = _defaultEnv ?? await CoreWebView2Environment.CreateAsync(
            null, SessionStorageService.GetUserDataFolder(false));
        await GeminiWebView.EnsureCoreWebView2Async(env);
        GeminiWebView.CoreWebView2.Navigate("https://gemini.google.com/app");
    }

    private async void BtnGeminiToggle_Click(object sender, RoutedEventArgs e)
    {
        if (GeminiDrawer.Visibility == Visibility.Visible)
        {
            GeminiDrawer.Visibility = Visibility.Collapsed;
        }
        else
        {
            await EnsureGeminiPanelOpenAsync();
        }
    }

    private async void BtnSummarizePage_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2 is null || _activeTab.IsHome)
        {
            MessageBox.Show("Özetlemek için önce bir web sayfası açın.", "Sayfayı Özetle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var prompt = $"Bu web sayfasını Türkçe olarak maddeler halinde özetle: {_activeTab.Url}";
        await EnsureGeminiPanelOpenAsync();

        var promptJson = JsonSerializer.Serialize(prompt);
        var sent = false;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (GeminiWebView.CoreWebView2 is not null)
            {
                var injectScript = $$"""
                    (() => {
                      const editor = document.querySelector('rich-textarea .ql-editor, div[contenteditable="true"][role="textbox"], div[contenteditable="true"], textarea');
                      if (!editor) return 'not-ready';
                      editor.focus();
                      if (editor.tagName === 'TEXTAREA') {
                        editor.value = {{promptJson}};
                        editor.dispatchEvent(new Event('input', { bubbles: true }));
                      } else {
                        const sel = window.getSelection();
                        const range = document.createRange();
                        range.selectNodeContents(editor);
                        sel.removeAllRanges();
                        sel.addRange(range);
                        document.execCommand('insertText', false, {{promptJson}});
                      }
                      setTimeout(() => {
                        const sendBtn = document.querySelector('button[aria-label*="Gönder"], button[aria-label*="Send"], button.send-button, button[data-test-id="send-button"]');
                        if (sendBtn && !sendBtn.disabled) {
                          sendBtn.click();
                        } else {
                          editor.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                        }
                      }, 220);
                      return 'ok';
                    })();
                    """;
                var res = await GeminiWebView.CoreWebView2.ExecuteScriptAsync(injectScript);
                if (res.Contains("ok", StringComparison.Ordinal))
                {
                    sent = true;
                    break;
                }
            }
            await Task.Delay(500);
        }

        if (!sent)
        {
            Clipboard.SetText(prompt);
            MessageBox.Show(
                "Gemini sohbet kutusu henüz hazır değil (oturum açmanız gerekebilir). İstem panoya kopyalandı.",
                "✨ Sayfayı Özetle",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    // --- DASHBOARD: KONUM, YER İMLERİ, HATIRLATICILAR, NOTLAR ---

    private void BtnChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        LocSearchBox.Clear();
        LocationResultsList.ItemsSource = null;
        WebAreaGrid.Visibility = Visibility.Hidden;
        LocationModalOverlay.Visibility = Visibility.Visible;
        LocSearchBox.Focus();
    }

    private void BtnCloseLocationModal_Click(object sender, RoutedEventArgs e)
    {
        LocationModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private async void BtnSearchLocation_Click(object sender, RoutedEventArgs e) => await PerformLocationSearchAsync();

    private async void BtnAutoLocate_Click(object sender, RoutedEventArgs e)
    {
        var detected = await DashboardApiService.AutoDetectLocationAsync();
        if (detected is null)
        {
            MessageBox.Show("Konum otomatik algılanamadı. Lütfen listeden arayın.", "Konum", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _sessionData.Location = new LocationConfig
        {
            DisplayName = detected.DisplayName,
            Latitude = detected.Latitude,
            Longitude = detected.Longitude
        };
        LocationModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
        SaveCurrentSessionState();
        await RefreshWeatherAndPrayerAsync();
    }

    private async void LocSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await PerformLocationSearchAsync();
    }

    private async Task PerformLocationSearchAsync()
    {
        _locationSearchResults = await DashboardApiService.SearchLocationsAsync(LocSearchBox.Text);
        LocationResultsList.ItemsSource = _locationSearchResults.Select(x => x.DisplayName).ToList();
    }

    private async void LocationResultsList_DoubleClick(object sender, MouseButtonEventArgs e) => await ApplySelectedLocationAsync();
    private async void BtnApplySelectedLocation_Click(object sender, RoutedEventArgs e) => await ApplySelectedLocationAsync();

    private async Task ApplySelectedLocationAsync()
    {
        var idx = LocationResultsList.SelectedIndex;
        if (idx < 0 || idx >= _locationSearchResults.Count) return;

        var chosen = _locationSearchResults[idx];
        _sessionData.Location = new LocationConfig
        {
            DisplayName = chosen.DisplayName,
            Latitude = chosen.Latitude,
            Longitude = chosen.Longitude
        };
        LocationModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
        SaveCurrentSessionState();
        await RefreshWeatherAndPrayerAsync();
    }

    private void BtnClosePrayerToast_Click(object sender, RoutedEventArgs e) => PrayerToast.Visibility = Visibility.Collapsed;

    private void BmBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BtnAddBookmark_Click(sender, e);
        }
    }

    private void BtnAddBookmark_Click(object sender, RoutedEventArgs e)
    {
        var title = BmTitleBox.Text.Trim();
        var url = BmUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) return;

        _bookmarks.Add(new BookmarkItem { Title = title, Url = NormalizeUrl(url) });
        BmTitleBox.Clear();
        BmUrlBox.Clear();
        SaveCurrentSessionState();
    }

    private void BookmarkItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is BookmarkItem bm)
        {
            NavigateActiveTab(bm.Url);
        }
    }

    private void BtnDeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BookmarkItem bm)
        {
            _bookmarks.Remove(bm);
            SaveCurrentSessionState();
        }
    }

    private bool _isFormattingTime;

    private void RemTimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isFormattingTime) return;
        var digits = new string(RemTimeBox.Text.Where(char.IsDigit).Take(4).ToArray());
        var formatted = digits.Length switch
        {
            >= 3 => $"{digits[..2]}:{digits[2..]}",
            _ => digits
        };

        if (RemTimeBox.Text != formatted)
        {
            _isFormattingTime = true;
            RemTimeBox.Text = formatted;
            RemTimeBox.CaretIndex = formatted.Length;
            _isFormattingTime = false;
        }
    }

    private void RemBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnAddReminder_Click(sender, e);
    }

    private void BtnAddReminder_Click(object sender, RoutedEventArgs e)
    {
        var text = RemTextBox.Text.Trim();
        var timeInput = RemTimeBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var selectedDate = RemDatePicker.SelectedDate ?? DateTime.Today;
        DateTime due;
        if (string.IsNullOrEmpty(timeInput))
        {
            due = selectedDate.Date == DateTime.Today
                ? DateTime.Now.AddMinutes(30)
                : selectedDate.Date.AddHours(9);
        }
        else if (TimeSpan.TryParse(timeInput, out var onlyTime))
        {
            due = selectedDate.Date.Add(onlyTime);
        }
        else
        {
            MessageBox.Show("Geçerli bir saat girin (örn: 14:30).", "Hatırlatıcı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _reminders.Add(new ReminderItem { Text = text, DueTime = due });
        RemTextBox.Clear();
        RemTimeBox.Clear();
        SaveCurrentSessionState();
    }

    private void BtnDeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ReminderItem rem)
        {
            _reminders.Remove(rem);
            SaveCurrentSessionState();
        }
    }

    private void NoteInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddStickyNote();
    }

    private void BtnAddNote_Click(object sender, RoutedEventArgs e) => AddStickyNote();

    private void AddStickyNote()
    {
        var text = NoteInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        string[] palette = ["#FEF08A", "#BBF7D0", "#BAE6FD", "#FBCFE8", "#DDD6FE"];
        var color = palette[_notes.Count % palette.Length];
        _notes.Insert(0, new StickyNoteItem { Text = text, ColorHex = color });
        NoteInputBox.Clear();
        SaveCurrentSessionState();
    }

    private void BtnNoteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string colorHex && btn.DataContext is StickyNoteItem note)
        {
            note.ColorHex = colorHex;
            SaveCurrentSessionState();
        }
    }

    private void StickyNote_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _noteDragStartPoint = e.GetPosition(null);
    }

    private void BtnIncognitoSearch_Click(object sender, RoutedEventArgs e)
    {
        NavigateActiveTab(IncognitoSearchBox.Text);
        IncognitoSearchBox.Clear();
    }

    private void IncognitoSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateActiveTab(IncognitoSearchBox.Text);
            IncognitoSearchBox.Clear();
        }
    }

    private void StickyNote_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.OriginalSource is Button) return;

        var diff = _noteDragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StickyNoteItem note)
            {
                note.IsDragging = true;
                try
                {
                    DragDrop.DoDragDrop(fe, note, DragDropEffects.Move);
                }
                finally
                {
                    note.IsDragging = false;
                    foreach (var n in _notes) n.IsDragTarget = false;
                }
            }
        }
    }

    private void StickyNote_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(StickyNoteItem)) is StickyNoteItem sourceNote &&
            sender is FrameworkElement fe && fe.DataContext is StickyNoteItem targetNote &&
            sourceNote != targetNote)
        {
            targetNote.IsDragTarget = true;
        }
    }

    private void StickyNote_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is StickyNoteItem targetNote)
        {
            targetNote.IsDragTarget = false;
        }
    }

    private void StickyNote_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(StickyNoteItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StickyNote_Drop(object sender, DragEventArgs e)
    {
        foreach (var n in _notes)
        {
            n.IsDragging = false;
            n.IsDragTarget = false;
        }

        if (e.Data.GetData(typeof(StickyNoteItem)) is not StickyNoteItem sourceNote) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not StickyNoteItem targetNote) return;
        if (sourceNote == targetNote) return;

        var oldIdx = _notes.IndexOf(sourceNote);
        var newIdx = _notes.IndexOf(targetNote);
        if (oldIdx >= 0 && newIdx >= 0)
        {
            _notes.Move(oldIdx, newIdx);
            SaveCurrentSessionState();
        }
    }

    private void GroupPill_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TabGroup group)
        {
            ToggleGroupCollapse(group);
        }
    }

    private void GroupMenuToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is TabGroup group)
        {
            ToggleGroupCollapse(group);
        }
    }

    private void ToggleGroupCollapse(TabGroup group)
    {
        group.IsCollapsed = !group.IsCollapsed;
        if (group.IsCollapsed && _activeTab?.GroupId == group.Id)
        {
            var otherVisible = _tabs.FirstOrDefault(t =>
                t.GroupId != group.Id && !(_groups.FirstOrDefault(g => g.Id == t.GroupId)?.IsCollapsed ?? false));
            if (otherVisible is not null) SwitchToTab(otherVisible);
        }
        else if (!group.IsCollapsed)
        {
            var firstTab = _tabs.FirstOrDefault(t => t.GroupId == group.Id);
            if (firstTab is not null) SwitchToTab(firstTab);
        }
        RefreshTabBarVisibility();
        SaveCurrentSessionState();
    }

    private void GroupMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not TabGroup group) return;
        if (group.Id == "general")
        {
            MessageBox.Show("Varsayılan 'Genel' grubu silinemez.", "Gruplar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var general = _groups.FirstOrDefault(g => g.Id == "general") ?? _groups.First();
        foreach (var tab in _tabs.Where(t => t.GroupId == group.Id).ToList())
        {
            group.Tabs.Remove(tab);
            if (!general.Tabs.Contains(tab)) general.Tabs.Add(tab);
            tab.GroupId = general.Id;
            tab.GroupColorHex = general.ColorHex;
        }

        _groups.Remove(group);
        SortTabsByGroup();
        RefreshTabBarVisibility();
        SaveCurrentSessionState();
    }

    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var tab = (menu.PlacementTarget as FrameworkElement)?.DataContext as BrowserTabItem;
        var moveItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MoveToGroupMenuItem");
        if (moveItem is null || tab is null) return;

        moveItem.Items.Clear();
        foreach (var group in _groups)
        {
            var sub = new MenuItem
            {
                Header = group.Name,
                IsChecked = tab.GroupId == group.Id
            };
            sub.Click += (_, _) => MoveTabToGroup(tab, group);
            moveItem.Items.Add(sub);
        }
    }

    private void MoveTabToGroup(BrowserTabItem tab, TabGroup group)
    {
        if (tab.IsIncognito) return;
        foreach (var g in _groups) g.Tabs.Remove(tab);
        if (!group.Tabs.Contains(tab)) group.Tabs.Add(tab);

        tab.GroupId = group.Id;
        tab.GroupColorHex = group.ColorHex;
        group.IsCollapsed = false;
        SortTabsByGroup();
        RefreshTabBarVisibility();
        SaveCurrentSessionState();
    }

    private void SortTabsByGroup()
    {
        var orderMap = _groups
            .Select((g, i) => (g.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

        var sorted = _tabs
            .OrderBy(t => orderMap.TryGetValue(t.GroupId, out var idx) ? idx : int.MaxValue)
            .ThenByDescending(t => t.IsPinned)
            .ToList();

        for (int targetIdx = 0; targetIdx < sorted.Count; targetIdx++)
        {
            var currentIdx = _tabs.IndexOf(sorted[targetIdx]);
            if (currentIdx != targetIdx && currentIdx >= 0)
            {
                _tabs.Move(currentIdx, targetIdx);
            }
        }

        foreach (var g in _groups)
        {
            var gSorted = g.Tabs.OrderByDescending(t => t.IsPinned).ToList();
            for (int i = 0; i < gSorted.Count; i++)
            {
                var cur = g.Tabs.IndexOf(gSorted[i]);
                if (cur != i && cur >= 0) g.Tabs.Move(cur, i);
            }
        }
    }

    private void RefreshTabBarVisibility()
    {
        foreach (var g in _groups)
        {
            g.TabCount = _tabs.Count(t => !t.IsIncognito && t.GroupId == g.Id);
        }

        foreach (var tab in _tabs)
        {
            if (tab.IsIncognito)
            {
                tab.IsVisibleInTabBar = true;
                continue;
            }
            var grp = _groups.FirstOrDefault(g => g.Id == tab.GroupId);
            var collapsed = grp?.IsCollapsed ?? false;
            tab.IsVisibleInTabBar = !collapsed || tab == _activeTab;
        }

        UpdateDynamicTabWidths();
    }

    private void UpdateDynamicTabWidths()
    {
        var visibleTabs = _tabs.Where(t => t.IsVisibleInTabBar).ToList();
        if (visibleTabs.Count == 0) return;

        var pinnedCount = visibleTabs.Count(t => t.IsPinned);
        var unpinnedCount = visibleTabs.Count - pinnedCount;
        if (unpinnedCount <= 0) return;

        var totalWindowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var reserved = 140 + (_groups.Count * 105) + (pinnedCount * 48);
        var availableWidth = Math.Max(260, totalWindowWidth - reserved);
        var targetWidth = Math.Clamp(Math.Floor(availableWidth / unpinnedCount) - 4, 68, 200);

        foreach (var tab in visibleTabs)
        {
            tab.TabWidth = targetWidth;
        }
    }

    private void TabsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabsScrollViewer.ScrollableWidth > 0)
        {
            TabsScrollViewer.ScrollToHorizontalOffset(TabsScrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void BtnDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StickyNoteItem note)
        {
            _notes.Remove(note);
            SaveCurrentSessionState();
        }
    }

    private void BtnEditNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not StickyNoteItem note) return;
        _editingNoteItem = note;
        EditNoteTextBox.Text = note.Text;
        WebAreaGrid.Visibility = Visibility.Hidden;
        EditNoteModalOverlay.Visibility = Visibility.Visible;
        EditNoteTextBox.Focus();
        EditNoteTextBox.CaretIndex = EditNoteTextBox.Text.Length;
    }

    private void BtnCloseEditNote_Click(object sender, RoutedEventArgs e)
    {
        _editingNoteItem = null;
        EditNoteModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BtnSaveEditNote_Click(object sender, RoutedEventArgs e)
    {
        if (_editingNoteItem is null) return;
        var text = EditNoteTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show("Not içeriği boş bırakılamaz.", "Yapışkan Not", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _editingNoteItem.Text = text;
        _editingNoteItem = null;
        EditNoteModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
        SaveCurrentSessionState();
    }

    // --- ŞİFRE KASASI & VERİ YEDEKLEME ---

    private void PwdSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilteredPasswords();

    private void BtnGeneratePwd_Click(object sender, RoutedEventArgs e)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*_-+";
        var buffer = new char[16];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        PwdPassBox.Text = new string(buffer);
    }

    private void HandleWebJsonMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "__pwd_capture") return;

            var site = root.TryGetProperty("site", out var s) ? s.GetString() ?? "" : "";
            var user = root.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var pass = root.TryGetProperty("pass", out var p) ? p.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(pass)) return;

            var existing = _sessionData.SavedPasswords.FirstOrDefault(x =>
                string.Equals(x.Site, site, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Username, user, StringComparison.OrdinalIgnoreCase));

            if (existing is not null && existing.Password == pass) return;

            _pendingCapturedPassword = new SavedPasswordItem
            {
                Site = site,
                Username = user,
                Password = pass
            };

            var actionText = existing is not null ? "güncellensin mi?" : "kasaya kaydedilsin mi?";
            var userLabel = string.IsNullOrWhiteSpace(user) ? "Bu hesap" : $"'{user}'";
            PasswordSavePromptText.Text = $"🔐 {site} için {userLabel} şifresi {actionText}";
            PasswordSaveBar.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void BtnSaveCapturedPwd_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingCapturedPassword is null) return;
        UpsertPassword(_pendingCapturedPassword.Site, _pendingCapturedPassword.Username, _pendingCapturedPassword.Password);
        _pendingCapturedPassword = null;
        PasswordSaveBar.Visibility = Visibility.Collapsed;
    }

    private void BtnDismissCapturedPwd_Click(object sender, RoutedEventArgs e)
    {
        _pendingCapturedPassword = null;
        PasswordSaveBar.Visibility = Visibility.Collapsed;
    }

    private void UpsertPassword(string site, string user, string pass)
    {
        var existing = _sessionData.SavedPasswords.FirstOrDefault(x =>
            string.Equals(x.Site, site, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Username, user, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Password = pass;
        }
        else
        {
            _sessionData.SavedPasswords.Add(new SavedPasswordItem
            {
                Site = site,
                Username = user,
                Password = pass
            });
        }

        RefreshFilteredPasswords();
        SaveCurrentSessionState();
    }

    private void BtnAddPwd_Click(object sender, RoutedEventArgs e)
    {
        var site = PwdSiteBox.Text.Trim();
        var user = PwdUserBox.Text.Trim();
        var pass = PwdPassBox.Text;
        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(pass)) return;

        UpsertPassword(site, user, pass);
        PwdSiteBox.Clear();
        PwdUserBox.Clear();
        PwdPassBox.Clear();
    }

    private async void BtnAutofillPwd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SavedPasswordItem item &&
            _activeTab?.WebView?.CoreWebView2 is not null && !_activeTab.IsHome)
        {
            await InjectCredentialsAsync(_activeTab.WebView.CoreWebView2, item);
        }
    }

    private void BtnCopyPwd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SavedPasswordItem item)
        {
            Clipboard.SetText(item.Password);
        }
    }

    private void BtnDeletePwd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SavedPasswordItem item)
        {
            _sessionData.SavedPasswords.Remove(item);
            RefreshFilteredPasswords();
            SaveCurrentSessionState();
        }
    }

    private void BtnTogglePwdVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SavedPasswordItem item)
        {
            item.IsPasswordVisible = !item.IsPasswordVisible;
        }
    }

    private void BtnEditPwd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SavedPasswordItem item) return;
        _editingPasswordItem = item;
        EditPwdSiteBox.Text = item.Site;
        EditPwdUserBox.Text = item.Username;
        EditPwdPassBox.Text = item.Password;
        WebAreaGrid.Visibility = Visibility.Hidden;
        EditPasswordModalOverlay.Visibility = Visibility.Visible;
        EditPwdPassBox.Focus();
    }

    private void BtnCloseEditPwd_Click(object sender, RoutedEventArgs e)
    {
        _editingPasswordItem = null;
        EditPasswordModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BtnGenerateEditPwd_Click(object sender, RoutedEventArgs e)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*_-+";
        var buffer = new char[16];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        EditPwdPassBox.Text = new string(buffer);
    }

    private void BtnSaveEditPwd_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPasswordItem is null) return;
        var site = EditPwdSiteBox.Text.Trim();
        var user = EditPwdUserBox.Text.Trim();
        var pass = EditPwdPassBox.Text;

        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(pass))
        {
            MessageBox.Show("Site ve şifre alanları boş bırakılamaz.", "Şifre Düzenle", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _editingPasswordItem.Site = site;
        _editingPasswordItem.Username = user;
        _editingPasswordItem.Password = pass;
        _editingPasswordItem = null;

        EditPasswordModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
        RefreshFilteredPasswords();
        SaveCurrentSessionState();
    }

    private void ShowMasterPasswordPrompt(string title, string description, Action<string> onConfirm)
    {
        MasterPwdTitleText.Text = title;
        MasterPwdDescText.Text = description;
        MasterPwdBox.Clear();
        _masterPwdCallback = onConfirm;
        WebAreaGrid.Visibility = Visibility.Hidden;
        MasterPasswordModalOverlay.Visibility = Visibility.Visible;
        MasterPwdBox.Focus();
    }

    private void BtnCancelMasterPwd_Click(object sender, RoutedEventArgs e)
    {
        _masterPwdCallback = null;
        MasterPasswordModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void MasterPwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnConfirmMasterPwd_Click(sender, e);
    }

    private void BtnConfirmMasterPwd_Click(object sender, RoutedEventArgs e)
    {
        var pwd = MasterPwdBox.Password;
        if (string.IsNullOrEmpty(pwd))
        {
            MessageBox.Show("Lütfen bir ana parola girin.", "Ana Parola", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cb = _masterPwdCallback;
        _masterPwdCallback = null;
        MasterPasswordModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
        cb?.Invoke(pwd);
    }

    private void BtnExportPasswords_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionData.SavedPasswords.Count == 0)
        {
            MessageBox.Show("Dışa aktarılacak kayıtlı şifre bulunmuyor.", "Şifre Kasası", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Şifreli Kasa - AES-256 (*.ozelpwd)|*.ozelpwd|Chrome / Edge Uyumlu CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = $"OzelTarayici_Sifreler_{DateTime.Now:yyyyMMdd}"
        };

        if (dlg.ShowDialog() != true) return;

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        if (ext == ".ozelpwd")
        {
            ShowMasterPasswordPrompt(
                "🔐 Şifreli Kasa Dışa Aktar",
                "Bu yedek dosyasını AES-256-GCM ile şifrelemek için bir ana parola belirleyin (başka bir bilgisayarda açarken bu parola gerekecektir):",
                masterPwd =>
                {
                    PasswordPortabilityService.ExportEncrypted(dlg.FileName, _sessionData.SavedPasswords, masterPwd);
                    MessageBox.Show($"{_sessionData.SavedPasswords.Count} şifre AES-256 ile şifrelenerek dışa aktarıldı.", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                });
        }
        else if (ext == ".csv")
        {
            var confirm = MessageBox.Show(
                "CSV dosyaları şifresiz (düz metin) olarak kaydedilir. Devam etmek istiyor musunuz?",
                "Güvenlik Uyarısı",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            PasswordPortabilityService.ExportCsv(dlg.FileName, _sessionData.SavedPasswords);
            MessageBox.Show($"{_sessionData.SavedPasswords.Count} şifre Chrome/Edge uyumlu CSV olarak dışa aktarıldı.", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            PasswordPortabilityService.ExportJson(dlg.FileName, _sessionData.SavedPasswords);
            MessageBox.Show($"{_sessionData.SavedPasswords.Count} şifre JSON olarak dışa aktarıldı.", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnImportPasswords_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Desteklenen Şifre Dosyaları (*.ozelpwd;*.csv;*.json)|*.ozelpwd;*.csv;*.json|Şifreli Kasa (*.ozelpwd)|*.ozelpwd|Chrome / Edge CSV (*.csv)|*.csv|JSON (*.json)|*.json"
        };

        if (dlg.ShowDialog() != true) return;

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        if (ext == ".ozelpwd")
        {
            ShowMasterPasswordPrompt(
                "🔓 Şifreli Kasa İçe Aktar",
                "Şifreli yedek dosyasını çözmek için ana parolayı girin:",
                masterPwd =>
                {
                    try
                    {
                        var imported = PasswordPortabilityService.ImportEncrypted(dlg.FileName, masterPwd);
                        FinalizePasswordImport(imported);
                    }
                    catch
                    {
                        MessageBox.Show("Ana parola hatalı veya dosya bozuk.", "İçe Aktarma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
        }
        else if (ext == ".csv")
        {
            try
            {
                var imported = PasswordPortabilityService.ImportCsv(dlg.FileName);
                FinalizePasswordImport(imported);
            }
            catch
            {
                MessageBox.Show("CSV dosyası okunamadı.", "İçe Aktarma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            try
            {
                var imported = PasswordPortabilityService.ImportJson(dlg.FileName);
                FinalizePasswordImport(imported);
            }
            catch
            {
                MessageBox.Show("JSON dosyası okunamadı.", "İçe Aktarma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void FinalizePasswordImport(List<SavedPasswordItem> imported)
    {
        var report = PasswordPortabilityService.MergeInto(_sessionData.SavedPasswords, imported);
        RefreshFilteredPasswords();
        SaveCurrentSessionState();
        MessageBox.Show(
            $"İçe aktarma tamamlandı:\n\n• {report.Added} yeni şifre eklendi\n• {report.Updated} mevcut şifre güncellendi\n• {report.Skipped} aynı kayıt atlandı",
            "Şifre İçe Aktarma Raporu",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void BtnExportData_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSessionState();
        var dlg = new SaveFileDialog
        {
            Filter = "JSON Yedeği (*.json)|*.json",
            FileName = $"OzelTarayici_Yedek_{DateTime.Now:yyyyMMdd}.json"
        };
        if (dlg.ShowDialog() == true)
        {
            var json = JsonSerializer.Serialize(_sessionData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            MessageBox.Show("Tüm tarayıcı verileri dışa aktarıldı.", "Yedekleme", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnImportData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON Yedeği (*.json)|*.json" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var raw = File.ReadAllText(dlg.FileName);
                var imported = JsonSerializer.Deserialize<AppSessionData>(raw);
                if (imported is not null)
                {
                    _sessionData = imported;
                    SyncCollectionsFromSession();
                    ApplyTheme(_sessionData.IsDarkTheme);
                    SaveCurrentSessionState();
                    await RefreshWeatherAndPrayerAsync();
                    MessageBox.Show("Veriler başarıyla içe aktarıldı.", "Yedekleme", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch
            {
                MessageBox.Show("Geçersiz yedek dosyası.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // --- TEMEL TARAYICI OLAYLARI ---

    private async void BtnNewTab_Click(object sender, RoutedEventArgs e)
        => await CreateTabAsync("homepage", _activeTab?.GroupId ?? "general", false, true);

    private async void BtnRestoreTab_Click(object sender, RoutedEventArgs e)
    {
        if (_closedTabs.Count == 0) return;
        var saved = _closedTabs.Pop();
        if (_closedTabs.Count == 0) BtnRestoreTab.Visibility = Visibility.Collapsed;
        await CreateTabAsync(saved.Url, saved.GroupId, false, true);
    }

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement fe && fe.DataContext is BrowserTabItem tab)
        {
            e.Handled = true;
            CloseTab(tab);
        }
    }

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(null);
        if (sender is FrameworkElement fe && fe.DataContext is BrowserTabItem tab)
        {
            SwitchToTab(tab);
        }
    }

    private void TabItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.OriginalSource is Button) return;

        var diff = _tabDragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is BrowserTabItem tab)
            {
                tab.IsDragging = true;
                try
                {
                    DragDrop.DoDragDrop(fe, tab, DragDropEffects.Move);
                }
                finally
                {
                    tab.IsDragging = false;
                    foreach (var t in _tabs) t.IsDragTarget = false;
                }
            }
        }
    }

    private void TabItem_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BrowserTabItem)) is BrowserTabItem sourceTab &&
            sender is FrameworkElement fe && fe.DataContext is BrowserTabItem targetTab &&
            sourceTab != targetTab &&
            sourceTab.IsIncognito == targetTab.IsIncognito)
        {
            targetTab.IsDragTarget = true;
        }
    }

    private void TabItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BrowserTabItem targetTab)
        {
            targetTab.IsDragTarget = false;
        }
    }

    private void TabItem_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BrowserTabItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TabItem_Drop(object sender, DragEventArgs e)
    {
        foreach (var t in _tabs)
        {
            t.IsDragging = false;
            t.IsDragTarget = false;
        }

        if (e.Data.GetData(typeof(BrowserTabItem)) is not BrowserTabItem sourceTab) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not BrowserTabItem targetTab) return;
        if (sourceTab == targetTab || sourceTab.IsIncognito != targetTab.IsIncognito) return;

        if (sourceTab.IsIncognito)
        {
            var oldIdx = _incognitoTabs.IndexOf(sourceTab);
            var newIdx = _incognitoTabs.IndexOf(targetTab);
            if (oldIdx >= 0 && newIdx >= 0) _incognitoTabs.Move(oldIdx, newIdx);
            return;
        }

        if (sourceTab.GroupId != targetTab.GroupId)
        {
            var targetGroup = _groups.FirstOrDefault(g => g.Id == targetTab.GroupId);
            if (targetGroup is not null) MoveTabToGroup(sourceTab, targetGroup);
        }

        var grp = _groups.FirstOrDefault(g => g.Id == targetTab.GroupId);
        if (grp is not null)
        {
            var oldIdx = grp.Tabs.IndexOf(sourceTab);
            var newIdx = grp.Tabs.IndexOf(targetTab);
            if (oldIdx >= 0 && newIdx >= 0) grp.Tabs.Move(oldIdx, newIdx);
        }

        var allOldIdx = _tabs.IndexOf(sourceTab);
        var allNewIdx = _tabs.IndexOf(targetTab);
        if (allOldIdx >= 0 && allNewIdx >= 0) _tabs.Move(allOldIdx, allNewIdx);

        SaveCurrentSessionState();
    }

    private void GroupPill_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BrowserTabItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void GroupPill_Drop(object sender, DragEventArgs e)
    {
        foreach (var t in _tabs)
        {
            t.IsDragging = false;
            t.IsDragTarget = false;
        }

        if (e.Data.GetData(typeof(BrowserTabItem)) is not BrowserTabItem sourceTab || sourceTab.IsIncognito) return;
        if (sender is FrameworkElement fe && fe.Tag is TabGroup targetGroup)
        {
            MoveTabToGroup(sourceTab, targetGroup);
        }
    }

    private void BtnCloseSpecificTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTabItem tab) CloseTab(tab);
    }

    private void BtnToggleTabMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTabItem tab && tab.WebView?.CoreWebView2 is not null)
        {
            tab.WebView.CoreWebView2.IsMuted = !tab.WebView.CoreWebView2.IsMuted;
            tab.IsMuted = tab.WebView.CoreWebView2.IsMuted;
        }
    }

    private async void BtnToggleTabMediaPlayback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is BrowserTabItem tab && tab.WebView?.CoreWebView2 is not null)
            {
                await tab.WebView.CoreWebView2.ExecuteScriptAsync(WebViewPrivacyService.ToggleMediaPlaybackScript);
            }
        }
        catch { }
    }

    private void TabMenuMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is BrowserTabItem tab && tab.WebView?.CoreWebView2 is not null)
        {
            tab.WebView.CoreWebView2.IsMuted = !tab.WebView.CoreWebView2.IsMuted;
            tab.IsMuted = tab.WebView.CoreWebView2.IsMuted;
        }
    }

    private void TabMenuPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is BrowserTabItem tab)
        {
            tab.IsPinned = !tab.IsPinned;
            SortTabsByGroup();
            UpdateDynamicTabWidths();
            SaveCurrentSessionState();
        }
    }

    private void TabMenuSleep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is BrowserTabItem tab)
        {
            if (tab == _activeTab)
            {
                var other = _tabs.FirstOrDefault(t => t != tab);
                if (other is not null) SwitchToTab(other);
            }
            SleepTab(tab);
        }
    }

    private void TabMenuSplit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is BrowserTabItem tab) ToggleSplitScreen(tab);
    }

    private async void TabMenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is BrowserTabItem tab)
        {
            await CreateTabAsync(tab.Url, tab.GroupId, tab.IsIncognito, true);
        }
    }

    private void TabMenuClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is BrowserTabItem tab) CloseTab(tab);
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        var core = _activeTab.WebView?.CoreWebView2;
        if (core is not null && core.CanGoBack) core.GoBack();
        else if (!_activeTab.IsHome) NavigateActiveTab("homepage");
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        var core = _activeTab?.WebView?.CoreWebView2;
        if (core is not null && core.CanGoForward)
        {
            if (_activeTab!.IsHome)
            {
                _activeTab.IsHome = false;
                RefreshWebViewHosts();
            }
            core.GoForward();
        }
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null && !_activeTab.IsHome) _activeTab.WebView?.CoreWebView2?.Reload();
    }

    private void BtnHome_Click(object sender, RoutedEventArgs e) => NavigateActiveTab("homepage");
    private void BtnGo_Click(object sender, RoutedEventArgs e) => NavigateActiveTab(UrlTextBox.Text);

    private void UrlTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateActiveTab(UrlTextBox.Text);
        }
        else if (e.Key == Key.Down && SuggestionsPopup.IsOpen && SuggestionsListBox.Items.Count > 0)
        {
            e.Handled = true;
            var next = SuggestionsListBox.SelectedIndex + 1;
            if (next >= SuggestionsListBox.Items.Count) next = 0;
            SuggestionsListBox.SelectedIndex = next;
            if (SuggestionsListBox.SelectedItem is string val)
            {
                SuggestionsListBox.ScrollIntoView(val);
                SetAddressBoxText(val);
                UrlTextBox.CaretIndex = val.Length;
            }
        }
        else if (e.Key == Key.Up && SuggestionsPopup.IsOpen && SuggestionsListBox.Items.Count > 0)
        {
            e.Handled = true;
            var prev = SuggestionsListBox.SelectedIndex - 1;
            if (prev < 0) prev = SuggestionsListBox.Items.Count - 1;
            SuggestionsListBox.SelectedIndex = prev;
            if (SuggestionsListBox.SelectedItem is string val)
            {
                SuggestionsListBox.ScrollIntoView(val);
                SetAddressBoxText(val);
                UrlTextBox.CaretIndex = val.Length;
            }
        }
        else if (e.Key == Key.Tab && SuggestionsPopup.IsOpen && SuggestionsListBox.Items.Count > 0)
        {
            e.Handled = true;
            var first = (SuggestionsListBox.SelectedItem ?? SuggestionsListBox.Items[0])?.ToString() ?? string.Empty;
            SetAddressBoxText(first);
            UrlTextBox.CaretIndex = first.Length;
            SuggestionsPopup.IsOpen = false;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestionsPopup.IsOpen = false;
        }
    }

    private void UrlTextBox_GotFocus(object sender, RoutedEventArgs e) => UrlTextBox.SelectAll();

    private void BtnHomeSearch_Click(object sender, RoutedEventArgs e)
    {
        HomeSuggestionsPopup.IsOpen = false;
        NavigateActiveTab(HomeSearchBox.Text);
        HomeSearchBox.Clear();
    }

    private void HomeSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            HomeSuggestionsPopup.IsOpen = false;
            NavigateActiveTab(HomeSearchBox.Text);
            HomeSearchBox.Clear();
        }
        else if (e.Key == Key.Down && HomeSuggestionsPopup.IsOpen && HomeSuggestionsListBox.Items.Count > 0)
        {
            e.Handled = true;
            var next = HomeSuggestionsListBox.SelectedIndex + 1;
            if (next >= HomeSuggestionsListBox.Items.Count) next = 0;
            HomeSuggestionsListBox.SelectedIndex = next;
            if (HomeSuggestionsListBox.SelectedItem is string val)
            {
                HomeSuggestionsListBox.ScrollIntoView(val);
                SetHomeSearchBoxText(val);
                HomeSearchBox.CaretIndex = val.Length;
            }
        }
        else if (e.Key == Key.Up && HomeSuggestionsPopup.IsOpen && HomeSuggestionsListBox.Items.Count > 0)
        {
            e.Handled = true;
            var prev = HomeSuggestionsListBox.SelectedIndex - 1;
            if (prev < 0) prev = HomeSuggestionsListBox.Items.Count - 1;
            HomeSuggestionsListBox.SelectedIndex = prev;
            if (HomeSuggestionsListBox.SelectedItem is string val)
            {
                HomeSuggestionsListBox.ScrollIntoView(val);
                SetHomeSearchBoxText(val);
                HomeSearchBox.CaretIndex = val.Length;
            }
        }
        else if (e.Key == Key.Tab && HomeSuggestionsPopup.IsOpen && HomeSuggestionsListBox.Items.Count > 0)
        {
            e.Handled = true;
            var first = (HomeSuggestionsListBox.SelectedItem ?? HomeSuggestionsListBox.Items[0])?.ToString() ?? string.Empty;
            SetHomeSearchBoxText(first);
            HomeSearchBox.CaretIndex = first.Length;
            HomeSuggestionsPopup.IsOpen = false;
        }
        else if (e.Key == Key.Escape)
        {
            HomeSuggestionsPopup.IsOpen = false;
        }
    }

    private async void BtnSplitScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_splitTab is not null)
        {
            _splitTab.IsSplitTarget = false;
            _splitTab = null;
            RefreshWebViewHosts();
            return;
        }

        var candidate = _tabs.FirstOrDefault(t => t != _activeTab && !t.IsHome)
                        ?? _tabs.FirstOrDefault(t => t != _activeTab);
        if (candidate is null)
        {
            candidate = await CreateTabAsync("https://www.google.com", _activeTab?.GroupId ?? "general", false, switchTo: false);
        }
        ToggleSplitScreen(candidate);
    }

    private void ToggleSplitScreen(BrowserTabItem target)
    {
        if (_splitTab == target)
        {
            _splitTab.IsSplitTarget = false;
            _splitTab = null;
        }
        else
        {
            if (_splitTab is not null) _splitTab.IsSplitTarget = false;
            _splitTab = target;
            _splitTab.IsSplitTarget = true;
        }
        RefreshWebViewHosts();
    }

    private void BtnSnip_Click(object sender, RoutedEventArgs e) => ScreenCaptureService.StartRegionSnipWatcher(_ => { });

    private async void BtnMiniPip_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeTab?.WebView?.CoreWebView2 is null || _activeTab.IsHome)
            {
                MessageBox.Show("Mini Video (PiP) için önce bir video sayfası açın.", "Mini Video", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var raw = await _activeTab.WebView.CoreWebView2.ExecuteScriptAsync(WebViewPrivacyService.PictureInPictureScript);
            var result = raw.Trim('"');
            if (result == "no-video")
            {
                MessageBox.Show("Bu sayfada oynatılabilir bir video algılanamadı.", "Mini Video", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mini Video başlatılamadı: {ex.Message}", "Mini Video", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = FindBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (FindBar.Visibility == Visibility.Visible)
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        }
    }

    private void BtnFindClose_Click(object sender, RoutedEventArgs e) => FindBar.Visibility = Visibility.Collapsed;

    private async void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var backward = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            await ExecuteFindInPageAsync(!backward);
        }
        else if (e.Key == Key.Escape) FindBar.Visibility = Visibility.Collapsed;
    }

    private async void BtnFindNext_Click(object sender, RoutedEventArgs e) => await ExecuteFindInPageAsync(true);
    private async void BtnFindPrev_Click(object sender, RoutedEventArgs e) => await ExecuteFindInPageAsync(false);

    private async Task ExecuteFindInPageAsync(bool forward)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query) || _activeTab?.WebView?.CoreWebView2 is null || _activeTab.IsHome) return;

        var escaped = query.Replace("\\", "\\\\").Replace("'", "\\'");
        var backwardsJs = forward ? "false" : "true";
        await _activeTab.WebView.CoreWebView2.ExecuteScriptAsync($"window.find('{escaped}', false, {backwardsJs}, true);");
    }

    private void AttachRichContextMenu(CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var env = core.Environment;
        var menuItems = args.MenuItems;
        int insertAt = 0;

        if (args.ContextMenuTarget.HasSelection && !string.IsNullOrWhiteSpace(args.ContextMenuTarget.SelectionText))
        {
            var selected = args.ContextMenuTarget.SelectionText.Trim();
            var preview = selected.Length > 24 ? selected[..24] + "..." : selected;

            var trItem = env.CreateContextMenuItem($"🌐 Türkçeye Çevir: \"{preview}\"", null, CoreWebView2ContextMenuItemKind.Command);
            trItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = TranslateAndShowAsync(selected, "tr"));
            menuItems.Insert(insertAt++, trItem);

            var enItem = env.CreateContextMenuItem($"🇬🇧 İngilizceye Çevir: \"{preview}\"", null, CoreWebView2ContextMenuItemKind.Command);
            enItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = TranslateAndShowAsync(selected, "en"));
            menuItems.Insert(insertAt++, enItem);

            var searchItem = env.CreateContextMenuItem($"🔍 Google'da Ara: \"{preview}\"", null, CoreWebView2ContextMenuItemKind.Command);
            searchItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = CreateTabAsync(selected, _activeTab?.GroupId ?? "general", _activeTab?.IsIncognito ?? false, true));
            menuItems.Insert(insertAt++, searchItem);

            var geminiItem = env.CreateContextMenuItem($"✨ Gemini'ye Sor", null, CoreWebView2ContextMenuItemKind.Command);
            geminiItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = AskGeminiTextAsync(selected));
            menuItems.Insert(insertAt++, geminiItem);

            menuItems.Insert(insertAt++, env.CreateContextMenuItem(string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
        }

        if (args.ContextMenuTarget.HasLinkUri && !string.IsNullOrWhiteSpace(args.ContextMenuTarget.LinkUri))
        {
            var linkUri = args.ContextMenuTarget.LinkUri;
            var openTabItem = env.CreateContextMenuItem("↗️ Bağlantıyı Yeni Sekmede Aç", null, CoreWebView2ContextMenuItemKind.Command);
            openTabItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = CreateTabAsync(linkUri, _activeTab?.GroupId ?? "general", _activeTab?.IsIncognito ?? false, true));
            menuItems.Insert(insertAt++, openTabItem);

            var openIncItem = env.CreateContextMenuItem("🕶️ Bağlantıyı Gizli Sekmede Aç", null, CoreWebView2ContextMenuItemKind.Command);
            openIncItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = CreateTabAsync(linkUri, "general", true, true));
            menuItems.Insert(insertAt++, openIncItem);

            menuItems.Insert(insertAt++, env.CreateContextMenuItem(string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
        }

        var pageTrItem = env.CreateContextMenuItem("🌐 Tüm Sayfayı Türkçeye Çevir", null, CoreWebView2ContextMenuItemKind.Command);
        pageTrItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var pageUrl = core.Source;
            if (!string.IsNullOrWhiteSpace(pageUrl) && pageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var translateUrl = $"https://translate.google.com/translate?sl=auto&tl=tr&u={Uri.EscapeDataString(pageUrl)}";
                _ = CreateTabAsync(translateUrl, _activeTab?.GroupId ?? "general", _activeTab?.IsIncognito ?? false, true);
            }
        });
        menuItems.Add(env.CreateContextMenuItem(string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
        menuItems.Add(pageTrItem);

        var readerItem = env.CreateContextMenuItem("📖 Okuma Modu", null, CoreWebView2ContextMenuItemKind.Command);
        readerItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => BtnReaderMode_Click(this, new RoutedEventArgs()));
        menuItems.Add(readerItem);

        var shotItem = env.CreateContextMenuItem("📷 Sayfa Ekran Görüntüsü", null, CoreWebView2ContextMenuItemKind.Command);
        shotItem.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => BtnScreenshot_Click(this, new RoutedEventArgs()));
        menuItems.Add(shotItem);
    }

    private async Task TranslateAndShowAsync(string text, string targetLang)
    {
        _lastTranslateSource = text;
        _currentTranslateTargetLang = targetLang;
        TranslateSourceBox.Text = text;
        TranslateResultBox.Text = "Çevriliyor...";
        TranslateTargetLabel.Text = targetLang == "tr" ? "Türkçe Çeviri:" : "İngilizce Çeviri:";
        WebAreaGrid.Visibility = Visibility.Hidden;
        TranslateModalOverlay.Visibility = Visibility.Visible;

        var res = await DashboardApiService.TranslateTextAsync(text, targetLang);
        if (res is null)
        {
            TranslateResultBox.Text = "Çeviri alınamadı. İnternet bağlantınızı kontrol edin.";
            return;
        }

        TranslateHeaderText.Text = $"🌐 Çeviri ({res.Value.DetectedLang.ToUpperInvariant()} → {targetLang.ToUpperInvariant()})";
        TranslateResultBox.Text = res.Value.TranslatedText;
    }

    private void PrintActiveTab()
    {
        if (_activeTab?.WebView?.CoreWebView2 is not null && !_activeTab.IsHome)
        {
            _activeTab.WebView.CoreWebView2.ShowPrintUI();
        }
    }

    // --- QR KOD ÜRETİCİ ---

    private void BtnQrCode_Click(object sender, RoutedEventArgs e)
    {
        var url = _activeTab is not null && !_activeTab.IsHome && !string.IsNullOrWhiteSpace(_activeTab.Url)
            ? _activeTab.Url
            : "https://www.google.com";

        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
        using var pngQr = new QRCoder.PngByteQRCode(data);
        _lastQrPngBytes = pngQr.GetGraphic(10);

        using var ms = new MemoryStream(_lastQrPngBytes);
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();

        QrCodeImage.Source = bmp;
        QrUrlText.Text = url;
        WebAreaGrid.Visibility = Visibility.Hidden;
        QrModalOverlay.Visibility = Visibility.Visible;
    }

    private void BtnCloseQrModal_Click(object sender, RoutedEventArgs e)
    {
        QrModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BtnSaveQrPng_Click(object sender, RoutedEventArgs e)
    {
        if (_lastQrPngBytes is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PNG Görseli (*.png)|*.png",
            FileName = $"QR_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllBytes(dlg.FileName, _lastQrPngBytes);
            ScreenCaptureService.RevealInExplorer(dlg.FileName);
        }
    }

    // --- SOL KAYAR / SABİTLENEBİLİR YAN UYGULAMA BARI ---

    private void UpdateSidebarPinnedState()
    {
        if (_sessionData.IsSidebarPinned)
        {
            SidebarRail.Visibility = Visibility.Visible;
            SidebarHoverTrigger.Visibility = Visibility.Collapsed;
            BtnPinSidebar.Content = "📌";
        }
        else
        {
            SidebarRail.Visibility = Visibility.Collapsed;
            SidebarHoverTrigger.Visibility = Visibility.Visible;
            BtnPinSidebar.Content = "📍";
        }
    }

    private void BtnPinSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sessionData.IsSidebarPinned = !_sessionData.IsSidebarPinned;
        UpdateSidebarPinnedState();
        SaveCurrentSessionState();
    }

    private void SidebarHoverTrigger_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_sessionData.IsSidebarPinned)
        {
            SidebarRail.Visibility = Visibility.Visible;
        }
    }

    private void SidebarArea_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_sessionData.IsSidebarPinned)
        {
            SidebarRail.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnSidebarApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SidebarAppItem app) return;

        if (SidebarWebDrawer.Visibility == Visibility.Visible &&
            string.Equals(_currentSidebarAppUrl, app.Url, StringComparison.OrdinalIgnoreCase))
        {
            SidebarWebDrawer.Visibility = Visibility.Collapsed;
            return;
        }

        _currentSidebarAppUrl = app.Url;
        SidebarDrawerTitleText.Text = $"{app.Icon} {app.Name}";
        SidebarWebDrawer.Visibility = Visibility.Visible;

        if (!_isSidebarWebInitialized)
        {
            _isSidebarWebInitialized = true;
            var env = _defaultEnv ?? await CoreWebView2Environment.CreateAsync(null, SessionStorageService.GetUserDataFolder(false));
            await SidebarWebView.EnsureCoreWebView2Async(env);
        }

        SidebarWebView.CoreWebView2?.Navigate(app.Url);
    }

    private void BtnCloseSidebarDrawer_Click(object sender, RoutedEventArgs e) => SidebarWebDrawer.Visibility = Visibility.Collapsed;
    private void BtnReloadSidebarDrawer_Click(object sender, RoutedEventArgs e) => SidebarWebView.CoreWebView2?.Reload();

    private async void SidebarMenuOpenTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is SidebarAppItem app)
        {
            await CreateTabAsync(app.Url, _activeTab?.GroupId ?? "general", false, true);
        }
    }

    private void SidebarMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is SidebarAppItem app)
        {
            _sidebarApps.Remove(app);
            SaveCurrentSessionState();
        }
    }

    private void BtnOpenAddSidebarApp_Click(object sender, RoutedEventArgs e)
    {
        SidebarAppIconBox.Text = "🌐";
        SidebarAppNameBox.Clear();
        SidebarAppUrlBox.Clear();
        WebAreaGrid.Visibility = Visibility.Hidden;
        AddSidebarAppModalOverlay.Visibility = Visibility.Visible;
        SidebarAppNameBox.Focus();
    }

    private void BtnCloseAddSidebarApp_Click(object sender, RoutedEventArgs e)
    {
        AddSidebarAppModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BtnSaveSidebarApp_Click(object sender, RoutedEventArgs e)
    {
        var icon = string.IsNullOrWhiteSpace(SidebarAppIconBox.Text) ? "🌐" : SidebarAppIconBox.Text.Trim();
        var name = SidebarAppNameBox.Text.Trim();
        var url = SidebarAppUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
        {
            MessageBox.Show("Uygulama adı ve URL adresi gereklidir.", "Yan Bar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newApp = new SidebarAppItem { Icon = icon, Name = name, Url = NormalizeUrl(url) };
        _sidebarApps.Add(newApp);
        AddSidebarAppModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
        SaveCurrentSessionState();
        _ = LoadSidebarFaviconsAsync();
    }

    // --- MASAÜSTÜ & BAŞLAT MENÜSÜ KISAYOLU ---

    private void BtnCreateShortcuts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Özel Tarayıcı.lnk");
            var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            Directory.CreateDirectory(startMenuDir);
            var startMenuLnk = Path.Combine(startMenuDir, "Özel Tarayıcı.lnk");

            CreateWindowsShortcut(desktopLnk, exePath);
            CreateWindowsShortcut(startMenuLnk, exePath);

            MessageBox.Show(
                "Masaüstü ve Başlat Menüsü kısayolları oluşturuldu!",
                "Kurulum / Kısayol",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Kısayol oluşturulamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void CreateWindowsShortcut(string shortcutPath, string targetExePath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null) return;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetExePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath);
        shortcut.IconLocation = targetExePath;
        shortcut.Description = "Özel Tarayıcı";
        shortcut.Save();
    }

    private void BtnCloseTranslateModal_Click(object sender, RoutedEventArgs e)
    {
        TranslateModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BtnCopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TranslateResultBox.Text))
        {
            Clipboard.SetText(TranslateResultBox.Text);
        }
    }

    private async void BtnSwapTranslateLang_Click(object sender, RoutedEventArgs e)
    {
        var nextLang = _currentTranslateTargetLang == "tr" ? "en" : "tr";
        await TranslateAndShowAsync(_lastTranslateSource, nextLang);
    }

    private async Task AskGeminiTextAsync(string text)
    {
        var prompt = $"Şu metni Türkçe olarak açıkla ve özetle:\n\n{text}";
        await EnsureGeminiPanelOpenAsync();
        var promptJson = JsonSerializer.Serialize(prompt);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (GeminiWebView.CoreWebView2 is not null)
            {
                var injectScript = $$"""
                    (() => {
                      const editor = document.querySelector('rich-textarea .ql-editor, div[contenteditable="true"][role="textbox"], div[contenteditable="true"], textarea');
                      if (!editor) return 'not-ready';
                      editor.focus();
                      const sel = window.getSelection();
                      const range = document.createRange();
                      range.selectNodeContents(editor);
                      sel.removeAllRanges();
                      sel.addRange(range);
                      document.execCommand('insertText', false, {{promptJson}});
                      setTimeout(() => {
                        const sendBtn = document.querySelector('button[aria-label*="Gönder"], button[aria-label*="Send"], button.send-button, button[data-test-id="send-button"]');
                        if (sendBtn && !sendBtn.disabled) sendBtn.click();
                      }, 220);
                      return 'ok';
                    })();
                    """;
                var res = await GeminiWebView.CoreWebView2.ExecuteScriptAsync(injectScript);
                if (res.Contains("ok", StringComparison.Ordinal)) break;
            }
            await Task.Delay(450);
        }
    }

    private void BtnDownloads_Click(object sender, RoutedEventArgs e) => OpenHistoryDownloadsDrawer(showDownloads: true);

    private void BtnHistory_Click(object sender, RoutedEventArgs e) => OpenHistoryDownloadsDrawer(showDownloads: false);

    private void OpenHistoryDownloadsDrawer(bool showDownloads)
    {
        if (HistoryDownloadsDrawer.Visibility == Visibility.Visible &&
            ((showDownloads && DownloadsPanel.Visibility == Visibility.Visible) ||
             (!showDownloads && HistoryPanel.Visibility == Visibility.Visible)))
        {
            HistoryDownloadsDrawer.Visibility = Visibility.Collapsed;
            return;
        }

        SettingsDrawer.Visibility = Visibility.Collapsed;
        GeminiDrawer.Visibility = Visibility.Collapsed;
        HistoryDownloadsDrawer.Visibility = Visibility.Visible;
        if (showDownloads) BtnSwitchToDownloadsTab_Click(this, new RoutedEventArgs());
        else BtnSwitchToHistoryTab_Click(this, new RoutedEventArgs());
    }

    private void BtnCloseHistoryDrawer_Click(object sender, RoutedEventArgs e) => HistoryDownloadsDrawer.Visibility = Visibility.Collapsed;

    private void BtnSwitchToHistoryTab_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Visible;
        DownloadsPanel.Visibility = Visibility.Collapsed;
        RefreshFilteredHistory();
    }

    private void BtnSwitchToDownloadsTab_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
        DownloadsPanel.Visibility = Visibility.Visible;
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilteredHistory();

    private async void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is HistoryEntry entry)
        {
            HistoryDownloadsDrawer.Visibility = Visibility.Collapsed;
            await CreateTabAsync(entry.Url, _activeTab?.GroupId ?? "general", false, true);
        }
    }

    private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _sessionData.History.Clear();
        RefreshFilteredHistory();
        SaveCurrentSessionState();
    }

    private void BtnOpenDownloadsRoot_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(SessionStorageService.GetDownloadsFolder()) { UseShellExecute = true });

    private void BtnRevealDownloadItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadRecord rec)
        {
            ScreenCaptureService.RevealInExplorer(rec.SavePath);
        }
    }

    private void BtnClearDownloadsList_Click(object sender, RoutedEventArgs e)
    {
        _sessionData.Downloads.Clear();
        _downloads.Clear();
        SaveCurrentSessionState();
    }

    // --- KOMUT PALETİ (Ctrl+K) ---

    private void BtnCommandPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OpenCommandPalette()
    {
        CmdPaletteSearchBox.Clear();
        BuildCommandPaletteResults(string.Empty);
        WebAreaGrid.Visibility = Visibility.Hidden;
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        CmdPaletteSearchBox.Focus();
    }

    private void BtnCloseCommandPalette_Click(object sender, RoutedEventArgs e)
    {
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BuildCommandPaletteResults(string query)
    {
        var all = new List<CommandPaletteEntry>
        {
            new("➕ Yeni Sekme Aç (Ctrl+T)", () => _ = CreateTabAsync("homepage", _activeTab?.GroupId ?? "general", false, true)),
            new("🕶️ Gizli Sekme Aç", () => _ = CreateTabAsync("homepage", "general", true, true)),
            new("📜 Tarama Geçmişini Aç (Ctrl+H)", () => OpenHistoryDownloadsDrawer(false)),
            new("📥 İndirilenleri Aç (Ctrl+J)", () => OpenHistoryDownloadsDrawer(true)),
            new("🌓 Tema Değiştir (Koyu / Açık)", () => ApplyTheme(!_sessionData.IsDarkTheme)),
            new("⚙️ Ayarlar Çekmecesini Aç", () => BtnSettings_Click(this, new RoutedEventArgs())),
            new("✨ Gemini Asistan Panelini Aç", () => _ = EnsureGeminiPanelOpenAsync())
        };

        foreach (var tab in _tabs)
        {
            var t = tab;
            all.Add(new CommandPaletteEntry($"🪟 Sekmeye Git: {t.Title}", () => SwitchToTab(t)));
        }
        foreach (var bm in _bookmarks)
        {
            var b = bm;
            all.Add(new CommandPaletteEntry($"🔖 Yer İmi: {b.Title} ({b.Url})", () => NavigateActiveTab(b.Url)));
        }
        foreach (var h in _sessionData.History.Take(30))
        {
            var entry = h;
            all.Add(new CommandPaletteEntry($"📜 Geçmiş: {entry.Title}", () => _ = CreateTabAsync(entry.Url, _activeTab?.GroupId ?? "general", false, true)));
        }

        _commandEntries = string.IsNullOrWhiteSpace(query)
            ? all.Take(20).ToList()
            : all.Where(x => x.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();

        CmdPaletteResultsList.ItemsSource = _commandEntries;
        if (_commandEntries.Count > 0) CmdPaletteResultsList.SelectedIndex = 0;
    }

    private void CmdPaletteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => BuildCommandPaletteResults(CmdPaletteSearchBox.Text.Trim());

    private void CmdPaletteSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && CmdPaletteResultsList.Items.Count > 0)
        {
            e.Handled = true;
            var next = (CmdPaletteResultsList.SelectedIndex + 1) % CmdPaletteResultsList.Items.Count;
            CmdPaletteResultsList.SelectedIndex = next;
            CmdPaletteResultsList.ScrollIntoView(CmdPaletteResultsList.SelectedItem);
        }
        else if (e.Key == Key.Up && CmdPaletteResultsList.Items.Count > 0)
        {
            e.Handled = true;
            var prev = CmdPaletteResultsList.SelectedIndex - 1;
            if (prev < 0) prev = CmdPaletteResultsList.Items.Count - 1;
            CmdPaletteResultsList.SelectedIndex = prev;
            CmdPaletteResultsList.ScrollIntoView(CmdPaletteResultsList.SelectedItem);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ExecuteSelectedCommandPaletteEntry();
        }
    }

    private void CmdPaletteResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => ExecuteSelectedCommandPaletteEntry();

    private void ExecuteSelectedCommandPaletteEntry()
    {
        if (CmdPaletteResultsList.SelectedItem is CommandPaletteEntry entry)
        {
            CommandPaletteOverlay.Visibility = Visibility.Collapsed;
            TranslateModalOverlay.Visibility = Visibility.Collapsed;
            QrModalOverlay.Visibility = Visibility.Collapsed;
            AddSidebarAppModalOverlay.Visibility = Visibility.Collapsed;
            EditNoteModalOverlay.Visibility = Visibility.Collapsed;
            WebAreaGrid.Visibility = Visibility.Visible;
            entry.Execute();
        }
    }

    private void BtnOpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastDownloadPath)) ScreenCaptureService.RevealInExplorer(_lastDownloadPath);
    }

    private void BtnCloseDownloadPill_Click(object sender, RoutedEventArgs e) => DownloadPill.Visibility = Visibility.Collapsed;

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var open = SettingsDrawer.Visibility != Visibility.Visible;
        SettingsDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            MemoryUsageText.Text = $"{GetTotalBrowserMemoryMb()} MB ({_tabs.Count} sekme)";
        }
    }

    private void BtnToggleTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_sessionData.IsDarkTheme);

    private async void BtnReaderMode_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2 is not null && !_activeTab.IsHome)
        {
            await _activeTab.WebView.CoreWebView2.ExecuteScriptAsync(WebViewPrivacyService.ReaderModeScript);
        }
    }

    private async void BtnScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView is not null && !_activeTab.IsHome)
        {
            await ScreenCaptureService.CaptureWebViewAsync(_activeTab.WebView);
        }
        else
        {
            MessageBox.Show("Ekran görüntüsü almak için bir web sayfası açın.", "Ekran Görüntüsü", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnNewIncognitoTab_Click(object sender, RoutedEventArgs e)
    {
        SettingsDrawer.Visibility = Visibility.Collapsed;
        await CreateTabAsync("homepage", "general", isIncognito: true, switchTo: true);
    }

    private void PrivacyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingSession) return;
        SaveCurrentSessionState();
    }

    private void ChkRunOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingSession) return;
        var enable = ChkRunOnStartup.IsChecked == true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is not null)
            {
                if (enable)
                {
                    var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath)) key.SetValue("OzelTarayici", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("OzelTarayici", throwOnMissingValue: false);
                }
            }
        }
        catch { }
        SaveCurrentSessionState();
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private long GetTotalBrowserMemoryMb()
    {
        long totalBytes = Process.GetCurrentProcess().WorkingSet64;
        try
        {
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                totalBytes += p.WorkingSet64;
            }
        }
        catch { }
        return totalBytes / (1024 * 1024);
    }

    private async void BtnOptimizeRam_Click(object sender, RoutedEventArgs e)
    {
        var beforeMb = GetTotalBrowserMemoryMb();
        int sleptCount = 0;

        foreach (var tab in _tabs.ToList())
        {
            if (tab == _activeTab || tab == _splitTab || tab.IsPinned || tab.IsHome || tab.IsSleeping || tab.IsPlayingAudio)
                continue;
            SleepTab(tab);
            sleptCount++;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        try
        {
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                try { EmptyWorkingSet(p.Handle); } catch { }
            }
        }
        catch { }

        await Task.Delay(250);
        var afterMb = GetTotalBrowserMemoryMb();
        var savedMb = Math.Max(0, beforeMb - afterMb);

        MemoryUsageText.Text = $"{afterMb} MB ({_tabs.Count} sekme)";
        MemoryCleanResultText.Text = $"✅ {sleptCount} arka plan sekmesi uyutuldu • {savedMb} MB bellek boşaltıldı ({beforeMb} MB → {afterMb} MB)";
        MemoryCleanResultText.Visibility = Visibility.Visible;
    }

    private async void BtnClearHistoryRange_Click(object sender, RoutedEventArgs e)
    {
        var rangeTag = (ClearHistoryRangeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var now = DateTime.Now;
        DateTime cutoff = rangeTag switch
        {
            "1h" => now.AddHours(-1),
            "1d" => now.AddDays(-1),
            "7d" => now.AddDays(-7),
            _ => DateTime.MinValue
        };

        int removedCount;
        if (rangeTag == "all")
        {
            removedCount = _sessionData.History.Count;
            _sessionData.History.Clear();
        }
        else
        {
            removedCount = _sessionData.History.RemoveAll(h => h.VisitedAt >= cutoff);
        }

        RefreshFilteredHistory();
        SaveCurrentSessionState();

        var webViewTarget = _tabs.FirstOrDefault(t => t.WebView?.CoreWebView2 is not null)?.WebView?.CoreWebView2;
        if (webViewTarget is not null)
        {
            try
            {
                const CoreWebView2BrowsingDataKinds kinds =
                    CoreWebView2BrowsingDataKinds.BrowsingHistory |
                    CoreWebView2BrowsingDataKinds.DownloadHistory;

                if (rangeTag == "all")
                {
                    await webViewTarget.Profile.ClearBrowsingDataAsync(kinds);
                }
                else
                {
                    await webViewTarget.Profile.ClearBrowsingDataAsync(kinds, cutoff.ToUniversalTime(), DateTime.UtcNow);
                }
            }
            catch { }
        }

        var label = (ClearHistoryRangeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Seçili aralık";
        MessageBox.Show(
            $"🧹 {label} için geçmiş temizlendi.\nSilinen kayıt sayısı: {removedCount}",
            "Geçmiş Temizlendi",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void BtnClearData_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2 is not null)
        {
            await _activeTab.WebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            MessageBox.Show("Tarama geçmişi, önbellek ve çerezler temizlendi.", "Gizlilik", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task LoadSidebarFaviconsAsync()
    {
        foreach (var app in _sidebarApps.ToList())
        {
            if (app.FaviconSource is not null) continue;
            var icon = await DashboardApiService.FetchSiteFaviconAsync(app.Url);
            if (icon is not null)
            {
                app.FaviconSource = icon;
            }
        }
    }

    private void BtnPickGroupColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            _selectedGroupColorHex = hex;
            SelectedGroupColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }

    private void BtnCreateGroup_Click(object sender, RoutedEventArgs e)
    {
        _editingGroup = null;
        GroupModalTitleText.Text = "Yeni Çalışma Grubu";
        BtnSaveGroup.Content = "Grubu Oluştur";
        GroupNameTextBox.Clear();
        _selectedGroupColorHex = "#00B37E";
        SelectedGroupColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedGroupColorHex));
        WebAreaGrid.Visibility = Visibility.Hidden;
        GroupModalOverlay.Visibility = Visibility.Visible;
        GroupNameTextBox.Focus();
    }

    private void GroupMenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not TabGroup group) return;
        _editingGroup = group;
        GroupModalTitleText.Text = "Grubu Düzenle";
        BtnSaveGroup.Content = "Değişiklikleri Kaydet";
        GroupNameTextBox.Text = group.Name;
        _selectedGroupColorHex = group.ColorHex;
        SelectedGroupColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedGroupColorHex));

        WebAreaGrid.Visibility = Visibility.Hidden;
        GroupModalOverlay.Visibility = Visibility.Visible;
        GroupNameTextBox.Focus();
    }

    private void BtnCloseGroupModal_Click(object sender, RoutedEventArgs e)
    {
        _editingGroup = null;
        GroupModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private void BtnSaveGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = GroupNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var color = _selectedGroupColorHex;

        if (_editingGroup is not null)
        {
            _editingGroup.Name = name;
            _editingGroup.ColorHex = color;
            foreach (var tab in _editingGroup.Tabs)
            {
                tab.GroupColorHex = color;
            }
            _editingGroup = null;
            RefreshTabBarVisibility();
            SaveCurrentSessionState();
        }
        else
        {
            var group = new TabGroup { Name = name, ColorHex = color };
            _groups.Add(group);

            if (_activeTab is not null && !_activeTab.IsIncognito)
            {
                MoveTabToGroup(_activeTab, group);
            }
            else
            {
                RefreshTabBarVisibility();
                SaveCurrentSessionState();
            }
        }

        GroupModalOverlay.Visibility = Visibility.Collapsed;
        WebAreaGrid.Visibility = Visibility.Visible;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl && !shift && e.Key == Key.T)
        {
            e.Handled = true;
            await CreateTabAsync("homepage", _activeTab?.GroupId ?? "general", false, true);
        }
        else if (ctrl && shift && e.Key == Key.T)
        {
            e.Handled = true;
            BtnRestoreTab_Click(sender, e);
        }
        else if (ctrl && e.Key == Key.W)
        {
            e.Handled = true;
            if (_activeTab is not null) CloseTab(_activeTab);
        }
        else if ((ctrl && e.Key == Key.R) || e.Key == Key.F5)
        {
            e.Handled = true;
            BtnReload_Click(sender, e);
        }
        else if (ctrl && e.Key == Key.L)
        {
            e.Handled = true;
            UrlTextBox.Focus();
            UrlTextBox.SelectAll();
        }
        else if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            BtnFind_Click(sender, e);
        }
        else if (ctrl && e.Key == Key.H)
        {
            e.Handled = true;
            OpenHistoryDownloadsDrawer(showDownloads: false);
        }
        else if (ctrl && e.Key == Key.J)
        {
            e.Handled = true;
            OpenHistoryDownloadsDrawer(showDownloads: true);
        }
        else if (ctrl && e.Key == Key.K)
        {
            e.Handled = true;
            OpenCommandPalette();
        }
        else if (ctrl && e.Key == Key.P)
        {
            e.Handled = true;
            PrintActiveTab();
        }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            e.Handled = true;
            if (_activeTab?.WebView is not null) _activeTab.WebView.ZoomFactor = Math.Min(3.0, _activeTab.WebView.ZoomFactor + 0.1);
        }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            e.Handled = true;
            if (_activeTab?.WebView is not null) _activeTab.WebView.ZoomFactor = Math.Max(0.3, _activeTab.WebView.ZoomFactor - 0.1);
        }
        else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            e.Handled = true;
            if (_activeTab?.WebView is not null) _activeTab.WebView.ZoomFactor = 1.0;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestionsPopup.IsOpen = false;
            FindBar.Visibility = Visibility.Collapsed;
            SettingsDrawer.Visibility = Visibility.Collapsed;
            HistoryDownloadsDrawer.Visibility = Visibility.Collapsed;
            GroupModalOverlay.Visibility = Visibility.Collapsed;
            LocationModalOverlay.Visibility = Visibility.Collapsed;
            MasterPasswordModalOverlay.Visibility = Visibility.Collapsed;
            EditPasswordModalOverlay.Visibility = Visibility.Collapsed;
            CommandPaletteOverlay.Visibility = Visibility.Collapsed;
            TranslateModalOverlay.Visibility = Visibility.Collapsed;
            QrModalOverlay.Visibility = Visibility.Collapsed;
            AddSidebarAppModalOverlay.Visibility = Visibility.Collapsed;
            EditNoteModalOverlay.Visibility = Visibility.Collapsed;
            WebAreaGrid.Visibility = Visibility.Visible;
        }
    }

    private void SaveCurrentSessionState()
    {
        _sessionData.Groups = _groups.ToList();
        _sessionData.Bookmarks = _bookmarks.ToList();
        _sessionData.Reminders = _reminders.ToList();
        _sessionData.StickyNotes = _notes.ToList();
        _sessionData.SidebarApps = _sidebarApps.ToList();
        _sessionData.RememberTabs = ChkRememberTabs.IsChecked == true;
        _sessionData.IsAdBlockEnabled = ChkAdBlock.IsChecked == true;
        _sessionData.IsAntiFingerprintEnabled = ChkFingerprint.IsChecked == true;
        _sessionData.BlockAllCookies = ChkBlockCookies.IsChecked == true;
        _sessionData.AutoSleepInactiveTabs = ChkAutoSleepTabs.IsChecked == true;
        _sessionData.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked == true;
        _sessionData.RunOnWindowsStartup = ChkRunOnStartup.IsChecked == true;

        if (_sessionData.RememberTabs)
        {
            var normalTabs = _tabs.Where(t => !t.IsIncognito).ToList();
            _sessionData.Tabs = normalTabs
                .Select(t => new SavedTabState
                {
                    Url = t.Url,
                    Title = t.Title,
                    IsHome = t.IsHome,
                    IsPinned = t.IsPinned,
                    GroupId = t.GroupId
                })
                .ToList();
            _sessionData.ActiveTabIndex = _activeTab is not null && !_activeTab.IsIncognito
                ? Math.Max(0, normalTabs.IndexOf(_activeTab))
                : 0;
        }
        else
        {
            _sessionData.Tabs.Clear();
        }

        SessionStorageService.Save(_sessionData);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_forceExit && ChkMinimizeToTray.IsChecked == true)
        {
            e.Cancel = true;
            SaveCurrentSessionState();
            Hide();
            return;
        }

        System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;
        _clockTimer.Stop();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        SaveCurrentSessionState();
        SessionStorageService.CleanupIncognitoFolder();
    }
}
