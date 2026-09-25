using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Jarvis.Domain;

public sealed class LocalNetworkScope : IDisposable
{
    private static readonly AsyncLocal<bool> Current = new();
    private readonly bool previous;
    public static bool Allowed => Current.Value;
    public LocalNetworkScope() { previous = Current.Value; Current.Value = true; }
    public void Dispose() => Current.Value = previous;
}

public sealed record LocalCredential(string Type, string Value, string Username = "", string Header = "X-Api-Key");
public sealed record LocalService
{
    public string Name { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Host { get; init; } = "";
    public string Protocol { get; init; } = "https";
    public int Port { get; init; } = 443;
    public string Type { get; init; } = "HTTP";
    public bool Enabled { get; init; }
    public bool AutoCheck { get; init; }
    public string Permission { get; init; } = "Read";
    public string CredentialRef { get; init; } = "";
    public bool AllowHttpCredentials { get; init; }
    public string[] ReadPaths { get; init; } = [];
    public void Validate()
    {
        if (Name.Length is < 1 or > 100 || Protocol is not ("http" or "https") || Port is < 1 or > 65535 || Type.Length > 50)
            throw new JarvisException("Ungültiger lokaler Dienst.");
        _ = LocalNetworkPolicy.Host(Host);
        if (Permission is not ("Read" or "Write" or "Admin")) throw new JarvisException("Berechtigung muss Read, Write oder Admin sein.");
        if (CredentialRef.Length > 0 && !Regex.IsMatch(CredentialRef, "^[a-zA-Z0-9_-]{1,60}$")) throw new JarvisException("Ungültige Secret-Referenz.");
        if (ReadPaths.Length > 30) throw new JarvisException("Maximal 30 Lesepfade erlaubt.");
        foreach (var path in ReadPaths) LocalNetworkPolicy.Path(path);
    }
    public string Url(string path) => new UriBuilder(Protocol, Host, Port) { Path = LocalNetworkPolicy.Path(path) }.Uri.AbsoluteUri;
}

public sealed record LocalRequest
{
    public string Operation { get; init; } = "Http";
    public string Url { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string? BodyBase64 { get; init; }
    public string ContentType { get; init; } = "application/json";
    public string[] Allowlist { get; init; } = [];
    public int[] Ports { get; init; } = [];
    public bool ProbeEnabled { get; init; }
    public bool AllowHttpCredentials { get; init; }
    public LocalCredential? Credential { get; init; }
    public bool Download { get; init; }
}
public sealed record LocalResponse(int Status, string Address, long LatencyMs, string ContentType, string? Text = null, string? BodyBase64 = null, string? Sha256 = null);

public static class LocalNetworkPolicy
{
    private static readonly IPNetwork[] LocalRanges = [IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("fc00::/7")];
    public static bool IsLocal(IPAddress ip) => !ip.IsIPv4MappedToIPv6 && ip.ScopeIdSafe() == 0 && LocalRanges.Any(n => n.Contains(ip));
    private static long ScopeIdSafe(this IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip.ScopeId : 0;
    public static string Host(string value)
    {
        if (value.Length is < 1 or > 253 || value != value.Trim() || value.IndexOfAny(['/', '\\', '@', '%', '?', '#']) >= 0 || value.Any(char.IsControl))
            throw new JarvisException("Ungültiger lokaler Host.");
        value = value.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(value, out var ip)) {
            if (!IsLocal(ip)) throw new JarvisException("Nur private LAN-/VPN-Adressen; Loopback, Link-Local und öffentliche Ziele sind gesperrt.", 403);
            return ip.ToString();
        }
        if (Uri.CheckHostName(value) != UriHostNameType.Dns || !Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$")) throw new JarvisException("Ungültiger DNS-Name.");
        return value;
    }
    public static string Path(string value)
    {
        // No embedded query credentials, authority changes, dot segments or ambiguous escaping.
        if (!value.StartsWith('/') || value.StartsWith("//") || value.Length > 1500 || value.IndexOfAny(['\\', '%', '?', '#']) >= 0 || value.Any(char.IsControl) || value.Split('/').Any(p => p is "." or ".."))
            throw new JarvisException("Nur absolute Dienstpfade ohne Query, Fragment oder Traversal erlaubt.");
        return value;
    }
    public static string Entry(string value)
    {
        if (!value.Contains('/')) return Host(value);
        if (!IPNetwork.TryParse(value, out var net) || !IsLocal(net.BaseAddress) || !LocalRanges.Any(parent => parent.Contains(net.BaseAddress) && net.PrefixLength >= parent.PrefixLength))
            throw new JarvisException("Allowlist enthält kein ausschließlich privates LAN-/VPN-Netz.");
        return net.ToString();
    }
    public static Uri Target(string url, IReadOnlyCollection<int> ports)
    {
        var decoded=Uri.UnescapeDataString(url);
        if(decoded.Any(char.IsControl)||decoded.Contains('\\')||decoded.Split('/').Any(p=>p is "." or ".."))throw new JarvisException("Mehrdeutiger Zielpfad.");
        if (url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new JarvisException("Nur HTTP(S)-URLs ohne Zugangsdaten, Query oder Fragment erlaubt.");
        _ = Host(uri.IdnHost); _ = Path(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!ports.Contains(uri.Port)) throw new JarvisException("Port ist nicht freigegeben.", 403);
        return uri;
    }
    public static void CheckAddresses(string host, IPAddress[] addresses, IReadOnlyCollection<string> entries, IReadOnlyCollection<string> denied)
    {
        if (entries.Count == 0 || addresses.Length is < 1 or > 16) throw new JarvisException("Keine freigegebene Zieladresse.", 403);
        // DNS names must be explicitly named AND resolve only inside approved IPs/CIDRs.
        if (!IPAddress.TryParse(host, out _) && !entries.Contains(host, StringComparer.OrdinalIgnoreCase)) throw new JarvisException("DNS-Name nicht freigegeben.", 403);
        bool Match(string entry, IPAddress ip) => IPNetwork.TryParse(entry, out var net) ? net.Contains(ip) : IPAddress.TryParse(entry, out var address) && address.Equals(ip);
        foreach (var ip in addresses)
            if (!IsLocal(ip) || denied.Any(e => Match(e, ip)) || !entries.Any(e => Match(e, ip)))
                throw new JarvisException("DNS/IP-Prüfung verweigert das Ziel; alle Antworten müssen innerhalb der Allowlist liegen.", 403);
    }
}
