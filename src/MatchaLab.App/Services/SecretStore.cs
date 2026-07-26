using System.IO;
using System.Security.Cryptography;
using System.Text;
using MatchaLab.Core;

namespace MatchaLab.App.Services;

public sealed class SecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.matcha.lab.secret");

    private static string Dir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MatchaLab");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string PathFor(string key) => Path.Combine(Dir, key + ".dat");

    public string? Get(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            byte[] plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : Convert.FromBase64String(Encoding.UTF8.GetString(bytes));
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public void Set(string key, string value)
    {
        var raw = Encoding.UTF8.GetBytes(value);
        byte[] stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser)
            : Encoding.UTF8.GetBytes(Convert.ToBase64String(raw));
        File.WriteAllBytes(PathFor(key), stored);
    }

    public void Remove(string key)
    {
        try { File.Delete(PathFor(key)); } catch { }
    }
}
