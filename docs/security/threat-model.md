# Threat-Model & Security-Posture — MCP-MCP v1.0

Stand: 2026-07-24. Ergänzt [SECURITY.md](../../SECURITY.md).

## Vertrauensgrenzen

```
[ Agent / REST-/CLI-Client ] --API-Key--> [ GATEWAY ] --stdio/HTTP/OpenAPI/CLI--> [ Upstream ]
[ Mensch ]              --Cookie---> [  (hält alle Credentials)  ]
```

Der Gateway ist der zentrale Vertrauensanker (ADR-0001): Er terminiert jeden Call, erzwingt RBAC/Rate-Limits und hält sämtliche Upstream-Credentials verschlüsselt. Kompromittierung des Gateway-Hosts = Kompromittierung aller angeschlossenen Systeme. Entsprechend härten (non-root — das Container-Image tut das —, TLS-Proxy, minimale Netzexposition).

## Bestätigt sauber (Audit)

- **AuthN/AuthZ:** `/mcp` und `/api` beide hinter API-Key-Middleware (401 ohne gültigen Key); alle Management-Endpoints hinter Global-Grant-Schranke; kein ungeschützter Management-Pfad.
- **SQL-Injection:** keine — ausschließlich parametrisierte EF-LINQ, kein Raw-SQL.
- **XSS:** Blazor-Auto-Encoding; die zwei `MarkupString`-Stellen sind statische Literale; fremde Tool-Beschreibungen/Audit-Inhalte werden encodiert.
- **Secret-Leakage:** `RedactionService` maskiert Secret-Muster in Audit-Argumenten;
  `UpstreamConfigRedactor` maskiert stdio-/CLI-Environment, HTTP-Header und OpenAPI-Credentials in
  Admin-Antworten; Connection-Test- und CLI-Prozessausgaben entfernen bekannte
  Konfigurationssecrets.
- **Crypto:** PBKDF2-SHA256, 100 000 Iterationen, 16-Byte-Salt (CSPRNG), `FixedTimeEquals`.
- **OpenAPI-Parser-DoS:** `$ref`-Tiefe auf 32 gecappt, Zyklen/externe Refs abgelehnt.

## In v1.0 gehärtet (Audit-Findings behoben)

| # | Finding | Fix |
|---|---|---|
| 1 | UI-Cookie ohne `Secure`-Flag | `SecurePolicy = Always` außerhalb Development |
| 3 | OpenAPI-Spec ohne Größenlimit (Memory-DoS) + SSRF/File-Read | 10-MB-Cap beim Laden (Datei + HTTP-Stream), 30-s-Timeout beim Spec-Fetch |
| 4 | Username-Enumeration per Timing | Dummy-PBKDF2-Verify im „User nicht gefunden"-Pfad |
| 5 | Header-Parameter-Injection (CR/LF) im OpenAPI-Connector | CR/LF-Werte werden abgelehnt |
| 6 | `/readyz` gab Upstream-Topologie anonym preis | nur noch aggregierte Zahlen |

## Nach v1.0 gefunden und behoben

| # | Finding | Fix |
|---|---|---|
| 7 | **Klartext-Secrets im Audit-Log über den Meta-Tool-Pfad.** `MetaToolService` schrieb die Argumente ungefiltert; bei `invoke_tool` enthält `args.arguments` die kompletten Ziel-Argumente. Ein Call über den Lazy-Pfad persistierte damit Passwörter/Tokens im Klartext, während derselbe Call über `tools/call` korrekt maskiert wurde. Gefunden bei einem unabhängigen Abgleich aller Muss-FRs gegen den Code, nicht durch den ursprünglichen Security-Audit. | Der Meta-Pfad läuft durch denselben `IRedactionService`; Regressionstest hält die Invariante. Betroffen sind Bestands-Logs aus v1.0/v1.1 — wer den Lazy-Pfad genutzt hat, sollte die Audit-Tabelle prüfen und ggf. betroffene Zeilen löschen sowie die dort sichtbar gewordenen Credentials rotieren. |

## Akzeptierte / dokumentierte Restrisiken

