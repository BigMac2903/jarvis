# JARVIS Projektstand

Arbeitsphase: Erweiterung um Anforderungen 61–200 am 25.09.2026; lokale Prüfserie und Dokumentation aktualisiert. Das Gesamtziel ist noch NICHT vollständig umgesetzt oder abgenommen. Neu im Quellstand: separater SIP-Dienst, G.711-Realtime-Brücke, Nextcloud/Immich, verschlüsselte strukturierte Erinnerungen mit Importvorschau, Modell-/Budgetsteuerung, persistente Aufgaben/Ziele und Ereignisregeln. Nächste Phase: echte Docker-/Datenbank-/Provider-Abnahme und fehlende Funktionen aus docs/REQUIREMENTS-61-200.md.

## Architektur

.NET 10 mit Domain/Application/Infrastructure/Agent/API, React/TypeScript/Vite, PostgreSQL/Npgsql, Redis, Caddy, Python/Playwright plus separatem DNS-prüfendem Egress-Proxy. Details: ARCHITECTURE.md.

## Bereits erzeugt

- Setup, sichere Sessions, CSRF, MFA/TOTP, verschlüsselter Vault, zentrale Tooldefinitionen und Einmalfreigaben, Audit.
- OpenAI Responses und kompatible Chat-Provider, Agent-Schleifen, Vision, WebRTC-Sprachmodus, Web/News/Bildsuche, mehrstufige Recherche mit Quellen, Cache, Abbruch.
- Browseraktionen, geschützte Profile, Downloads in Quarantäne, PDF-Parser als begrenzter Unterprozess, SSRF-Proxy.
- Google/Microsoft OAuth und Kalender-/Mail-Adapter, Kontakte, Memory/Obsidian, Twilio Gather und ConversationRelay-Streaming.
- Windows Tray-Agent, Swift macOS-Agent, iPhone-Kurzbefehlsprotokoll und Anleitung.
- Zeit-/Cron-/Ereignis-/Kalender-Automationen, Dashboard-Benachrichtigungen.
- Compose, Dockerfiles, Installations-, Backup- und Verifikationsskripte, CI.

## Prüfstand

Erfolgreich:

- Gesamte .NET-Solution als Release einschließlich Windows-Agent: 0 Fehler, 0 Warnungen.
- 39 .NET-Tests einschließlich vier echter lokaler SIP-/RTP-/SRTP-Verbindungen (PCMU/PCMA und DTMF); 1 echter API-/Datenbanktest ausdrücklich übersprungen.
- TypeScript-/Vite-Produktionsbuild. Zwei harmlose Rollup-Hinweise zu Kommentar-Annotationen im SignalR-Paket.
- Acht Python-Tests: DNS/SSRF und echte PDF-Parserprozesse (Text, OCR-Hinweis bei leerer Seite, Ablehnung ungültiger Inhalte).
- Echter Chromium-Smoke-Test: öffentlicher HTTPS-Abruf über Egress-Proxy, Navigation, Text, Titel, Screenshot und Tabs; fehlende Authentifizierung, private Zieladressen und file-URLs abgewiesen. Neuer authentifizierter binärer PDF-Endpunkt ebenfalls mit echtem Parser geprüft.
- Desktop-/Mobil-UI-Test für Setup, Dashboard, Recherche, Aufgaben/Ziele, persönliches Memory, Modelle und SIP, ohne horizontales Überlaufen oder unbehandelte JavaScript-Fehler. Dieser Test verwendet explizite API-Fixtures, keine echte Datenbank. Screenshots visuell geprüft.
- Compose-Konfiguration mit allen optionalen Profilen validiert; PowerShell-Skripte syntaktisch geprüft.
- npm Produktionsabhängigkeiten, .NET-Pakete und installierte Python-Pakete ohne gemeldete bekannte Schwachstellen zum Prüfzeitpunkt. Keine Garantie vollständiger Sicherheitsfehlerfreiheit.

