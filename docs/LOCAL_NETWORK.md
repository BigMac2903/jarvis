# Local Network – Anforderungen 201–213

Der lokale Zugriff ist ein eigener, standardmäßig deaktivierter Dienst. Er ist **keine Ausnahme im Research-Browser**. Alle Aktionen verwenden registrierte Dienste, eine serverseitige Allowlist, eine zusätzliche Dashboard-Allowlist, freigegebene Ports und die normale Toolpolicy. Der Benutzer muss keinen beliebigen URL-Proxy öffnen.

## Sicherheitsarchitektur

```text
Öffentliche Recherche → jarvis-browser → jarvis-egress → nur öffentliche IPs
Lokaler Auftrag      → Core           → jarvis-local-network-agent → erlaubte LAN-/VPN-Ziele
```

Der normale Chat, Research, Voice und allgemeine Hintergrundaufträge bekommen keine LocalNetwork-Tools. Ein serverseitiger Scope verweigert auch erfundene direkte Toolaufrufe. Im Dashboard gibt es einen eigenen lokalen Auftrag mit getrenntem Verlauf. Nur nach separater Zustimmung dürfen dessen Ergebnisse an den konfigurierten KI-Anbieter gehen. Direkte Toolaktionen im Local-Network-Dashboard benötigen dafür keinen KI-Aufruf.

Der lokale Dienst hat keinen veröffentlichten Port, keine Hostmounts, keine Shell-Tools, keine Docker-Socket-Verbindung und keine Browserengine. Er läuft ohne Linux-Capabilities, mit schreibgeschütztem Dateisystem und begrenzten Ressourcen. API und lokaler Agent teilen ausschließlich `local-control`; der Agent besitzt zusätzlich `local-egress`. Der Research-Browser hängt an keinem dieser beiden Netze.

