# API-Vertrag

Versionierte Routen liegen unter /api/v1. Die maschinenlesbare Spezifikation liegt nach Anmeldung unter /openapi/v1.json. Same-Origin-Cookie-Sitzungen verwenden X-CSRF-Token und einen passenden Origin-Header für alle Schreibaufrufe. GET /auth/me liefert den CSRF-Token. Tokens niemals in eine URL oder in ein Modellprompt kopieren.

## Kernrouten

| Bereich | Methode und Route | Bedeutung |
|---|---|---|
| Setup | GET /setup/status, POST /setup | Einmalige Einrichtung mit Setup-Token |
| Auth | POST /auth/login, /auth/refresh, /auth/logout | Sichere Sitzung und Rotation |
| MFA | POST /auth/mfa/enroll, /auth/mfa/confirm | TOTP-Schlüssel erzeugen und prüfen |
| Agent | POST /chat | message, conversation (optional), image (optional, PNG/JPEG Data-URL) |
| Tools | GET /tools, POST /tools/execute | Registry und name/args; niemals ein freies Shellkommando |
| Freigaben | GET /approvals, POST /approvals/{id} | approve=true/false; genau einmal ausführbar |
| Research | POST /research | question, mode: quick/research/deep, fresh |
| Research | GET /research, POST /research/{id}/cancel | Persistente Jobs und Abbruch |
| Settings | GET /settings, PUT /settings/{section} | Verschlüsselte Secretfelder werden nur geschrieben |
| Verbindungstest | POST /settings/{section}/test | ai, internet, google, microsoft, twilio |
| OAuth | POST /oauth/{provider}/start | Liefert Anbieter-URL; state und PKCE serverseitig |
| Geräte | POST /devices/pairing, POST /devices/pair | Kurzlebiger Code + starkes Token |
| Geräte | POST /devices/{id}/revoke | Dauerhafte Geräteberechtigung widerrufen |
| Audit | GET /audit | Zeit, Tool, Ergebnis, Dauer, Approval, Feldnamen und Hash |
| Daten | GET /data/{kind} | Dokumente des angemeldeten Administrators |

Alle Geräte-Aufrufe verwenden Authorization: Bearer GERÄTETOKEN und ausschließlich /device-agent/*. Ein Gerätetoken darf weder Einstellungen lesen noch Tools freigeben. Ein Gerät ruft GET /device-agent/commands ab und beantwortet POST /device-agent/commands/{id}/result mit ok und Daten. Kommandos laufen ab und werden nicht erneut ausgeliefert.

## Browser-Credentials

PUT /browser/credentials/{serviceId} mit JSON:

```json
{"host":"konto.example.com","username":"DEIN-NUTZER","password":"DEIN-PASSWORT","usernameSelector":"#username","passwordSelector":"#password","submitSelector":"button[type=submit]"}
```

Dies ist ein administrativer HTTPS-Aufruf, kein Agent-Tool und keine Chatnachricht. Danach kann Web.Login mit serviceId und tab angefragt werden. Die Domain muss exakt mit der aktuell geöffneten Seite übereinstimmen. Das Modell erhält weder Passwort noch Benutzername aus dem Vault. submitSelector ist optional. Cookies bleiben geschützt im eigenen Profil; die Private-Option verwirft den Sitzungszustand beim Schließen. Beim Wechsel zwischen persistent und privat wird die bisherige Browsersitzung geschlossen.

## Echtzeit

SignalR unter /hubs/events sendet sachliche Events: research, job, tool, device, call, automation, notification. Die Verbindung erhält ausschließlich die Gruppe ihres angemeldeten Benutzers. Interne Modellüberlegungen werden nicht übertragen.

POST /voice/session akzeptiert application/sdp. Der Server konfiguriert die WebRTC-Sitzung und hält den dauerhaften OpenAI-Key geheim. Die einzige Realtime-Funktion delegiert an den gemeinsamen /chat-Agenten; Modell-Toolaufrufe dürfen keine Freigaben fälschen.

Twilio-Webhooks und ConversationRelay-WebSockets sind gesondert per Twilio-Signatur und Anruf-ID gebunden. Sie akzeptieren keine normale Benutzersitzung als Ersatz für die Signatur.
