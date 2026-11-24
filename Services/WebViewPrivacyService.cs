using System.IO;
using Microsoft.Web.WebView2.Core;

namespace OzelTarayici.Services;

public static class WebViewPrivacyService
{
    public const string DefaultChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

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

    private static readonly string[] AllowlistKeywords =
    [
        "accounts.google.",
        "www.google.com/recaptcha",
        "gstatic.com",
        "challenges.cloudflare.com",
        "hcaptcha.com",
        "recaptcha.net"
    ];

    public const string AntiFingerprintAndStealthScript = """
        (() => {
          try {
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined, configurable: true });
            if (!window.chrome) {
              window.chrome = { runtime: {} };
            }
            window.addEventListener('mouseup', (e) => {
              if (e.button === 3) {
                e.preventDefault();
                window.chrome?.webview?.postMessage('__nav_back');
              } else if (e.button === 4) {
                e.preventDefault();
                window.chrome?.webview?.postMessage('__nav_forward');
              }
            }, true);

            // Giriş formlarından şifre yakalama
            const captureFormCreds = (root) => {
              try {
                const passEl = (root || document).querySelector('input[type="password"]');
                if (!passEl || !passEl.value) return;
                const scope = passEl.form || document;
                const userEl = scope.querySelector('input[type="email"], input[type="text"], input[name*="user"], input[name*="mail"], input[autocomplete="username"]');
                const payload = {
                  type: '__pwd_capture',
                  site: location.hostname,
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

            window.addEventListener('keydown', (e) => {
              if (e.ctrlKey && !e.shiftKey && !e.altKey) {
                const k = (e.key || '').toLowerCase();
                if (k === 'h' || k === 'j' || k === 'k' || k === 't' || k === 'w' || k === 'l' || k === 'p') {
                  e.preventDefault();
                  e.stopPropagation();
                  window.chrome?.webview?.postMessage('__shortcut_' + k);
                }
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

    public const string StrongFingerprintShieldScript = """
        (() => {
          try {
            if (window.__ozelFpShieldApplied) return;
            window.__ozelFpShieldApplied = true;

            const noise = Math.floor(Math.random() * 3) + 1;

            // 1. Canvas toDataURL & getImageData gürültüsü
            const origToDataURL = HTMLCanvasElement.prototype.toDataURL;
            HTMLCanvasElement.prototype.toDataURL = function(...args) {
              try {
                const ctx = this.getContext('2d');
                if (ctx && this.width > 0 && this.height > 0) {
                  const img = ctx.getImageData(0, 0, 1, 1);
                  img.data[0] = (img.data[0] ^ noise) & 255;
                  ctx.putImageData(img, 0, 0);
                }
              } catch {}
              return origToDataURL.apply(this, args);
            };

            const origGetImageData = CanvasRenderingContext2D.prototype.getImageData;
            CanvasRenderingContext2D.prototype.getImageData = function(sx, sy, sw, sh) {
              const data = origGetImageData.apply(this, [sx, sy, sw, sh]);
              if (data && data.data && data.data.length > 0) {
                data.data[0] = (data.data[0] ^ noise) & 255;
              }
              return data;
            };

            // 2. WebGL Vendor & Renderer maskeleme
            const patchWebGL = (proto) => {
              if (!proto || !proto.getParameter) return;
              const origGetParam = proto.getParameter;
              proto.getParameter = function(param) {
                if (param === 37445) return 'Intel Inc.';
                if (param === 37446) return 'Intel Iris OpenGL Engine';
                return origGetParam.apply(this, [param]);
              };
            };
            patchWebGL(window.WebGLRenderingContext && WebGLRenderingContext.prototype);
            patchWebGL(window.WebGL2RenderingContext && WebGL2RenderingContext.prototype);

            // 3. Donanım sabitleme
            Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 8, configurable: true });
            Object.defineProperty(navigator, 'deviceMemory', { get: () => 8, configurable: true });
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
          const title = document.title;
          const paragraphs = Array.from(article.querySelectorAll('h1, h2, h3, p, img'))
            .map(el => el.outerHTML)
            .join('');
          document.body.innerHTML = `
            <div style="max-width: 760px; margin: 40px auto; padding: 24px; font-family: 'Segoe UI', Roboto, sans-serif; line-height: 1.8; font-size: 19px; color: #e4e4e7; background: #18181b;">
              <button onclick="location.reload()" style="padding: 8px 16px; margin-bottom: 24px; cursor: pointer; border-radius: 6px; border: 1px solid #3f3f46; background: #27272a; color: #fff; font-weight: bold;">✕ Okuma Modundan Çık</button>
              <h1 style="color: #00b37e; margin-bottom: 20px;">${title}</h1>
              ${paragraphs}
            </div>
          `;
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

    public static async Task ConfigureCoreWebViewAsync(
        CoreWebView2 core,
        Func<bool> isAdBlockEnabled,
        Func<bool> isBlockAllCookies,
        Func<bool> isAntiFingerprintEnabled)
    {
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(AntiFingerprintAndStealthScript);
        if (isAdBlockEnabled())
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticAdBlockScript);
        }
        if (isAntiFingerprintEnabled())
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(StrongFingerprintShieldScript);
        }

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
        core.WebResourceRequested += (sender, args) =>
        {
            if (!isAdBlockEnabled()) return;
            var url = args.Request.Uri ?? string.Empty;
            if (ShouldBlockUrl(url))
            {
                args.Response = core.Environment.CreateWebResourceResponse(
                    Stream.Null, 403, "Blocked by OzelTarayici", "Content-Type: text/plain");
            }
        };

        core.NavigationCompleted += async (_, _) =>
        {
            if (isAdBlockEnabled())
            {
                try { await core.ExecuteScriptAsync(CosmeticAdBlockScript); } catch { }
            }

            if (!isBlockAllCookies()) return;
            var currentUrl = core.Source ?? string.Empty;
            if (currentUrl.Contains("google.com", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("ti.com", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                core.CookieManager.DeleteAllCookies();
            }
            catch
            {
                // Sessizce geç
            }
        };

        core.ServerCertificateErrorDetected += (_, args) =>
        {
            args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
    }

    private static bool ShouldBlockUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        foreach (var allowed in AllowlistKeywords)
        {
            if (url.Contains(allowed, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var blocked in BlockedDomains)
        {
            if (url.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
