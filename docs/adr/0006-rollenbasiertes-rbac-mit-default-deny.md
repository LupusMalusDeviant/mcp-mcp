# ADR-0006: Rollenbasiertes RBAC mit Default-Deny und Sichtbarkeit-folgt-Berechtigung

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

FR-27 bis FR-32 fordern steuerbare Zugriffe pro Agent: welcher Agent darf welchen Server / welches Tool / welche Aktion. Zusätzlich existieren menschliche UI-Rollen (Admin/Operator/Auditor, FR-30). Zu entscheiden ist die Autorisierungs-Architektur: hausgemachtes Rollenmodell, feingranulare Policy-Engine oder simple Key-Scopes.

**Kernfrage:** Mit welchem Autorisierungsmodell werden Agenten- und UI-Zugriffe geprüft?

## Anforderungen

### Funktional

- Wirkungsebenen: Server (alles darunter), Tool (einzeln), Aktionstyp (tools/resources/prompts) — mit Allow-Vererbung Server→Tool (FR-28).
- Default-Deny; Sichtbarkeit = Berechtigung: nicht Erlaubtes erscheint nirgends (FR-29).
- Rate-Limits/Key-Gültigkeit pro Rolle (FR-31, Should).
- Jede Deny-Entscheidung wird geloggt (FR-22).

### Nicht-Funktional

- Autorisierungs-Entscheidung im Hot Path: Ziel < 1 ms (In-Memory), da sie in *jedem* Call und in jeder Katalog-Filterung steckt (NFR-01).
- Verwaltbar über Web-UI durch eine Einzelperson — Modell muss im Kopf behaltbar sein.

## Betrachtete Optionen

### Option 0: Eigenes, schlankes RBAC (Identität → Rollen → Grants)

Datenmodell: `Identity` (Agent oder UI-User) —n:m→ `Role` —1:n→ `Grant` (Scope: Server|Tool, Aktionen: Use|Read|Prompt, Effekt: Allow). Deny-Grants gibt es nicht — was nicht allowed ist, ist verboten (Default-Deny macht explizite Denies unnötig). Entscheidung als reine In-Memory-Auswertung über einen kompilierten Berechtigungs-Snapshot pro Identität.

**Positiv:**
- Exakt auf die Anforderung zugeschnitten; in Minuten erklärbar; UI-Formulare trivial.
- Snapshot-Auswertung ist O(1)-Lookup — Hot-Path-Ziel sicher erreichbar.
- Keine externe Abhängigkeit; Testbarkeit 100 % (reine Funktionen).

**Negativ:**
- Kein Ausdrucksreichtum für Sonderfälle (attributbasierte Regeln, „erlaubt wenn Argument X < 100") — müsste später angebaut werden.
- Eigenverantwortung für Korrektheit (kein battle-tested Engine-Code).

### Option 1: Policy-Engine (Casbin.NET / OPA)

Autorisierung an eine generische Engine mit Policy-Sprache delegieren.

**Positiv:**
- Beliebig ausdrucksstark (ABAC, Bedingungen), erprobte Semantik.
- Policies als Text versionierbar.

**Negativ:**
- Policy-Sprache in einer Single-Operator-Web-UI zu verwalten ist eine UX-Hypothek — genau die Zielgruppe schreibt keine Rego/Casbin-Modelle.
- OPA = externer Prozess (verletzt Deployment-Einfachheit); Casbin.NET in-proc, aber Modell-Debugging deutlich schwerer als eigene Grants.
- Overkill für drei Wirkungsebenen und einen Effekt.

### Option 2: Scoped API-Keys (Berechtigungen direkt am Key)

Keine Rollen; jeder Key trägt seine Tool-/Server-Liste direkt.

**Positiv:**
- Simpelstes Modell, ein Join weniger.

**Negativ:**
- Keine Wiederverwendung: „Read-Only-Recherche" für 5 Agenten heißt 5-fach pflegen; Änderungen skalieren nicht.
- Key-Rotation verliert Berechtigungs-Historie; Auditor-Frage „welche Rolle hatte der Agent" unbeantwortbar.

## Vorschlag des Autors

Option 0. Die Sonderfall-Schwäche ist der einzige echte Nachteil, und sie ist über die vorhandene Interface-Grenze heilbar: die Entscheidung läuft über `IAuthorizationService.Evaluate(identity, scope, action)` — sollte später ABAC nötig sein, wird die Implementierung getauscht, nicht das Modell der Aufrufer. Rate-Limits (FR-31) hängen als Rollen-Attribute am selben Snapshot.

## Entscheidung

**Gewählte Option:** „Eigenes, schlankes RBAC"

Verwaltbarkeit durch eine Einzelperson und Hot-Path-Performance geben den Ausschlag; der Verzicht auf ABAC-Ausdruckskraft ist für die dokumentierten Anforderungen kein Verlust und bleibt hinter dem Interface nachrüstbar.

## Konsequenzen

### Positiv

- RBAC-Entscheidungslogik ist eine reine, vollständig unit-testbare Funktion (100 %-Branch-Ziel aus NFR-08 realistisch).
- Ein Berechtigungs-Snapshot speist konsistent alle drei Pfade: `tools/list`, Meta-Tools, REST-Bridge (verhindert die in [ADR-0003](./0003-hybride-token-strategie-lazy-discovery-plus-profile.md) benannte Divergenzgefahr).

### Negativ

- Bedingungsbasierte Regeln (z. B. argumentabhängig) sind v1 nicht möglich.
- Eigene Verantwortung für Korrektheit → RBAC bekommt die strengste Testpflicht im Projekt (Property-Tests + 20er-Abnahmematrix aus PRD Kriterium 4).

### Folge-Entscheidungen

- Approval-Flows (FR-32, Could) bauen später auf demselben Evaluate-Punkt auf (Ergebnis „RequiresApproval" als dritter Effekt).

### Review

**Reality-Check geplant für:** 2026-09-15
