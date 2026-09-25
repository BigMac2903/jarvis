# SIP-Telefonie

SIP ist der Standardprovider hinter `IPhoneProvider`. Twilio bleibt optional und muss unter Telefonie ausdrücklich ausgewählt werden. Ohne Kontokonfiguration, freigegebene Länder, Audiofreigabe und verfügbare KI wird nicht gewählt. Es wurden keine PSTN-Testanrufe durchgeführt.

## Dienst und Netzwerk

Der separate .NET-Dienst `jarvis-sip` benutzt SIPSorcery 10.0.16. Er registriert Konten, behandelt SIP/SDP und transportiert G.711 zwischen RTP und der privaten WebSocket-Brücke im Core. OpenAI Realtime verarbeitet dort Sprache und erzeugt Audio. Audio wird nicht als Datei gespeichert; Transkript und Zusammenfassung werden gespeichert. Kein lokaler STT/TTS-Ersatz ist enthalten.

1. Bestehende Installation sichern, Konfigurationsskript erneut mit `--configure-only` bzw. `-ConfigureOnly` ausführen. Dadurch kommt der neue zufällige interne `SIP_SERVICE_TOKEN` hinzu; vorhandene Werte bleiben bestehen.
2. `COMPOSE_PROFILES=sip` setzen (weitere Profile kommagetrennt). Für Erreichbarkeit vom PBX-Netz `SIP_BIND_ADDRESS` auf die eigene LAN-Adresse und `SIP_ADVERTISE_ADDRESS` auf die von PBX und Mediengateway erreichbare IP setzen.
3. Mit beiden Compose-Dateien starten:

```bash
docker compose -f docker-compose.yml -f docker-compose.sip.yml --profile sip up -d --build --wait
```

Die zweite Datei veröffentlicht 5060/UDP+TCP, 5061/TCP und 20000–20099/UDP. Standard-Bindung ist absichtlich nur Loopback. SIP/RTP nicht ungeschützt ins Internet öffnen: Hostfirewall auf bekannte PBX- und Medienadressen begrenzen. Der interne HTTP-/WebSocket-Port 8080 wird niemals veröffentlicht. Die Core-Endpunkte `/internal/sip/*` sind nicht über Caddy freigegeben und benötigen den separaten Dienstschlüssel.

Docker-NAT und manche PBX verlangen zusätzliche Portweiterleitungen/Contact-Einstellungen. STUN/ICE/TURN für SIP ist nicht implementiert. Bei NAT bevorzugt eine erreichbare PBX/SBC vor dem Dienst einsetzen; der konkrete Aufbau ist noch praktisch abzunehmen.

TLS ist für ausgehende Verbindungen und wiederverwendete Providerverbindungen implementiert, Zertifikatsprüfung bleibt aktiv. Ein eigener TLS-Server mit eingebundenem Serverzertifikat für neue eingehende TLS-Verbindungen ist noch nicht konfigurierbar; gegebenenfalls TLS am kontrollierten SBC terminieren. UDP/TCP ohne TLS nur im vertrauenswürdigen privaten Netz verwenden. SDES-SRTP wird außerhalb des isolierten Tests ausschließlich zusammen mit TLS zugelassen. Es gibt keinen stillen Downgrade von SRTP auf Klartext.

## Konto anlegen

Settings → SIP-Konten → Konten-JSON; Beispielwerte durch echte Providerdaten ersetzen:

```json
[
  {
    "id": "private",
    "name": "Privat",
    "enabled": true,
    "server": "pbx.example.invalid",
    "port": 5061,
    "username": "1001",
    "authId": "1001",
    "domain": "pbx.example.invalid",
    "displayName": "JARVIS",
    "callerId": "1001",
    "transport": "TLS",
    "useTls": true,
    "srtp": true,
    "registrationInterval": 300,
    "outboundProxy": "",
    "allowedDirections": ["outbound"],
    "defaultUsage": "private",
    "trustedPeers": [],
    "incomingMode": "OFF",
    "incomingAllowlist": []
  }
]
```

`defaultAccount=private` setzen. Unter „Konto-ID für das folgende Passwort“ `private` und im maskierten Feld das zugehörige Passwort eintragen. Nach Speichern wird es aus der Antwort entfernt und im Vault abgelegt. Je Konto wiederholen; maximal acht Konten. Passwörter weder in Notizen noch in Chat oder Screenshots eintragen. Änderungen werden ungefähr alle 15 Sekunden geladen; eine Konfigurationsänderung beendet bestehende Gespräche und registriert Konten neu.

Unter Telefonie mindestens `provider=sip`, `allowedCountries=["+49"]` und gewünschte Dauer-/Versuchslimits setzen. `allowedNumbers` kann zusätzlich eine exakte Allowlist begrenzen. Teure Sondervorwahlen sind zusätzlich gesperrt; das ist kein vollständiges internationales Tarifverzeichnis. Sperren beim Telefonanbieter sind weiterhin sinnvoll. E.164 ist verpflichtend, keine beliebigen SIP-URIs als Wahlziel.

