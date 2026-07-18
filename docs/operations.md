# Betrieb — MCP-MCP Gateway

Praxisleitfaden zum Deployment und Betrieb. Zielgruppe: Self-hosted Single-Operator (ADR-0001).

## Schnellstart (Docker)

```bash
docker compose up -d          # SQLite-Default, ein Volume
docker compose logs mcpmcp    # Bootstrap-Zugangsdaten NUR beim Erststart ablesen
```

Beim **Erststart** legt der Gateway zwei Zugänge an und loggt sie **genau einmal** (Henne-Ei — danach nie wieder):

```
ERSTSTART: Bootstrap-Admin (Agent) angelegt. API-Key (wird NIE wieder angezeigt): mcpk_...
ERSTSTART: UI-Admin 'admin' angelegt. Passwort (wird NIE wieder angezeigt): ...
```

- **API-Key** → für Agenten (Claude Code, MCP Inspector) und die REST-Fassade.
- **UI-Passwort** → Login der Web-UI (`http://localhost:8080`, Benutzer `admin`).

Beide Werte sofort sichern. Verloren? Siehe [Zugang zurücksetzen](#zugang-zurücksetzen).

## Konfiguration (Env-Vars)

| Variable | Default | Zweck |
|---|---|---|
| `MCPMCP_DATA_DIR` | `data` (bzw. `/data` im Container) | Verzeichnis für SQLite-DB **und** DataProtection-Key-Ring |
| `MCPMCP_DB_PROVIDER` | `sqlite` | `sqlite` oder `postgres` |
| `MCPMCP_DB_CONNECTION` | `Data Source=<datadir>/mcpmcp.db` | Connection-String (bei Postgres Pflicht) |
| `ASPNETCORE_URLS` | `http://+:8080` (Container) | Bind-Adresse/Port |
| `MCPMCP_KEYRING_CERT_PATH` | *(nicht gesetzt)* | PFX-Zertifikat zum Verschlüsseln des Key-Rings (siehe [Key-Ring schützen](#key-ring-schützen)) |
| `MCPMCP_KEYRING_CERT_PASSWORD` | *(nicht gesetzt)* | Passwort des PFX |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(nicht gesetzt)* | Ziel für den Metriken-Export (siehe [Metriken](#metriken)) |
| `MCPMCP_AUDIT_DEBUG_PAYLOADS` | *(aus)* | `1`/`true` schaltet den Debug-Modus des Audits ein (siehe [Audit-Debug-Modus](#audit-debug-modus)) |

## Audit-Debug-Modus

Standardmäßig schreibt das Audit **keine** Ergebnis-Payloads mit — nur deren Größe in Bytes.
Zur Fehlersuche lässt sich das umschalten:

```
MCPMCP_AUDIT_DEBUG_PAYLOADS=1
```

Dann landet der vollständige Antwort-Payload im Audit-Log, **maskiert** durch dieselbe Redaction
wie die Argumente. Zwei Dinge dazu:

- Der Schalter ist als Debug-Hilfe gedacht, nicht für Dauerbetrieb: Antworten können groß sein und
  die Audit-Tabelle schnell aufblähen. Die Retention greift zwar, aber der Plattenbedarf steigt spürbar.
- Redaction maskiert bekannte Secret-Feldnamen. Trägt ein Upstream Geheimnisse in *unbenannten*
  Strukturen (Freitext, Base64-Blobs), hilft das nicht — in dem Fall den Schalter aus lassen.

Zusätzliche Muster pro Tool lassen sich in der Web-UI unter **Tools → \[Tool wählen\]** als
Admin pflegen; sie gelten zusätzlich zu den globalen Mustern (`password`, `token`, `secret`, `key` …).

## Agent anbinden

```bash
claude mcp add --transport http mcpmcp http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

Der Agent sieht dann die Meta-Tools `search_tools` / `describe_tool` / `invoke_tool` (Lazy-Default) plus die im Profil gepinnten Tools. Upstream-Server, Rollen und Profile werden über die Web-UI oder die REST-API verwaltet.

## PostgreSQL statt SQLite

Für größere Setups (viel Audit-Volumen, mehrere Instanzen an einer DB):

```bash
docker compose --profile postgres up -d
# in docker-compose.yml MCPMCP_DB_PROVIDER + MCPMCP_DB_CONNECTION einkommentieren,
# und das Passwort (CHANGE_ME) ersetzen.
```

Das Schema wird beim Start automatisch über EF-Migrationen angelegt (siehe [Schema & Upgrades](#schema--upgrades)).

## Schema & Upgrades

Ab **v1.1** verwaltet der Gateway sein Datenbankschema über EF-Core-Migrationen. Beim Start passiert automatisch genau eine von drei Sachen — das Ergebnis steht im Log (`Datenbank initialisiert (…)`):

| Vorgefunden | Aktion | Log-Ausgabe |
|---|---|---|
| Leere/neue DB | Schema aus Migrationen anlegen | `CreatedFromMigrations` |
| **v1.0-DB** (per `EnsureCreated` erzeugt, ohne Migrationshistorie) | Initial-Migration als Baseline stempeln (**kein DDL, keine Datenänderung**), dann migrieren | `BaselinedLegacySchema` |
| Bereits migrationsverwaltet | ausstehende Migrationen anwenden | `Migrated` |

### Upgrade von v1.0 auf v1.1

Es ist **kein manueller Schritt nötig** — der Gateway erkennt das Alt-Schema selbst und stempelt die Baseline. Trotzdem gilt die übliche Sorgfalt:

1. Dienst stoppen.
2. **Datenverzeichnis sichern** (`mcpmcp.db` **und** `keys/`, siehe [Backup](#backup)).
3. Neue Version starten und im Log `BaselinedLegacySchema` bestätigen.

Beim Rollback auf v1.0 ist die zusätzliche Tabelle `__EFMigrationsHistory` unschädlich — v1.0 ignoriert sie.

Jeder Provider hat eine eigene Migrations-Assembly (`McpMcp.Persistence.Migrations.Sqlite` bzw. `.Postgres`), weil SQLite und PostgreSQL unterschiedliches DDL brauchen. Beide sind im Image enthalten; die Auswahl erfolgt automatisch über `MCPMCP_DB_PROVIDER`.

## TLS / Reverse-Proxy

Der Gateway terminiert selbst kein TLS. **Immer hinter einen Reverse-Proxy** (Caddy, nginx, Traefik) mit TLS setzen — der Gateway hält Upstream-Credentials und API-Keys, ein Klartext-Transport ist inakzeptabel (NFR-04). Beispiel Caddy:

```
gateway.example.com {
    reverse_proxy localhost:8080
}
```

Der Proxy sollte `X-Forwarded-*`-Header setzen; das UI-Cookie ist `SameSite=Strict` + `HttpOnly`.

## Backup

Alles Persistente liegt im Datenverzeichnis (`MCPMCP_DATA_DIR`):

- `mcpmcp.db` — Konfiguration, RBAC, API-Key-Hashes, Audit-Log (bei SQLite).
- `keys/` — **DataProtection-Key-Ring**. Ohne ihn sind die verschlüsselten Upstream-Credentials unbrauchbar.

Beide **zusammen** sichern (Volume-Snapshot bei gestopptem Container oder DB-Dump + `keys/`-Kopie). Bei PostgreSQL: DB separat dumpen, `keys/` weiterhin aus dem Datenvolume sichern.

## Audit-Retention

Das Audit-Log wächst mit jedem Call. Default-Aufbewahrung: 30 Tage, stündlicher Bereinigungs-Job (FR-25). Bei SQLite ist Retention **Betriebspflicht** (ADR-0007) — sehr große Logs (> ~10 GB) sind ein Grund, auf PostgreSQL zu wechseln.

## Metriken

Der Gateway misst jeden Tool-Call (FR-26) unter dem Meter `McpMcp.Gateway`:

| Instrument | Bedeutung | Dimensionen |
|---|---|---|
| `mcpmcp.tool_calls` | Zähler aller Calls — daraus ergeben sich Calls/s und Fehlerquote | `server`, `tool`, `status`, `origin` |
| `mcpmcp.tool_call_duration` | Latenz-Histogramm (ms) — daraus Perzentile | `server`, `tool`, `status` |

Der Export ist **aus**, solange kein Ziel konfiguriert ist (sonst würde der Exporter dauerhaft ins Leere laufen):

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4317
```

Exportiert wird per **OTLP** — der OpenTelemetry-Standard. Für **Prometheus** einen OTel-Collector davorschalten, der OTLP annimmt und einen Scrape-Endpoint anbietet; ein direkter Prometheus-Exporter ist im .NET-Ökosystem noch nicht stabil veröffentlicht, deshalb bewusst dieser Weg.

## Health / Readiness

- `GET /healthz` — Prozess lebt (anonym).
- `GET /readyz` — DB erreichbar + Upstream-Zustände (anonym).

Der Container-Healthcheck nutzt `dotnet McpMcp.Server.dll --healthcheck` (self-ping, da das schlanke Runtime-Image kein `curl` enthält). Der Container läuft als non-root `app`-User.

## Key-Ring schützen

Der DataProtection-Key-Ring unter `<datadir>/keys/` entschlüsselt die at-rest verschlüsselten Upstream-Credentials. Ohne Zusatzschutz liegt er im Klartext neben der Datenbank — der Gateway warnt beim Start entsprechend.

Ab v1.1 lässt er sich mit einem X509-Zertifikat verschlüsseln (bewusst zertifikatsbasiert statt Cloud-KMS, damit es self-hosted funktioniert):

```bash
# Zertifikat einmalig erzeugen (Beispiel, OpenSSL):
openssl req -x509 -newkey rsa:2048 -keyout k.pem -out c.pem -days 3650 -nodes -subj "/CN=mcpmcp-keyring"
openssl pkcs12 -export -out keyring.pfx -inkey k.pem -in c.pem -password pass:GEHEIM

# Gateway damit starten:
MCPMCP_KEYRING_CERT_PATH=/secrets/keyring.pfx
MCPMCP_KEYRING_CERT_PASSWORD=GEHEIM
```

Danach enthalten die XML-Dateien im Key-Ring nur noch verschlüsseltes Material. **Das Zertifikat wird zum Entschlüsseln gebraucht** — geht es verloren, sind die gespeicherten Upstream-Credentials unbrauchbar (die Server müssen dann neu konfiguriert werden). Zertifikat also getrennt vom Datenverzeichnis sichern. Beim Zertifikatswechsel bleibt Altmaterial lesbar, solange das alte Zertifikat weiterhin angegeben wird.

## Zugang zurücksetzen

Bootstrap-Zugänge werden nur bei **leerer** DB erzeugt. Für verlorene Zugänge gibt es ab v1.1 zwei Kommandos, die gegen die konfigurierte Datenbank laufen, den Zugang **einmalig** ausgeben und sich beenden, ohne den Gateway zu starten:

```bash
# UI-Passwort zurücksetzen (Default-Benutzer "admin"; Rolle bleibt unverändert,
# ein fehlender Nutzer wird als Admin angelegt):
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --reset-ui-admin
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --reset-ui-admin betreiber

# Notfall-API-Key: legt eine NEUE Agenten-Identität mit Global-Grant an
# (bestehende bleiben unangetastet):
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --issue-bootstrap-key
```

Ohne Container analog mit `dotnet run --project src/McpMcp.Server -- --reset-ui-admin`. Den Notfall-Zugang nach Gebrauch wieder entfernen, falls er nur der Wiederherstellung diente.

- **UI-Passwort vergessen, aber anderer Admin existiert** → einfacher über die UI (Seite „UI-Nutzer") neu setzen.

## Sicherheit

Vor dem Produktivbetrieb unbedingt [SECURITY.md](../SECURITY.md) und das [Threat-Model](security/threat-model.md) lesen — insbesondere: **nur vertrauenswürdige stdio-Server anschließen** (v1 ohne Sandbox, ADR-0005), Gateway als non-root betreiben (das Container-Image tut das bereits), Netzexposition minimieren.
