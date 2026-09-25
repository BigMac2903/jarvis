using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;
namespace Jarvis.Infrastructure;
public static class Crypto
{
    public static string Token(int bytes = 32) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool EqualsSecret(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
    public static string Password(string password)
    {
        if (password.Length is < 12 or > 256) throw new JarvisException("Passwort muss 12 bis 256 Zeichen enthalten.");
        var salt = RandomNumberGenerator.GetBytes(32);
        return "pbkdf2-sha512$210000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA512, 64));
    }
    public static bool VerifyPassword(string password, string hash)
    {
        if (password.Length > 256) return false;
        try { var p = hash.Split('$'); return p.Length == 4 && p[0] == "pbkdf2-sha512" && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(p[3]), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[2]), int.Parse(p[1]), HashAlgorithmName.SHA512, 64)); }
        catch (FormatException) { return false; }
    }
}
public sealed class Vault(IConfiguration configuration, IDocumentStore store)
{
    private readonly byte[] key = ReadKey(configuration["MASTER_KEY"]);
    private static byte[] ReadKey(string? text)
    {
        try { var value = Convert.FromBase64String(text ?? ""); if (value.Length == 32) return value; } catch (FormatException) { }
        throw new InvalidOperationException("MASTER_KEY muss ein Base64-kodierter 32-Byte-Schlüssel sein. Installationsskript ausführen.");
    }
    public string Encrypt(string text, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public string Decrypt(string text, string context)
    {
        var b = Convert.FromBase64String(text); var plain = new byte[b.Length - 28];
        using var aes = new AesGcm(key, 16); aes.Decrypt(b.AsSpan(0, 12), b.AsSpan(28), b.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(plain);
    }
    public Task PutAsync(string owner, string name, string secret, CancellationToken ct) =>
        store.PutAsync(owner, "secrets", name, new JsonObject { ["cipher"] = Encrypt(secret, owner + ":" + name) }, ct);
    public async Task<string?> GetAsync(string owner, string name, CancellationToken ct)
    {
        var doc = await store.GetAsync(owner, "secrets", name, ct);
        return doc is null ? null : Decrypt(doc.Data["cipher"]!.GetValue<string>(), owner + ":" + name);
    }
}
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret()
    {
        var output = new StringBuilder(); var bits = 0; var value = 0;
        foreach (var b in RandomNumberGenerator.GetBytes(20)) { value = (value << 8) | b; bits += 8; while (bits >= 5) { output.Append(Alphabet[(value >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) output.Append(Alphabet[(value << (5 - bits)) & 31]);
        return output.ToString();
    }
    private static byte[] Decode(string text)
    {
        var bytes = new List<byte>(); var bits = 0; var value = 0;
        foreach (var c in text) { var digit = Alphabet.IndexOf(c); if (digit < 0) throw new FormatException(); value = (value << 5) | digit; bits += 5; if (bits >= 8) { bytes.Add((byte)(value >> (bits - 8))); bits -= 8; } }
        return bytes.ToArray();
    }
    public static string Code(string secret, long step)
    {
        var counter = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(step));
        var hash = HMACSHA1.HashData(Decode(secret), counter); var offset = hash[^1] & 15;
        var value = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1000000).ToString("D6");
    }
    public static long? Verify(string secret, string code, long lastStep)
    {
        var current = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var step = current - 1; step <= current + 1; step++) if (step > lastStep && Crypto.EqualsSecret(Code(secret, step), code)) return step;
        return null;
    }
}