Ein echter API-/PostgreSQL-/Redis-Integrationstest ist vorhanden, lokal ausdrücklich übersprungen. Build-/Startversuche mit Compose scheitern an fehlender Docker-Engine (docker_engine Named Pipe nicht vorhanden). Die portable Compose-CLI validiert die Konfiguration, ersetzt keine Engine. Swift-Compiler/macOS und externe Konten/API-Keys sind nicht vorhanden.

Browser-Python-Pakete wurden in einer separaten portablen x64-Python-Umgebung installiert, nachdem die ARM64-Installation an fehlendem C++-Linker für cryptography scheiterte. Linux-Container haben eigene Abhängigkeiten.

## Noch zu bearbeiten / verifizieren

- README, Restore-Anleitung, Geräteinstallation, API-/Automationsdokumentation, Chat-Historie/Quellen sowie Browser-/UI-Prüfskripte sind vorhanden. Diese Punkte sind nicht mehr offen.
- Docker-Images bauen und gesamten Stack mit Docker Engine starten, Healthchecks und realen Integrationstest ausführen.
- Docker-Netzisolation/Chromium-Sandbox, Sicherung und Wiederherstellung unter Linux-Containern praktisch prüfen.
- Swift-Build auf macOS und reale native Desktop-Steuerung prüfen.
- Provider-Verbindungstests mit vom Benutzer eingetragenen Konten ausführen; keine Kosten verursachenden Calls automatisch ausführen.
- Fehlende Varianten sind in docs/STATUS.md ausdrücklich aufgelistet: u. a. CalDAV/IMAP, lokale STT/TTS, native Mikrofonaufnahme, OCR, Web Push bei geschlossenem Browser, Wakeword, automatischer verbindlicher Telefon-Buchungsabschluss und vollständiger Containerlog-Aggregator. Sie sind nicht als implementiert zu verstehen.
- Neue Grenzen: semantische Memory-Konfliktanalyse, Tagesbriefing/Timeline, vollständige Connector-Watcher, Qualitätsbenchmarks/lernendes Routing, Audio-/Telefonkostenjournal, autonome IVR/Voicemail sowie unabhängige Zielverifikation fehlen. Details in docs/REQUIREMENTS-61-200.md.
- Bei KI-Hard-Limit ist Realtime vorsorglich gesperrt. SIP-Bibliothek hat zusätzliche Nutzungsbeschränkung; Originaltext unter docs/licenses/SIPSorcery.txt. Keine uneingeschränkt BSD-lizenzierte SIP-Gesamtanwendung behaupten.

## Reproduzierbare Prüfungen

dotnet build Jarvis.slnx -c Release

dotnet test tests/Jarvis.Tests/Jarvis.Tests.csproj -c Release

Im Ordner src/Jarvis.Web: npm ci und npm run build.

Mit installiertem Python 3.12 und services/browser/requirements.txt: python -m unittest discover -s tests/browser -v; nach playwright install chromium zusätzlich python tests/browser/smoke_live.py und python tests/ui/smoke.py.

Docker nach Bereitstellung einer laufenden Engine: Installationsskript ausführen; danach scripts/verify-installation und API-Integrationstest mit eigener jarvis_test-Datenbank. CI enthält diese Prüfungen, wurde hier aber nicht extern ausgeführt.

## Wichtige Entscheidungen

Ein Administrator, eine API-Replik. Geheimnisse bleiben im Vault; Browser besitzt einen eigenen Schlüssel. Kein Remote-Shell-Tool. Externe Schreibaktionen gehen durch bestätigte, unveränderliche Argumente. Browserklicks brauchen immer Bestätigung. Desktop-Steuerung zusätzlich lokal freischalten. Forschungsinhalte bleiben untrusted. Keine generischen Claim- oder Erfolgsmeldungen ohne Tool-Ergebnis. Keine Veröffentlichung bei GitHub oder auf einem Server ohne entsprechenden Auftrag.

## Dateistruktur

src/Jarvis.Domain, Jarvis.Application, Jarvis.Infrastructure, Jarvis.Agent, Jarvis.Api, Jarvis.Web; agents/windows, macos, ios; services/browser und services/sip; docker; scripts; tests; docs; .github/workflows.

Diese Datei vor jeder neuen Arbeitsphase zuerst lesen. Funktionierende Architektur nicht ohne Grund wechseln.
