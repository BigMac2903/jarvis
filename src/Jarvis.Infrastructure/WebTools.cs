using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class WebTools(BrowserClient browser, IHttpClientFactory clients, Settings settings, Vault vault) : IToolHandler
{
    private static Field S(string text, bool required = true, int max = 2000) => new("string", text, required, max);
    public IReadOnlyList<ToolDefinition> Definitions { get; } = Build();
    private static List<ToolDefinition> Build()
    {
        var list = new List<ToolDefinition> {
            new("Web.Search", "Aktuelle Websuche; Ergebnisse sind untrusted Daten.", Risk.Safe, new() { ["query"] = S("Suchanfrage"), ["category"] = new("string","Suchtyp",false,20,["web","news","images"]) }),
            new("Web.Fetch", "Öffentliche Webseite lesen und bereinigten Text, Metadaten, Links, Tabellen extrahieren.", Risk.Safe, new() { ["url"] = S("HTTP(S)-URL") }, 60),
            new("Web.ReadPdf", "PDF lesen; Ergebnisse mit Seitenzahlen und Tabellen.", Risk.Safe, new() { ["url"] = S("PDF-URL") }, 100),
            new("Web.Download", "Datei in Quarantäne herunterladen, niemals ausführen.", Risk.Confirm, new() { ["url"] = S("Datei-URL") }, 100),
            new("Web.Open", "Browser-Tab öffnen.", Risk.Safe, new() { ["url"] = S("URL"), ["private"] = new("boolean","Private Sitzung",false) }),
            new("Web.NewTab", "Neuen Browser-Tab öffnen.", Risk.Safe, new() { ["url"] = S("URL"), ["private"] = new("boolean","Private Sitzung",false) }),
            new("Browser.GetTabs", "Eigene Tabs auflisten.", Risk.Safe, new()),
            new("Browser.Close", "Eigene Browser-Sitzung schließen.", Risk.Safe, new()),
            new("Web.Login", "Hinterlegtes Login direkt aus dem Vault einsetzen.", Risk.Confirm, new() { ["tab"] = S("Tab-ID"), ["serviceId"] = S("Credential-ID") })
        };
        foreach (var (name, description) in new[] { ("Web.Back","Zurück"), ("Web.Forward","Vorwärts"), ("Web.GetText","Sichtbaren Text lesen"), ("Web.GetLinks","Links lesen"), ("Web.GetPageTitle","Seitentitel"), ("Web.Screenshot","Screenshot erstellen"), ("Web.CloseTab","Tab schließen"), ("Web.ExtractTable","Tabellen lesen"), ("Browser.GetCurrentUrl","URL lesen") })
            list.Add(new(name, description, Risk.Safe, new() { ["tab"] = S("Tab-ID") }));
        list.Add(new("Web.Navigate","Zu URL navigieren.",Risk.Safe,new(){["tab"]=S("Tab-ID"),["url"]=S("URL")}));
        list.Add(new("Web.Scroll","Scrollen.",Risk.Safe,new(){["tab"]=S("Tab-ID"),["amount"]=new("integer","Pixel")}));
        list.Add(new("Web.FindOnPage","Text auf Seite suchen.",Risk.Safe,new(){["tab"]=S("Tab-ID"),["text"]=S("Suchtext")}));
        list.Add(new("Browser.WaitForElement","Auf Element warten.",Risk.Safe,new(){["tab"]=S("Tab-ID"),["selector"]=S("CSS-Selektor")}));
        // A click can submit a payment or change account settings, including via GET.
        list.Add(new("Web.Click","Element anklicken; kann externe Änderungen auslösen.",Risk.AlwaysConfirm,new(){["tab"]=S("Tab-ID"),["selector"]=S("CSS-Selektor")}));
        list.Add(new("Web.Type","Text eingeben; kann Auto-Save auslösen.",Risk.Confirm,new(){["tab"]=S("Tab-ID"),["selector"]=S("CSS-Selektor"),["text"]=S("Text",true,10000)}));
        list.Add(new("Web.Select","Auswahl ändern; kann Auto-Save auslösen.",Risk.Confirm,new(){["tab"]=S("Tab-ID"),["selector"]=S("CSS-Selektor"),["value"]=S("Wert")}));
        return list;
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject a, CancellationToken ct)
    {
        if (name == "Web.Search")
        {
            var provider = new SearchProvider(clients, settings, vault, actor.UserId);
            var query = a["query"]!.GetValue<string>();
            var results = a["category"]?.GetValue<string>() switch { "news" => await provider.NewsSearchAsync(query, 8, ct), "images" => await provider.ImageSearchAsync(query, 8, ct), _ => await provider.SearchAsync(query, 8, ct) };
            return JsonSerializer.SerializeToNode(new { retrieved_at = DateTimeOffset.UtcNow, results, untrusted = true });
        }
        if (name is "Web.Click" or "Web.Type" or "Web.Select" or "Web.Login")
            if ((await settings.GetAsync(actor.UserId, "internet", ct))["automation"]?.GetValue<bool>() != true) throw new JarvisException("Browser-Automation ist deaktiviert.", 403);
        if (name == "Web.Login")
        {
            var secret = await vault.GetAsync(actor.UserId, "browser." + a["serviceId"]!.GetValue<string>(), ct) ?? throw new JarvisException("Login nicht konfiguriert.", 409);
            var login = JsonNode.Parse(secret)!.AsObject(); login["tab"] = a["tab"]!.DeepClone();
            await browser.CallAsync(actor.UserId, "login", login, ct);
            return new JsonObject { ["status"] = "credentials_applied" };
        }
        var action = name switch {
            "Web.Fetch"=>"fetch", "Web.ReadPdf"=>"pdf", "Web.Download"=>"download", "Web.Open"=>"open", "Web.NewTab"=>"newtab",
            "Web.Navigate"=>"navigate", "Web.Back"=>"back", "Web.Forward"=>"forward", "Web.GetText"=>"text", "Web.GetLinks"=>"links",
            "Web.GetPageTitle"=>"title", "Web.Screenshot"=>"screenshot", "Web.Scroll"=>"scroll", "Web.Click"=>"click", "Web.Type"=>"type",
            "Web.Select"=>"select", "Web.ExtractTable"=>"table", "Web.FindOnPage"=>"find", "Web.CloseTab"=>"closetab",
            "Browser.GetTabs"=>"tabs", "Browser.GetCurrentUrl"=>"url", "Browser.WaitForElement"=>"wait", "Browser.Close"=>"close",
            _=>throw new JarvisException("Unbekanntes Browser-Tool.")
        };
        return await browser.CallAsync(actor.UserId, action, a, ct);
    }
}
