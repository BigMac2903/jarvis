# JARVIS von GitHub auf Unraid installieren

Das Projekt ist unter [BigMac2903/jarvis](https://github.com/BigMac2903/jarvis) veröffentlicht. Gib auf Unraid den Link `https://github.com/BigMac2903/jarvis` ein. Der Installer lädt den Standardbranch, erzeugt interne Schlüssel, erkennt die private Unraid-IPv4, sucht freie Ports ab 8080/8443, richtet die Datenordner ein, baut die Images und startet den Stack. Ein eigenes Docker-Hub-Konto ist dafür nicht nötig.

## Optional: ein eigenes Repository anlegen

Für die Installation aus `BigMac2903/jarvis` ist dieser Schritt nicht erforderlich. Die folgenden Hinweise gelten nur, wenn du eine eigene Kopie veröffentlichen möchtest.

Auf dem PC mit diesem Projekt kannst du in GitHub Desktop **File → Add local repository** wählen und den bestehenden Ordner `outputs/jarvis` hinzufügen. Wähle danach **Publish repository**, einen Namen wie `jarvis` und die gewünschte Sichtbarkeit.

Für den untenstehenden Startblock muss das Repository **öffentlich** sein; dadurch ist der Quellcode öffentlich lesbar. Persönliche Daten werden erst auf Unraid angelegt und gehören nicht in GitHub. Das Quellcode-ZIP enthält keine echte `.env`, Provider-Schlüssel oder Anwendungsdaten. `.env.example` enthält nur leere Konfigurationsfelder und gehört zum Code.

Falls du stattdessen das ZIP verwendest: entpacken, in GitHub Desktop ein neues lokales Repository erstellen und den **Inhalt** des entpackten `jarvis`-Ordners hineinkopieren. Alle Quelldateien committen und mit **Publish repository** hochladen. Auf oberster Ebene müssen `install-from-github.sh`, `install-unraid.sh`, `docker-compose.yml`, `src` und `services` stehen. Die ZIP selbst hochzuladen oder einen zusätzlichen `jarvis`-Unterordner anzulegen reicht nicht.

Alternativ kannst du auf [GitHub ein Repository anlegen](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository) und die Dateien hochladen. Der Browser erlaubt laut [GitHub-Dokumentation](https://docs.github.com/en/repositories/working-with-files/managing-files/adding-a-file-to-a-repository) höchstens 100 Dateien pro Upload; dieses Projekt benötigt mehrere Uploads. GitHub Desktop übernimmt sie gemeinsam, einschließlich der Punktdateien.

## Auf Unraid: Startblock einfügen und Link eingeben

Starte Array/Pool und Docker. Die Freigabe `/mnt/user/appdata` muss vorhanden sein. Öffne das Unraid-Terminal, kopiere den gesamten folgenden Block hinein und drücke Enter. Danach gibst du als Repository-Link `https://github.com/BigMac2903/jarvis` ein.

```bash
(
  set -e
  read -r -p 'Dein GitHub-Link: ' JARVIS_REPO </dev/tty
  JARVIS_REPO="${JARVIS_REPO%/}"
  JARVIS_REPO="${JARVIS_REPO%.git}"
  [[ "$JARVIS_REPO" =~ ^https://github\.com/[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$ ]] || { echo 'Bitte https://github.com/NAME/REPOSITORY eingeben.'; exit 1; }
  JARVIS_INSTALLER=$(mktemp /tmp/jarvis-install.XXXXXX)
  curl --fail --show-error --silent --location --proto '=https' --proto-redir '=https' --connect-timeout 20 --max-time 120 \
    "https://raw.githubusercontent.com/${JARVIS_REPO#https://github.com/}/HEAD/install-from-github.sh" -o "$JARVIS_INSTALLER"
  bash "$JARVIS_INSTALLER" "$JARVIS_REPO"
)
```

Derselbe Block steht zum Kopieren in [UNRAID-START.txt](../UNRAID-START.txt). Der Installer wird aus deinem angegebenen Repository heruntergeladen und auf Unraid als root ausgeführt. Verwende deinen eigenen veröffentlichten JARVIS-Code.

Compose Manager Plus ist für diesen Weg optional. Wenn bereits ein Compose-Plugin vorhanden ist, wird es verwendet. Andernfalls lädt der Installer Docker Compose **v5.5.1 für Linux x86_64** aus der offiziellen Docker-Veröffentlichung und prüft die im Installer hinterlegte SHA-256-Prüfsumme vor dem ersten Aufruf. Das Plugin liegt anschließend nur für JARVIS unter `appdata/jarvis/tools/docker-cli`. Der Verwalter `unraid-compose.sh` findet es auch nach einem Neustart wieder. Diese manuell installierte Version wird nicht automatisch aktualisiert; [Docker beschreibt dieses Installationsverfahren](https://docs.docker.com/compose/install/linux/).

## Nach dem Start

Das Terminal zeigt die tatsächlich eingerichtete HTTPS-Adresse. Ein Beispiel wäre `https://192.168.178.20:8443`; bei belegten Ports wählt der Installer einen anderen Port. Merke dir die ausgegebene Adresse.

Das persönliche Setup-Token steht nur auf deinem Unraid-Server:

```bash
cat /mnt/user/appdata/jarvis/START-HIER.txt
```

Für lokale HTTPS-Adressen muss die eigene Caddy-CA auf deinem Client vertraut werden. Der Installer versucht, ihr öffentliches Root-Zertifikat nach `/mnt/user/appdata/jarvis/caddy-root.crt` zu exportieren. Importiere dieses Zertifikat auf dem Gerät, auf dem du JARVIS öffnest. Falls die Datei noch fehlt, öffne zunächst die ausgegebene HTTPS-Adresse und exportiere sie danach:

```bash
cd /mnt/user/appdata/jarvis/source
bash unraid-compose.sh cp reverse-proxy:/data/caddy/pki/authorities/local/root.crt /mnt/user/appdata/jarvis/caddy-root.crt
```

Melde dich an, sobald dein Browser der Verbindung vertraut. Im Setup legst du deinen Administrator an und trägst deinen KI-Key ein. GitHub kann dir diese persönlichen Angaben nicht abnehmen. Eine feste DHCP-Zuordnung für Unraid verhindert, dass sich die eingerichtete Adresse ändert.

## Dateien und spätere Befehle

- Quellcode: `/mnt/user/appdata/jarvis/source`
- Schlüssel und Konfiguration: `/mnt/user/appdata/jarvis/source/.env`
- Persistente Basisdaten: `/mnt/user/appdata/jarvis/{postgres,redis,api,browser,caddy,caddy-config,obsidian}`
- Privates Compose-Plugin, falls nötig: `/mnt/user/appdata/jarvis/tools/docker-cli`

Status und Logs liest du ohne zusätzliche Pfadangaben:

```bash
cd /mnt/user/appdata/jarvis/source
bash unraid-compose.sh ps
bash unraid-compose.sh logs --tail=100 jarvis-api reverse-proxy
```

Nach einem fehlgeschlagenen Build oder Start kannst du `bash install-unraid.sh` im selben Quellordner erneut ausführen. Vorhandene nichtleere Schlüssel bleiben erhalten. Bei bestehenden Daten und fehlenden Schlüsseln stoppt der Installer. Eine alte Installation mit Docker-Named-Volumes wird nicht automatisch auf Bindmounts umgestellt. Die Anleitung für die manuelle Variante steht unter [UNRAID_INSTALL.md](UNRAID_INSTALL.md).

Updates bleiben bewusst ein eigener Schritt: erst Datenbank und Schlüssel sichern, dann im Quellordner `git pull --ff-only` und `bash install-unraid.sh` ausführen. Bei eigenen Änderungen oder abweichendem Branch hält Git an. Es gibt kein automatisches Verwerfen lokaler Änderungen und keine automatische Datenmigration aus alten Speicherpfaden.

SIP und LAN-Zugriff bleiben beim ersten Start aus. Nach Einrichtung gemäß [SIP.md](SIP.md) oder [LOCAL_NETWORK.md](LOCAL_NETWORK.md) ergänzt du `COMPOSE_PROFILES` in `.env`, zum Beispiel `sip,local-network`. `unraid-compose.sh` ergänzt für das Profil `sip` automatisch die SIP-Portdatei. Lokale KI, Suchdienst und Virenscanner verwenden weiterhin die im Basis-Compose definierten zusätzlichen Volumes; sie sind nicht Teil der automatischen Bindmount-Umstellung des Basis-Stacks.

## Private Repositories und Grenzen

Ein privates Repository braucht zusätzlich GitHub-Authentifizierung; ein nackter Link reicht dafür nicht. Nutze dann einen bereits authentifizierten Git-Zugang auf Unraid, klone das Repository und starte `install-unraid.sh`. Tokens gehören weder in den Link noch in `.env`. Der öffentliche Startblock kann keine privaten Raw-Dateien abrufen.

Die Installation wurde hier nicht auf einem echten Unraid-Host ausgeführt. Die Compose-Zusammenführung wird lokal geprüft; Linux-Funktionstests für Eingaben, Konfiguration, Portwahl und Schlüsselerhalt sind im GitHub-Workflow enthalten. Wegen fehlender lokaler Bash-Laufzeit konnten diese Tests hier nicht ausgeführt werden. Nach dem Push muss der Workflow erfolgreich laufen; auch das ersetzt keinen echten Unraid-Start.