- **stdio-Upstreams ohne Sandbox** (ADR-0005): Admin-kontrollierter Command/Args/Env läuft ungesandboxt als Kindprozess mit Gateway-Rechten. Trust-Boundary: **nur vertrauenswürdige Server anschließen**; nur Admins dürfen Upstreams anlegen. Container-Isolation pro Upstream ist v2-Kandidat.
- **CLI-Hostmodus ohne Sandbox** (ADR-0014/0018): Absolute kanonische Pfade, Roots, optionaler
  SHA-256-Pin, isoliertes Environment, typisierte Parameter, Byte-/Zeit-/Parallelitätslimits und
  Prozessbaum-Kill reduzieren die Angriffsfläche, bilden aber keine Kernel-Sandbox. Untrusted native
  Programme benötigen den geplanten Containerpfad; neue Plugins sollen WASI Components verwenden.
- **DataProtection-Key-Ring standardmäßig im Klartext auf der Platte** (`<datadir>/keys/`): entschlüsselt die at-rest verschlüsselten Upstream-Credentials. ✅ **v1.1 entschärft:** per `MCPMCP_KEYRING_CERT_PATH` lässt sich der Key-Ring mit einem X509-Zertifikat verschlüsseln (siehe [operations.md](../operations.md#key-ring-schützen)); ohne Konfiguration warnt der Gateway beim Start. Bleibt es beim Default, gilt weiterhin: **Datenvolume-Zugriff restriktiv** halten und wie ein Secret behandeln.
- **Bootstrap-Key/UI-Passwort einmalig im Klartext geloggt**: bewusste Henne-Ei-Ausnahme (nur bei leerer DB, einmalig, LogLevel Warning). Ohne sie wäre eine frische Instanz unbenutzbar.
- **Login-Endpoint ohne Antiforgery-Token**: vor der Anmeldung existiert kein gültiges Token; Login-CSRF-Restrisiko durch `SameSite=Strict` mitigiert.
- **UI-Tool-Test unter Global-Grant-Identität**: UI-Operatoren können jedes Tool testen, unabhängig vom per-Key-RBAC — gerahmt durch die UI-Rollen (nur Operator/Admin). So gewollt („Test-Aufruf mit Admin-Rechten").
- **Existenz-Leak bei `tools/call`-Deny**: ein verbotenes Tool liefert `Denied` (statt `ToolNotFound`), bestätigt also seine Existenz. `describe_tool` leakt bewusst nicht (verhält sich wie „nicht gefunden"). Minor Info-Disclosure.
- **Federations-Loop-Erkennung deckt nur den direkten Selbstbezug** (FR-05): Der Header
  `X-McpMcp-Instance` wird beim Aufbau der Upstream-Verbindung *einmal* gesetzt und kennt den
  auslösenden Request nicht — eine Instanz-Kette lässt sich damit nicht weiterreichen. Erkannt wird
  daher A→A, **nicht** A→B→A. Die Fehlermeldung behauptete zwischenzeitlich „direkt oder transitiv";
  das war eine Zusicherung, die der Mechanismus nie eingelöst hat, und ist korrigiert.
  **Mitigation:** Der Call-Timeout je Upstream (`DefaultCallTimeout`, FR-09) begrenzt den Schaden —
  ein zyklischer Verbund läuft in Timeouts statt in unbegrenzte Rekursion, und die Fehlerquote im
  Dashboard wird sofort auffällig. **Betriebsregel:** Gateway-Verbünde azyklisch konfigurieren.
  Echte transitive Erkennung bräuchte Call-Metadaten statt Verbindungs-Header — v2-Kandidat.
- ~~**PBKDF2 100k < OWASP-Empfehlung (600k)**~~ ✅ **in v1.1 behoben:** neue Hashes nutzen 600 000 Iterationen. Bestandshashes tragen ihre Iterationszahl im Format und bleiben verifizierbar (per Test belegt), ein Upgrade sperrt also niemanden aus.

## Reporting

Schwachstellen bitte über [GitHub Private Vulnerability Reporting](https://github.com/LupusMalusDeviant/mcp-mcp/security/advisories/new) melden (siehe SECURITY.md).
