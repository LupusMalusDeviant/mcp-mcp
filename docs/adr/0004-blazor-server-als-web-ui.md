# ADR-0004: Blazor (Interactive Server) als Web-UI

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

Keyfeature 6 verlangt eine Web-UI für Server-Verwaltung, RBAC, Tool-Explorer, Logs und Dashboard (FR-33 bis FR-38). Der Stack ist per [ADR-0002](./0002-dotnet-mit-offiziellem-csharp-mcp-sdk.md) .NET; das Dashboard braucht Live-Daten (Server-Health, laufende Calls). Betreiber ist eine Einzelperson bzw. kleines Team — kein Bedarf an einem separat skalierendem Frontend.

**Kernfrage:** Mit welchem UI-Framework wird die Verwaltungsoberfläche gebaut?

## Anforderungen

### Funktional

- Formulare (Server-Config, Rollen), Tabellen mit Filterung (Logs), Live-Status-Updates (Health, Sessions).
- Test-Aufrufe von Tools aus der UI.

### Nicht-Funktional

- Ein Deployment-Artefakt (NFR-05); geringer Pflegeaufwand; Auth-Integration mit dem Gateway-Backend ohne CORS/Token-Gymnastik.

## Betrachtete Optionen

### Option 0: Blazor Interactive Server (im Gateway-Host)

Blazor-Web-App im selben ASP.NET-Core-Prozess; UI-Logik ruft die Application-Services direkt (in-process), Live-Updates über den bestehenden SignalR-Circuit.

**Positiv:**
- Ein Stack, ein Build, ein Container; C# durchgängig — maximaler Team-Fit.
- Live-Updates (Health/Sessions) sind mit Blazor Server nativ trivial.
- Kein CORS, kein separates Auth-Token-Handling; Cookie-Auth gegen denselben Host.

**Negativ:**
- UI hält Websocket-Verbindung + Server-State pro Browser-Session (für Single-Operator irrelevant, für „viele UI-User" ungünstig).
- Weniger frei bei ausgefallenen UI-Bibliotheken als das React-Ökosystem.
- UI-Code lebt im selben Prozess wie der Gateway — Bugs in der UI können theoretisch den Dienst beeinträchtigen (durch Circuit-Isolation gemildert).

### Option 1: React/Vue-SPA gegen die REST-API

Separates Frontend, das ausschließlich die ohnehin existierende Management-API (FR-17 ff. + Admin-Endpoints) konsumiert.

**Positiv:**
- Erzwingt saubere, vollständige Management-API (API-first-Disziplin).
- Reichhaltigstes Komponenten-Ökosystem; UI unabhängig deploybar.

**Negativ:**
- Zweiter Stack (Node-Toolchain, npm-Pflege) für einen .NET-fokussierten Owner — dauerhafte Wartungslast.
- CORS-, Token- und Build-Pipeline-Aufwand ohne funktionalen Mehrwert im Single-Operator-Szenario.

### Option 2: Kein UI in v1 (API + CLI)

Verwaltung zunächst nur über REST-API/CLI; UI später.

**Positiv:**
- Schnellster Weg zum funktionierenden Gateway-Kern.

**Negativ:**
- Verfehlt Keyfeature 6 und Abnahmekriterium 1 des PRD („ohne Config-Datei anfassbar") direkt.

## Vorschlag des Autors

Option 0, mit einer Leitplanke aus Option 1: Die Blazor-UI ruft **nicht** wahllos Interna, sondern ausschließlich dieselben Application-Service-Interfaces, die auch die Management-REST-API bedienen. Damit bleibt der Pfad zu einer späteren SPA offen, ohne heute deren Kosten zu zahlen.

## Entscheidung

**Gewählte Option:** „Blazor Interactive Server"

Team-Fit, ein Artefakt und native Live-Updates überwiegen; die Skalierungs-Schwäche von Blazor Server ist im Self-hosted-Kontext bedeutungslos. Die Application-Service-Leitplanke hält die Architektur reversibel.

## Konsequenzen

### Positiv

- UI-Features kosten wenig (kein API-Roundtrip-Design pro Formular nötig).
- Dashboard-Live-Daten ohne zusätzliche Infrastruktur.

### Negativ

- UI ist an Gateway-Releases gekoppelt (kein unabhängiges UI-Deployment).
- Blazor-Server-Latenz macht die UI im Remote-Zugriff über schlechte Leitungen zäher als eine SPA.

### Folge-Entscheidungen

- UI-Komponentenbibliothek (Bewertung: schlankes eigenes CSS vs. MudBlazor/FluentUI) — Entscheidung im UI-Arbeitspaket, kein eigenes ADR nötig.

### Review

**Reality-Check geplant für:** 2026-09-15
