using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace OzelTarayici.Services;

public sealed record LocationSearchResult(string DisplayName, double Latitude, double Longitude);
public sealed record WeatherInfo(string Icon, string TemperatureText, string Description);
public sealed record PrayerTimesInfo(
    string Imsak,
    string Gunesh,
    string Ogle,
    string Ikindi,
    string Aksam,
    string Yatsi);

public static class DashboardApiService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static async Task<List<LocationSearchResult>> SearchLocationsAsync(string query)
    {
        var results = new List<LocationSearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=10&language=tr&format=json";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr)) return results;

            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var admin1 = item.TryGetProperty("admin1", out var a1) ? a1.GetString() ?? "" : "";
                var country = item.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                var lat = item.TryGetProperty("latitude", out var la) ? la.GetDouble() : 0;
                var lon = item.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : 0;

                var label = string.IsNullOrWhiteSpace(admin1) || admin1 == name
                    ? $"{name}, {country}".Trim(' ', ',')
                    : $"{admin1}, {name}".Trim(' ', ',');

                if (!string.IsNullOrWhiteSpace(label))
                {
                    results.Add(new LocationSearchResult(label, lat, lon));
                }
            }
        }
        catch { }

        return results;
    }

    public static async Task<WeatherInfo?> FetchWeatherAsync(double lat, double lon)
    {
        try
        {
            var latStr = lat.ToString(CultureInfo.InvariantCulture);
            var lonStr = lon.ToString(CultureInfo.InvariantCulture);
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}&current_weather=true";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("current_weather", out var cw)) return null;

            var temp = cw.TryGetProperty("temperature", out var t) ? Math.Round(t.GetDouble()) : 0;
            var code = cw.TryGetProperty("weathercode", out var wc) ? wc.GetInt32() : 0;
            var (icon, desc) = MapWeatherCode(code);

            return new WeatherInfo(icon, $"{temp}°C", desc);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<PrayerTimesInfo?> FetchPrayerTimesAsync(double lat, double lon)
    {
        try
        {
            var latStr = lat.ToString(CultureInfo.InvariantCulture);
            var lonStr = lon.ToString(CultureInfo.InvariantCulture);
            var url = $"https://api.aladhan.com/v1/timings?latitude={latStr}&longitude={lonStr}&method=13";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("timings", out var timings))
            {
                return null;
            }

            static string CleanTime(JsonElement root, string prop)
            {
                if (!root.TryGetProperty(prop, out var el)) return "--:--";
                var raw = el.GetString() ?? "--:--";
                var spaceIdx = raw.IndexOf(' ');
                return spaceIdx > 0 ? raw[..spaceIdx] : raw;
            }

            return new PrayerTimesInfo(
                CleanTime(timings, "Fajr"),
                CleanTime(timings, "Sunrise"),
                CleanTime(timings, "Dhuhr"),
                CleanTime(timings, "Asr"),
                CleanTime(timings, "Maghrib"),
                CleanTime(timings, "Isha"));
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<string>> FetchSearchSuggestionsAsync(string query)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return list;

        try
        {
            var url = $"https://suggestqueries.google.com/complete/search?client=chrome&q={Uri.EscapeDataString(query)}";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 1)
            {
                foreach (var item in doc.RootElement[1].EnumerateArray().Take(7))
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
        }
        catch { }

        return list;
    }

    public static async Task<LocationSearchResult?> AutoDetectLocationAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://ipapi.co/json/");
            using var doc = JsonDocument.Parse(json);
            var city = doc.RootElement.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
            var region = doc.RootElement.TryGetProperty("region", out var r) ? r.GetString() ?? "" : "";
            var lat = doc.RootElement.TryGetProperty("latitude", out var la) ? la.GetDouble() : 0;
            var lon = doc.RootElement.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : 0;

            if (lat == 0 && lon == 0) return null;
            var label = string.IsNullOrWhiteSpace(region) || region == city ? city : $"{region}, {city}";
            return new LocationSearchResult(string.IsNullOrWhiteSpace(label) ? "Mevcut Konum" : label, lat, lon);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(string TranslatedText, string DetectedLang)?> TranslateTextAsync(
        string text,
        string targetLang = "tr",
        string sourceLang = "auto")
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(sourceLang)}&tl={Uri.EscapeDataString(targetLang)}&dt=t&q={Uri.EscapeDataString(text)}";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var sb = new System.Text.StringBuilder();
            foreach (var part in doc.RootElement[0].EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0 && part[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(part[0].GetString());
                }
            }

            var detected = doc.RootElement.GetArrayLength() > 2 && doc.RootElement[2].ValueKind == JsonValueKind.String
                ? doc.RootElement[2].GetString() ?? sourceLang
                : sourceLang;

            return (sb.ToString(), detected);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<System.Windows.Media.ImageSource?> FetchSiteFaviconAsync(string siteUrl)
    {
        try
        {
            if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                return null;

            var faviconApi = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(uri.Host)}&sz=64";
            var bytes = await Http.GetByteArrayAsync(faviconApi);
            if (bytes.Length == 0) return null;

            using var ms = new System.IO.MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static (string Icon, string Description) MapWeatherCode(int code) => code switch
    {
        0 => ("☀️", "Açık"),
        1 or 2 => ("⛅", "Az Bulutlu"),
        3 => ("☁️", "Bulutlu"),
        45 or 48 => ("🌫️", "Sisli"),
        51 or 53 or 55 or 61 or 63 or 65 or 80 or 81 or 82 => ("🌧️", "Yağmurlu"),
        71 or 73 or 75 or 85 or 86 => ("❄️", "Karlı"),
        95 or 96 or 99 => ("⛈️", "Fırtınalı"),
        _ => ("⛅", "Parçalı Bulutlu")
    };
}
