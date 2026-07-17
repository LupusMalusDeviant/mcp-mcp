# syntax=docker/dockerfile:1

# ── Build ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore zuerst (Layer-Cache): nur die Projekt-/Props-Dateien kopieren.
COPY Directory.Build.props Directory.Packages.props nuget.config MCPMCP.slnx ./
COPY src/McpMcp.Abstractions/McpMcp.Abstractions.csproj src/McpMcp.Abstractions/
COPY src/McpMcp.Core/McpMcp.Core.csproj src/McpMcp.Core/
COPY src/McpMcp.Upstream/McpMcp.Upstream.csproj src/McpMcp.Upstream/
COPY src/McpMcp.Persistence/McpMcp.Persistence.csproj src/McpMcp.Persistence/
COPY src/McpMcp.Web/McpMcp.Web.csproj src/McpMcp.Web/
COPY src/McpMcp.Server/McpMcp.Server.csproj src/McpMcp.Server/
RUN dotnet restore src/McpMcp.Server/McpMcp.Server.csproj

COPY src/ src/
RUN dotnet publish src/McpMcp.Server/McpMcp.Server.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# Datenverzeichnis-Vorlage für die Runtime. Die .keep-Datei ist nötig, damit COPY das Zielverzeichnis
# tatsächlich anlegt — ein leeres Quellverzeichnis würde von COPY übersprungen (Docker-Gotcha), dann
# erstellte VOLUME das Verzeichnis wieder als root. chiseled hat keine Shell, daher kein RUN chown zur Laufzeit.
RUN mkdir -p /data-template && touch /data-template/.keep

# ── Runtime (chiseled, non-root, ~110 MB Basis) ──────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app ./

# /data gehört dem non-root-User (UID 64198 = "app"): ein darauf gemountetes leeres Volume
# erbt Owner/Rechte des Image-Verzeichnisses, sodass SQLite-DB + DataProtection-Keys schreibbar sind.
COPY --from=build --chown=64198:64198 /data-template /data
ENV MCPMCP_DATA_DIR=/data \
    ASPNETCORE_URLS=http://+:8080
VOLUME /data
EXPOSE 8080

ENTRYPOINT ["dotnet", "McpMcp.Server.dll"]
