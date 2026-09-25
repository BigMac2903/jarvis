# Operator, Konnektoren, Memory und Modellsteuerung

Dieser Stand implementiert einen begrenzten Operator-Kern, nicht bereits sämtliche Anforderungen 94–172. [Abnahmestand](STATUS.md) und [Erweiterungsmatrix](REQUIREMENTS-61-200.md) unterscheiden Code, lokale Tests und offene Funktionen.

## Nextcloud und Immich

Settings → Nextcloud: öffentliche HTTPS-Basis-URL der Installation, Benutzer, eigenes App-Passwort und optional freigegebenen Unterordner eintragen. Es werden ausschließlich feste WebDAV-/OCS-Pfade an diesem administrativ festgelegten Server aufgerufen. Keine Redirects mit Zugangsdaten, kein freier URL-Proxy, kein Überschreiten des freigegebenen Roots. Seit Erweiterung 201–213 werden Nextcloud und Immich auf diesem Pfad zusätzlich über den öffentlichen Egress-Proxy begrenzt. Private HTTPS-Server müssen in [Local Network](LOCAL_NETWORK.md) registriert werden; eine Durchleitung der vollständigen WebDAV-/Immich-Spezialadapter durch den lokalen Agenten steht noch aus.

Tools: Auflisten, Dateinamensuche, Lesen, Textdatei neu anlegen, Ordner anlegen, Kopieren, Verschieben, Löschen und Capabilities. Schreiben benötigt zusätzlich `writable`; Anlegen und Move/Copy überschreiben keine vorhandenen Ziele. Löschen benötigt stets Bestätigung. Lesen maximal 10 MB; Office-ZIP-Expansion und XML sind begrenzt. PDF nutzt den privaten begrenzten Parserprozess. Alte binäre Officeformate, Makros, OCR und perfekte tabellarische Rekonstruktion sind nicht enthalten. Bilder können bei einer angefragten Leseaktion dem konfigurierten Vision-Modell übermittelt werden.

Für `Nextcloud.Index` separat `indexEnabled` einschalten und die Übertragung an den konfigurierten Embedding-Provider bestätigen. Ein Lauf betrachtet maximal 500 Dateien/1000 Ordner. ETags sparen erneute Extraktion und Embeddings; Texte und Vektoren des neuen Nextcloud-Index werden verschlüsselt gespeichert. `Nextcloud.SemanticSearch` prüft ETags der besten Treffer erneut und verwendet keine veränderten, gelöschten oder nicht mehr zugänglichen Quellen. Noch kein lückenloser Datei-Watcher: wiederholte begrenzte Scans können über eine Automation aufgerufen werden, besitzen aber keinen Fortsetzungscursor für beliebig große Bibliotheken.

Settings → Immich: HTTPS-Root-URL, nicht `/api`, sowie separaten API-Key speichern. Suche läuft über Immich Smart Search; dessen eigener ML-Dienst muss verfügbar sein. Album-/Asset-Metadaten und bestätigte Thumbnail-Vorschau sind vorhanden. Vorschauen brauchen `allowVision` und eine Toolfreigabe. Originalbilder bleiben beim Server. Keine automatische Foto-Personenidentifikation, Fotolöschung, Timeline-Korrelation oder ungefragte Erstellung persönlicher Fakten.

