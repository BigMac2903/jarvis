using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Domain;
using DnsClient;

namespace Jarvis.LocalNetwork;

public interface ILocalResolver { Task<IPAddress[]> Resolve(string host, CancellationToken ct); }
public sealed class LocalResolver(IConfiguration configuration) : ILocalResolver
{
    public async Task<IPAddress[]> Resolve(string host, CancellationToken ct)
    {
        if(IPAddress.TryParse(host,out var ip))return [ip];
        if(!IPAddress.TryParse(configuration["LOCAL_NETWORK_DNS"],out var dns))throw new JarvisException("Lokaler DNS-Server muss explizit konfiguriert sein; kein öffentlicher DNS-Fallback.",403);
        var allowed=(configuration["LOCAL_NETWORK_ALLOWLIST"]??"").Split([',','\n','\r'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var denied=(configuration["LOCAL_NETWORK_DENYLIST"]??"").Split([',','\n','\r'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Concat(["172.30.254.0/28","172.30.254.16/28"]).ToArray();
        LocalNetworkPolicy.CheckAddresses(dns.ToString(),[dns],allowed,denied);
        var client=new LookupClient(new LookupClientOptions(dns){UseCache=false,Retries=0,Timeout=TimeSpan.FromSeconds(3),UseTcpFallback=true,EnableAuditTrail=false,AutoResolveNameServers=false});
        var a=await client.QueryAsync(host,QueryType.A,cancellationToken:ct);
        var aaaa=await client.QueryAsync(host,QueryType.AAAA,cancellationToken:ct);
        if(a.HasError||aaaa.HasError)throw new JarvisException("Lokale DNS-Auflösung unvollständig; Zugriff verweigert.",403);
        return a.Answers.ARecords().Select(x=>x.Address).Concat(aaaa.Answers.AaaaRecords().Select(x=>x.Address)).Distinct().ToArray();
    }
}
public interface ILocalDialer { ValueTask<Stream> Connect(IPAddress ip, int port, CancellationToken ct); }
public sealed class LocalDialer : ILocalDialer
{
    public async ValueTask<Stream> Connect(IPAddress ip, int port, CancellationToken ct)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(new IPEndPoint(ip, port), ct); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
}
public sealed class LocalTransport(ILocalResolver resolver, ILocalDialer dialer)
{
    public async Task<LocalResponse> Execute(LocalRequest input, string[] ceiling, int[] ports, string[] denied, bool probes, CancellationToken ct)
    {
        if (input.Operation is not ("Http" or "CheckHost" or "CheckPort")) throw new JarvisException("Unbekannte Netzwerkoperation.");
        if (input.Allowlist.Length > 100 || input.Ports.Length > 30) throw new JarvisException("Policy zu groß.");
        foreach (var entry in input.Allowlist) _ = LocalNetworkPolicy.Entry(entry);
        var uri = LocalNetworkPolicy.Target(input.Url, ports.Intersect(input.Ports).ToArray());
        var host = LocalNetworkPolicy.Host(uri.IdnHost);
        // Do not leak unapproved internal names even to the configured DNS server.
        if(!IPAddress.TryParse(host,out _) && (!ceiling.Contains(host,StringComparer.OrdinalIgnoreCase)||!input.Allowlist.Contains(host,StringComparer.OrdinalIgnoreCase)))throw new JarvisException("DNS-Name nicht freigegeben.",403);
        var addresses = await resolver.Resolve(host, ct);
        LocalNetworkPolicy.CheckAddresses(host, addresses, ceiling, denied);
        LocalNetworkPolicy.CheckAddresses(host, addresses, input.Allowlist, denied);
        var watch = Stopwatch.StartNew();
        var ip = addresses[0]; // Pin one verified IP. No second DNS lookup or proxy fallback.
        if (input.Operation != "Http") {
            if (!probes || !input.ProbeEnabled) throw new JarvisException("Host-/Portprüfungen sind nicht ausdrücklich aktiviert.", 403);
            if (input.Operation == "CheckPort") { await using var connection = await dialer.Connect(ip, uri.Port, ct); }
            return new(200, ip.ToString(), watch.ElapsedMilliseconds, "application/json", input.Operation == "CheckHost" ? "DNS/IP policy valid; not a reachability test" : "TCP connection established; service identity not verified");
        }
        if (input.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE")) throw new JarvisException("HTTP-Methode gesperrt.");
        if (input.Credential is not null && uri.Scheme != "https" && !input.AllowHttpCredentials) throw new JarvisException("Credentials erfordern HTTPS oder eine explizite Ausnahme im Dienst.", 403);
        using var handler = new SocketsHttpHandler {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = (_, cancellation) => dialer.Connect(ip, uri.Port, cancellation)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(new HttpMethod(input.Method), uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.UserAgent.ParseAdd("JARVIS-LocalNetwork/1.0");
        if (input.BodyBase64 is not null) {
            if (input.Method is "GET" or "HEAD") throw new JarvisException("Keine Lesemethode mit Body.");
            if (input.BodyBase64.Length > 7_000_000) throw new JarvisException("Upload zu groß.", 413);
            var bytes = Convert.FromBase64String(input.BodyBase64);
            if (bytes.Length > 5_000_000) throw new JarvisException("Upload maximal 5 MB.", 413);
            if (input.ContentType is not ("application/json" or "text/plain" or "application/octet-stream")) throw new JarvisException("Content-Type gesperrt.");
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new MediaTypeHeaderValue(input.ContentType);
        }
        var secrets = new List<string>();
        if (input.Credential is LocalCredential credential) {
            if (credential.Value.Length is < 1 or > 8192 || credential.Value.Any(char.IsControl) || credential.Username.Any(char.IsControl)) throw new JarvisException("Ungültige Credentials.");
            secrets.Add(credential.Value);
            switch (credential.Type) {
                case "Bearer": request.Headers.Authorization = new("Bearer", credential.Value); break;
                case "Basic":
                    if (credential.Username.Contains(':')) throw new JarvisException("Basic-Benutzername ungültig.");
                    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Value));
                    secrets.Add(basic); request.Headers.Authorization = new("Basic", basic); break;
                case "Header":
                    if (credential.Header is not ("X-Api-Key" or "Api-Key" or "X-Auth-Token")) throw new JarvisException("Credential-Header nicht erlaubt.");
                    request.Headers.Add(credential.Header, credential.Value); break;
                default: throw new JarvisException("Credential-Typ nicht erlaubt.");
            }
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and <= 399) throw new JarvisException("Redirect blockiert; Ziel separat registrieren. Credentials werden nicht weitergeleitet.", 409);
        if (!response.IsSuccessStatusCode) throw new JarvisException("Lokaler Dienst meldet HTTP " + (int)response.StatusCode + ".", 502);
        var max = input.Download ? 5_000_000 : 128_000;
        if (response.Content.Headers.ContentLength > max) throw new JarvisException("Antwort zu groß.", 413);
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0) { if (output.Length + count > max) throw new JarvisException("Antwort zu groß.", 413); output.Write(buffer, 0, count); }
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var content = output.ToArray();
        if (input.Download) return new((int)response.StatusCode, ip.ToString(), watch.ElapsedMilliseconds, contentType, BodyBase64: Convert.ToBase64String(content), Sha256: Convert.ToHexString(SHA256.HashData(content)));
        if (!contentType.StartsWith("text/") && contentType != "application/json" && !contentType.EndsWith("+json"))
            throw new JarvisException("Binärdaten nur über DownloadFile in Quarantäne abrufen.", 415);
        var text = Encoding.UTF8.GetString(content);
        foreach (var secret in secrets) {
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal)
                .Replace(JsonSerializer.Serialize(secret)[1..^1], "[REDACTED]", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(secret), "[REDACTED]", StringComparison.Ordinal);
        }
        if(contentType=="application/json"||contentType.EndsWith("+json")) {
            try {
                var json=JsonNode.Parse(text,documentOptions:new(){MaxDepth=32});
                void Redact(JsonNode? node) {
                    if(node is JsonObject obj)foreach(var key in obj.Select(k=>k.Key).ToArray()) {
                        if(Regex.IsMatch(key,"password|passwd|secret|token|api.?key|authorization|cookie",RegexOptions.IgnoreCase))obj[key]="[REDACTED]";
                        else Redact(obj[key]);
                    }else if(node is JsonArray array)foreach(var item in array)Redact(item);
                }
                Redact(json);text=json?.ToJsonString()??"null";
            }catch(JsonException){throw new JarvisException("Lokale JSON-Antwort ist ungültig oder zu tief verschachtelt.",422);}
        }
        return new((int)response.StatusCode, ip.ToString(), watch.ElapsedMilliseconds, contentType, Text: text);
    }
}
