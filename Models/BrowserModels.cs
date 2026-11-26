using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace OzelTarayici.Models;

public sealed class TabGroup : INotifyPropertyChanged
{
    private string _name = "Genel";
    private string _colorHex = "#00B37E";
    private bool _isCollapsed;
    private int _tabCount;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }

    public string ColorHex
    {
        get => _colorHex;
        set { _colorHex = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorHex))); }
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { _isCollapsed = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed))); }
    }

    [JsonIgnore]
    public int TabCount
    {
        get => _tabCount;
        set { _tabCount = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabCount))); }
    }

    [JsonIgnore]
    public ObservableCollection<BrowserTabItem> Tabs { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BrowserTabItem : INotifyPropertyChanged
{
    private string _title = "Ana Sayfa";
    private string _url = "homepage";
    private bool _isHome = true;
    private bool _isLoading;
    private bool _isMuted;
    private bool _isPlayingAudio;
    private bool _isPinned;
    private bool _isSleeping;
    private bool _isSelected;
    private bool _isVisibleInTabBar = true;
    private bool _isDragging;
    private bool _isDragTarget;
    private double _tabWidth = 175;
    private string _groupId = "general";
    private string _groupColorHex = "#00B37E";
    private bool _canGoBack;
    private bool _canGoForward;
    private ImageSource? _faviconSource;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public bool IsIncognito { get; set; }

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(FullTooltip)); }
    }

    public string Url
    {
        get => _url;
        set { _url = value; OnPropertyChanged(); OnPropertyChanged(nameof(FullTooltip)); }
    }

    public bool IsHome
    {
        get => _isHome;
        set { _isHome = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAudioEqualizer)); }
    }

    [JsonIgnore]
    public bool IsPlayingAudio
    {
        get => _isPlayingAudio;
        set { _isPlayingAudio = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAudioEqualizer)); }
    }

    [JsonIgnore]
    public bool ShowAudioEqualizer => _isPlayingAudio && !_isMuted;

    [JsonIgnore]
    public ImageSource? FaviconSource
    {
        get => _faviconSource;
        set { _faviconSource = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string FullTooltip => IsHome ? Title : $"{Title}\n{Url}";

    public bool IsPinned
    {
        get => _isPinned;
        set { _isPinned = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowCloseButton)); OnPropertyChanged(nameof(EffectiveTabWidth)); }
    }

    [JsonIgnore]
    public bool IsSleeping
    {
        get => _isSleeping;
        set { _isSleeping = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public bool ShowCloseButton => !_isPinned;

    [JsonIgnore]
    public double EffectiveTabWidth => _isPinned ? 44 : _tabWidth;

    [JsonIgnore]
    public DateTime LastActiveAt { get; set; } = DateTime.Now;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsVisibleInTabBar
    {
        get => _isVisibleInTabBar;
        set { _isVisibleInTabBar = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public double TabWidth
    {
        get => _tabWidth;
        set { _tabWidth = value; OnPropertyChanged(); OnPropertyChanged(nameof(EffectiveTabWidth)); }
    }

    [JsonIgnore]
    public bool IsDragging
    {
        get => _isDragging;
        set { _isDragging = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set { _isDragTarget = value; OnPropertyChanged(); }
    }

    public string GroupId
    {
        get => _groupId;
        set { _groupId = value; OnPropertyChanged(); }
    }

    public string GroupColorHex
    {
        get => _groupColorHex;
        set { _groupColorHex = value; OnPropertyChanged(); }
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set { _canGoBack = value; OnPropertyChanged(); }
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set { _canGoForward = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public WebView2? WebView { get; set; }

    [JsonIgnore]
    public Services.WebViewPrivacyController? PrivacyController { get; set; }

    [JsonIgnore]
    public bool IsPdfDocument { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SavedTabState
{
    public string Url { get; set; } = "homepage";
    public string Title { get; set; } = "Ana Sayfa";
    public bool IsHome { get; set; } = true;
    public bool IsPinned { get; set; }
    public string GroupId { get; set; } = "general";
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; } = DateTime.Now;
}

public sealed class DownloadRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FileName { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public DateTime DownloadedAt { get; set; } = DateTime.Now;
    public string State { get; set; } = "Tamamlandı";
}

public sealed class BookmarkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class ReminderItem : INotifyPropertyChanged
{
    private DateTime _dueTime = DateTime.Now.AddHours(1);
    private bool _isTriggered;
    private bool _isCompleted;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = string.Empty;

    public DateTime DueTime
    {
        get => _dueTime;
        set { _dueTime = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DueTime))); }
    }

    public bool IsTriggered
    {
        get => _isTriggered;
        set { _isTriggered = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTriggered))); }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set { _isCompleted = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class StickyNoteItem : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private string _colorHex = "#FEF08A";
    private bool _isDragging;
    private bool _isDragTarget;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Text
    {
        get => _text;
        set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }

    public string ColorHex
    {
        get => _colorHex;
        set { _colorHex = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorHex))); }
    }

    [JsonIgnore]
    public bool IsDragging
    {
        get => _isDragging;
        set { _isDragging = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragging))); }
    }

    [JsonIgnore]
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set { _isDragTarget = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragTarget))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SavedPasswordItem : INotifyPropertyChanged
{
    private string _site = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _isPasswordVisible;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Site
    {
        get => _site;
        set { _site = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Site))); }
    }

    public string Username
    {
        get => _username;
        set { _username = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Username))); }
    }

    public string EncryptedPassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password
    {
        get => _password;
        set { _password = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Password))); }
    }

    [JsonIgnore]
    public bool IsPasswordVisible
    {
        get => _isPasswordVisible;
        set { _isPasswordVisible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPasswordVisible))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LocationConfig
{
    public string DisplayName { get; set; } = "İstanbul, Kadıköy";
    public double Latitude { get; set; } = 40.9919;
    public double Longitude { get; set; } = 29.0275;
}

