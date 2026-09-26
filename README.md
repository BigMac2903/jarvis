# JARVIS

Ein persönlicher, lokal selbst hostbarer KI-Assistent mit Chat, Sprache, Recherche, Quellen, freigegebenen Geräten und bestätigten Aktionen.

Neu: SIP als Standardtelefonie, Nextcloud/Immich, verschlüsseltes persönliches Memory mit Importvorschau, Aufgaben/Ziele, Ereignisregeln sowie Modellregistry und Budgetjournal. Einrichtung: [SIP](docs/SIP.md) und [Operator/Konnektoren/Modelle](docs/OPERATOR.md). Die [Matrix 61–200](docs/REQUIREMENTS-61-200.md) nennt ausdrücklich auch noch fehlende Anforderungen.

Ergänzung 201–213: [Local Network](docs/LOCAL_NETWORK.md) mit separatem Agenten, zweistufiger Allowlist, explizitem lokalem DNS, Registry, Vault-Credentials, lokalem Auftragskontext und begrenzten Prüfungen. Standardmäßig deaktiviert. Die frühere Research-Ausnahme `INTERNAL_ALLOW_HOSTS` entfällt; private Nextcloud-/Immich-Endpunkte müssen über den lokalen Zugriff eingerichtet werden. Die Anleitung beschreibt die noch fehlende Durchleitung der Spezialadapter sowie den VPN-Prüfstand.

Unraid mit GitHub-Link installieren: [GitHub-Schnellstart](docs/GITHUB_UNRAID.md). Der Installer erkennt die Server-IP, wählt freie Ports, erzeugt Schlüssel und startet Docker. Den Kopierblock findest du in [UNRAID-START.txt](UNRAID-START.txt). Alternativ: [manuelle Unraid-Anleitung](docs/UNRAID_INSTALL.md).

**Prüfstand:** Quellcode, lokale Builds, Sicherheitsprüfungen und ein echter Chromium-Smoke-Test sind vorhanden. Auf dem Entwicklungsrechner fehlt die Docker-Engine; der vollständige Containerstart und die PostgreSQL-/Redis-Integration sind dort noch nicht abgenommen. Dies ist keine Behauptung eines fertig geprüften Produktivsystems. Details und verbleibende Grenzen stehen in [PROJECT_STATE.md](PROJECT_STATE.md) und [Funktionsstatus](docs/STATUS.md).

## 1. Was du brauchst

- Einen Windows-PC mit Docker Desktop im Modus **Linux-Container**, einen Mac mit Docker Desktop oder einen Linux-Server mit Docker Engine und Compose.
- Als Ausgangspunkt ungefähr 4 CPU-Kerne, 8 GB RAM und 15 GB freien Speicher. Lokale KI benötigt je nach Modell deutlich mehr RAM/VRAM.
- Für Cloud-KI ein Konto beim gewählten Anbieter und einen API-Key. Das ChatGPT-Abonnement ersetzt keinen API-Key.
- Für Kalender, Mail und Telefonie jeweils eigene Zugangsdaten. Du kannst diese Funktionen zunächst deaktiviert lassen.
- Für Mikrofon, Kamera, Geräte-Agenten und OAuth eine vertrauenswürdige HTTPS-Adresse. SIP braucht eine erreichbare PBX und passende SIP-/RTP-Netzkonfiguration; nur der optionale Twilio-Pfad braucht eine öffentlich erreichbare Webhook-Domain.

Der Basis-Stack veröffentlicht nur Ports 80 und 443 des Reverse Proxys. Datenbank, Redis und Browser besitzen keine veröffentlichten Hostports. Der ausdrücklich aktivierte SIP-Portoverride ergänzt SIP/RTP; die Hostfirewall muss diese auf PBX-/Medienpeers begrenzen.

## 2. Repository verwenden

Dieses Verzeichnis ist ein lokales Git-Repository. Es wurde noch nicht bei einem Hostingdienst veröffentlicht, daher gibt es noch keine echte Clone-URL. Kopiere es auf deinen Server oder veröffentliche es später in deinem eigenen privaten Repository. Bei einem vorhandenen Remote-Repository ist der Ablauf:

