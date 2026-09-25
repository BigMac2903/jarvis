# JARVIS Architektur

Stand: 25.09.2026. Dieses Repository ist ein Entwicklungsstand des Gesamtauftrags mit Erweiterungen bis Anforderung 200, nicht dessen vollständige Umsetzung. Der tatsächliche Prüfstand steht in PROJECT_STATE.md, offene Funktionen in docs/REQUIREMENTS-61-200.md; nicht ausgeführte Prüfungen gelten nicht als bestanden.

## Komponenten

- ASP.NET Core / .NET 10: REST unter `/api/v1`, SignalR für Status, ein gemeinsamer Agent für Text, Sprache, Recherche und freigegebene Geräte.
- Domain: Verträge, Tooldefinitionen, Risiko- und Berechtigungsmodell ohne Infrastrukturabhängigkeit.
- Application: Tooldispatcher, Freigaben, Validierung und Agent-Orchestrierung. Jeder Aufruf läuft durch dieselbe Berechtigungsgrenze.
- Infrastructure: PostgreSQL, Redis, verschlüsselter Secret Store, Provider für KI, Kalender, Mail, Telefonie, Recherche und Obsidian.
- React/TypeScript/Vite: Same-Origin-Dashboard mit Setup, Login, Chat, Recherche, Freigaben, Geräten und Administration.
- Separater Python/Playwright-Browserdienst: isolierte Kontexte, strukturierte Aktionen, Downloads in Quarantäne, PDF-Verarbeitung. Ein eigener Egress-Proxy prüft Zieladressen und pinnt DNS-Auflösungen. Der Browser hat ausschließlich ein internes Docker-Netz; nur der Proxy besitzt Internetzugang.
- Native Desktop-Agenten: .NET auf Windows, Swift auf macOS. Ausgehende HTTPS-Verbindung, feste Kommandoliste und lokale Anwendungsfreigaben; keine Remote-Shell.
- iOS: authentifizierte Shortcuts für Standort, Akkustand und Ereignisse; keine behauptete allgemeine Fernsteuerung.
- Docker Compose: Caddy als einziger veröffentlichter Dienst im Basis-Stack; optionaler SIP-Portoverride für explizit freigegebene PBX-Netze. PostgreSQL und Redis intern, persistente Daten, Healthchecks, optionale lokale KI und Virenscanner.

## Vertrauensgrenzen

Die erste Administration wird ausschließlich mit einem zufälligen Setup-Token angelegt. Setup und Migration sind transaktional gesperrt. Passwörter werden mit PBKDF2-SHA512, zufälligem Salt und hohem Work-Factor gehasht. Browser-Sessions sind HttpOnly, Secure, SameSite=Strict und werden serverseitig widerrufen. Schreibzugriffe prüfen Origin und einen an die Sitzung gebundenen CSRF-Token. MFA verwendet TOTP mit Replay-Schutz. Geräte besitzen eigene gehashte Tokens und können keine Benutzer-Endpunkte nutzen.

Secrets werden vor Speicherung mit AES-GCM verschlüsselt; der Master-Key kommt aus der Umgebung. API und Audit geben weder Secrets noch rohe Fehlerantworten von Drittanbietern zurück. Browser-Logins setzen Credentials serverseitig ein. Freigaben binden Benutzer, Tool und die unveränderlichen Argumente, laufen ab und werden atomar genau einmal verbraucht. Finanzielle Aktionen und Kontoveränderungen bleiben bestätigungspflichtig. Automationen dürfen Freigaben nicht umgehen.

Webseiten, Mails, PDFs, Kamera- und Bildschirminhalte sind nicht vertrauenswürdige Daten. Sie können keine Berechtigungen verändern. Der Research Agent verfügt nur über Lesetools. URL-Prüfung gilt bei jeder Verbindung und Weiterleitung, einschließlich IPv6, DNS-Auflösung und Browser-Unterressourcen. Downloads werden begrenzt, gehasht und vor Freigabe geprüft; ausführbare Inhalte werden nicht ausgeführt.

