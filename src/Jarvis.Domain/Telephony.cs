using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jarvis.Domain;

public interface IPhoneProvider
{
    string Name { get; }
    Task<JsonNode?> StartAsync(string owner, JsonObject request, CancellationToken ct);
    Task<JsonNode?> ControlAsync(string owner, string callId, string operation, JsonObject args, CancellationToken ct);
    Task<JsonNode?> HealthAsync(string owner, CancellationToken ct);
}

public sealed record SipAccount
{
    public string Id { get; init; } = "private";
    public string Name { get; init; } = "Private";
    public string Owner { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Server { get; init; } = "";
    public int Port { get; init; } = 5061;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string AuthId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string DisplayName { get; init; } = "JARVIS";
    public string CallerId { get; init; } = "";
    public string Transport { get; init; } = "TLS";
    public bool UseTls { get; init; } = true;
    public bool Srtp { get; init; } = true;
    public int RegistrationInterval { get; init; } = 300;
    public string OutboundProxy { get; init; } = "";
    public string[] AllowedDirections { get; init; } = ["outbound"];
    public string DefaultUsage { get; init; } = "private";
    public string[] TrustedPeers { get; init; } = [];
    public string IncomingMode { get; init; } = "OFF";
    public string[] IncomingAllowlist { get; init; } = [];

    public void Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-zA-Z0-9_-]{1,40}$") || Uri.CheckHostName(Server) == UriHostNameType.Unknown ||
            Port is < 1 or > 65535 || RegistrationInterval is < 60 or > 3600)
            throw new JarvisException("SIP-Konto: ID, Server, Port oder Registrierungsintervall ungültig.");
        foreach (var value in new[] { Username, AuthId, Domain, CallerId, DisplayName, OutboundProxy })
            if (value.Length > 250 || value.IndexOfAny(['\r', '\n', '"', '<', '>']) >= 0)
                throw new JarvisException("SIP-Feld enthält unzulässige Zeichen.");
        if (!Regex.IsMatch(Username, "^[a-zA-Z0-9_.+-]{1,120}$") ||
            (Domain.Length > 0 && Uri.CheckHostName(Domain) == UriHostNameType.Unknown))
            throw new JarvisException("SIP-Benutzername oder Domain ungültig.");
        if (CallerId.Length > 0 && !Regex.IsMatch(CallerId, "^[a-zA-Z0-9_.+-]{1,120}$")) throw new JarvisException("Caller-ID enthält unzulässige SIP-URI-Zeichen.");
        if (OutboundProxy.Length > 0 && !Regex.IsMatch(OutboundProxy, @"^sips?:[a-zA-Z0-9.\-]+(:[0-9]{1,5})?(;transport=(udp|tcp|tls))?$")) throw new JarvisException("Outbound-Proxy muss eine SIP-Host-URI ohne Zugangsdaten sein.");
        if (Transport is not ("UDP" or "TCP" or "TLS") || UseTls != (Transport == "TLS") || (Srtp && !UseTls))
            throw new JarvisException("SIP: TLS-Schalter muss zum Transport passen; SDES-SRTP benötigt TLS.");
        if (IncomingMode is not ("OFF" or "KNOWN_CONTACTS_ONLY" or "ALLOWLIST" or "ALL_CALLERS" or "UNKNOWN_CALLERS_TO_SCREENING"))
            throw new JarvisException("Ungültiger Modus für eingehende Anrufe.");
        if (AllowedDirections.Any(x => x is not ("inbound" or "outbound"))) throw new JarvisException("Ungültige Anrufrichtung.");
        if (TrustedPeers.Any(x => !System.Net.IPAddress.TryParse(x, out _))) throw new JarvisException("TrustedPeers benötigt exakte PBX-IP-Adressen.");
    }
}

public static class PhonePolicy
{
    public static string Number(string number, JsonObject cfg)
    {
        if (!Regex.IsMatch(number, @"^\+[1-9]\d{6,14}$")) throw new JarvisException("E.164-Nummer erforderlich, zum Beispiel +49301234567.");
        var blocked = new[] { "+49900", "+49137", "+49180", "+4484", "+4487", "+449", "+1900", "+979", "+882", "+883" }
            .Concat(Strings(cfg["blockedPrefixes"]));
        if (blocked.Any(number.StartsWith)) throw new JarvisException("Diese Rufnummer ist durch die Telefonie-Policy gesperrt.", 403);
        var countries = Strings(cfg["allowedCountries"]);
        if (countries.Count == 0 || !countries.Any(number.StartsWith))
            throw new JarvisException("Ländervorwahl zunächst unter Telefonie freigeben.", 403);
        var allow = Strings(cfg["allowedNumbers"]);
        if (allow.Count > 0 && !allow.Contains(number)) throw new JarvisException("Rufnummer nicht in der Allowlist.", 403);
        return number;
    }

    public static int Duration(JsonObject request, JsonObject cfg) => Math.Clamp(
        request["max_duration"]?.GetValue<int>() ?? 300, 30, Math.Clamp(cfg["maxCallDuration"]?.GetValue<int>() ?? 600, 30, 1800));
    public static List<string> Strings(JsonNode? node) => node is JsonArray array ? array.Select(x => x!.GetValue<string>()).ToList() : [];
    public static string Route(JsonObject cfg, IReadOnlyList<SipAccount> accounts, string number, string usage, string group, string taskType, int hour)
    {
        foreach (var rule in cfg["routingRules"]?.AsArray() ?? [])
        {
            if (rule is not JsonObject r) continue;
            bool Match(string key, string actual) => r[key] is null || r[key]!.GetValue<string>() == actual;
            var prefix = r["prefix"]?.GetValue<string>() ?? "";
            var country = r["country"]?.GetValue<string>() ?? "";
            var start = r["startHour"]?.GetValue<int>() ?? 0;
            var end = r["endHour"]?.GetValue<int>() ?? 24;
            var hours = start <= end ? hour >= start && hour < end : hour >= start || hour < end;
            if (hours && number.StartsWith(prefix, StringComparison.Ordinal) && number.StartsWith(country, StringComparison.Ordinal) &&
                Match("usage", usage) && Match("contactGroup", group) && Match("taskType", taskType))
                return r["accountId"]?.GetValue<string>() ?? throw new JarvisException("Routing-Regel ohne Konto.");
        }
        return accounts.FirstOrDefault(a => a.Enabled && a.DefaultUsage == usage && a.AllowedDirections.Contains("outbound"))?.Id
            ?? cfg["defaultAccount"]?.GetValue<string>() ?? throw new JarvisException("Kein passendes SIP-Konto konfiguriert.", 409);
    }
}
