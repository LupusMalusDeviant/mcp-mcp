# ADR-0005: Hot-Swap-Modell — Upstreams als verwaltete Kindprozesse/Verbindungen mit Supervisor

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

FR-06 bis FR-10 fordern: Upstream-Server zur Laufzeit hinzufügen/entfernen/ändern, überwachter Lebenszyklus mit Auto-Restart, Fault Isolation. stdio-Server sind lokale Prozesse (npx/uvx/Binaries), HTTP-Server sind Remote-Verbindungen. Die Frage ist, *wer* diese Lebenszyklen besitzt und wie stark isoliert wird — von „Prozess im Gateway-Prozessbaum" bis „Container pro Server" (PRD OQ-1).

**Kernfrage:** Wie werden Upstream-Server-Lebenszyklen verwaltet, sodass Hot-Swap ohne Gateway-/Agenten-Neustart funktioniert und ein defekter Upstream nichts anderes mitreißt?

## Anforderungen

### Funktional

- Add/Remove/Enable/Disable/Reconfigure zur Laufzeit; `tools/list_changed` an Clients (FR-07).
- Health-Zustandsmaschine (Starting/Healthy/Degraded/Stopped/Failed) + Restart-Policy mit Backoff (FR-08).
- Config-Versionierung mit Rollback (FR-10).

### Nicht-Funktional

- Kein Upstream darf den Gateway blockieren (Timeouts pro Call, NFR-03).
- v1 muss ohne Container-Runtime-Abhängigkeit laufen (NFR-05: auch `dotnet run` bare).

## Betrachtete Optionen

### Option 0: Kindprozesse + In-Proc-Verbindungen, eigener Supervisor im Gateway

stdio-Upstreams laufen als Kindprozesse des Gateways, HTTP-Upstreams als verwaltete Client-Verbindungen. Eine Supervisor-Komponente (Hosted Service) besitzt pro Upstream eine Zustandsmaschine, Health-Checks (`ping`), Restart mit Exponential Backoff und kapselt alles hinter `IUpstreamConnection`.

**Positiv:**
- Keine externe Abhängigkeit — läuft überall, auch bare `dotnet run`.
- Volle Kontrolle über Lifecycle-Events → `list_changed` exakt dann, wenn sich der Katalog wirklich ändert.
- Prozess-Kill/Crash eines stdio-Servers ist sauber erkennbar (Exit-Code, Stream-EOF).

**Negativ:**
- Kindprozesse teilen Ressourcen (CPU/RAM/Dateisystem) und Schicksal des Gateway-Prozesses: Gateway-Restart reißt alle stdio-Server mit.
- Kein Security-Sandboxing: ein bösartiger stdio-Server hat die Rechte des Gateway-Users.
- Zombie-/Orphan-Prozess-Handling (besonders unter Windows vs. Linux) muss selbst gebaut werden.

### Option 1: Container pro Upstream-Server

Jeder stdio-Server läuft in einem eigenen Container (Docker-API), Gateway spricht ihn über stdio-Attach oder gebridgtes HTTP an.

**Positiv:**
- Echte Isolation (Ressourcenlimits, Dateisystem, Netz) — deutlich besseres Sicherheitsprofil.
- Upstream-Lebenszyklus unabhängig vom Gateway-Prozess.

**Negativ:**
- Harte Abhängigkeit von Docker/Podman-Socket — verletzt NFR-05 (bare-Betrieb) und erhöht Setup-Hürde massiv.
- Image-Verwaltung für beliebige npx/uvx-Server ist ein eigenes Teilprojekt.
- Latenz und Komplexität pro Call steigen.

### Option 2: Externer Prozess-Manager (systemd/PM2-ähnlich) + Gateway verbindet nur

Server werden außerhalb (systemd-Units, Compose-Services) betrieben; der Gateway verbindet sich nur zu HTTP-Endpoints.

**Positiv:**
- Gateway wird einfacher (nur Verbindungs-, kein Prozessmanagement).
- Betriebsbewährte Prozess-Supervision.

**Negativ:**
- „Server über die Web-UI hinzufügen" (FR-34, Abnahmekriterium 1) bricht: stdio-Server müssten außerhalb der UI provisioniert werden.
- stdio-only-Server (die Mehrheit des Ökosystems) bräuchten alle einen zusätzlichen stdio→HTTP-Adapter.

## Vorschlag des Autors

Option 0 für v1 — sie ist die einzige, die FR-34 (UI-only-Provisionierung) und NFR-05 gleichzeitig erfüllt. Das Isolations-Defizit wird (a) dokumentiert („nur vertrauenswürdige Server anschließen"), (b) durch Prozess-Hygiene begrenzt (Job Objects unter Windows / Prozessgruppen unter Linux, damit keine Orphans überleben) und (c) architektonisch offengehalten: `IUpstreamConnection`/`IUpstreamLauncher` sind so geschnitten, dass ein `ContainerLauncher` in v2 eine reine Zusatzimplementierung ist ([ADR-0001]-Folge, PRD Non-Goal „Sandboxing v1").

## Entscheidung

**Gewählte Option:** „Kindprozesse + In-Proc-Verbindungen mit eigenem Supervisor"

UI-Provisionierung und Bare-Betrieb sind Muss-Kriterien, die nur diese Option erfüllt. Das schwächere Isolationsprofil wird bewusst akzeptiert und als dokumentiertes Betriebsrisiko + v2-Erweiterungspunkt geführt.

## Konsequenzen

### Positiv

- Hot-Swap komplett in eigener Hand: Add→Connect→Discover→`list_changed` als eine Transaktion; Remove analog mit Drain (laufende Calls beenden/abbrechen mit Timeout).
- Einheitliche Zustandsmaschine für stdio **und** HTTP (HTTP: „Prozess" = Verbindung + Health-Ping).

### Negativ

- Security-Grenze ist der Gateway-User-Account; bösartige stdio-Server sind ein reales Risiko (dokumentationspflichtig).
- Windows/Linux-Unterschiede im Prozessmanagement erfordern plattformspezifischen Code + Tests (Job Objects vs. Prozessgruppen/SIGTERM-Kaskade).
- Gateway-Deployment (Neustart) unterbricht stdio-Upstreams kurz — akzeptiert, da Agenten-Sessions über Reconnect-fähiges Streamable HTTP wieder aufsetzen.

### Folge-Entscheidungen

- Drain-/Timeout-Semantik beim Entfernen unter Last (Design im Pflichtenheft, WP-Gateway-Kern).
- v2-ADR „Container-Isolation pro Upstream", sobald Bedarf real wird.

### Review

**Reality-Check geplant für:** 2026-09-15
