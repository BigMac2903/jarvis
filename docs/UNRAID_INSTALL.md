# JARVIS auf Unraid installieren

Für die automatische Installation mit deinem Repository-Link nutze den [GitHub-Schnellstart](GITHUB_UNRAID.md). Die folgenden Schritte beschreiben die manuelle Variante.

Diese Anleitung richtet den normalen JARVIS-Stack auf einem Unraid-Server ein. Sie verwendet die Compose-Dateien des Projekts und speichert Anwendungsdaten dauerhaft unter `/mnt/user/appdata/jarvis`. SIP-Telefonie und der optionale Local-Network-Agent werden anschließend bei Bedarf eingeschaltet.

Als grobe Ausgangsbasis empfiehlt das Projekt vier CPU-Kerne, 8 GB RAM und 15 GB freien Speicher. Die tatsächliche Nutzung hängt von Browsern, Aufgaben und optionaler lokaler KI ab. Lege `appdata` möglichst auf einem SSD-Pool an.

## 1. Unraid vorbereiten

1. Starte das Array und kontrolliere unter **Settings → Docker**, dass Docker aktiviert ist.
2. Installiere im Tab **Apps** das Plugin **Compose Manager Plus**. Falls der Apps-Tab fehlt, installiere zuerst das Plugin **Community Applications**. Compose Manager Plus gibt es im [Unraid Community Apps Katalog](https://ca.unraid.net/apps/compose-manager-plus-0wft8je0ra0zhy?category=Plugins&type=plugin).
3. Lege auf einem PC, der auf Unraid zugreifen kann, den Quellcode als ZIP bereit: `jarvis-source.zip` aus dem Projekt-Download.
4. Übertrage die ZIP-Datei in eine freigegebene Unraid-Freigabe, zum Beispiel `\<UNRAID-NAME>\appdata`. Wenn `appdata` nicht per SMB freigegeben ist, nimm eine andere freigegebene Ablage und passe den Pfad im nächsten Schritt an.

Compose Manager Plus ist ein Plugin zum Verwalten von Compose-Stacks. JARVIS selbst erscheint nicht als einzelner Eintrag im Community-Apps-Katalog.

## 2. Quellcode nach appdata entpacken

Öffne **Unraid → Terminal** und führe aus. Ersetze `jarvis-source.zip` durch den Pfad, an den du die Datei kopiert hast:

```bash
mkdir -p /mnt/user/appdata
unzip -q /mnt/user/appdata/jarvis-source.zip -d /mnt/user/appdata
ls /mnt/user/appdata/jarvis/docker-compose.yml
```

Wenn du die ZIP in eine andere Freigabe kopiert hast, ersetze im `unzip`-Befehl den Quellpfad, zum Beispiel `/mnt/user/transfer/jarvis-source.zip`. Der letzte Befehl muss den Dateipfad ausgeben. Die ZIP-Datei enthält einen Ordner `jarvis`, der unter `/mnt/user/appdata/jarvis` entpackt wird.

Erstelle nun die persistenten Datenordner und bereite geheime Schlüssel vor:

```bash
cd /mnt/user/appdata/jarvis
mkdir -p postgres redis api browser caddy caddy-config obsidian
bash install.sh --configure-only
chmod 600 .env
```

Das Skript füllt interne Datenbankpasswörter, Verschlüsselungsschlüssel und Diensttokens mit Zufallswerten. Es startet noch keine Container.

## 3. Hostname und Ports eintragen

Hole dir die IP-Adresse deines Unraid-Servers unter **Main** oder **Settings → Network Settings**. Lege am Router nach Möglichkeit eine DHCP-Reservierung für diese IP an, damit sich die Adresse nicht ändert.

Öffne die Datei `/mnt/user/appdata/jarvis/.env` im Unraid-Dateibrowser oder über ein Terminal mit `vi .env`. Wenn du `vi` verwendest: `i` drücken, Text ändern, dann `Esc`, `:wq` und Enter zum Speichern. Passe mindestens diese Zeilen an. Beispiel: Unraid hat die feste IP `192.168.1.20`:

```dotenv
PUBLIC_URL=https://192.168.1.20:8443
SITE_ADDRESS=192.168.1.20
HTTP_PORT=8080
HTTPS_PORT=8443
OBSIDIAN_HOST_PATH=/mnt/user/appdata/jarvis/obsidian
COMPOSE_PROFILES=
LOCAL_NETWORK_ENABLED=false
```

Speichere die Datei. Lass `MASTER_KEY`, `BROWSER_MASTER_KEY`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `BROWSER_TOKEN`, `SETUP_TOKEN` und `LOCAL_NETWORK_TOKEN` so stehen, wie das Skript sie erzeugt hat. Sichere `.env` in einem verschlüsselten Backup. Ohne die beiden Master-Keys sind gespeicherte Zugangsdaten nicht wiederherstellbar.

Ports `8080` und `8443` vermeiden normalerweise Konflikte mit der Unraid-Weboberfläche auf `80` und `443`. Falls diese Ports bei dir bereits belegt sind, wähle freie Hostports und ändere zugleich `PUBLIC_URL` sowie `HTTP_PORT`/`HTTPS_PORT`.

## 4. Unraid-Schreibrechte vorbereiten

Einige Container laufen mit eigener Linux-Benutzer-ID. Gib den persistierten Verzeichnissen passende Eigentümer:

```bash
chown -R 1654:1654 /mnt/user/appdata/jarvis/api /mnt/user/appdata/jarvis/obsidian
chown -R 10001:10001 /mnt/user/appdata/jarvis/browser
chown -R 1000:1000 /mnt/user/appdata/jarvis/caddy /mnt/user/appdata/jarvis/caddy-config
```

PostgreSQL und Redis setzen die Rechte ihrer Datenverzeichnisse beim Start selbst.

## 5. Konfiguration prüfen und JARVIS starten

Prüfe zuerst, dass keine Docker-Netze auf Unraid, deinem Router oder VPN dieselben Bereiche wie `172.30.254.0/28` und `172.30.254.16/28` verwenden. Das sind die internen Compose-Netze des Projekts. Compose-Netze bleiben intern; gib JARVIS keine zusätzliche `br0`-Adresse und verwende kein `host`-Netzwerk. Unraids [Beschreibung der Docker-Netzwerkarten](https://docs.unraid.net/unraid-os/using-unraid-to/run-docker-containers/managing-and-customizing-containers/) erklärt die Unterschiede.

Im Unraid-Terminal:

```bash
cd /mnt/user/appdata/jarvis
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml config --quiet
```

Wenn keine Fehlermeldung erscheint, baue und starte den Stack:

```bash
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml up -d --build --wait --wait-timeout 300
```

Der erste Build kann einige Minuten dauern und benötigt ausreichend freien Speicher und Internetzugang. Compose verwendet `restart: unless-stopped`; gestartete Container fahren nach einem normalen Unraid-Neustart wieder hoch.

Alternativ kannst du die Compose-Dateien in Compose Manager Plus als Stack eintragen und die gleiche `.env` verwenden. Für die erste Einrichtung ist das Terminal mit den obigen Befehlen die klarste Möglichkeit, die Projektdateien gemeinsam mit der Unraid-Override-Datei zu laden.

## 6. JARVIS öffnen und Konto einrichten

Öffne auf einem Gerät in deinem Heimnetz:

```text
https://192.168.1.20:8443
```

Ersetze die Beispiel-IP durch deine Unraid-IP. Caddy erstellt für diese IP ein Zertifikat seiner lokalen CA. Der Browser wird es anfangs als nicht vertrauenswürdig markieren. Für eine sichere Verbindung musst du Caddys Root-Zertifikat auf jedem Gerät installieren, von dem du JARVIS öffnest. Caddy erklärt [lokale HTTPS-Zertifikate und die Root-CA](https://caddyserver.com/docs/automatic-https#local-https).

Der erste Aufruf löst nur die Zertifikaterstellung aus. Melde dich erst an, nachdem du das Root-Zertifikat installiert hast und der Browser die HTTPS-Verbindung als vertrauenswürdig anzeigt.

Exportiere das Zertifikat auf Unraid:

```bash
cd /mnt/user/appdata/jarvis
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml cp reverse-proxy:/data/caddy/pki/authorities/local/root.crt ./caddy-root.crt
```

Übertrage `caddy-root.crt` auf deine Geräte und importiere es in deren vertrauenswürdigen Zertifikatsspeicher. Gib diese Datei nicht an andere weiter. Öffne danach JARVIS erneut und richte das Administratorkonto ein. Das Setup-Token findest du in `.env` bei `SETUP_TOKEN`. Danach unter **Settings → AI Models** einen KI-Anbieter und API-Key eintragen. Ein ChatGPT-Web-Abo ersetzt keinen API-Key für die API.

## 7. Optional: lokale Dienste freischalten

Der Local-Network-Agent ist standardmäßig nicht aktiv. Wenn du ihn brauchst:

1. Setze in `.env` `COMPOSE_PROFILES=local-network` und `LOCAL_NETWORK_ENABLED=true`.
2. Trage bei `LOCAL_NETWORK_ALLOWLIST` nur konkrete private IP-Netze oder Hosts ein, etwa `192.168.1.0/24,nas.home`. Interne DNS-Namen benötigen zusätzlich eine passende IP-/CIDR-Freigabe.
3. Wenn du DNS-Namen verwenden willst, trage bei `LOCAL_NETWORK_DNS` die private IP deines DNS-Servers ein. Auch diese IP muss erlaubt sein.
4. Starte den Stack mit zusätzlichem Profil:

```bash
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml --profile local-network up -d --build --wait --wait-timeout 300
```

5. Öffne in JARVIS **Settings → Local Network**, prüfe die Serverregeln und erlaube die gewünschten Hosts und Ports auch dort.

Die beiden Allowlisten begrenzen den Zugriff zusammen. Lass gezielte Portprüfungen und den automatischen Watcher ausgeschaltet, solange du sie nicht benötigst. Details stehen unter [Local Network](LOCAL_NETWORK.md).

## 8. Optional: SIP-Telefonie

SIP benötigt PBX-Zugangsdaten und korrekte SIP-/RTP-Routen. Richte es erst ein, wenn die Basisinstallation funktioniert. Folge danach [docs/SIP.md](SIP.md). Für SIP müssen `docker-compose.sip.yml` und das Profil `sip` zusätzlich aktiviert werden. Passe dafür `SIP_BIND_ADDRESS` an die Unraid-IP und `SIP_ADVERTISE_ADDRESS` an die von der PBX erreichbare Adresse an. Starte dann mit:

```bash
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml -f docker-compose.sip.yml --profile sip up -d --build --wait --wait-timeout 300
```

Die Standardbindung steht aus Sicherheitsgründen auf `127.0.0.1`; passe sie nur passend zu deiner PBX und Unraid-Firewall an. Öffne SIP/RTP nicht pauschal ins Internet.

## 9. Kontrolle und Updates

Status und Logs:

```bash
cd /mnt/user/appdata/jarvis
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml ps
docker compose --project-name jarvis --env-file .env -f docker-compose.yml -f docker-compose.unraid.yml logs --tail=100 jarvis-api reverse-proxy
```

Vor Updates sichere `/mnt/user/appdata/jarvis`, insbesondere `.env`, PostgreSQL, API-Daten und Caddy-Daten. Lege die neue Quellcodeversion getrennt bereit, prüfe die Release-Hinweise und verwende dieselben Schlüssel. Ein neues Quellcode-ZIP allein ist kein Datenbankbackup.

## Häufige Probleme

- **„port is already allocated“:** Ändere `HTTP_PORT`/`HTTPS_PORT` auf freie Hostports und aktualisiere `PUBLIC_URL`.
- **502 oder Container starten nicht:** Prüfe `docker compose ... ps` und die Logs. Stelle sicher, dass `.env` vorhanden ist und die Konfigurationsgenerierung erfolgreich lief.
- **Browser warnt vor Zertifikat:** Installiere Caddys `caddy-root.crt` auf dem Client. Schalte TLS-Prüfungen nicht ab.
- **Konfigurationsfehler bei Networks:** Prüfe, ob die IPAM-Bereiche der Compose-Datei mit vorhandenen Docker-, LAN- oder VPN-Netzen kollidieren.
- **Unraid-Docker-GUI zeigt den Stack nicht wie erwartet:** Verwalte den Stack über Compose Manager Plus oder mit denselben Compose-Befehlen im Terminal. Vermeide, dieselben Container zusätzlich als einzelne Templates anzulegen.

Die Compose-Konfiguration wurde für Unraid angepasst, aber in dieser Entwicklungsumgebung nicht auf einem echten Unraid-Server gestartet. SIP, lokale DNS-/VPN-Ziele und Container-Netzisolation brauchen eine Prüfung auf deiner Installation.
