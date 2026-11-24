using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OzelTarayici.Models;

namespace OzelTarayici.Services;

public static class SessionStorageService
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OzelTarayici");

    private static readonly string SessionFile = Path.Combine(DataDir, "browser_session.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSessionData Load()
    {
        try
        {
            if (File.Exists(SessionFile))
            {
                var raw = File.ReadAllText(SessionFile);
                var data = JsonSerializer.Deserialize<AppSessionData>(raw, JsonOptions);
                if (data is not null)
                {
                    if (data.Groups.Count == 0)
                    {
                        data.Groups.Add(new TabGroup { Id = "general", Name = "Genel", ColorHex = "#00B37E" });
                    }
                    foreach (var pwd in data.SavedPasswords)
                    {
                        if (!string.IsNullOrEmpty(pwd.EncryptedPassword))
                        {
                            pwd.Password = UnprotectString(pwd.EncryptedPassword);
                        }
                    }
                    return data;
                }
            }
        }
        catch
        {
            // Bozuk oturum dosyasında varsayılan oturumla devam et
        }

        return new AppSessionData();
    }

    public static void Save(AppSessionData data)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (var pwd in data.SavedPasswords)
            {
                if (!string.IsNullOrEmpty(pwd.Password))
                {
                    pwd.EncryptedPassword = ProtectString(pwd.Password);
                }
            }
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var tmpFile = SessionFile + ".tmp";
            File.WriteAllText(tmpFile, json);
            File.Move(tmpFile, SessionFile, overwrite: true);
        }
        catch
        {
            // Disk yazma hatalarında uygulamayı kilitleme
        }
    }

    public static string ProtectString(string plain)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return plain;
        }
    }

    public static string UnprotectString(string cipher)
    {
        try
        {
            var bytes = Convert.FromBase64String(cipher);
            var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch
        {
            return cipher;
        }
    }

    public static string GetUserDataFolder(bool isIncognito)
    {
        var folderName = isIncognito
            ? Path.Combine(DataDir, "IncognitoSession")
            : Path.Combine(DataDir, "WebView2Profile");

        Directory.CreateDirectory(folderName);
        return folderName;
    }

    public static void CleanupIncognitoFolder()
    {
        try
        {
            var incDir = Path.Combine(DataDir, "Incognito");
            if (Directory.Exists(incDir)) Directory.Delete(incDir, true);

            var incSessionDir = Path.Combine(DataDir, "IncognitoSession");
            if (Directory.Exists(incSessionDir)) Directory.Delete(incSessionDir, true);
        }
        catch
        {
            // Dosya kilitliyse bir sonraki açılışta temizlenir
        }
    }

    public static string GetDownloadsFolder()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(userProfile, "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }
}
