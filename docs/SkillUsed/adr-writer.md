# Skill-Log: adr-writer

## 2026-07-24 — Run

**Aufgabe:** ADR für CLI-Programme als vierten Upstream-Transport (UpstreamTransportKind.Cli) schreiben.

**Entscheidungen:**
- Kontext/Optionen/Entscheidung vollständig im Prompt vorgegeben → keine Rückfragen nötig; Status „Akzeptiert" (Bau ist beauftragt).
- Supersede: nein — additiv, ergänzt die Connector-Familie (ADR-0005/0008), ersetzt nichts.
- Index docs/adr/README.md um Zeile 0014 erweitert.

**Artefakte:**
- docs/adr/0014-cli-programme-als-upstream-transport.md
- docs/adr/README.md (Index-Zeile 0014)

**Status:** abgeschlossen

---

## 2026-07-17 — Run

**Aufgabe:** Initialer ADR-Satz (8 Entscheidungen) für MCP-MCP aus PRD 0001 ableiten.

**Entscheidungen:**
- Alle 8 Entscheidungen vom Product Owner vorab bestätigt (AskUserQuestion-Runde vor PRD) → Status direkt „Akzeptiert".
- Supersede: nicht relevant (erste ADRs im Repo).
- Index docs/adr/README.md neu angelegt (existierte nicht; Anlage im Rahmen des beauftragten Gesamt-Doku-Pakets).

**Artefakte:**
- docs/adr/0001-zentraler-proxy-gateway-statt-direktanbindung.md
- docs/adr/0002-dotnet-mit-offiziellem-csharp-mcp-sdk.md
- docs/adr/0003-hybride-token-strategie-lazy-discovery-plus-profile.md
- docs/adr/0004-blazor-server-als-web-ui.md
- docs/adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md
- docs/adr/0006-rollenbasiertes-rbac-mit-default-deny.md
- docs/adr/0007-ef-core-mit-sqlite-default-postgres-optional.md
- docs/adr/0008-api-mcp-bridge-als-erstklassige-fassaden.md
- docs/adr/README.md (Index, neu)

**Status:** abgeschlossen

---
