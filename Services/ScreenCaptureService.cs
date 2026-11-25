using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OzelTarayici.Services;

public static class ScreenCaptureService
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static DispatcherTimer? _activeSnipTimer;
    private static EventHandler? _activeSnipHandler;

    public static async Task<string?> CaptureWebViewAsync(WebView2 webView)
    {
        if (webView.CoreWebView2 is null) return null;

        var downloads = SessionStorageService.GetDownloadsFolder();
        var filePath = Path.Combine(downloads, $"ekran-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.png");

        await using var fs = File.Create(filePath);
        await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);

        RevealInExplorer(filePath);
        return filePath;
    }

    public static void StartRegionSnipWatcher(Action<string> onSaved)
    {
        StopActiveSnipWatcher();
        var initialSeq = GetClipboardSequenceNumber();

        Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });

        var elapsedMs = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            elapsedMs += 400;
            try
            {
                var currentSeq = GetClipboardSequenceNumber();
                if (currentSeq != initialSeq && Clipboard.ContainsImage())
                {
                    var current = Clipboard.GetImage();
                    if (current is not null)
                    {
                        StopActiveSnipWatcher();
                        var downloads = SessionStorageService.GetDownloadsFolder();
                        var savePath = Path.Combine(downloads, $"kirpma-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.png");
                        SaveBitmapAsPng(current, savePath);
                        RevealInExplorer(savePath);
                        onSaved(savePath);
                        return;
                    }
                }
            }
            catch { }

            if (elapsedMs >= 45000)
            {
                StopActiveSnipWatcher();
            }
        };

        _activeSnipTimer = timer;
        _activeSnipHandler = handler;
        timer.Tick += handler;
        timer.Start();
    }

    private static void StopActiveSnipWatcher()
    {
        if (_activeSnipTimer is not null)
        {
            _activeSnipTimer.Stop();
            if (_activeSnipHandler is not null)
            {
                _activeSnipTimer.Tick -= _activeSnipHandler;
            }
        }
        _activeSnipTimer = null;
        _activeSnipHandler = null;
    }

    public static void RevealInExplorer(string filePath)
    {
        if (File.Exists(filePath))
        {
            var explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorerPath, $"/select,\"{filePath}\"") { UseShellExecute = false });
        }
    }

    private static void SaveBitmapAsPng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
