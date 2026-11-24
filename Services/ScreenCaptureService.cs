using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OzelTarayici.Services;

public static class ScreenCaptureService
{
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
        BitmapSource? initialImage = null;
        try
        {
            if (Clipboard.ContainsImage())
                initialImage = Clipboard.GetImage();
        }
        catch { }

        Process.Start(new ProcessStartInfo("explorer.exe", "ms-screenclip:") { UseShellExecute = true });

        var elapsedMs = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            elapsedMs += 400;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var current = Clipboard.GetImage();
                    if (current is not null && !AreImagesEqual(initialImage, current))
                    {
                        timer.Stop();
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
                timer.Stop();
            }
        };
        timer.Start();
    }

    public static void RevealInExplorer(string filePath)
    {
        if (File.Exists(filePath))
        {
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
    }

    private static bool AreImagesEqual(BitmapSource? a, BitmapSource b)
    {
        if (a is null) return false;
        return a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight;
    }

    private static void SaveBitmapAsPng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
