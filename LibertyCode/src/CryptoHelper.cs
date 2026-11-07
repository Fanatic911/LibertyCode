using System.Security.Cryptography;
using System.Text;

namespace LibertyCode;

/// <summary>
/// Stores the API key encrypted with a key derived from the machine's hardware ID (BIOS UUID / board serial),
/// so the file is useless on any other computer. Everything is silent: nothing is ever printed about it.
/// </summary>
internal static class CryptoHelper
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LibertyCode-2024-Salt");

    /// <summary>Keeps only printable ASCII without spaces: anything else makes HTTP headers fail.</summary>
    public static string CleanKey(string? key) =>
        new string((key ?? "").Where(c => c >= '!' && c <= '~').ToArray());

    private static string KeyFile(string provider) => Path.Combine(Paths.DataDir, $".env.{provider}.enc");

    /// <summary>Old versions stored one key for all providers: it belongs to the provider that was configured then.</summary>
    public static void MigrateLegacy(string provider)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(provider) || !File.Exists(Paths.EnvFile) || File.Exists(KeyFile(provider))) return;
            File.Move(Paths.EnvFile, KeyFile(provider));
        }
        catch { }
    }

    // ── Hardware-bound encryption (AES-256-GCM, key derived from the machine's BIOS/board ID) ──
    private static readonly byte[] Magic = "LC2"u8.ToArray();
    private static readonly Lazy<string> Hwid = new(ReadHwid);

    private static bool Junk(string? v)
    {
        string l = (v ?? "").Trim().ToLowerInvariant();
        return l.Length == 0 || l.Trim('0', '-', 'f').Length == 0 || l.Contains("o.e.m") || l.Contains("default string")
            || l is "none" or "n/a" or "unknown" or "not applicable" or "system serial number";
    }

    private static string? Wmi(string cls, string prop)
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var o in q.Get())
            {
                string? v = o[prop]?.ToString();
                if (!Junk(v)) return v!.Trim();
            }
        }
        catch { }
        return null;
    }

    private static string ReadHwid()
    {
        string? id = null;
        try
        {
            if (OperatingSystem.IsWindows())
                id = Wmi("Win32_ComputerSystemProduct", "UUID") ?? Wmi("Win32_BaseBoard", "SerialNumber") ?? Wmi("Win32_BIOS", "SerialNumber")
                     ?? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)?.ToString();
            else if (File.Exists("/etc/machine-id"))
                id = File.ReadAllText("/etc/machine-id").Trim();
        }
        catch { }
        return id ?? "";
    }

    private static byte[] DeriveKey(byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Hwid.Value), salt.Concat(Entropy).ToArray(), 100_000, HashAlgorithmName.SHA256, 32);

    private static byte[] Encrypt(byte[] plain)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length], tag = new byte[16];
        using var aes = new AesGcm(DeriveKey(salt), 16);
        aes.Encrypt(nonce, plain, cipher, tag, Magic);
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    private static byte[]? Decrypt(byte[] data)
    {
        if (data.Length < 3 + 16 + 12 + 16 || !data.AsSpan(0, 3).SequenceEqual(Magic)) return null;
        var salt = data.AsSpan(3, 16).ToArray();
        var nonce = data.AsSpan(19, 12);
        var tag = data.AsSpan(31, 16);
        var cipher = data.AsSpan(47);
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(DeriveKey(salt), 16);
        aes.Decrypt(nonce, cipher, tag, plain, Magic);
        return plain;
    }

    public static void SaveApiKey(string key, string provider)
    {
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllBytes(KeyFile(provider), Encrypt(Encoding.UTF8.GetBytes(CleanKey(key))));
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile(provider), UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    /// <summary>Decrypts silently. A file from another machine, a damaged file or an old-format file (migrated on the spot) never prints anything.</summary>
    public static string? LoadApiKey(string provider)
    {
        if (!File.Exists(KeyFile(provider))) return null;
        try
        {
            byte[] data = File.ReadAllBytes(KeyFile(provider));
            byte[]? plain = Decrypt(data);
            if (plain == null) // old format (DPAPI): read it, then re-encrypt with the hardware key
            {
                plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser) : data;
                try { File.WriteAllBytes(KeyFile(provider), Encrypt(plain)); } catch { }
            }
            string key = CleanKey(Encoding.UTF8.GetString(plain));
            return key.Length == 0 ? null : key;
        }
        catch { return null; }
    }
}