```bash
git clone DEINE-REPOSITORY-URL jarvis
cd jarvis
```

## 3. Konfiguration anlegen

Die Installationsskripte kopieren .env.example bei Bedarf und erzeugen zufällige **interne** Passwörter und Schlüssel. Bestehende Werte bleiben erhalten. Dein Administratorpasswort wählst du selbst im Browser.

Windows PowerShell:

```powershell
.\install.ps1 -ConfigureOnly
```

Linux/macOS:

```bash
bash install.sh --configure-only
```

Öffne danach .env mit einem Texteditor. Für die Nutzung ausschließlich auf diesem Computer kannst du PUBLIC_URL=https://localhost und SITE_ADDRESS=localhost belassen. Für einen Server trägst du z. B. PUBLIC_URL=https://jarvis.deine-domain.de und SITE_ADDRESS=jarvis.deine-domain.de ein. Die Domain muss auf den Server zeigen.

**MASTER_KEY und BROWSER_MASTER_KEY sicher sichern.** Ohne die ursprünglichen Schlüssel lassen sich gespeicherte Zugangsdaten bzw. Browsersessions nicht entschlüsseln. .env niemals in Git einchecken oder teilen.

## 4. Docker starten

Starte Docker Desktop und warte, bis die Engine bereit ist. Anschließend:

```powershell
.\install.ps1
```

oder:

```bash
bash install.sh
```

Die Skripte prüfen Docker, erzeugen Verzeichnisse, bauen Images, starten Dienste und warten auf Healthchecks. Alternativ nach vollständiger .env-Konfiguration:

```bash
docker compose config --quiet
docker compose up -d --build --wait --wait-timeout 300
```

Der erste Build lädt unter anderem .NET, Node, Python und Chromium. Das kann einige Minuten dauern. Änderungen an Code erfordern einen erneuten Build.

## 5. Browser und HTTPS

Öffne PUBLIC_URL aus .env. Für öffentlich auflösbare Domains bezieht Caddy automatisch ein Zertifikat, sofern Ports und DNS korrekt eingerichtet sind. Für localhost verwendet Caddy eine lokale CA, die dein System noch nicht kennt.

Lokales Stammzertifikat exportieren:

```bash
docker compose cp reverse-proxy:/data/caddy/pki/authorities/local/root.crt ./jarvis-local-root.crt
```

Importiere nur das Zertifikat deines eigenen Servers in den vertrauenswürdigen Zertifikatsspeicher deines Betriebssystems. Prüfe den Fingerprint auf dem Server. Auf weiteren Geräten ist das ebenfalls erforderlich, wenn du eine interne CA nutzt. Die Geräte-Agenten deaktivieren keine Zertifikatsprüfung. Eine öffentliche HTTPS-Domain vereinfacht die Einrichtung mehrerer Geräte.

## 6. Setup-Wizard

/setup und die Startseite zeigen vor der ersten Einrichtung den Wizard:

1. SETUP_TOKEN aus deiner .env eintragen.
2. Benutzername, mindestens zwölf Zeichen langes Passwort, E-Mail und Zeitzone festlegen.
3. Gewünschte Integrationen konfigurieren; jede bleibt ohne Aktivierung ausgeschaltet.
4. JARVIS einrichten.

Danach ist Setup serverseitig gesperrt. Einstellungen bleiben nach Anmeldung änderbar. Änderungen sind beim nächsten Aufruf wirksam; ein Neustart ist dafür nicht nötig. Das persönliche System verwendet einen Administrator. Mehrbenutzeradministration ist noch nicht enthalten.

## 7. KI und Sprache konfigurieren

Unter **AI Models / Settings → KI & Modelle**:

- Aktivieren.
- Für OpenAI: Protokoll responses, Basis-URL https://api.openai.com/v1, API-Key und in deinem API-Konto verfügbaren Modellnamen eintragen.
- Für einen OpenAI-kompatiblen Anbieter: Protokoll chat und dessen Basis-URL. Nur Anbieter eintragen, denen du die übermittelten Aufgaben anvertrauen möchtest.
- Ein Vision-Modell für Bilder und ein Realtime-Modell für Live Voice festlegen.
- Optional ein Embedding-Modell für semantische Suche festlegen.
- Speichern und „Verbindung testen“. Der Test liest die Modellliste; er beweist noch nicht die Verfügbarkeit jedes eingetragenen Modells.

