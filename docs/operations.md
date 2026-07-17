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

Das Schema wird beim Start automatisch angelegt (v1: `EnsureCreated`; Migrations-Baseline ab v1.1).

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

## Health / Readiness

- `GET /healthz` — Prozess lebt (anonym).
- `GET /readyz` — DB erreichbar + Upstream-Zustände (anonym).

Der Container-Healthcheck nutzt `dotnet McpMcp.Server.dll --healthcheck` (self-ping, da das chiseled-Image kein `curl` hat).

## Zugang zurücksetzen

Bootstrap-Zugänge werden nur bei **leerer** DB erzeugt. Verlorene Zugänge:

- **UI-Passwort vergessen, aber anderer Admin existiert** → über die UI (Seite „UI-Nutzer") neu setzen.
- **Kein Zugang mehr** → betrieblicher Reset nötig: Datenverzeichnis sichern, DB-Zeilen `UiUsers` (für UI) bzw. `ApiKeys` (für Agenten) gezielt entfernen und den Dienst neu starten — der Bootstrap greift wieder, sobald die jeweilige Tabelle leer ist. (Ein CLI-Reset-Kommando ist v1.1-Kandidat.)

## Sicherheit

Vor dem Produktivbetrieb unbedingt [SECURITY.md](../SECURITY.md) und das [Threat-Model](security/threat-model.md) lesen — insbesondere: **nur vertrauenswürdige stdio-Server anschließen** (v1 ohne Sandbox, ADR-0005), Gateway als non-root betreiben (das Container-Image tut das bereits), Netzexposition minimieren.
