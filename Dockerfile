# syntax=docker/dockerfile:1

# ── Build ────────────────────────────────────────────────────────────────────
# Distro explizit gepinnt statt nur ":10.0": Der Default-Alias zeigt heute auf Ubuntu 24.04
# (noble), wandert aber bei einem künftigen Distro-Wechsel weiter — das soll nicht unbemerkt
# unter einem laufenden Build passieren.
#
# Bewusst KEINE -chiseled-Variante: Das Laufzeit-Image richtet /data per chown/chmod ein und
# braucht dafür eine Shell (siehe unten). Chiseled ist distroless und hat keine.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore zuerst (Layer-Cache): nur die Projekt-/Props-Dateien kopieren.
COPY Directory.Build.props Directory.Packages.props nuget.config MCPMCP.slnx ./
COPY src/McpMcp.Abstractions/McpMcp.Abstractions.csproj src/McpMcp.Abstractions/
COPY src/McpMcp.Core/McpMcp.Core.csproj src/McpMcp.Core/
COPY src/McpMcp.Upstream/McpMcp.Upstream.csproj src/McpMcp.Upstream/
COPY src/McpMcp.Persistence/McpMcp.Persistence.csproj src/McpMcp.Persistence/
COPY src/McpMcp.Persistence.Migrations.Sqlite/McpMcp.Persistence.Migrations.Sqlite.csproj src/McpMcp.Persistence.Migrations.Sqlite/
COPY src/McpMcp.Persistence.Migrations.Postgres/McpMcp.Persistence.Migrations.Postgres.csproj src/McpMcp.Persistence.Migrations.Postgres/
COPY src/McpMcp.Web/McpMcp.Web.csproj src/McpMcp.Web/
COPY src/McpMcp.Server/McpMcp.Server.csproj src/McpMcp.Server/
RUN dotnet restore src/McpMcp.Server/McpMcp.Server.csproj

COPY src/ src/
RUN dotnet publish src/McpMcp.Server/McpMcp.Server.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# ── Runtime (Ubuntu-noble-Basis mit Shell, non-root; ~230 MB < 300 MB) ───────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
COPY --from=build /app ./

# Datenverzeichnis (SQLite-DB + DataProtection-Keys) beschreibbar anlegen und dem non-root
# app-User geben. Chiseled-Images haben keine Shell für RUN chmod — die Ubuntu-Basis schon,
# was diese Verzeichnisrechte zuverlässig macht.
RUN mkdir -p /data && chown app:app /data && chmod 0770 /data

ENV MCPMCP_DATA_DIR=/data \
    ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
VOLUME /data

ENTRYPOINT ["dotnet", "McpMcp.Server.dll"]