Der Chat führt echte Modellanfragen aus. Ohne aktivierten Provider zeigt er einen Konfigurationsfehler. Es gibt keine vorgetäuschten Antworten.

Unter Settings → Live Voice die Audioübertragung aktivieren. **Voice** verbindet nach Klick auf den Mikrofonknopf über WebRTC. Push-to-talk ist wählbar; im kontinuierlichen Modus übernimmt der Provider Sprachaktivitätserkennung. Sprechen unterbricht die Ausgabe. Der Stop-Knopf schließt Verbindung und Mikrofon. Sprache verwendet den gemeinsamen Agenten und dieselben Toolfreigaben.

Eine einmalige Kamera- oder Bildschirmaufnahme im Chat erfolgt über die jeweiligen Buttons. Das Bild wird erst mit deiner Nachricht an den Vision-Provider gesendet. Der Browser fragt nach deiner Freigabe und beendet die Aufnahme danach.

Optional lokale KI: COMPOSE_PROFILES=local-ai, Stack neu starten, ein Modell selbst laden:

```bash
docker compose exec ollama ollama pull DEIN-MODELL
```

Danach Basis-URL http://ollama:11434/v1, Protokoll chat und den geladenen Modellnamen verwenden. Der enthaltene WebRTC-Sprachpfad verwendet OpenAI; ein vollständig lokaler STT/TTS-Pfad ist nicht implementiert.

## 8. Kalender verbinden

Google: eigenes OAuth-Webprojekt mit aktivierter Calendar API und Gmail API erstellen. Client-ID und Client-Secret unter Google eintragen. Die dort angezeigte Callback-URL exakt als autorisierte Weiterleitung registrieren. Speichern, „Konto verbinden“, die gewünschten Berechtigungen beim Anbieter bestätigen, anschließend testen.

Microsoft: eigene App-Registrierung für Microsoft Graph einrichten. Web-Callback-URL exakt übernehmen. Delegierte Berechtigungen für User.Read, Calendars.ReadWrite, Mail.Read/Mail.ReadWrite und Mail.Send sowie offline_access ermöglichen. Client-ID und Client-Secret speichern, verbinden und testen. Organisationen können eine Administratorfreigabe verlangen.

Unter Calendar stehen Lesen, Verfügbarkeit, Anlegen, Ändern und Löschen zur Verfügung. Zeitangaben enthalten eine Zeitzone. Schreibaktionen benötigen standardmäßig eine Freigabe. Verfügbarkeit liefert die belegten Zeitfenster; der gemeinsame Agent kann daraus passende Vorschläge ableiten. CalDAV ist derzeit nicht enthalten.

## 9. E-Mail und Telefonie

Mail nutzt die verbundenen Google-/Microsoft-Konten. Suche, Lesen, Entwurf und Versand sind implementiert. Entwürfe und Versand werden standardmäßig bestätigt. Der Chat kann Kontaktdaten über Contacts.Search finden. IMAP/SMTP ist derzeit nicht enthalten.

Standard SIP: Dienstprofil aktivieren, Konto und Passwort im Vault konfigurieren, Registrierung testen und freigegebene Länder eintragen. Die vollständige Anleitung einschließlich NAT, Ports, SRTP und Grenzen steht in [docs/SIP.md](docs/SIP.md).

Optional Twilio: Unter Settings → Telefonie ausdrücklich `provider=twilio` wählen. Account SID, Auth Token, Twilio-Nummer und verifizierte Caller-ID unter Settings → Twilio speichern und testen. Die Testfunktion liest Nummern; sie tätigt keinen Anruf. PUBLIC_URL muss öffentlich per HTTPS erreichbar sein. Twilio erhält signierte Webhook-Endpunkte automatisch beim Anruf.

