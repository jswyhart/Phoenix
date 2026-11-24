using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Halcyon.Core;

namespace Halcyon.Desktop;

public static class Platform
{
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>~/Library/Application Support/Halcyon on macOS; a separate folder from the Windows app's data elsewhere.</summary>
    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), IsMac ? "Halcyon" : IsLinux ? "halcyon" : "HalcyonDesktop");

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void RevealFile(string path)
    {
        try
        {
            if (IsMac) Process.Start("open", new[] { "-R", path });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start("xdg-open", new[] { Path.GetDirectoryName(path) ?? path });
        }
        catch { }
    }

    /// <summary>
    /// Encrypts tokens with AES-GCM. On macOS the key lives in the login Keychain (via the system `security` tool);
    /// elsewhere, or if the Keychain can't be used, it is kept in a file next to the settings.
    /// </summary>
    public class Secrets : ISecretProtector
    {
        const string Service = "Halcyon media player", Account = "settings-key";
        readonly string _keyFile;
        byte[]? _key;

        public Secrets(string dataDir) => _keyFile = Path.Combine(dataDir, "settings.key");

        byte[] Key()
        {
            if (_key != null) return _key;
            if (IsMac)
            {
                var stored = Run("security", "find-generic-password", "-s", Service, "-a", Account, "-w");
                if (stored != null && TryKey(stored.Trim(), out var k)) return _key = k;
                var fresh = RandomNumberGenerator.GetBytes(32);
                if (Run("security", "add-generic-password", "-U", "-s", Service, "-a", Account, "-w", Convert.ToBase64String(fresh)) != null) return _key = fresh;
            }
            try { if (TryKey(File.ReadAllText(_keyFile).Trim(), out var k)) return _key = k; } catch { }
            _key = RandomNumberGenerator.GetBytes(32);
            try { File.WriteAllText(_keyFile, Convert.ToBase64String(_key)); } catch { }
            return _key;
        }

        static bool TryKey(string base64, out byte[] key)
        {
            key = Array.Empty<byte>();
            try { key = Convert.FromBase64String(base64); return key.Length == 32; } catch { return false; }
        }

        static string? Run(string file, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0 ? output : null;
            }
            catch { return null; }
        }

        public string Protect(string plain)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var data = Encoding.UTF8.GetBytes(plain);
            var cipher = new byte[data.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key(), 16);
            aes.Encrypt(nonce, data, cipher, tag);
            return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
        }

        public string Unprotect(string stored)
        {
            var all = Convert.FromBase64String(stored);
            var plain = new byte[all.Length - 28];
            using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
    }
}
