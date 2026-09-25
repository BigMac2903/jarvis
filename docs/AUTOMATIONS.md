# Automationen

Regeln werden im Dashboard unter Automations als JSON konfiguriert. Alle Actions sind registrierte Tools und laufen durch Berechtigungen und Audit. Eine erforderliche Freigabe erscheint unter Freigaben; sie wird niemals durch einen Trigger ersetzt. Pro Installation genau eine API-Replik betreiben.

Einmalige oder regelmäßige Zeitregel:

```json
{"name":"Wochenstart","enabled":true,"trigger":"cron","cron":"0 7 * * 1","timezone":"Europe/Berlin","tool":"Notification.Send","args":{"title":"Wochenstart","message":"Prüfe deinen Wochenkalender."}}
```

Cron nutzt fünf Felder (Minute, Stunde, Tag, Monat, Wochentag) und berücksichtigt die Zeitzone. Für einen einmaligen Termin: `trigger: time`, `nextRun` als ISO-8601 und `intervalMinutes: 0`. Wiederkehrende Intervalle ab einer Minute.

Geräte-/Standortereignis: `trigger: device` oder `location`, `event: home.arrived`, optional `deviceId`; der iPhone-Kurzbefehl sendet dasselbe Ereignis. Neue Regeln starten ab ihrer Erstellung und spielen alte Ereignisse nicht nach.

Webhook: authentifiziert POST `/api/v1/automation-hooks` mit `{"event":"external.ready"}`; die zurückgegebene URL ist ein Secret und wird nur einmal ausgegeben. Externe Systeme dürfen diese URL per POST aufrufen. Die Regel verwendet `trigger: webhook`, `event: external.ready`. Eingehende Payloads verändern weder Tool noch Argumente.

Kalendererinnerung: `trigger: calendar`, `provider: google` oder `microsoft`, `minutesBefore: 30`. Prüfung ungefähr einmal pro Minute, Deduplizierung nach Ereignis-ID. Das Ereignis wird an die statisch konfigurierte Toolaktion gekoppelt. Keine aus untrusted Kalendereinträgen erzeugten Toolargumente.

Fehlgeschlagene Regeln werden deaktiviert und zeigen den Fehler. Externe Aktionen werden vor Ausführung persistent beansprucht. Nach einem Absturz wird eine möglicherweise bereits ausgeführte Schreibaktion nicht automatisch wiederholt. Bei `running` nach einem Absturz Audit und externen Dienst prüfen, bevor du die Regel neu aktivierst.