public sealed class SidebarAppItem : INotifyPropertyChanged
{
    private ImageSource? _faviconSource;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Icon { get; set; } = "🌐";
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    [JsonIgnore]
    public ImageSource? FaviconSource
    {
        get => _faviconSource;
        set { _faviconSource = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FaviconSource))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFavicon))); }
    }

    [JsonIgnore]
    public bool HasFavicon => _faviconSource is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AppSessionData
{
    public List<TabGroup> Groups { get; set; } = new()
    {
        new TabGroup { Id = "general", Name = "Genel", ColorHex = "#00B37E" }
    };

    public List<SavedTabState> Tabs { get; set; } = new();
    public List<BookmarkItem> Bookmarks { get; set; } = new()
    {
        new BookmarkItem { Title = "Google", Url = "https://www.google.com" },
        new BookmarkItem { Title = "YouTube", Url = "https://www.youtube.com" },
        new BookmarkItem { Title = "Gemini", Url = "https://gemini.google.com" },
        new BookmarkItem { Title = "GitHub", Url = "https://github.com" }
    };
    public List<ReminderItem> Reminders { get; set; } = new();
    public List<StickyNoteItem> StickyNotes { get; set; } = new()
    {
        new StickyNoteItem { Text = "Özel Tarayıcı'ya hoş geldiniz! Notlarınızı buraya ekleyebilirsiniz." }
    };
    public List<SavedPasswordItem> SavedPasswords { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public List<DownloadRecord> Downloads { get; set; } = new();
    public List<SidebarAppItem> SidebarApps { get; set; } = new()
    {
        new SidebarAppItem { Icon = "💬", Name = "WhatsApp", Url = "https://web.whatsapp.com" },
        new SidebarAppItem { Icon = "✈️", Name = "Telegram", Url = "https://web.telegram.org" },
        new SidebarAppItem { Icon = "🎵", Name = "Spotify", Url = "https://open.spotify.com" }
    };
    public LocationConfig Location { get; set; } = new();

    public int ActiveTabIndex { get; set; }
    public bool IsDarkTheme { get; set; } = true;
    public bool IsAdBlockEnabled { get; set; } = true;
    public bool BlockAllCookies { get; set; }
    public bool IsAntiFingerprintEnabled { get; set; } = true;
    public List<string> CompatibleDomains { get; set; } = new();
    public bool RememberTabs { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; }
    public bool AutoSleepInactiveTabs { get; set; } = true;
    public bool RunOnWindowsStartup { get; set; }
    public bool IsSidebarPinned { get; set; } = true;
}