Referenzen: [Nextcloud WebDAV](https://docs.nextcloud.com/server/stable/developer_manual/client_apis/WebDAV/basic.html), [DAV SEARCH](https://docs.nextcloud.com/server/stable/developer_manual/client_apis/WebDAV/search.html), [Immich OpenAPI](https://github.com/immich-app/immich/blob/main/open-api/immich-openapi-specs.json).

## Persönliches Gedächtnis

Memory enthält einen Editor mit Kategorien, Quelle, Zeitstempeln, Konfidenz, Sensitivität, Anheften und verschlüsselter Versionshistorie. Neue `Memory.Write`-Aktionen speichern ebenfalls hier und sind standardmäßig sensibel. Identity, Relationships und Finances bleiben zwingend sensibel. Andere Kategorien kann der Benutzer ausdrücklich zur Kontextnutzung freigeben. `Memory.PersonalSearch` wählt höchstens acht passende freigegebene Fakten anhand von Suchwörtern; dies ist noch kein semantischer persönlicher Kontextbuilder. `Memory.ReadSensitive` benötigt immer eine neue konkrete Freigabe.

Import: lokale `conversations.json` aus einem ChatGPT-Export, Fakt-JSON oder Markdown/TXT, maximal 5 MB/500 Kandidaten. ZIP erst selbst entpacken. Importvorschau enthält Benutzeraussagen, keine als Fakten übernommenen Assistentenantworten. Jeder Kandidat muss geprüft/bearbeitet und übernommen werden. Exakte Dubletten werden markiert; semantische Konflikte werden noch nicht automatisch gelöst. Keine Verbindung zu internem ChatGPT-Memory, keine Cloudanalyse beim Import. Passwörter/API-Schlüssel nicht importieren; die einfache Erkennung offensichtlicher Secret-Zuweisungen ersetzt keine Inhaltsprüfung.

Verschlüsselung betrifft neue persönliche Fakten/Versionen, den Nextcloud-Index und das Aktionsjournal. Bestehende Legacy-Memory-Datensätze, Obsidian-Dateien/Vektoren, Chats, ausstehende Freigabeargumente und Telefontranskripte sind nicht pauschal verschlüsselt. Deshalb verschlüsselte Datenträger/Backups verwenden. Legacy-Erinnerungen bleiben sichtbar, werden aber nicht automatisch als persönlicher Modellkontext abgerufen oder neu eingebettet. Gewünschte Einträge manuell in den neuen Editor übernehmen und alte Kopien nach Prüfung selbst löschen. Die Umsetzung löscht keine vorhandenen Nutzerdaten stillschweigend.

Das Löschen einer Erinnerung entfernt ihren Datensatz und ihre Memory-Historie, nicht frühere Chats, externe Providerdaten oder alte Backups. Explizit gelesene sensible Fakten können bereits in Antworten/Verläufen vorkommen.

## Aufgaben, Ziele und Ereignisse

Tasks & Goals startet persistente Aufträge. Ziele werden in höchstens acht sequenzielle Teilaufgaben zerlegt; vorhandene Tools und Freigaben bleiben wirksam. Aufgaben laufen seriell in einem separaten Hintergrunddienst. Pro Lauf acht Minuten, pro Chat acht Modellrunden mit bis zu acht Toolaktionen. `NeedsApproval` pausiert, die konkrete Freigabe wird im Dashboard bearbeitet. Nach einem Serverabbruch wird Running zu Waiting statt blind erneut ausgeführt.

Das Aktionsjournal bindet Schreibaktionen an Aufgabe, Tool und kanonische Argumente. Abgeschlossene Aktionen liefern ihr gespeichertes Ergebnis; unklare begonnene Aktionen werden gesperrt. Das ist kein verteiltes Exactly-once-Versprechen: eine externe Wirkung kann bei Verbindungsabbruch unbekannt bleiben. Vor Fortsetzen externe Ergebnisse prüfen. Stoppen ist kooperativ und kann bereits gesendete Aktionen nicht zurücknehmen; noch offene Freigaben werden verworfen. Ziele planen einmalig, nicht unbegrenzt neu. Ein modellseitig abgeschlossener Auftrag ist noch keine unabhängige fachliche Erfolgsprüfung.

Autonomie: Standard-Level 3. Level 0 sperrt schreibende Tools; Level 1 bleibt bei gewöhnlichen Bestätigungen. Level 2/3 kann die begrenzte Allowlist (Memory.Write, Nextcloud Copy/Move/CreateFolder, Device.OpenApplication) mit Aktivitätsmeldung ausführen. Explizite Sperren haben Vorrang. AUTO/NOTIFY/ASK/ALWAYS_CONFIRM sind auswählbar; irreversible Tools bleiben immer ASK. Es gibt kein selbstlernendes Anheben von Berechtigungen.

Events bewertet konfigurierte Geräteereignisse anhand von Dringlichkeit (40 %), Auswirkung (40 %) und Relevanz (20 %). Die Prioritäten LOW/NORMAL/IMPORTANT/CRITICAL bestimmen Log/Benachrichtigung; LOW/NORMAL warten in der Ruhezeit. Kritische Anrufe brauchen sowohl `criticalCalls` in Settings als auch `allowCall` in der jeweiligen Regel und passieren anschließend die normale Telefonpolicy. Ereignis-IDs verhindern wiederholte Anrufversuche. Regeln verwenden feste eigene Meldungstexte, nicht vom Gerät eingeschleuste Anweisungen.

```json
{"event":"battery-critical","enabled":true,"urgency":90,"impact":90,"relevance":90,"message":"Das konfigurierte Gerät meldet einen kritischen Zustand.","allowCall":false}
```

Noch kein zentraler Multi-Source-Korrelator, Daily-Briefing-Generator, Notification-Digest, automatisch gelernte Präferenzen oder laufende Ziel-Neuplanung. Bestehende Kalender-/Webhook-/Zeitautomationen bleiben separat verfügbar.

## Modellregistry und Budget

Die Registry steuert Text-/Agentmodelle am einen konfigurierten Provider-Endpunkt. „Verfügbare Modelle entdecken“ liest dessen Modellliste und legt neue Einträge deaktiviert als `AVAILABLE_NOT_VALIDATED` an. Die Existenz eines Namens beweist weder Fähigkeiten noch Preise. Sobald Registry-Einträge vorhanden sind, muss mindestens ein passendes Modell freigegeben sein; sonst wird ausdrücklich abgebrochen. Ohne Registry werden die bisherigen expliziten Modellfelder verwendet (Preis unbekannt).

Expert-JSON, mit realem Modellnamen, aktuellen Anbieterangaben und geprüften Fähigkeiten ausfüllen; niemals Beispielpreise ungeprüft übernehmen:

```json
{
  "model_name": "DEIN_VERFUEGBARES_MODELL",
  "enabled": false,
  "validated": false,
  "capability_level": 0,
  "context_window": 32000,
  "supports_tools": true,
  "supports_vision": false,
  "supports_reasoning": false,
  "reasoning_efforts": [],
  "input_cost": null,
  "cached_input_cost": null,
  "output_cost": null
}
```

Preise: USD je Million Tokens, keine Token-Einzelpreise. Level 0 günstig, 1 ausgewogen, 2 stark, 3 Frontier. Level 4 ist für spezialisierte explizite Modelle reserviert und derzeit nicht als routinemäßiger Text-Fallback nutzbar. Heuristik wählt passende validierte Tool-/Vision-/Kontextfähigkeiten und dann geringe geschätzte Kosten. Komplexere Aufgaben benötigen Level 2; Frontier ist kein Routine-Default, auch nicht unter Maximum Quality. Reasoning-Effort wird nur verwendet, wenn entsprechende erlaubte Werte hinterlegt sind. Bis zu drei Modelle desselben Providers können bei Providerfehlern versucht werden. Kein automatisches Cross-Provider-Routing und noch keine qualitätsbasierte Selbstbenchmark-/Confidence-Eskalation.

Economy senkt Outputbudgets; Soft-Limits reduzieren ebenfalls. Balanced/Best Value sind derzeit derselbe kostenorientierte Pfad. Custom hat noch keinen eigenen Regel-Editor. Hard-Limits für UTC-Kalendertag und -monat werden vor Text-/Embedding-/Textstream-Anfragen über transaktionale Kostenreservierungen geprüft; unbekannte Preise blockieren solche Anfragen bei aktivem Hard-Limit. Bei unbekanntem Ausgang bleibt die konservative Reservierung bestehen. Tatsächliche Rechnungen können wegen Providerpreisen, Zusatzgebühren oder fehlenden Usage-Daten abweichen: das Journal ist keine Zahlungsabrechnung.

Input-, Cache-, Output- und Reasoning-Tokens sowie Dauer/Modell/Tasktyp werden gespeichert. Reasoning ist in Output enthalten und wird nicht doppelt bepreist. Agent-/Connector-Spalten sind noch nicht überall granular befüllt. Die Dashboardfenster beruhen auf UTC-Tagesaggregaten, nicht sekundengenauen rollenden 24 Stunden. Audio- und Telefongebühren sind nicht enthalten. Realtime ist deshalb bei Hard-Limit gesperrt. Zusätzlich beim Provider selbst verbindliche Kosten-/Tariflimits setzen.

## Neue API-Endpunkte

- `POST /api/v1/operator/tasks` oder `/goals`: Beschreibung und optionale Priorität/Deadline.
- `POST /api/v1/operator/tasks/{id}/stop` bzw. `/resume`.
- `GET /api/v1/memory/personal`, `PUT/DELETE /api/v1/memory/personal/{id}`, `GET …/{id}/history`.
- `POST /api/v1/memory/import/preview`: `{text, format}` mit `json` oder `markdown`.
- `GET /api/v1/ai/usage`, `POST /api/v1/ai/models/discover`.
- Registry/Eventregeln über authentifizierte `/api/v1/data/ai-models` bzw. `/event-rules`.
- `GET /api/v1/phone/sip/status`.

Alle Änderungen benötigen Benutzersession, passende Origin und CSRF-Token; Gerätetokens erhalten keine Administrationsrechte. Ein Lauf mit echter Datenbank und echten Konnektoren steht noch aus.