## Daten und Ausführung

PostgreSQL ist maßgeblich für Nutzer, Sessions, Einstellungen, Kontakte, Gespräche, Freigaben, Audit, Recherche, Gerätebefehle und Automationen. Redis speichert kurzlebige Zustände und dient als Healthcheck-Abhängigkeit. Schemaänderungen verwenden versionierte SQL-Migrationen unter PostgreSQL-Advisory-Lock. Hintergrundarbeit besitzt Zeitlimits, Cancellation und persistente Zustände; ein Neustart markiert unterbrochene Arbeit nachvollziehbar.

KI-Provider sind konfigurierbar: OpenAI Responses API und OpenAI-kompatible Chat-Completions. Modellnamen werden vom Administrator gesetzt. Tools werden explizit in JSON Schema beschrieben, serverseitig validiert und mit begrenzter Schleifenzahl ausgeführt. Recherche läuft als eigenständiger Job und sendet nur sachliche Fortschrittsmeldungen. Quellen enthalten URL, Titel, Abrufzeit, optional Publikationsdatum und Seitenzahl.

## Sichere Standardentscheidungen

Ein persönlicher Administrator pro Installation; Daten sind trotzdem konsequent einem Benutzer zugeordnet. Weitere Benutzerverwaltung ist eine separat zu prüfende Erweiterung. OAuth nutzt Authorization Code + PKCE und kurzlebigen einmaligen State. Externe Funktionen sind bis zur expliziten Konfiguration deaktiviert. Schreibaktionen brauchen Freigabe, mit einer begrenzten reversiblen NOTIFY-Allowlist auf Autonomie-Level 2/3. Obsidian-Schreibzugriff ist separat schaltbar. Kein Host-Shell-Tool, keine freie AppleScript-Ausführung und kein Docker-Socket im Browser.

## Verifikation

Unit- und Sicherheitsprüfungen testen Berechtigungen, Einmalfreigaben, URL-Grenzen, Secret Store und Pfadgrenzen. Integrationstests laufen gegen PostgreSQL/Redis. Installationstests prüfen Compose, Migration, API, Web und Healthchecks. Native Plattformtests benötigen die jeweilige Plattform. Provider-Tests benötigen ausdrücklich konfigurierte Konten und rufen ausschließlich lesende Testendpunkte auf. Kostenpflichtige Telefonanrufe oder externe Schreibaktionen sind keine automatischen Installationstests.
# Erweiterung 61–200: Operator und SIP

SIP wird der Standard-Provider hinter IPhoneProvider; Twilio bleibt explizit wählbar. Ein separater .NET-Dienst nutzt SIPSorcery für Registrierung, SIP-Dialoge und RTP, nicht eine Eigenimplementierung des Protokolls. Core und SIP-Dienst authentifizieren interne HTTP/WebSocket-Aufrufe mit einem separaten Diensttoken. SIP-Konten bleiben im Core-Vault; der SIP-Prozess hält benötigte Zugangsdaten nur im RAM. PCMU/PCMA werden ohne Transcoding an die Realtime-Audiobrücke übergeben. Telefonpartner gelten niemals als authentifizierte Administratoren. Nummernrouting, Limits und Freigaben werden vor kostenpflichtigen Aktionen geprüft; ein Fallback erfolgt nur vor der Wahl, nie nach einem unklaren Call-Start.

Zusätzliche Connectoren (Nextcloud WebDAV/OCS, Immich API), strukturiertes Memory/Import, regelbasierter ModelRouter mit Kostenledger sowie persistente Tasks/Goals/Eventbewertungen ergänzen die vorhandenen Layer. Providerdaten können keine Policy ändern. High Autonomy erlaubt keine Umgehung von ALWAYS_CONFIRM. Automatische Aufgaben bleiben innerhalb explizit aktivierter Regeln und Budgetgrenzen. Keine reale Telefonie, Providerkosten oder Veröffentlichung während Entwicklungstests.