Phone.Call erzeugt standardmäßig eine Freigabe mit Telefonnummer und Gesprächsauftrag. Audioaufzeichnung ist ausgeschaltet; Textprotokolle werden gespeichert. SIP nutzt die separate Realtime-Audiobrücke. Twilio Gather verarbeitet Sprache und DTMF; für Twilio-Streaming nach Freischaltung „ConversationRelay Streaming“ aktivieren. Gesprächsergebnisse erscheinen unter Calls. Externe Terminänderungen werden anschließend über Calendar und eine eigene Freigabe ausgeführt.

Gespräche haben konfigurierbare Dauer-/Versuchslimits (maximale Dauer standardmäßig zehn Minuten, einzelner Auftrag standardmäßig fünf). Verbindliche Zusagen werden nicht aus erfundenen Kalenderdaten abgeleitet. Prüfe ein angebotenes Zeitfenster vor der Kalenderfreigabe.

## 10. Obsidian und Gedächtnis

Setze OBSIDIAN_HOST_PATH auf deinen lokalen Vault-Ordner. Unter Windows sind Pfade wie C:/Users/DeinName/Documents/Vault möglich. Der Container sieht diesen als /data/obsidian. Danach Stack neu erstellen und in Settings → Obsidian aktivieren.

Für Linux muss UID 1654 den Vault lesen können; Schreibzugriff erfordert passende Dateirechte. Verwende einen dafür vorgesehenen Vault und vergib Rechte gezielt. Der Installer verändert nicht pauschal die Rechte bestehender persönlicher Ordner.

Schreibzugriff ist zusätzlich im Dashboard schaltbar und benötigt pro Aktion die Capability-Freigabe. Nur Markdown innerhalb des Vaults ist erlaubt; Traversal und symbolische Links werden abgewiesen. Frontmatter kann als Bestandteil des Markdown gelesen und geschrieben werden. Tags und Wiki-Links werden beim Lesen extrahiert. Interne Erinnerungen bleiben getrennt in PostgreSQL.

Persönliche Erinnerungen werden im neuen Memory-Editor verschlüsselt gespeichert. Importierte Benutzeraussagen bleiben bis zur Einzelprüfung Kandidaten; sensible Fakten werden nicht automatisch Modellkontext. Kategorien, Historie, Quellen und Grenzen der Legacy-Daten stehen in [docs/OPERATOR.md](docs/OPERATOR.md).

Semantische Obsidian-Suche: Embedding-Modell konfigurieren, dann Memory.Index anfragen und die Datenübertragung bestätigen. Es indexiert begrenzt viele freigegebene Vaultdateien, keine unklassifizierten Legacy-Erinnerungen. Memory.Search kombiniert freigegebene persönliche Texttreffer mit Obsidian-Text-/Ähnlichkeitssuche. Veränderte oder gelöschte Dateien werden nicht aus einem veralteten Vektorindex zitiert; erneut indexieren.

## 11. Windows-Agent

Der Agent läuft auf deinem Windows-Desktop, nicht in Docker. Er benötigt keine Administratorrechte.

Mit installiertem .NET 10 SDK:

```powershell
.\scripts\install-windows-agent.ps1
```

Alternativ selbst veröffentlichen:

```powershell
dotnet publish agents/windows/Jarvis.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/windows
```

Für ARM-PCs win-arm64 wählen. Programm öffnen, HTTPS-Serveradresse und unter Devices erzeugten Code **plus** Pairing-Token eintragen. Das dauerhafte Token wird mit Windows DPAPI an dein Benutzerkonto gebunden gespeichert.

„Lokale Freigaben öffnen“ zeigt config.json. Unter Applications ordnest du einen selbst gewählten Namen einem vollständigen EXE-Pfad zu. AllowedFolders enthält gezielt freigegebene Ordner. Nach Änderungen Agent neu starten. Interpreter und Shells bleiben gesperrt. „Bildschirm und UI-Steuerung … freigeben“ erlaubt den Zugriff für diese Sitzung. Im Tray kannst du ihn jederzeit stoppen.

Ein Windows-Service wäre wegen der isolierten Session 0 ungeeignet für normale Desktopsteuerung; deshalb ist dieser Agent eine Tray-Anwendung. Kein offener Listener, keine Remote-Shell.

## 12. macOS-Agent

macOS 14 oder neuer und die Xcode Command Line Tools werden benötigt:

