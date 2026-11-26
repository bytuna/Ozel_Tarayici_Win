using System.IO;
using Microsoft.Web.WebView2.Core;

namespace OzelTarayici.Services;

public sealed class WebViewPrivacyController
{
    private readonly CoreWebView2 _core;
    private readonly Func<bool> _isAdBlockEnabled;
    private readonly Func<bool> _isBlockAllCookies;
    private readonly Func<bool> _isTrackingPreventionEnabled;
    private readonly Func<string, bool> _isCompatibleUrl;
    private readonly bool _isIncognito;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private string? _shortcutScriptId;
    private string? _passwordScriptId;
    private string? _cosmeticAdBlockScriptId;
    private volatile string _currentNavigationUrl = string.Empty;

    internal WebViewPrivacyController(
        CoreWebView2 core,
        Func<bool> isAdBlockEnabled,
        Func<bool> isBlockAllCookies,
        Func<bool> isTrackingPreventionEnabled,
        Func<string, bool> isCompatibleUrl,
        bool isIncognito)
    {
        _core = core;
        _isAdBlockEnabled = isAdBlockEnabled;
        _isBlockAllCookies = isBlockAllCookies;
        _isTrackingPreventionEnabled = isTrackingPreventionEnabled;
        _isCompatibleUrl = isCompatibleUrl;
        _isIncognito = isIncognito;
    }

    internal void HandleNavigationStarting(string? uri)
    {
        _currentNavigationUrl = uri ?? string.Empty;
        ApplyFastSyncSettings(_currentNavigationUrl);
        _ = SyncScriptsAndSettingsAsync(_currentNavigationUrl);
    }

    internal bool IsCurrentPageCompatible()
    {
        var url = !string.IsNullOrWhiteSpace(_currentNavigationUrl)
            ? _currentNavigationUrl
            : (_core.Source ?? string.Empty);
        return _isCompatibleUrl(url);
    }

    public Task PrepareForNavigationAsync(string? targetUrl)
    {
        _currentNavigationUrl = targetUrl ?? string.Empty;
        return SyncScriptsAndSettingsAsync(_currentNavigationUrl);
    }

    public Task RefreshPolicyAsync(string? overrideUrl = null)
    {
        var url = overrideUrl ?? (!string.IsNullOrWhiteSpace(_core.Source) ? _core.Source : _currentNavigationUrl);
        return SyncScriptsAndSettingsAsync(url);
    }

    private void ApplyFastSyncSettings(string? targetUrl)
    {
        try
        {
            var compatible = !string.IsNullOrWhiteSpace(targetUrl) && _isCompatibleUrl(targetUrl);
            var isPdf = WebViewPrivacyService.IsPdfUrl(targetUrl);

            _core.Settings.AreHostObjectsAllowed = false;
            _core.Settings.IsWebMessageEnabled = !compatible;
            _core.Settings.AreBrowserAcceleratorKeysEnabled = compatible || isPdf;

            if (compatible)
            {
                RemoveScriptIfPresent(ref _shortcutScriptId);
                RemoveScriptIfPresent(ref _passwordScriptId);
                RemoveScriptIfPresent(ref _cosmeticAdBlockScriptId);
            }
        }
        catch
        {
            // WebView kapanıyorsa sessizce geç
        }
    }

