# Drittanbieter

Der vollständige Apache-2.0-Lizenztext ist in LICENSE-APACHE-2.0.txt enthalten. Copyright (c) Microsoft Corporation für den übernommenen Playwright-Bestandteil.

Das Docker-seccomp-Profil basiert auf microsoft/playwright, utils/docker/seccomp_profile.json, Version v1.51.1, das seinerseits auf dem Docker/Moby-Standardprofil basiert. Ergänzt wurde eine explizite ENOSYS-Antwort für clone3, damit libc auf clone zurückfällt. Quelle: https://github.com/microsoft/playwright/blob/v1.51.1/utils/docker/seccomp_profile.json . Playwright und Docker/Moby stehen unter Apache-2.0.

Weitere Bibliotheken werden über NuGet, npm und PyPI bezogen. Ihre jeweiligen Lizenztexte und Copyright-Hinweise bleiben in den installierten Paketen erhalten. React, Vite, TypeScript, Npgsql, Redis, PostgreSQL, Caddy, Chromium, pdfplumber und die übrigen Laufzeitabhängigkeiten besitzen eigene Lizenzbedingungen. Dieses Repository ersetzt diese nicht.
