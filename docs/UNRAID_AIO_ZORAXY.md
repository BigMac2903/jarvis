# Ein JARVIS-Container hinter Zoraxy

Ziel: **https://jarvis.mc-media.eu**. Zoraxy übernimmt Zertifikat und HTTPS.
Ein einziger JARVIS-Container enthält Nginx (nur HTTP), API/Agent, PostgreSQL 17,
Redis 7.4, Chromium-Browser und Egress-Proxy. SIP und LAN-Agent sind im Image
enthalten und bei Aktivierung Prozesse im selben Container, keine weiteren Container.
Zoraxy selbst ist dein vorhandener, separat verwalteter Proxy.

## Wichtig: bewusster Sicherheitskompromiss

Die Netzwerkisolation zwischen Browser, API und Datenbank entfällt. Die Prozesse
haben getrennte Linux-Benutzer, passende Dateirechte und eigene Umgebungsvariablen.
Chromiums Sandbox, Seccomp und `no-new-privileges` bleiben aktiv. Das ersetzt keine
getrennten Container: Ein kompromittierter Browser kann lokale Dienste erreichen.
Der Prozessmanager läuft als root mit einer begrenzten Capability-Liste zum
Benutzerwechsel und geordneten Stoppen; weder `privileged` noch `SYS_ADMIN` sind nötig.
Nur Port 8080 im Container wird als HTTP-Upstream veröffentlicht. Datenbank, Redis,
Browser und Dienst-APIs sind an Loopback gebunden. Zugriff auf den veröffentlichten
Hostport am Host/Netzwerk auf Zoraxy begrenzen; ihn NICHT am Router ins Internet leiten.
Keine Docker-Socket-Einbindung, kein Host-Netzwerk und keine eigene TLS-Verwaltung.

## Bestehende Unraid-Installation umstellen

Voraussetzungen: x86_64-Unraid, bestehende Bindmount-Installation aus diesem Projekt,
originale `.env` unter `source` sowie freier Speicher für Image und vollständige Sicherung.
Aktive Telefonate werden beim Stoppen beendet. Das Projekt ist noch kein vollständig
abgenommenes Produktivsystem; Ergebnisse des neuen Workflows `single-container` prüfen.

Im Unraid-Terminal:

```bash
cd /mnt/user/appdata/jarvis/source
git pull --ff-only origin main
bash install-unraid-aio.sh --migrate
```

Wenn `git pull` eigene Änderungen meldet: STOPP und Ausgabe prüfen. Kein `reset --hard`.
Die früher empfohlenen chmod-Korrekturen ändern keine Dateiinhalte; die lokale Änderung
an `docker/browser-seccomp.json` bleibt bestehen, AIO verwendet eine neue eigene Datei.

Der Installer baut das Image, bevor er den alten Stack stoppt. Danach erstellt er eine
konsistente Dateisicherung von PostgreSQL, Redis, API, Browser und Obsidian sowie eine
Kopie der ORIGINALEN `.env` in einem neuen privaten Ordner `backups-aio.*`.
Er prüft verfügbare Kapazität und verändert keine bestehenden Schlüssel.
Bei gescheiterter Sicherung startet er die zuvor laufenden Container wieder.
Bei gescheitertem AIO-Start bleiben die alten Container gestoppt und erhalten, um
gleichzeitigen Zugriff zweier Datenbankserver auf dieselben Dateien zu vermeiden.
Erst nach erfolgreichem Healthcheck entfernt er die alten JARVIS-Container **ohne
Volumes zu löschen**. Caddy-Daten und Sicherungen bleiben erhalten; Caddy läuft nicht mehr.

Die Sicherung enthält private Daten und Schlüssel: nicht hochladen, privat/offline sichern.
Die vorhandenen Mount-Wurzeln erhalten die Prozess-UIDs; nur im Redis-Datenordner werden
die Dateibesitzer für dessen neue UID 10004 angepasst. Die übrigen Daten werden nicht
rekursiv umberechtigt. Obsidian muss vorher dem bisherigen API-Benutzer 1654 zugänglich sein.

## Zoraxy einrichten

Am Ende gibt der Installer z. B. `http://192.168.178.20:8090` aus. Diese konkrete
Adresse verwenden, nicht das Beispiel. Die private Unraid-IP wird automatisch erkannt;
ein freier Port wird ab 8090 gesucht. Für ein anderes Interface vor dem Start
`AIO_BIND_ADDRESS` in `.env` auf die richtige private Unraid-IP setzen.

In deinem bestehenden Zoraxy:

