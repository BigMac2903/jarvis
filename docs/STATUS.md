# Funktions- und Abnahmestand

Dieser Stand unterscheidet implementierten Code von tatsächlich geprüften Integrationen. **Die Gesamtabnahme ist offen.**

Ergänzungen 61–200 im Detail: [Anforderungsmatrix](REQUIREMENTS-61-200.md), [SIP-Einrichtung](SIP.md), [Operator/Konnektoren/Memory/Modelle](OPERATOR.md).

| Bereich | Implementierung | Tatsächlicher Prüfstand |
|---|---|---|
| .NET Backend / Layer | API, Domain, Application, Infrastructure, Agent | Lokal kompiliert, Unit-/Security-Tests bestanden |
| Setup / Login / MFA / CSRF | Einmaliges Setup, sichere Sessions, Rotation, TOTP | Komponenten getestet; End-to-End mit PostgreSQL noch offen |
| PostgreSQL / Redis | Versionierte transaktionale Migration und persistente Dienste | Compose validiert; Docker-Engine fehlt, Integrationstest übersprungen |
| Dashboard | React/TypeScript, mobile Ansicht, reale API-Anbindung | Produktionsbuild bestanden; Layout-/Navigationstest mit ausdrücklich isolierten API-Testfixtures |
| Tool-/Permission-System | Schema, Validierung, Risiko, Audit, Einmalfreigaben | Unit-Tests für Validierung, Berechtigungen, Replay und Identität bestanden |
| Chat / Vision | Responses und Chat-Completions, gemeinsame Tools, Bilder/Screenshots | Kompiliert; kein Live-KI-Konto vorhanden |
| Voice | WebRTC, VAD, Unterbrechen, Push-to-talk, gemeinsamer Agent | Kompiliert; kein Live-Realtime-Konto vorhanden |
| Research | Drei Modi, mehrere Suchschritte, Quellen, Widerspruchshinweise, Cache, Historie, Abbruch | Kompiliert; Recherche mit Such-/KI-Key noch offen |
| Browser / Fetch | Echter Chromium, DOM/Text/Links/Tabellen/Screenshots, Tabs, direkte Webseiten | Echter lokaler Smoke-Test mit example.com bestanden |
| SSRF | Egress-Proxy, DNS-Pinning, Redirectprüfung, IPv4/IPv6-Sperren | Unit- und Live-Smoke-Tests bestanden; Docker-Netzisolation noch nicht live geprüft |
| PDFs / Downloads | Begrenzter Parserprozess, Seitentext/Tabellen, Quarantäne, Hash/MIME/Größe, optional ClamAV | Drei echte Parser-Tests bestanden (Text, OCR-Hinweis, ungültiger Inhalt); Container-Virenscanner und Download-Endabnahme noch offen |
| Google / Microsoft | OAuth-PKCE, Refresh, Kalender und Mail | Kompiliert; echte Konten und OAuth-Registrierungen fehlen |
| Twilio | Ausgehende bestätigte Calls, Gather, DTMF, signierte Webhooks, ConversationRelay, Transkript/Summary | Kompiliert; keine kostenpflichtigen Testanrufe durchgeführt |
| SIP (Standard) | Separater Dienst, Registrierung, Routing, RTP/SRTP PCMU/PCMA, DTMF, Hold/Resume/REFER, Screening, Realtime-Brücke | Vier echte lokale SIP-/RTP-/SRTP-/DTMF-Tests bestanden; PBX und Cloudaudio noch offen |
| Nextcloud / Immich | WebDAV, begrenzte Dokumentextraktion, ETag-Vektorindex, Smart Search und freigegebene Vorschauen | Kompiliert, Pfad-/XML-Sicherheit und PDF-Pipeline geprüft; keine echten Konten |
| Persönliches Memory | Verschlüsselte Fakten/Versionen, Quellen, Sensitivität, Importkandidaten und Editor | Verschlüsselung, Sensitivitätsfilter und Exportfilter getestet; semantische Konfliktauflösung fehlt |
| Tasks / Goals / Events | Persistente Queue und Teilaufgaben, Freigabepausen, Aktionsjournal, Geräteereignisse/Ruhezeit | Kompiliert, Ereignisregeln getestet; Datenbank-/Crash-/Nebenläufigkeitsabnahme offen |
| Modellsteuerung / Kosten | Validierte Registry, heuristisches Preisrouting, Reservierungen und Tokenjournal | Auswahl-/Kostenformeln getestet; Provider-/DB-Abnahme offen, Audioabrechnung fehlt |
| Obsidian / Memory | Markdown, sichere Pfade, Tags/Links, Vektorindex über echten Embedding-Provider | Pfadtests bestanden; Embedding-Aufrufe nicht live getestet |
| Windows | Tray, DPAPI, lokale Freigaben, UI Automation, Screenshot, Apps/Dateien | Lokal kompiliert; Pairing und native Bedienung benötigen laufenden Server |
| macOS | Swift, Keychain, ScreenCaptureKit, Accessibility, Apps/Dateien | Quellcode vorhanden; auf Windows nicht kompilier-/plattformtestbar |
| iPhone | Authentifizierte Shortcuts für Standort, Akku und Ereignisse | Servercode vorhanden; manuelle Einrichtung auf iPhone erforderlich |
| Automationen | Cron/Zeit/Device/Location/Webhook/Calendar, Toolaktionen, Freigaben | Kompiliert; Runtime-Prüfung mit Datenbank noch offen |
| Betrieb | Compose, Installer, Healthchecks, Backup, Restore-Anleitung, CI | Compose-Syntax bestanden; Containerbuilds, Healthchecks und Restore offen |