Unter KI OpenAI explizit aktivieren, `https://api.openai.com/v1`, API-Key, verfügbares Realtime- und Transkriptionsmodell konfigurieren. Unter Telefonie die Übertragung von Audio und Speicherung des Textprotokolls freigeben. Bei aktivem KI-Hard-Limit ist Realtime derzeit gesperrt: Audioabrechnung ist noch nicht zuverlässig in das Budgetjournal integriert. Telefonproviderkosten sind nie im KI-Tokenbudget enthalten.

„Verbindung testen“ verlangt mindestens ein tatsächlich registriertes Konto, nicht nur einen erreichbaren Prozess. Im Phone-Dashboard werden Registrierung, letzter Versuch und laufende Gespräche angezeigt.

## Routing und Anrufe

Routing-Regeln werden in Reihenfolge ausgewertet. Mögliche Felder: `accountId`, `prefix`, `country` (Vorwahl), `usage`, `contactGroup`, `taskType`, `startHour`, `endHour`. Zeitfenster benutzen die Benutzerzeitzone und dürfen über Mitternacht reichen.

```json
[{"accountId":"business","contactGroup":"company","startHour":8,"endHour":18}]
```

Danach folgt das Konto mit passendem `defaultUsage`, danach `defaultAccount`. Fallback auf `fallbackAccount` ist nur vor einem Wahlversuch und bei `allowFallback=true` erlaubt; ein unbekanntes Wählergebnis erzeugt keinen zweiten Anruf.

`Phone.Call`: Nummer oder Kontakt-ID und Gesprächsauftrag; optional Kontext, Dauer, Priorität und Nutzung. `Phone.CallUser`: eigene konfigurierte Nummer, Zweck und optional Ereignis-ID zur Deduplizierung. Kontakte verwenden das vorhandene Telefonbuch. Anrufe werden standardmäßig angefragt; Ausnahmen müssen ausdrücklich in Permissions gewählt werden. `Phone.Transfer` bleibt immer bestätigungspflichtig und verwendet SIP REFER. Hold/Resume und DTMF sind Tools, keine automatische Menü-Navigation.

Eingehende Anrufe benötigen zusätzlich `inbound` in `allowedDirections`, exakte PBX-IP-Adressen in `trustedPeers` und einen der Modi `KNOWN_CONTACTS_ONLY`, `ALLOWLIST`, `ALL_CALLERS`, `UNKNOWN_CALLERS_TO_SCREENING`. `OFF` ist Standard. Caller-ID ist keine Authentifizierung: auch bekannte Anrufer erhalten keine administrativen Tools, Geheimnisse oder privaten Kalenderdetails. Die aktuelle Audiobrücke führt Screening-Gespräche und sammelt Rückrufdaten; sie besitzt keine Tools zum verbindlichen Buchen oder selbstständigen Weiterverbinden.

## Abnahme und Grenzen

Lokal geprüft: echte SIP-INVITE/Answer/BYE-Dialoge über Loopback, RTP-Nutzdaten mit PCMU und PCMA, jeweils unverschlüsselt und mit SDES-SRTP, sowie DTMF-Empfang. Das sind Bibliotheksintegrationstests, keine Abnahme des vollständigen Docker-Dienstes oder der Cloud-Audiobrücke.

Noch zu prüfen: Providerregistrierung und Authentifizierung, echte Rufnummern, NAT, TLS, eingehende Anrufe parallel zu laufenden Gesprächen, Hold/Resume/REFER am Ziel-PBX, Ende-zu-Ende-Realtime-Audio, Unterbrechungen und Call-Summary. Noch fehlend: automatische IVR-Planung, Voicemail-Erkennung, Wartemusikerkennung, lokale STT/TTS, konfigurierbare Audioaufnahme, Bridge-Fallback ohne REFER, Codec-Transkodierung für G.722/Opus, Jitterpuffer/Paketumordnung und exakte Kürzung bereits generierter Modell-Audiohistorie nach Barge-in.

Die Callliste zeigt persistente Zustände, Dauer, Konto, Transkript und Summary. Ein fehlgeschlagener Summary-Aufruf wird nicht als erfolgreiche Zusammenfassung ausgegeben. Registrierungsfehler sind bisher nur als nicht registriert/erneuter Versuch sichtbar; detaillierte Statuscodes und RTP-Qualitätsmetriken fehlen.

SIPSorcery hat zusätzlich zur BSD-3-Clause-Basis eine Nutzungseinschränkung; deshalb keine Behauptung einer uneingeschränkt BSD-lizenzierten Gesamtanwendung. Originalhinweise: [Drittanbieter](THIRD_PARTY_NOTICES.md), [Lizenztext](licenses/SIPSorcery.txt). Technische Referenzen: [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery), [Realtime WebSocket](https://developers.openai.com/api/docs/guides/voice-websockets).
