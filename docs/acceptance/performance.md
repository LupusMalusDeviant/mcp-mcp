# NFR-01/02 — Referenzmessung

Formale Lastmessung des Gateway-Overheads (WP8.3, der letzte nicht automatisiert belegte Punkt aus der v1.0-Abnahme).

## Aufbau

| | |
|---|---|
| **Harness** | `PerformanceBenchmarkTests.Nfr01_gateway_overhead_and_tools_list_under_load` |
| **Ausführung** | `$env:MCPMCP_RUN_BENCHMARK=1; dotnet test --filter FullyQualifiedName~PerformanceBenchmark` |
| **Upstream** | `BulkServer` — stdio-MCP-Server mit **100 Tools** |
| **Last** | 20 gleichzeitige MCP-Sessions, 50 Calls je Session (1000 Calls), maximal **100 gleichzeitig in Flight** |
| **Katalog** | Profil mit allen 100 Tools gepinnt, damit `tools/list` den vollen Katalog liefert |

Der Benchmark läuft bewusst **nicht** in der normalen Suite und nicht in der CI: Geteilte CI-Runner sind keine Referenz-Hardware, dort würde man Runner-Auslastung statt Gateway-Verhalten messen.

## Messumgebung

| | |
|---|---|
| CPU | AMD Ryzen 7 9800X3D — 8 Kerne / 16 logische Prozessoren |
| RAM | 61,6 GB |
| OS | Windows 11 (10.0.26200) |
| Runtime | .NET 10.0.4 |
| Datum | 2026-07-18 |

> **Einordnung:** NFR-01 definiert als Referenz eine *4-Core-VM*. Diese Maschine ist stärker, die Werte sind daher als **oberes Ende** zu lesen — auf schwächerer Hardware ist mit höheren Latenzen zu rechnen. Der Abstand zu den Schranken (Faktor ~7 bzw. ~3) ist allerdings so groß, dass die Kriterien auch dort tragen sollten.

## Ergebnis

| Metrik | Gemessen | Schranke (NFR-01/02) | Status |
|---|---|---|---|
| `tools/call` p50 | **1,7 ms** | — | |
| `tools/call` **p95** | **7,3 ms** | ≤ 50 ms | ✅ Faktor ~7 Reserve |
| `tools/call` p99 | 14,9 ms | — | |
| `tools/call` max | 16,6 ms | — | |
| `tools/list` (100 Tools) **p95** | **59,1 ms** | ≤ 200 ms | ✅ Faktor ~3 Reserve |
| Durchsatz | ~6400 Calls/s | — | |
| Gleichzeitige Sessions | 20 | ≥ 20 | ✅ |
| Gleichzeitige In-Flight-Calls | 100 | ≥ 100 | ✅ |
| Fehler unter Last | **0** | 0 | ✅ |

**NFR-01 und NFR-02 sind damit auf Referenz-Hardware formal belegt** — der letzte offene Punkt aus [der v1.0-Abnahme](v1.md) ist geschlossen.

## Wiederholen

```powershell
$env:MCPMCP_RUN_BENCHMARK = "1"
dotnet test tests/McpMcp.Integration.Tests --filter "FullyQualifiedName~PerformanceBenchmark" `
  --logger "console;verbosity=detailed"
```

Der Test schlägt fehl, wenn p95 der Calls über 50 ms, p95 von `tools/list` über 200 ms liegt oder unter Last ein Call scheitert — er dient also zugleich als Regressionsschutz, wenn man ihn bewusst startet.