    public async Task SyncScriptsAndSettingsAsync(string? targetUrl)
    {
        await _syncLock.WaitAsync();
        try
        {
            var url = targetUrl ?? string.Empty;
            var compatible = !string.IsNullOrWhiteSpace(url) && _isCompatibleUrl(url);
            var isPdf = WebViewPrivacyService.IsPdfUrl(url);

            _core.Settings.IsStatusBarEnabled = false;
            _core.Settings.AreDefaultContextMenusEnabled = true;
            _core.Settings.AreHostObjectsAllowed = false;
            _core.Settings.IsWebMessageEnabled = !compatible;
            _core.Settings.AreBrowserAcceleratorKeysEnabled = compatible || isPdf;

            try
            {
                _core.Profile.PreferredTrackingPreventionLevel = _isTrackingPreventionEnabled()
                    ? CoreWebView2TrackingPreventionLevel.Balanced
                    : CoreWebView2TrackingPreventionLevel.Basic;
            }
            catch
            {
                // Eski WebView2 çalışma zamanı desteklemiyorsa geç
            }

            if (compatible)
            {
                RemoveScriptIfPresent(ref _shortcutScriptId);
                RemoveScriptIfPresent(ref _passwordScriptId);
                RemoveScriptIfPresent(ref _cosmeticAdBlockScriptId);
                return;
            }

            if (_shortcutScriptId is null)
            {
                var id = await _core.AddScriptToExecuteOnDocumentCreatedAsync(
                    WebViewPrivacyService.KeyboardShortcutScript);
                if (_isCompatibleUrl(_currentNavigationUrl))
                {
                    RemoveScriptIdSafe(id);
                    return;
                }
                _shortcutScriptId = id;
            }

            if (!_isIncognito)
            {
                if (_passwordScriptId is null)
                {
                    var id = await _core.AddScriptToExecuteOnDocumentCreatedAsync(
                        WebViewPrivacyService.PasswordCaptureScript);
                    if (_isCompatibleUrl(_currentNavigationUrl))
                    {
                        RemoveScriptIdSafe(id);
                        return;
                    }
                    _passwordScriptId = id;
                }
            }
            else
            {
                RemoveScriptIfPresent(ref _passwordScriptId);
            }

            if (_isAdBlockEnabled())
            {
                if (_cosmeticAdBlockScriptId is null)
                {
                    var id = await _core.AddScriptToExecuteOnDocumentCreatedAsync(
                        WebViewPrivacyService.CosmeticAdBlockScript);
                    if (_isCompatibleUrl(_currentNavigationUrl))
                    {
                        RemoveScriptIdSafe(id);
                        return;
                    }
                    _cosmeticAdBlockScriptId = id;
                }
            }
            else
            {
                RemoveScriptIfPresent(ref _cosmeticAdBlockScriptId);
            }
        }
        catch
        {
            // WebView kapanmış veya geçersiz durumda olabilir
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void RemoveScriptIfPresent(ref string? scriptId)
    {
        var id = Interlocked.Exchange(ref scriptId, null);
        if (id is null) return;
        RemoveScriptIdSafe(id);
    }

    private void RemoveScriptIdSafe(string id)
    {
        try
        {
            _core.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch
        {
            // Zaten kaldırılmış olabilir
        }
    }
}

public static class WebViewPrivacyService
{
    private static readonly string[] BlockedDomains =
    [
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "adservice.google.",
        "google-analytics.com",
        "googletagmanager.com",
        "adnxs.com",
        "advertising.com",
        "criteo.com",
        "criteo.net",
        "amazon-adsystem.com",
        "taboola.com",
        "outbrain.com",
        "mgid.com",
        "popads.net",
        "popcash.net",
        "propellerads.com",
        "bidswitch.net",
        "pubmatic.com",
        "rubiconproject.com",
        "openx.net",
        "adform.net",
        "casalemedia.com",
        "smartadserver.com",
        "moatads.com",
        "adsafeprotected.com",
        "scorecardresearch.com",
        "quantserve.com",
        "hotjar.com",
        "clarity.ms",
        "connect.facebook.net",
        "an.yandex.ru",
        "yandex.ru/ads",
        "ads-twitter.com",
        "ads.yahoo.com"
    ];

    private static readonly string[] AllowlistPaths =
    [
        "www.google.com/recaptcha"
    ];

    private static readonly string[] AllowlistHosts =
    [
        "accounts.google.com",
        "gstatic.com",
        "challenges.cloudflare.com",
        "hcaptcha.com",
        "recaptcha.net"
    ];

    public const string KeyboardShortcutScript = """
        (() => {
          try {
            if (window.__ozelShortcutHooked) return;
            window.__ozelShortcutHooked = true;
            window.addEventListener('keydown', (e) => {
              if (e.ctrlKey && !e.shiftKey && !e.altKey) {
                const k = (e.key || '').toLowerCase();
                if (k === 'h' || k === 'j' || k === 'k' || k === 't' || k === 'w' || k === 'l' || k === 'p' || k === 'f' || k === 'r') {
                  e.preventDefault();
                  e.stopPropagation();
                  window.chrome?.webview?.postMessage('__shortcut_' + k);
                }
              } else if (e.key === 'F5' && !e.ctrlKey && !e.altKey) {
                e.preventDefault();
                e.stopPropagation();
                window.chrome?.webview?.postMessage('__shortcut_r');
              } else if (e.key === 'Escape') {
                window.chrome?.webview?.postMessage('__shortcut_esc');
              }
            }, true);
          } catch {}
        })();
        """;

    public const string PasswordCaptureScript = """
        (() => {
          try {
            if (window.__ozelPwdCaptureHooked) return;
            window.__ozelPwdCaptureHooked = true;
            const captureFormCreds = (root) => {
              try {
                const passEl = (root || document).querySelector('input[type="password"]');
                if (!passEl || !passEl.value) return;
                const scope = passEl.form || document;
                const userEl = scope.querySelector('input[type="email"], input[type="text"], input[name*="user"], input[name*="mail"], input[autocomplete="username"]');
                const payload = {
                  type: '__pwd_capture',
                  user: userEl ? (userEl.value || '') : '',
                  pass: passEl.value
                };
                window.chrome?.webview?.postMessage(JSON.stringify(payload));
              } catch {}
            };

            document.addEventListener('submit', (e) => captureFormCreds(e.target), true);
            document.addEventListener('keydown', (e) => {
              if (e.key === 'Enter' && e.target && e.target.type === 'password') {
                captureFormCreds(e.target.form || document);
              }
            }, true);
          } catch {}
        })();
        """;

    public const string CosmeticAdBlockScript = """
        (() => {
          try {
            if (window.__ozelCosmeticAdBlock) return;
            window.__ozelCosmeticAdBlock = true;
            const style = document.createElement('style');
            style.textContent = `
              ins.adsbygoogle, .adsbygoogle, [data-ad-slot], [data-ad-client],
              iframe[src*="doubleclick.net"], iframe[src*="googlesyndication.com"],
              iframe[id^="google_ads_iframe"], div[id^="div-gpt-ad"] {
                display: none !important;
                height: 0 !important;
                min-height: 0 !important;
              }
            `;
            (document.head || document.documentElement).appendChild(style);
          } catch {}
        })();
        """;

    public const string ReaderModeScript = """
        (() => {
          if (window.__readerModeActive) {
            location.reload();
            return 'exited';
          }
          window.__readerModeActive = true;
          const article = document.querySelector('article') || document.querySelector('main') || document.body;
          const title = document.title || '';

          const container = document.createElement('div');
          container.setAttribute('style', "max-width: 760px; margin: 40px auto; padding: 24px; font-family: 'Segoe UI', Roboto, sans-serif; line-height: 1.8; font-size: 19px; color: #e4e4e7; background: #18181b;");

          const exitBtn = document.createElement('button');
          exitBtn.type = 'button';
          exitBtn.textContent = '✕ Okuma Modundan Çık';
          exitBtn.setAttribute('style', 'padding: 8px 16px; margin-bottom: 24px; cursor: pointer; border-radius: 6px; border: 1px solid #3f3f46; background: #27272a; color: #fff; font-weight: bold;');
          exitBtn.addEventListener('click', () => location.reload());
          container.appendChild(exitBtn);

          const h1 = document.createElement('h1');
          h1.setAttribute('style', 'color: #00b37e; margin-bottom: 20px;');
          h1.textContent = title;
          container.appendChild(h1);

          const nodes = article ? article.querySelectorAll('h1, h2, h3, p, img') : [];
          for (const el of nodes) {
            const tag = el.tagName.toLowerCase();
            if (tag === 'img') {
              const rawSrc = el.getAttribute('src') || '';
              try {
                const resolved = new URL(rawSrc, location.href);
                if (resolved.protocol === 'http:' || resolved.protocol === 'https:') {
                  const safeImg = document.createElement('img');
                  safeImg.src = resolved.href;
                  safeImg.alt = el.getAttribute('alt') || '';
                  safeImg.setAttribute('style', 'max-width: 100%; height: auto; border-radius: 8px; margin: 12px 0;');
                  container.appendChild(safeImg);
                }
              } catch {}
            } else {
              const text = (el.textContent || '').trim();
              if (!text) continue;
              const safeEl = document.createElement(tag);
              safeEl.textContent = text;
              container.appendChild(safeEl);
            }
          }

          document.body.replaceChildren(container);
          document.body.style.backgroundColor = '#18181b';
          return 'entered';
        })();
        """;

    public const string ToggleMediaPlaybackScript = """
        (() => {
          try {
            const media = Array.from(document.querySelectorAll('video, audio'));
            if (media.length === 0) return 'no-media';
            const playing = media.find(m => !m.paused && !m.ended);
            if (playing) {
              playing.pause();
              return 'paused';
            }
            const target = media.find(m => m.readyState > 0) || media[0];
            target.play().catch(() => {});
            return 'played';
          } catch {
            return 'error';
          }
        })();
        """;

    public const string PictureInPictureScript = """
        (() => {
          try {
            if (document.pictureInPictureElement) {
              document.exitPictureInPicture().catch(() => {});
              return 'exited';
            }
            const videos = Array.from(document.querySelectorAll('video'));
            if (videos.length === 0) return 'no-video';
            const activeVideo = videos.find(v => !v.paused && v.currentTime > 0) || videos[0];
            if (activeVideo.disablePictureInPicture) {
              activeVideo.disablePictureInPicture = false;
            }
            activeVideo.requestPictureInPicture().catch(() => {});
            return 'entered';
          } catch (err) {
            return 'error:' + err.message;
          }
        })();
        """;

    public static async Task<WebViewPrivacyController> ConfigureCoreWebViewAsync(
        CoreWebView2 core,
        Func<bool> isAdBlockEnabled,
        Func<bool> isBlockAllCookies,
        Func<bool> isTrackingPreventionEnabled,
        Func<string, bool> isCompatibleUrl,
        bool isIncognito = false,
        string? initialUrl = null)
    {
        var controller = new WebViewPrivacyController(
            core,
            isAdBlockEnabled,
            isBlockAllCookies,
            isTrackingPreventionEnabled,
            isCompatibleUrl,
            isIncognito);

        await controller.PrepareForNavigationAsync(initialUrl);

        core.NavigationStarting += (_, args) =>
        {
            controller.HandleNavigationStarting(args.Uri);
        };

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
        core.WebResourceRequested += (_, args) =>
        {
            if (!isAdBlockEnabled()) return;
            if (controller.IsCurrentPageCompatible()) return;

            var url = args.Request.Uri ?? string.Empty;
            if (isCompatibleUrl(url)) return;

            if (ShouldBlockUrl(url))
            {
                args.Response = core.Environment.CreateWebResourceResponse(
                    Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
            }
        };

        core.NavigationCompleted += async (_, _) =>
        {
            var currentUrl = core.Source ?? string.Empty;
            var isCompatible = isCompatibleUrl(currentUrl);

            if (isAdBlockEnabled() && !isCompatible)
            {
                try { await core.ExecuteScriptAsync(CosmeticAdBlockScript); } catch { }
            }

            if (!isBlockAllCookies() || isCompatible) return;
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri)) return;
            var host = currentUri.Host;
            if (IsHostOrSubdomain(host, "google.com") ||
                IsHostOrSubdomain(host, "cloudflare.com") ||
                IsHostOrSubdomain(host, "ti.com"))
            {
                return;
            }

            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(currentUri.AbsoluteUri);
                foreach (var cookie in cookies)
                {
                    core.CookieManager.DeleteCookie(cookie);
                }
            }
            catch
            {
                // Sessizce geç
            }
        };

        core.ServerCertificateErrorDetected += (_, args) =>
        {
            if (Uri.TryCreate(args.RequestUri, UriKind.Absolute, out var certUri) && certUri.IsLoopback)
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            }
            else
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.Default;
            }
        };