```bash
bash scripts/install-macos-agent.sh
```

Beim ersten Start Serveradresse und Pairing-Daten eingeben. Das Token landet im macOS-Schlüsselbund. Lokale Konfiguration: ~/Library/Application Support/Jarvis/config.json. Applications ordnet Namen erlaubten Bundle-IDs zu; AllowedFolders begrenzt Dateiaktionen. screenConsent muss ausdrücklich gesetzt werden.

Unter Systemeinstellungen → Datenschutz & Sicherheit **Bildschirmaufnahme** für Screenshots und **Bedienungshilfen** für UI-Interaktionen freigeben. Je nach Installationspfad kann macOS die Freigabe der aufrufenden Terminal-App verlangen. Der Agent verwendet ScreenCaptureKit und Accessibility APIs. Keine beliebige AppleScript-Ausführung. Dieser Swift-Build muss noch auf einem echten Mac geprüft werden.

## 13. iPhone verbinden

Die genaue Schrittfolge steht in [agents/ios/README.md](agents/ios/README.md). Apple Kurzbefehle können Standort, Akkustand und freiwillige Ereignisse per HTTPS senden. Standort wird als letzte Meldung mit Zeitpunkt angezeigt. iOS erlaubt keine allgemeine fernbediente Oberfläche fremder Apps. JARVIS umgeht diese Beschränkung nicht.

## 14. Recherche und Berechtigungen

Settings → Internet & Research:

- Brave mit eigenem Search API-Key oder SearXNG als austauschbaren Suchprovider wählen.
- Für den enthaltenen SearXNG-Dienst COMPOSE_PROFILES=search verwenden; URL http://jarvis-search:8080.
- Browser, Automation, Deep Research, Downloads und PDF-Lesen einzeln aktivieren.
- Seitenzahl, Laufzeit und Dateigröße begrenzen.

Research bietet Quick Search, Research und Deep Research. Laufende Recherche kann beendet werden; Chat und Voice bleiben separat bedienbar. Ergebnisse enthalten Quellen, Abrufzeiten und sichtbare Unsicherheiten. Caches haben je nach Thema unterschiedliche Laufzeiten; „Frisch recherchieren“ umgeht den Cache. Webseiten sind Daten und besitzen keine Agentberechtigungen.

Browser unterstützt Tabs, Texte, Links, Tabellen, Screenshots, Navigation und bestätigte Interaktionen. Zugangsdaten für Browser-Logins können über den authentifizierten Vault-Endpunkt hinterlegt werden, siehe [API](docs/API.md). Sie werden serverseitig eingesetzt und nicht an das Modell ausgegeben. Downloads bleiben in Quarantäne; keine automatische Ausführung oder Weitergabe. Für Antivirus COMPOSE_PROFILES um antivirus ergänzen und CLAMAV_HOST=clamav setzen. Ohne Scanner wird ausdrücklich scanned=false gemeldet.

Permissions: Standard-Autonomiepolicy oder explizit AUTO/NOTIFY/ASK/ALWAYS_CONFIRM, erlauben, sperren oder zehn Minuten erlauben. Level 3 ist Standard, aber nur eine kleine reversible Allowlist wird ohne Einzelfrage mit Aktivitätsmeldung ausgeführt. Browserklicks bleiben immer bestätigungspflichtig, weil der Server nicht zuverlässig erkennen kann, ob ein Klick einen Kauf oder eine Kontoveränderung auslöst. Freigaben sind einmalig und an Tool, Nutzer und Argumente gebunden. Audit speichert Feldnamen und Argument-Hash statt sensibler Inhalte.

Unter Settings MFA aktivieren. Dein Passwortmanager sollte zusätzlich den TOTP-Schlüssel und Wiederherstellungsinformationen verwahren. Bei Verlust ist eine administrative Offline-Wiederherstellung erforderlich; es gibt keine versteckte MFA-Umgehung.

## 15. Backup und Restore

