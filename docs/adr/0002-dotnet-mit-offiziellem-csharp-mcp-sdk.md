# ADR-0002: .NET 10 mit offiziellem C# MCP SDK als Technologie-Basis

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

Der Gateway ([ADR-0001](./0001-zentraler-proxy-gateway-statt-direktanbindung.md)) braucht eine Implementierungsbasis. Der Product Owner wünscht explizit „Interface first, bestenfalls auf .NET-Basis". Am Markt existieren Gateways in TypeScript (MetaMCP), Go (MCPJungle, Obot) und Rust (agentgateway), aber **kein** .NET-Gateway. Das offizielle C# SDK `ModelContextProtocol` ist seit Februar 2026 stabil (v1.x) und wird gemeinsam mit Microsoft gepflegt; es bietet Client- **und** Server-Seite plus ASP.NET-Core-Hosting — exakt die beiden Hälften, die ein Proxy braucht.

**Kernfrage:** Auf welcher Sprach-/SDK-Basis wird der Gateway gebaut?

## Anforderungen

### Funktional

- MCP-Client (stdio + Streamable HTTP) und MCP-Server (Streamable HTTP) im selben Prozess.
- Dynamische Tool-Registrierung zur Laufzeit (Hot-Swap, FR-06/07).

### Nicht-Funktional

- Team-Skill: Product Owner arbeitet primär mit .NET/C# (CoreCMS-Umfeld).
- Hohe Nebenläufigkeit (NFR-02) und niedriger Overhead (NFR-01).
- Interface-first-Testbarkeit (NFR-08).

## Betrachtete Optionen

### Option 0: Fork/Adaption einer bestehenden Lösung (TypeScript/Go/Rust)

MetaMCP, MCPJungle oder agentgateway forken und um fehlende Features erweitern.

**Positiv:**
- Monate an Grundlagenarbeit (Protokoll, Aggregation) geschenkt.
- Bestehende Community und Bugfixes.

**Negativ:**
- Fremder Stack außerhalb der Kernkompetenz des Owners — jede Anpassung teuer, Wartung langfristig unrealistisch.
- Keine der Basen deckt RBAC + Token-Hybrid + UI gleichzeitig ab; der Umbau wäre invasiv.
- „Interface first auf .NET" nicht erfüllbar.

### Option 1: .NET 10 + offizielles C# SDK `ModelContextProtocol`

Greenfield auf .NET 10 (LTS, Nov. 2025), Protokoll-Schicht vollständig aus dem offiziellen SDK (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`, `ModelContextProtocol.Core`).

**Positiv:**
- Wunsch-Stack des Owners; ein Ökosystem für Gateway, Bridge und Blazor-UI.
- SDK liefert beide Protokoll-Hälften, Transporte, DI-Integration; stabil (v1.2+), Microsoft-kofinanzierte Pflege folgt der Spec zeitnah.
- Kestrel/ASP.NET Core erfüllt die Nebenläufigkeits- und Latenzziele nachweislich.
- Marktlücke: erstes ernsthaftes .NET-Gateway.

**Negativ:**
- Alle Gateway-Fachlogik (Aggregation, RBAC, Hot-Swap) entsteht neu — höchster Initialaufwand.
- Abhängigkeit von SDK-Release-Zyklen bei neuen Spec-Features (z. B. künftige Protokoll-Revisionen).
- Low-Level-Zugriff (z. B. rohe JSON-RPC-Durchleitung) muss ggf. gegen SDK-Abstraktionen erkämpft werden.

### Option 2: .NET mit eigener Protokoll-Implementierung

JSON-RPC/MCP selbst implementieren, um volle Kontrolle über Passthrough zu behalten.

**Positiv:**
- Maximale Kontrolle, ideal für transparentes Proxying ohne Deserialisierungs-Overhead.

**Negativ:**
- Protokoll-Pflege (Spec-Revisionen, Auth, Transporte) dauerhaft selbst zu tragen — genau die Arbeit, die das SDK abnimmt.
- Hohes Risiko subtiler Inkompatibilitäten mit Clients.

## Vorschlag des Autors

Option 1. Der Eigenanteil (Aggregation, RBAC, UI) ist ohnehin der Kern des Produkts; die Protokoll-Schicht ist Commodity und gehört ausgelagert. Wo das SDK Passthrough-Grenzen hat, kapseln wir es hinter eigenen Interfaces (`IUpstreamConnection`), sodass ein punktueller Drop auf `ModelContextProtocol.Core`-Low-Level-APIs möglich bleibt.

## Entscheidung

**Gewählte Option:** „.NET 10 + offizielles C# SDK"

Team-Fit und Marktlücke geben den Ausschlag; der höhere Initialaufwand wird durch das SDK auf die eigentliche Produktlogik begrenzt. SDK-Abhängigkeit wird durch Interface-Kapselung entschärft.

## Konsequenzen

### Positiv

- Durchgängig C# von Protokoll bis UI; ein Build, ein Deployment.
- Spec-Konformität weitgehend geschenkt (SDK-Updates statt Eigenpflege).

### Negativ

- Kein Code-Reuse aus bestehenden Gateways; Zeit bis zum ersten nutzbaren Stand länger als bei Fork.
- Bei SDK-Lücken (exotische Protokoll-Features) sind Workarounds nötig.

### Folge-Entscheidungen

- UI-Framework → [ADR-0004](./0004-blazor-server-als-web-ui.md)
- Persistenz → [ADR-0007](./0007-ef-core-mit-sqlite-default-postgres-optional.md)

### Review

**Reality-Check geplant für:** 2026-09-15

## Weitere Informationen

### Tooling-Empfehlung

.NET 10 SDK, zentrale Paketversionen (`Directory.Packages.props`), `dotnet format` + Analyzer (`TreatWarningsAsErrors`).

### Referenzen

- [Offizielles C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) · [SDK-Doku](https://csharp.sdk.modelcontextprotocol.io/)
- [MS-Blog: Build an MCP server in C#](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/)
