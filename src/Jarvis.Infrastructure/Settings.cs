using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class Settings(IDocumentStore store, Vault vault)
{
    public static readonly HashSet<string> Sections = ["ai", "internet", "google", "microsoft", "twilio", "obsidian", "voice", "phone", "sip", "nextcloud", "immich", "autonomy", "budget", "events", "local-network"];
    public async Task<JsonObject> GetAsync(string owner, string section, CancellationToken ct) =>
        (await store.GetAsync(owner, "settings", section, ct))?.Data ?? new JsonObject();
    public async Task SaveAsync(string owner, string section, JsonObject data, CancellationToken ct)
    {
        if (!Sections.Contains(section)) throw new JarvisException("Unbekannter Einstellungsbereich.");
        if (data.ToJsonString().Length > 20000) throw new JarvisException("Konfiguration zu groß.");
        var copy = data.DeepClone().AsObject();
        if(section=="local-network") {
            var entries=PhonePolicy.Strings(copy["allowlist"]);
            if(entries.Count>100)throw new JarvisException("Allowlist maximal 100 Einträge.");
            copy["allowlist"]=new JsonArray(entries.Select(e=>(JsonNode?)JsonValue.Create(LocalNetworkPolicy.Entry(e))).ToArray());
            if(copy["ports"] is not JsonArray ports || ports.Count>30 || ports.Any(p=>p is not JsonValue v || !v.TryGetValue<int>(out var number) || number is <1 or >65535))throw new JarvisException("Bis zu 30 einzelne erlaubte Ports erforderlich.");
            if(copy["discoveryEnabled"]?.GetValue<bool>()==true && copy["probesEnabled"]?.GetValue<bool>()!=true)throw new JarvisException("Dienstprüfung benötigt explizite Host-/Portfreigabe.");
        }
        if (section == "budget") {
            foreach (var key in new[] { "dailySoft", "dailyHard", "monthlySoft", "monthlyHard" })
                if (copy[key] is JsonNode limit && (limit.GetValue<decimal>() < 0 || limit.GetValue<decimal>() > 100000)) throw new JarvisException("Budget muss zwischen 0 und 100.000 USD liegen.");
            if (copy["mode"]?.GetValue<string>() is string mode && mode is not ("economy" or "balanced" or "best_value" or "maximum_quality" or "custom")) throw new JarvisException("Ungültiger Budgetmodus.");
        }
        if (section == "autonomy" && copy["level"]?.GetValue<int>() is < 0 or > 3) throw new JarvisException("Autonomie-Level muss 0–3 sein.");
        if (section == "events" && (copy["quietStart"]?.GetValue<int>() is < 0 or > 23 || copy["quietEnd"]?.GetValue<int>() is < 0 or > 23)) throw new JarvisException("Ruhezeit benötigt Stunden 0–23.");
        if (section == "phone") {
            if (copy["provider"]?.GetValue<string>() is string provider && provider is not ("sip" or "twilio")) throw new JarvisException("Provider muss sip oder twilio sein.");
            foreach (var key in new[] { "allowedCountries", "allowedNumbers", "blockedPrefixes" })
                if (copy[key] is JsonNode list && (list is not JsonArray array || array.Count > 100 || array.Any(v => v is not JsonValue value || !value.TryGetValue<string>(out var s) || !System.Text.RegularExpressions.Regex.IsMatch(s, "^\\+[1-9][0-9]{0,14}$")))) throw new JarvisException("Rufnummernlisten benötigen E.164-Präfixe mit +.");
        }
        if (section is "nextcloud" or "immich" && copy["enabled"]?.GetValue<bool>() == true) _ = ConnectorHttp.BaseUrl(copy);
        if (section == "sip")
        {
            var ids = new HashSet<string>();
            foreach (var account in copy["accounts"]?.AsArray() ?? [])
            {
                var obj = account?.AsObject() ?? throw new JarvisException("SIP-Konto ungültig.");
                var parsed = System.Text.Json.JsonSerializer.Deserialize<SipAccount>(obj.ToJsonString(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
                parsed.Validate();
                if (!ids.Add(parsed.Id) || ids.Count > 8) throw new JarvisException("Maximal acht eindeutige SIP-Konten erlaubt.");
                obj.Remove("owner");
                if (obj.Remove("password", out var password) && !string.IsNullOrEmpty(password?.GetValue<string>()))
                    await vault.PutAsync(owner, "sip.account." + parsed.Id, password.GetValue<string>(), ct);
            }
            copy.Remove("accountPassword", out var accountPassword);
            copy.Remove("accountSecretId", out var accountSecretId);
            if (!string.IsNullOrEmpty(accountPassword?.GetValue<string>())) {
                var accountId = accountSecretId?.GetValue<string>() ?? "";
                if (!ids.Contains(accountId)) throw new JarvisException("Passwort benötigt die ID eines konfigurierten SIP-Kontos.");
                await vault.PutAsync(owner, "sip.account." + accountId, accountPassword.GetValue<string>(), ct);
            }
        }
        foreach (var key in new[] { "apiKey", "clientSecret", "authToken", "password" })
            if (copy.Remove(key, out var secret) && secret is not null && secret.GetValue<string>().Length > 0)
                await vault.PutAsync(owner, section + "." + key, secret.GetValue<string>(), ct);
        if (section == "ai" && copy["baseUrl"] is JsonNode url)
        {
            if (!Uri.TryCreate(url.GetValue<string>(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new JarvisException("Ungültige Provider-URL.");
            if (uri.Scheme == "http" && uri.Host != "ollama" && !uri.IsLoopback) throw new JarvisException("Externe KI-Provider benötigen HTTPS.");
        }
        await store.PutAsync(owner, "settings", section, copy, ct);
    }
    public async Task RequireAsync(string owner, string section, CancellationToken ct)
    {
        if ((await GetAsync(owner, section, ct))["enabled"]?.GetValue<bool>() != true) throw new JarvisException(section + " ist deaktiviert.", 409);
    }
}
