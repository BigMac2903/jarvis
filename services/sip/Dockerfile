FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props .
COPY src/Jarvis.Domain/ src/Jarvis.Domain/
COPY services/sip/ services/sip/
RUN dotnet publish services/sip/Jarvis.Sip.csproj -c Release -o /out --nologo
FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
USER 1654:1654
ENV ASPNETCORE_HTTP_PORTS=8080
HEALTHCHECK --interval=15s --timeout=5s CMD curl -fsS http://127.0.0.1:8080/health/live
ENTRYPOINT ["dotnet", "Jarvis.Sip.dll"]