1. HTTP-Proxy-Regel für **jarvis.mc-media.eu** anlegen oder die vorhandene bearbeiten.
2. Upstream/Ziel auf die vom Installer ausgegebene **HTTP-Adresse** setzen.
3. Upstream-TLS/HTTPS ausschalten; Zoraxy spricht intern HTTP mit JARVIS.
4. Ein gültiges Zertifikat für `jarvis.mc-media.eu` in Zoraxy zuweisen/beschaffen und
   öffentlich HTTPS verwenden. DNS muss auf deinen Zoraxy-Eingang zeigen.
5. Host und Origin nicht auf eine interne IP umschreiben. Zoraxy unterstützt WebSockets
   automatisch; WebSocket-Upgrades und Streaming dürfen von zusätzlichen Regeln nicht blockiert werden.

Zoraxy kann auf demselben oder einem anderen Rechner laufen. In einem Zoraxy-Container
bezeichnet `127.0.0.1` Zoraxy selbst, nicht Unraid: die ausgegebene LAN-IP verwenden.
Bei Unraid-Netzwerken mit Host-Zugriffsisolation muss die Erreichbarkeit separat eingerichtet
werden; der Installer ändert keine Host-Netzwerk- oder Firewall-Einstellungen.

Danach **https://jarvis.mc-media.eu** öffnen. Kein lokales Caddy-Zertifikat importieren,
keine Zertifikatsprüfung abschalten. Vorhandene Anmeldung bleibt erhalten. Bei einer
neuen Installation steht das Setup-Token ausschließlich lokal in
`/mnt/user/appdata/jarvis/START-HIER.txt`. API-Keys gehören nur in das Dashboard.

## Betrieb und Wiederherstellung

```bash
cd /mnt/user/appdata/jarvis/source
bash aio-compose.sh ps
bash aio-compose.sh logs --tail=100
```

Der Healthcheck prüft HTTP, API mit PostgreSQL/Redis und den gestarteten Browser.
Stürzt ein verwalteter Prozess ab, beendet der Manager die anderen geordnet und
Docker startet den Container neu. Beim Stoppen werden Verbraucher zuerst und die
Datenbank zuletzt beendet. Alle Dienste werden gemeinsam aktualisiert.

Für ein Backup: `bash aio-compose.sh stop`, `.env` und die fünf Datenordner privat
sichern, anschließend `bash aio-compose.sh start`. Für Updates vorher sichern,
`git pull --ff-only origin main`, dann `bash install-unraid-aio.sh` ausführen.

Rollback nicht durch gleichzeitiges Starten beider Varianten versuchen. Erst AIO stoppen.
Bei bereits verändertem Datenbestand aus der VORHER erstellten Sicherung in leere
Zielordner wiederherstellen, Original-`.env` und alten Quellstand verwenden. Bestehende
Ordner vorher verschieben/sichern, nicht über eine laufende Datenbank entpacken.
Die alten Compose-Dateien bleiben als Rückweg im Repository. Ein automatisches
Downgrade/Restore wird aus Datensicherheitsgründen nicht durchgeführt.

## Optionale Funktionen und Grenzen

- SIP: `AIO_SIP_ENABLED=true`, passendes `SIP_BIND_ADDRESS` und `SIP_ADVERTISE_ADDRESS`
  sowie PBX-/RTP-Konfiguration. `aio-compose.sh` ergänzt die Portdatei, aber keinen
  weiteren Container. SIP/RTP nicht über den HTTP-Proxy, Hostfirewall auf PBX begrenzen.
- LAN: `LOCAL_NETWORK_ENABLED=true` und gezielte `LOCAL_NETWORK_ALLOWLIST`/Ports/DNS.
  Die zusätzlichen Dashboard-Freigaben bleiben erforderlich. Keine pauschale LAN-Freigabe.
- SearXNG, Ollama und ClamAV sind **nicht** im AIO-Image enthalten. Externe Anbieter
  können konfiguriert werden; lokal aktivierte Alt-Profile führen vor Migration zum
  Abbruch, statt stillschweigend Funktionen zu entfernen. Cloud-KI braucht keinen
  weiteren lokalen Container. Das Image erfindet keine lokale KI ohne Modell.
- Der Hostport ist absichtlich HTTP für Zoraxy. Login-Cookies bleiben Secure;
  die Anmeldung direkt über `http://UNRAID-IP:PORT` ist nicht der vorgesehene Betrieb.
- Benutzer-IP-basierte API-Limits sehen zurzeit den internen Gateway-Client und gelten
  damit gemeinsam. Fremde `X-Forwarded-For`-Header werden nicht blind vertraut.
- Builds benötigen Internet und mehrere GB Speicher. Basisimage-Tags können sich
  aktualisieren; für reproduzierbaren Produktivbetrieb geprüfte Digests festlegen.

Referenzen: [Docker: mehrere Prozesse](https://docs.docker.com/engine/containers/multi-service_container/),
[Zoraxy: TLS und automatische WebSockets](https://github.com/tobychui/zoraxy).