## Bewusst begrenzte bzw. noch fehlende Varianten

- Ein Administrator pro Installation; keine vollständige Mehrbenutzerverwaltung.
- Keine beliebige Remote-Shell oder freie AppleScript-Ausführung. Keine allgemeine iOS-Fernsteuerung.
- Wakeword, native Hintergrund-Mikrofonaufnahme, vollständig lokale STT/TTS-Pipeline, CalDAV, IMAP/SMTP, OCR und Web Push bei geschlossenem Dashboard sind nicht implementiert. Dies sind nicht aktivierte Optionen mit einem Scheinergebnis.
- Telefonie erzeugt eine Gesprächszusammenfassung. Ein daraus gewünschter Kalendertermin wird über den gemeinsamen Agenten/Calendar und eine eigene Freigabe angelegt; es gibt noch keinen vollständig automatischen verbindlichen Buchungsabschluss.
- Browseransicht erfolgt über Screenshots und definierte Aktionen, nicht über einen Remote-Desktop-/VNC-Stream. Formulare benötigen derzeit Selektoren/administrative Konfiguration.
- Keine automatische Freigabe von Quarantäne-Downloads an andere Dienste. ClamAV-Ergebnis und Quarantänestatus sind getrennt.
- Quellenpriorisierung verwendet Hinweise wie Domain und Dokumentationstyp; sie ist keine garantierte Vertrauensbewertung. Widerspruchserkennung wird vom Modell anhand der gelesenen Belege vorgenommen.
- Obsidian-Frontmatter wird als Markdown erhalten; es gibt noch keinen eigenen YAML-Editor. Semantischer Index arbeitet begrenzt mit Auszügen und muss nach Änderungen erneuert werden.
- Zeitregeln und Eventregeln führen konfigurierte Toolaktionen aus. Es gibt noch keinen visuellen Ablaufeditor oder vollautomatischen natürlichen Regelgenerator.
- Dashboard-Systemansicht zeigt Health und Aktions-Audit; kein vollständiger Containerlog-Aggregator.

Diese Grenzen sind nicht durch Dummy-Adapter verdeckt. Der Auftrag in seiner vollen beschriebenen Breite ist damit noch nicht abgeschlossen; insbesondere sind erfolgreicher Docker-Start und externe/native Integrationstests Voraussetzung für eine belastbare Abnahme.
