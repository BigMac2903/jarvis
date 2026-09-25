# Drittanbieter

## SIP

Der separate SIP-Dienst und die SIP-Tests verwenden SIPSorcery 10.0.16. Upstream: https://github.com/sipsorcery-org/sipsorcery . Copyright (c) 2006–2026 Aaron Clauson. Der mitgelieferte originale [Lizenztext](licenses/SIPSorcery.txt) enthält eine BSD-3-Clause-Basis **und eine zusätzliche geografische/politische Nutzungseinschränkung**. Die Abhängigkeit darf deshalb nicht als uneingeschränkt BSD-lizenziert beschrieben werden. Bedingungen vor eigenem Einsatz oder Weiterverteilung prüfen. Der im Upstream-Lizenzdokument ebenfalls enthaltene LGPL-Abschnitt betrifft SIPSorceryMedia.FFmpeg; dieses optionale FFmpeg-Paket wird hier nicht verwendet. Drittanbieterlizenzen werden durch die Repository-Lizenz nicht ersetzt.

## Browser und weitere Pakete

Der lokale Netzwerkdienst verwendet DnsClient.NET 1.8.0, Copyright (c) 2024 Michael Conrad, unter Apache-2.0. [Upstream](https://github.com/MichaCo/DnsClient.NET), [Paket](https://www.nuget.org/packages/DnsClient/1.8.0); vollständiger Lizenztext: [Apache-2.0](LICENSE-APACHE-2.0.txt).

Der vollständige Apache-2.0-Lizenztext ist in LICENSE-APACHE-2.0.txt enthalten. Copyright (c) Microsoft Corporation für den übernommenen Playwright-Bestandteil.

Das Docker-seccomp-Profil basiert auf microsoft/playwright, utils/docker/seccomp_profile.json, Version v1.51.1, das seinerseits auf dem Docker/Moby-Standardprofil basiert. Ergänzt wurde eine explizite ENOSYS-Antwort für clone3, damit libc auf clone zurückfällt. Quelle: https://github.com/microsoft/playwright/blob/v1.51.1/utils/docker/seccomp_profile.json . Playwright und Docker/Moby stehen unter Apache-2.0.

Weitere Bibliotheken werden über NuGet, npm und PyPI bezogen. Ihre jeweiligen Lizenztexte und Copyright-Hinweise bleiben in den installierten Paketen erhalten. React, Vite, TypeScript, Npgsql, Redis, PostgreSQL, Caddy, Chromium, pdfplumber und die übrigen Laufzeitabhängigkeiten besitzen eigene Lizenzbedingungen. Dieses Repository ersetzt diese nicht.