Die Allowlist wird unmittelbar vor jeder Verbindung geprüft. Alle DNS-Antworten müssen erlaubt sein; eine einzelne private/fremde Antwort außerhalb des erlaubten Bereichs verwirft das gesamte Ergebnis. Der Socket verbindet eine bereits geprüfte IP, während Hostheader und TLS-Prüfung beim ursprünglichen Namen bleiben. Kein Systemproxy, keine Redirects, keine Cookies und kein TLS-Bypass. Die Umsetzung verwendet [.NET ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0) und [IPNetwork.Contains](https://learn.microsoft.com/en-us/dotnet/api/system.net.ipnetwork.contains?view=net-10.0).

## 1. Server explizit freigeben

Vor Update sichern und `install.ps1 -ConfigureOnly` bzw. `bash install.sh --configure-only` erneut ausführen. Dadurch wird der interne `LOCAL_NETWORK_TOKEN` ergänzt. Keine echten Gerätezugangsdaten in `.env`, Prompts oder Supporttickets schreiben.

Beispiel in `.env`, an das eigene Netz anpassen:

```dotenv
COMPOSE_PROFILES=local-network
LOCAL_NETWORK_ENABLED=true
LOCAL_NETWORK_ALLOWLIST=192.168.178.0/24,nas.home,homeassistant.home
LOCAL_NETWORK_PORTS=80,443,8123
LOCAL_NETWORK_PROBES_ENABLED=false
LOCAL_NETWORK_DNS=192.168.178.1
LOCAL_NETWORK_DENYLIST=
```

Mehrere Profile kommagetrennt, etwa `sip,local-network`. Anschließend:

```bash
docker compose --profile local-network up -d --build --wait
```

Die serverseitige Allowlist ist die Obergrenze, nicht die vollständige Freigabe. Das Dashboard kann sie einschränken, nicht erweitern. Erlaubt sind RFC1918, IPv6-ULA und ausdrücklich freigegebene VPN-Adressen aus `100.64.0.0/10`. Loopback, Link-Local einschließlich Cloud-Metadatenadressen, Multicast, IPv4-mapped IPv6 und öffentliche IPs bleiben gesperrt. Zusätzliche Denylist-Einträge haben Vorrang.

Reserviert und immer gesperrt sind `172.30.254.0/28` und `172.30.254.16/28`, die beiden Docker-Netze des Agenten. Bei einem Konflikt mit vorhandenen Netzen IPAM und zusätzliche Denylist gemeinsam administrativ anpassen. Nicht bloß die Kontrollnetz-Sperre entfernen. Eine Hostfirewall sollte den Container-Egress zusätzlich auf Zielnetze/Ports und den eigenen DNS-Server begrenzen; die Anwendungspolicy ersetzt keine Firewall gegen einen vollständig kompromittierten Container. Docker-Netzisolation ist in dieser Entwicklungsumgebung noch nicht praktisch abgenommen.

## 2. DNS nur über das eigene Netz

Für Namen einen einzelnen privaten DNS-Server als `LOCAL_NETWORK_DNS` angeben. Auch dessen Adresse muss von der Server-Allowlist umfasst sein. Der Agent fragt diesen über DNS/53 ab; kein öffentlicher Resolver-Fallback und keine automatische Nameservererkennung. Ohne diese Einstellung sind nur IP-Ziele möglich. [DnsClient](https://dnsclient.michaco.net/docs/DnsClient.LookupClientOptions.html) wird mit explizitem Nameserver, ohne Cache und ohne automatische Nameserverauflösung verwendet.

Ein Name wie `nas.home` muss **zusätzlich** zum passenden IP-/CIDR-Eintrag in beiden Allowlists stehen. Nur den Namen einzutragen reicht absichtlich nicht: nach einer DNS-Änderung darf er nicht plötzlich auf ein anderes internes Gerät zeigen. A und AAAA müssen vollständig auf erlaubte Ziele zeigen. Ein DNS-Fehler führt nicht zu einem unsicheren Fallback.

`.local` funktioniert nur, wenn dein angegebener DNS-Server diese Zone tatsächlich beantwortet. Multicast-mDNS, Bonjour, Broadcast-Discovery und Netzwerknamensuche sind nicht implementiert. Keine interne Zone ungeprüft an öffentliche DNS-Server weiterleiten lassen.

## 3. Dashboard und Service Registry

Settings → Local Network enthält die Aktivierung, Dashboard-Allowlist, Ports, Probe-/Watcher-Opt-in und Modellkontextfreigabe. Die eigene Seite **Local Network** ergänzt:

- Netz/Host hinzufügen und entfernen, Zugriff deaktivieren, Servergrenzen prüfen.
- Dienste mit Name, Host, HTTP/HTTPS, Port, Typ, Secret-Referenz und Read/Write/Admin erfassen.
- Status, Latenz, letzte erfolgreiche Verbindung und letzte Prüfung anzeigen.
- Verbindung gezielt prüfen, Dienst aktivieren/deaktivieren oder entfernen.
- Credentials maskiert speichern/entfernen, lokale Aufträge und Dateiquarantäne verwalten.

Neue Dienste sind deaktiviert und haben keine freigegebenen Lesepfade. Exakte Lesepfade wie `/api/status` bevorzugen. Ein abschließender Slash erlaubt Unterpfade; `/` erlaubt alle GET-Pfade des Dienstes. **GET bedeutet nicht bei jedem Gerät nebenwirkungsfrei.** Deshalb nur tatsächlich geprüfte Endpunkte als Lesepfade eintragen. Keine unkontrollierte Freigabe von Router-/Drucker-Adminoberflächen.

Jede Änderung eines Dienstes erzeugt eine neue `revision`. Tools müssen die aktuelle Version aus `LocalNetwork.ListServices` oder dem Dashboard angeben. Eine ausstehende Freigabe für eine alte Revision kann nicht auf ein nachträglich geändertes Ziel angewendet werden. Freigabekarten zeigen den aktuellen Host/Port und markieren einen Versionskonflikt.

Credentials unterstützen Basic, Bearer oder die festen Header X-Api-Key, Api-Key und X-Auth-Token. Sie werden unter einer eigenen Secret-Referenz verschlüsselt abgelegt und nur zwischen Core und authentifiziertem Agenten verwendet. Standardmäßig sind Credentials bei HTTP verboten; eine Ausnahme muss beim Dienst ausdrücklich aktiviert werden. Bevorzugt HTTPS mit vertrauenswürdigem Zertifikat oder zusätzlich ein vertrauenswürdiges VPN verwenden. Niemals Zertifikatsprüfung abschalten.

Entfernen eines Dienstes löscht nicht automatisch einen möglicherweise gemeinsam verwendeten Secret-Eintrag. Diesen separat entfernen. Interne JSON-Antworten werden nach typischen Secret-Feldnamen und bekannten Credentialwerten redigiert; das ist keine Garantie, dass beliebiger Freitext keine vertraulichen Daten enthält. Modellkontext daher nur bewusst aktivieren. Raw-Downloads bleiben außerhalb des Modellkontexts in Quarantäne.

## 4. Werkzeuge und Grenzen

| Tool | Verhalten / Standardpolicy |
|---|---|
| LocalNetwork.ListServices | Registry ohne Secrets lesen; AUTO im lokalen Scope |
| LocalNetwork.HttpGet | Nur administrativ freigegebener GET-Pfad; AUTO |
| LocalNetwork.OpenWebUi | HTML/Text abrufen und Link zurückgeben; kein JavaScript oder Research-Browser |
| LocalNetwork.CheckHost | Genau einen registrierten Namen/IP prüfen; DNS-Erfolg ist kein Erreichbarkeitsbeweis |
| LocalNetwork.CheckPort | Genau eine TCP-Verbindung, kein Portbereich; ASK und zusätzlich Probe-Opt-in |
| LocalNetwork.HttpPost | JSON-POST; ALWAYS_CONFIRM |
| LocalNetwork.CallApi | GET/HEAD/POST/PUT/PATCH/DELETE, keine freien Header; ALWAYS_CONFIRM |
| LocalNetwork.DownloadFile | Bis 5 MB in verschlüsselte Quarantäne; ASK |
| LocalNetwork.UploadFile | Bereitgestellte Datei per PUT, maximal 5 MB; ALWAYS_CONFIRM |

Generische Schreib-APIs sind absichtlich strenger als das gewünschte NOTIFY-Beispiel: Ohne dienstspezifischen Adapter kann eine scheinbar normale API-Aktion Firmware, Benutzerrechte oder Gerätezustand ändern. Read/Write/Admin ist eine zusätzliche Grenze, keine Umgehung der Bestätigung; DELETE verlangt Admin. Ein zukünftiger adaptergeprüfter, reversibler APIWrite-Pfad könnte NOTIFY nutzen. Freie DELETE-/Firmware-/Admin-Aufrufe werden heute nicht automatisch ausgeführt.

HTTP-Antworten maximal 128 KB, Requests maximal 20 Sekunden, ausschließlich HTTP/1.1 und feste Methoden; Downloads/Uploads 5 MB. Kein beliebiges CONNECT, keine URL-Credentials, keine Querystrings, keine benutzerdefinierten Header, keine Multipart-/Digest-/WebSocket-/WebDAV-/SQL-/MQTT-Kommandos. Query-basierte APIs benötigen einen passenden Adapter; Tokens gehören nie in URLs.

FRITZ!Box, NAS, Home Assistant, Proxmox, Kameras und Drucker können mit den generischen HTTP-Funktionen angesprochen werden, **soweit ihre konkreten APIs diese Authentifizierung und Methoden unterstützen**. Ein Porttest bei MQTT oder einer Datenbank ist noch kein vollständiger Protokolladapter. Es werden keine Geräteeigenschaften erfunden.

Dateien sind verschlüsselte temporäre DB-Datensätze, maximal zehn gleichzeitig, eine Stunde abrufbar. Keine automatische Ausführung, kein direktes HTML-Rendering und kein automatischer Virenscan. Abgelaufene Einträge müssen über das Dashboard entfernt werden. Ein bewusstes Herunterladen erfolgt als Attachment; Upload an einen Dienst braucht eine eigene Freigabe. Lokale Dateipfade des Servers sind nicht auswählbar.

## 5. Begrenzte automatische Prüfung

Keine automatische Host-Discovery oder Subnetz-/Portenumeration. Optional prüft ein Worker ausschließlich **bereits registrierte**, aktivierte und einzeln mit `autoCheck` markierte Dienste alle 15 Minuten. Beide Probe-Opt-ins und `discoveryEnabled` müssen aktiv sein. Höchstens 16 Ziele pro Zyklus, drei Sekunden Pause, nur bekannte Ports: 80, 443, 631, 1883, 2283, 5000, 5001, 8006, 8080, 8123, 8443, 8883, 9100.

Er durchläuft die normale Toolpolicy. Ist CheckPort noch ASK, entsteht eine Freigabe und die automatische Prüfung dieses Eintrags pausiert, bis der Dienst erneut gespeichert wird; es werden nicht laufend neue Freigaben erzeugt. Für bewusst gewünschte wiederholte Prüfungen CheckPort explizit AUTO erlauben. Der Agent begrenzt alle Requests zusätzlich auf einen neuen Token alle zwei Sekunden, Burst zwei. Zu viele Requests erhalten HTTP 429 und werden nicht automatisch als Schreibaktion wiederholt.

Das ist eine risikoarme Erreichbarkeitsprüfung, **noch keine automatische Identifikation neuer Nextcloud-/Immich-/NAS-Geräte**. Dienstnamen und Typen stammen aus der Registry; ein offener Port wird nicht als verifizierte Produktidentität ausgegeben. Vollständige passive/gezielte Produktfingerprints und Registrierungsvorschläge bleiben offen.

## 6. VPN ohne öffentliche Gerätefreigabe

Der Agent benutzt die Routingtabelle seines Hosts/Docker-Netzes. Eine bestehende WireGuard-Verbindung zum Heimnetz oder ein Tailscale-Subnet-Router kann die freigegebenen privaten Zielnetze erreichbar machen. Ein VPN wird weder ungefragt installiert noch mit erfundenen Keys konfiguriert. Offizielle Einrichtung: [WireGuard Quick Start](https://www.wireguard.com/quickstart/) und [Tailscale Subnet Routers](https://tailscale.com/docs/features/subnet-routers).

Vorgehen: zuerst den VPN-Pfad administrativ auf dem Host bzw. Gateway einrichten, nur benötigte Heimnetzrouten zulassen, dann Docker-Egress/NAT und Rückweg prüfen. Beim VPN ebenfalls passende Firewall-/ACL-Regeln auf die Geräteports und DNS/53 begrenzen. Dieselben JARVIS-Allowlists bleiben wirksam. Keine öffentlichen Portfreigaben für NAS, Router, Drucker oder Kameras und kein Tailscale Funnel als Ersatz für privaten Zugriff. Der tatsächliche Host-/VPN-/Containerpfad muss im Zielnetz abgenommen werden; dieser Stand enthält keinen VPN-Einrichtungsassistenten.

## Migration und bestehende Connectoren

`INTERNAL_ALLOW_HOSTS` ist entfernt und wird selbst in einer alten `.env` nicht mehr als Research-Ausnahme ausgewertet. Der öffentliche Nextcloud-/Immich-HTTP-Pfad verwendet jetzt ebenfalls den öffentlichen Egress-Proxy statt eines direkten LAN-Zugriffs; nur öffentliche HTTPS-Ziele auf Port 443 sind dort erreichbar. Private Instanzen müssen explizit in Local Network registriert werden. Ihre generischen HTTP-Endpunkte sind dort nutzbar; die vollständigen bisherigen WebDAV-/Immich-Spezialadapter sind **noch nicht** durch den lokalen Agenten geroutet. Diese Grenze darf nicht durch erneutes Öffnen des Research-Proxys umgangen werden.

Bereits getrennte, ausdrücklich konfigurierte Spezialdienste wie SIP/PBX und lokale KI besitzen weiterhin eigene Protokollpfade; Local Network ist kein globaler Ersatz für jede ausgehende Verbindung des gesamten Stacks. Die neue Sicherheitsgrenze betrifft Research, generische lokale HTTP-Tools und die genannten HTTP-Connectoren.

## Abnahme

Lokal bestanden: .NET-Policy-/Transporttests für private/öffentliche Adressen, Netze, Ports, Rebinding/Mixed DNS, Credentialredaktion, Redirects und Scope-Trennung; echter eigenständiger HTTP-Agent mit Authentifizierung, deaktiviertem Default, gesperrtem Loopback und Rate Limit; Browser-Regression für abgeschaffte interne Ausnahme; mobiler Dashboardtest mit expliziten API-Fixtures.

Die HTTP-Transporttests verwenden echte HttpClient-Protokollverarbeitung mit kontrollierten In-Memory-Verbindungen. Es wurde **kein privates Gerät angesprochen oder Netz gescannt**. Docker-Engine, private DNS-/VPN-Infrastruktur und Testgeräte fehlen weiterhin. Container-Netzisolation, echte TLS-/DNS-/VPN-Verbindungen, Registry-/Freigabeabläufe mit PostgreSQL und Upload/Download an echten Diensten sind nicht als bestanden zu verstehen.