Windows: .\scripts\backup.ps1. Linux/macOS: bash scripts/backup.sh. Für einen konsistenten Stand werden ein laufender SIP-Dienst, API und Browser kurz gestoppt und anschließend auch bei Fehlern wieder gestartet; aktive Telefonate werden dabei beendet. PostgreSQL, Konfiguration, Vault, API-Daten und Browserprofile werden nach backups/ZEITPUNKT kopiert. Das Backup enthält Schlüssel und private Informationen: offline verschlüsseln und separat aufbewahren.

Wiederherstellung, Sicherheitsregeln und Prüfung: [docs/OPERATIONS.md](docs/OPERATIONS.md).

## 16. Updates

Vor Updates ein Backup erstellen. Dann im Repository:

```bash
git pull
docker compose pull
docker compose build
docker compose up -d --wait --wait-timeout 300
bash scripts/verify-installation.sh
```

Auf Windows das PowerShell-Verifikationsskript verwenden. SQL-Migrationen laufen unter einer Datenbanksperre transaktional und werden versioniert. Ein Image-Rollback ersetzt kein Datenbank-Restore. Optionale Images mit latest/major Tags vor produktivem Einsatz auf geprüfte Digests festsetzen.

## 17. Fehlerbehebung und Tests

- Docker nicht erreichbar: Docker Desktop/Engine starten, Linux-Container-Modus prüfen. Die Compose-CLI allein genügt nicht.
- Browserzertifikat nicht vertraut: richtige Domain oder eigene Caddy-CA installieren. TLS-Prüfungen niemals pauschal abschalten.
- Login-Cookie fehlt: HTTPS und PUBLIC_URL/SITE_ADDRESS müssen stimmen.
- CSRF-Fehler: Dashboard exakt unter PUBLIC_URL öffnen; kein abweichender Host/Port.
- KI 401/403/429: API-Key, Modellberechtigung und API-Guthaben prüfen.
- Provider ausgeschaltet: bewusst aktivieren, speichern, Verbindung testen.
- Geräteaktionen verweigert: Capability und lokale Bildschirm-/App-Freigabe prüfen.
- Obsidian-Datei nicht schreibbar: Dashboard-Schalter und Host-Dateirechte prüfen.
- Chromium-Sandboxfehler: seccomp-Profil und User-Namespaces des Docker-Hosts prüfen. Sandbox nicht einfach abschalten.
- Containerlogs: docker compose logs --tail=100 jarvis-api jarvis-browser. Keine Secrets in Fehlermeldungen oder Supporttickets kopieren.

Lokal entwickeln / testen:

```bash
dotnet build Jarvis.slnx
dotnet test tests/Jarvis.Tests/Jarvis.Tests.csproj
cd src/Jarvis.Web
npm ci
npm run build
```

Python-Netzwerktests: python -m unittest discover -s tests/browser -v. Echter Browser-Test: Browser-Anforderungen installieren, playwright install chromium, danach python tests/browser/smoke_live.py. Dieser Test nutzt example.com, benötigt keinen Such- oder KI-Key und ersetzt keine Prüfung des Docker-Netzwerks.

Mit installierten Browser-Anforderungen prüfen dieselben Unit-Tests auch den echten PDF-Parser. Nach dem Frontend-Build prüft python tests/ui/smoke.py das Desktop-/Mobil-Layout mit ausdrücklich isolierten API-Testfixtures; Bilder werden unter artifacts/ui-tests gespeichert. Das ist kein Nachweis eines funktionierenden Backends. Die CI führt diese Prüfungen zusätzlich aus.

API-Integrationstests benötigen eine **eigene leere** PostgreSQL-Datenbank namens jarvis_test. JARVIS_TEST_DATABASE auf deren Verbindungsstring und JARVIS_TEST_REDIS auf den Test-Redis setzen. Ohne diese Konfiguration wird der Test sichtbar übersprungen. Niemals Produktionsdaten dafür verwenden. CI enthält Linux-Container-, Backend-, Frontend-, Windows- und macOS-Jobs; ihre Existenz ist noch kein Nachweis eines erfolgreichen externen CI-Laufs.

[Architektur](ARCHITECTURE.md) · [Betrieb](docs/OPERATIONS.md) · [Automationen](docs/AUTOMATIONS.md) · [API](docs/API.md) · [Prüfstand](PROJECT_STATE.md)
