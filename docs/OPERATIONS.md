# Betrieb und Wiederherstellung

## Daten und Secrets

PostgreSQL enthält Benutzer, verschlüsselte Provider-Secrets, Sitzungen, Kontakte, Chats, Recherchen, Freigaben, Audit, Geräte, Kommandos und Automationen. Redis verwendet ein eigenes internes Passwort. Browserdaten liegen in einem getrennten Volume; Sessions sind mit BROWSER_MASTER_KEY verschlüsselt. Obsidian ist ein konfigurierter Host-Mount. Die API läuft als UID 1654; der Browser als UID 10001.

API und Browser schreiben JSON-/Console-Logs; Docker rotiert diese mit 10 MB × 3 je Container. Das Dashboard zeigt das strukturierte Aktions-Audit. Es aggregiert nicht die Rohlogs aller Container. Logsammlung wie Loki/Grafana ist eine optionale externe Ergänzung.

## Restore auf eine frische Installation

Nutze für den Restore ein neues Installationsverzeichnis und neue Docker-Volumes. Halte die alte Installation für die Rückkehr erhalten. Die folgenden Schritte dürfen nicht versehentlich auf produktiven Volumes ausgeführt werden.

1. Dieselbe Repository-Version wie beim Backup bereitstellen.
2. config.env aus dem Backup als .env in die neue Installation kopieren. Domain/Ports ggf. anpassen, Master-Keys beibehalten. Alten und neuen Stack nicht mit demselben Compose-Projektnamen gleichzeitig starten; bei Parallelbetrieb den Projektnamen gezielt ändern.
3. Nur PostgreSQL und Redis starten: docker compose up -d postgres redis.
4. Den Dump kopieren: docker compose cp /DEIN/BACKUP/postgres.dump postgres:/tmp/restore.dump.
5. In die leere Datenbank importieren: docker compose exec -T postgres pg_restore -U jarvis -d jarvis --no-owner --exit-on-error /tmp/restore.dump. Enthält die Zieldatenbank bereits Tabellen, anhalten und das Ziel prüfen. Kein automatisches --clean gegen vorhandene Daten.
6. Den gesicherten Obsidian-Inhalt an den in OBSIDIAN_HOST_PATH konfigurierten neuen Hostpfad kopieren.
7. API und Browser zunächst nur erzeugen: docker compose create jarvis-api jarvis-browser. Mit docker compose cp die gesicherten Inhalte aus uploads nach jarvis-api:/data/jarvis und aus browser nach jarvis-browser:/data kopieren. Auf den tatsächlichen Inhalt achten, keinen zusätzlichen Unterordner verschachteln. Die Dateieigentümer müssen den oben genannten Container-UIDs entsprechen.
8. Vollständigen Stack starten: docker compose up -d --wait --wait-timeout 300.
9. verify-installation ausführen, anmelden, Memory-Dateien lesen, Kontotests ausführen und ein zuvor freigegebenes Gerät prüfen.
10. Neue interne Caddy-CA bei Bedarf auf Clients vertrauen. Caddy-Zertifikatsdaten werden nicht im Standard-Backup mitgesichert; bei eigener PKI diese separat nach deren Verfahren sichern.

Ein Backup ist erst belastbar, wenn eine Wiederherstellung auf separaten Volumes tatsächlich geprüft wurde. Diese Restore-Prozedur ist im vorliegenden Entwicklungsumfeld noch nicht ausgeführt worden.

## MFA-Wiederherstellung

Es gibt keine ungeschützte HTTP-Resetroute. Ein Serveradministrator kann nach Prüfung der eigenen Identität mit lokalem PostgreSQL-Zugriff MFA des betroffenen Kontos entfernen und alle Sessions löschen. Dies umgeht absichtlich nicht die physische/administrative Serverkontrolle. Vorher Datenbankbackup erstellen; danach Passwort wechseln und MFA neu einrichten. Der normale Benutzerpfad ist das Sichern des TOTP-Secrets im Passwortmanager.

## Sicher betreiben

- Nur einen API-Prozess pro Installation betreiben. Das System ist für persönliche Nutzung ausgelegt, nicht als öffentliches Mehrmandantensystem.
- Docker-Socket niemals in einen Dienst mounten. Keine Browser-Sandboxabschaltung, kein privileged und kein Hostnetz.
- INTERNAL_ALLOW_HOSTS normalerweise leer lassen. Einträge erlauben ausdrücklich interne Ziele über den Forschungsproxy; damit wird eine Sicherheitsgrenze erweitert.
- Cloudintegrationen bewusst aktivieren. Modellanfragen können freigegebene Nachrichten, Bilder, Toolresultate und Rechercheauszüge enthalten.
- Kein automatischer Kaufabschluss. JARVIS bindet Freigaben an die konkrete Aktion. Bei unklaren externen Resultaten nicht blind erneut senden.
- Externe Dienste sind nicht Teil der lokalen Backupgarantie. Versandte Mails, Anrufe und entfernte Kalenderänderungen werden durch einen lokalen Restore nicht rückgängig gemacht.
- Löschen von Chats/Recherchen entfernt Nutzungsdaten; sicherheitsrelevante Auditdaten bleiben erhalten. Download-Quarantäne, Datenbankbackups und externe Providerdaten haben einen eigenen Lebenszyklus.

## Zeit, Neustarts und Fehler

TOTP und Terminplanung benötigen eine korrekt synchronisierte Systemuhr. Unterbrochene Recherchejobs werden beim Neustart als interrupted markiert. Automationen beanspruchen einen Trigger vor der Ausführung, damit ein Absturz keine blinde Wiederholung auslöst. Ein nicht bestätigtes externes Ergebnis muss manuell geprüft werden.
