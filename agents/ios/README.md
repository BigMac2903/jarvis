# iPhone über Apple Kurzbefehle

Diese Integration nutzt nur von iOS erlaubte Aktionen. Sie liest keine fremden Apps, bedient keine beliebigen Bildschirme und umgeht keine Standortfreigabe.

1. JARVIS über eine vom iPhone vertrauenswürdig erreichbare HTTPS-Adresse öffnen.
2. Unter Devices einen Pairing-Code und das zusätzliche Token erzeugen.
3. In Kurzbefehle einen Kurzbefehl „JARVIS verbinden“ erstellen: URL `https://DEIN-SERVER/api/v1/devices/pair`, „Inhalte von URL abrufen“, Methode POST, JSON mit `token`, `code`, `name: iPhone`, `platform: ios`.
4. Das zurückgegebene Gerätetoken privat speichern. Es kann nur Geräte-Endpunkte aufrufen. Nicht in geteilten Kurzbefehlen veröffentlichen. Bei Verlust im Dashboard widerrufen.
5. Kurzbefehl „JARVIS Standort“: Aktion „Aktuellen Standort abrufen“, Breiten-/Längengrad lesen, POST an `/api/v1/device-agent/location`, Header `Authorization: Bearer GERÄTETOKEN`, JSON `{"latitude":…, "longitude":…}`.
6. Kurzbefehl „JARVIS Status“: „Batteriestatus abrufen“, POST `/api/v1/device-agent/status`, gleicher Header, JSON `{"battery":…, "online":true}`.
7. Persönliche Automation „Ankunft“ / „Verlassen“ kann den Kurzbefehl ausführen und `/api/v1/device-agent/event` mit `{"name":"home.arrived"}` bzw. `home.left` aufrufen. iOS kann eine Bestätigung verlangen; dies wird nicht umgangen.

Die Standortanzeige zeigt die letzte freiwillige Meldung mit Zeitpunkt. Sie ist keine kontinuierliche Ortung und kein Ersatz für Apples „Wo ist?“. Fremde Kurzbefehle können nicht beliebig im Hintergrund vom Server gestartet werden. Für lokale, vom Benutzer ausgelöste Aktionen kann ein eigener Kurzbefehl eine erlaubte Aktion ausführen und das Ergebnis an den Ereignis-Endpunkt melden.
