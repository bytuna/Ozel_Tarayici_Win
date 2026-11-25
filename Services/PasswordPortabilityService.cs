using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OzelTarayici.Models;

namespace OzelTarayici.Services;

public sealed record PasswordImportReport(int Added, int Updated, int Skipped);

public static class PasswordPortabilityService
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2IterationsV1 = 100_000;
    private const int Pbkdf2IterationsV2 = 600_000;
    private static readonly byte[] VaultAadV2 = Encoding.UTF8.GetBytes("OzelTarayici.Vault.v2");

    private sealed class EncryptedVaultEnvelope
    {
        public int Version { get; set; } = 2;
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string CipherText { get; set; } = string.Empty;
    }

    private sealed class PortablePasswordDto
    {
        public string Site { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public static void ExportEncrypted(string filePath, IEnumerable<SavedPasswordItem> items, string masterPassword)
    {
        var payload = items.Select(x => new PortablePasswordDto
        {
            Site = x.Site,
            Username = x.Username,
            Password = x.Password
        }).ToList();

        var plainBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plainBytes.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(masterPassword, salt, Pbkdf2IterationsV2, HashAlgorithmName.SHA256, KeySize);

        try
        {
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plainBytes, cipher, tag, VaultAadV2);
            }

            var envelope = new EncryptedVaultEnvelope
            {
                Version = 2,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                CipherText = Convert.ToBase64String(cipher)
            };

            File.WriteAllText(filePath, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static List<SavedPasswordItem> ImportEncrypted(string filePath, string masterPassword)
    {
        var raw = File.ReadAllText(filePath, Encoding.UTF8);
        var envelope = JsonSerializer.Deserialize<EncryptedVaultEnvelope>(raw)
            ?? throw new InvalidDataException("Şifreli kasa dosyası okunamadı.");

        if (envelope.Version is not (1 or 2))
            throw new InvalidDataException($"Desteklenmeyen kasa sürümü: {envelope.Version}");

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var cipher = Convert.FromBase64String(envelope.CipherText);

        if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize)
            throw new InvalidDataException("Şifreli kasa başlık boyutları geçersiz.");

        var plain = new byte[cipher.Length];
        var iterations = envelope.Version == 1 ? Pbkdf2IterationsV1 : Pbkdf2IterationsV2;
        var key = Rfc2898DeriveBytes.Pbkdf2(masterPassword, salt, iterations, HashAlgorithmName.SHA256, KeySize);

        try
        {
            using (var aes = new AesGcm(key, TagSize))
            {
                if (envelope.Version == 2)
                    aes.Decrypt(nonce, cipher, tag, plain, VaultAadV2);
                else
                    aes.Decrypt(nonce, cipher, tag, plain);
            }

            var list = JsonSerializer.Deserialize<List<PortablePasswordDto>>(Encoding.UTF8.GetString(plain)) ?? new();
            return list.Select(x => new SavedPasswordItem
            {
                Site = x.Site,
                Username = x.Username,
                Password = x.Password
            }).ToList();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void ExportCsv(string filePath, IEnumerable<SavedPasswordItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,url,username,password");
        foreach (var item in items)
        {
            var url = item.Site.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? item.Site
                : $"https://{item.Site}";
            sb.AppendLine($"{EscapeCsv(item.Site)},{EscapeCsv(url)},{EscapeCsv(item.Username)},{EscapeCsv(item.Password)}");
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void ExportJson(string filePath, IEnumerable<SavedPasswordItem> items)
    {
        var payload = items.Select(x => new PortablePasswordDto
        {
            Site = x.Site,
            Username = x.Username,
            Password = x.Password
        }).ToList();

        File.WriteAllText(filePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    public static List<SavedPasswordItem> ImportCsv(string filePath)
    {
        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        var result = new List<SavedPasswordItem>();
        if (lines.Length == 0) return result;

        var header = ParseCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var nameIdx = header.IndexOf("name");
        var urlIdx = header.IndexOf("url");
        var userIdx = header.IndexOf("username");
        var passIdx = header.IndexOf("password");

        var startRow = 1;
        if (urlIdx < 0 || passIdx < 0)
        {
            // Başlıksız CSV fallback: site/url, username, password
            nameIdx = -1;
            urlIdx = 0;
            userIdx = 1;
            passIdx = 2;
            startRow = 0;
        }

        for (int i = startRow; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cols = ParseCsvLine(lines[i]);
            if (cols.Count <= Math.Max(urlIdx, passIdx)) continue;

            var rawUrl = cols[urlIdx].Trim();
            var rawName = nameIdx >= 0 && nameIdx < cols.Count ? cols[nameIdx].Trim() : string.Empty;
            var site = NormalizeSite(string.IsNullOrWhiteSpace(rawUrl) ? rawName : rawUrl);
            var user = userIdx >= 0 && userIdx < cols.Count ? cols[userIdx].Trim() : string.Empty;
            var pass = cols[passIdx];

            if (!string.IsNullOrWhiteSpace(site) && !string.IsNullOrEmpty(pass))
            {
                result.Add(new SavedPasswordItem { Site = site, Username = user, Password = pass });
            }
        }

        return result;
    }

    public static List<SavedPasswordItem> ImportJson(string filePath)
    {
        var raw = File.ReadAllText(filePath, Encoding.UTF8);
        using var doc = JsonDocument.Parse(raw);
        var result = new List<SavedPasswordItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var site = GetProp(el, "Site", "site", "url", "Url", "name");
            var user = GetProp(el, "Username", "username", "user", "User");
            var pass = GetProp(el, "Password", "password", "pass", "Pass");

            if (!string.IsNullOrWhiteSpace(site) && !string.IsNullOrEmpty(pass))
            {
                result.Add(new SavedPasswordItem
                {
                    Site = NormalizeSite(site),
                    Username = user,
                    Password = pass
                });
            }
        }
        return result;
    }

    public static PasswordImportReport MergeInto(IList<SavedPasswordItem> target, IEnumerable<SavedPasswordItem> incoming)
    {
        int added = 0, updated = 0, skipped = 0;

        foreach (var item in incoming)
        {
            var existing = target.FirstOrDefault(x =>
                string.Equals(x.Site, item.Site, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Username, item.Username, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                target.Add(new SavedPasswordItem
                {
                    Site = item.Site,
                    Username = item.Username,
                    Password = item.Password
                });
                added++;
            }
            else if (existing.Password != item.Password)
            {
                existing.Password = item.Password;
                updated++;
            }
            else
            {
                skipped++;
            }
        }

        return new PasswordImportReport(added, updated, skipped);
    }

    private static string GetProp(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string NormalizeSite(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;
        return input.Trim();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cols = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { cols.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        cols.Add(sb.ToString());
        return cols;
    }
}