        return controller;
    }

    public static bool IsPdfUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static string NormalizeSiteKey(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return string.Empty;
        var raw = urlOrHost.Trim();
        string host;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            host = uri.Host;
        }
        else if (!raw.Contains("://", StringComparison.Ordinal) &&
                 Uri.TryCreate("https://" + raw, UriKind.Absolute, out var fallbackUri))
        {
            host = fallbackUri.Host;
        }
        else
        {
            return string.Empty;
        }

        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.IndexOf('.', 4) > 0)
        {
            host = host[4..];
        }
        return host;
    }

    public static bool IsUrlInDomains(string? url, IEnumerable<string> domains)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        foreach (var domain in domains)
        {
            if (IsHostOrSubdomain(host, domain))
                return true;
        }
        return false;
    }

    public static bool IsHostOrSubdomain(string host, string domain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(domain)) return false;
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldBlockUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        var hostAndPath = host + uri.AbsolutePath;

        foreach (var allowedHost in AllowlistHosts)
        {
            if (IsHostOrSubdomain(host, allowedHost))
                return false;
        }
        foreach (var allowedPath in AllowlistPaths)
        {
            if (hostAndPath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var blocked in BlockedDomains)
        {
            if (blocked.Contains('/'))
            {
                if (hostAndPath.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (blocked.EndsWith('.'))
            {
                if (host.StartsWith(blocked, StringComparison.OrdinalIgnoreCase) ||
                    host.Contains("." + blocked, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (IsHostOrSubdomain(host, blocked))
            {
                return true;
            }
        }

        return false;
    }
}
