# Connector-Entscheidungsmatrix und releasefähige Roadmap

## Vergleich

Bewertung: **L/M/H** = niedrig/mittel/hoch. Aufwand und Risiko sind negativ, Nutzen positiv.

| Connector | Zielmarkt / Beispiele | Discovery / Schema | Aufwand / Wartung | Security / Auth | Async, Stream, Binary | Nutzen / Differenzierung | Platzierung / Entscheidung |
|---|---|---|---|---|---|---|---|
| MCP | Agent-Tools; offizielle MCP-Server | H / H | M / M | M / M | Notifications heute, weitere Semantik protokollabhängig | H / M | Core, vorhanden |
| OpenAPI HTTP | SaaS/REST; GitHub, interne APIs | H / H, aber Spezifikationsqualität schwankt | M / M | H / H | meist synchron, Binary begrenzen | H / M | Core, vorhanden |
| CLI Host | lokale/Legacy-Tools; git, ffmpeg, eigene Binaries | Manifest M / H | M / H | H / M | Prozessstream, Dateien/Binary | H / H | Core-Runtime, nur trusted/dev |
| WASI Component | portable lokale Tools und Connectoren | WIT H / H | H / M | M / M | WIT list/stream/future; stufenweise | H / H | Core-Runtime + offizielles SDK, priorisiert |
| Native Container | bestehende CLI/stdio/Connectoren | abhängig vom Connector | H / H | H / H | vollständig, aber IPC nötig | H / H | Core-Runtimeadapter, priorisiert |
| OpenRPC/JSON-RPC | Nodes, Wallets, interne RPCs | OpenRPC H, `rpc.discover` optional / H | M / M | H / M | v1 synchron; Notifications/Batch später | H / H | offizieller isolierter Connector, zuerst |
| gRPC Unary | Enterprise-Microservices | Reflection optional oder Descriptor Set / H | H / H | H / H | v1 Unary; bytes/Artifacts | H / H | offizieller isolierter Connector |
| GraphQL kuratiert | SaaS/Enterprise GraphQL | Introspection H / H | M / M | H / H | v1 Query/Mutation; Subscriptions später | H / M | offizieller Connector, nur registrierte Operationen |
| AsyncAPI | Broker/Event-Plattformen; Kafka, AMQP | Dokument H / H | H / H | H / H | Kernzweck Events/Streams/Binary | M-H / H | offizieller Import + ein Referenztransport nach Task/Event |
| A2A | externe Agentendelegation | Agent Card H / M-H | H / H | sehr H / sehr H | Tasks, Input, Artifacts, Streaming | strategisch H / H | offizieller Connector erst nach Delegationsmodell |
| SOAP/WSDL | Legacy Enterprise | WSDL/XSD H, Profile variabel | sehr H / sehr H | sehr H / sehr H | Attachments möglich | Nachfrage unklar / L | kein Meilenstein; nur nach Zielsystem/Fixtures |

## Semantische Detailmatrix

| Connector | Cancellation | Long-running | Events | Fehler | Testbarkeit | optionale Discovery |
|---|---|---|---|---|---|---|
| CLI | Prozessbaum | Taskadapter später | nein | Exit/stdout/stderr | H mit Probe | nein, Manifest Pflicht |
| WASI | Epoch/Fuel + Host | Future/Task später | Stream später | Trap + `result` | H mit deterministischen Components | WIT im Component |
| OpenRPC | HTTP-Abbruch | Task nur nativ modelliert | Notifications später | JSON-RPC code/data | H mit Fixture-Server | `rpc.discover` optional |
| gRPC | Deadline/Cancel | Servermodell abhängig | Streaming später | Status + Details | H mit Descriptor-Fixtures | Reflection optional |
| GraphQL | HTTP-Abbruch | Operation abhängig | Subscription später | data + errors | H mit Schema-Fixtures | Introspection kann deaktiviert sein |
| AsyncAPI | Consumer/Producer abhängig | Request/Reply als Task | ja | Broker-/Delivery-spezifisch | M; Broker-Fixtures nötig | Dokumentimport |
| A2A | Task cancel | ja | Status/Artifact updates | A2A-Fehler + Taskstate | M; Multi-Turn-Fixtures | Agent Card erforderlich |

## Sicherheitsanalyse

- **Schemaimporte:** ausschließlich begrenzte Größe/Tiefe; HTTPS/Datei-Roots; SSRF-Schutz;
  Redirect-, Host- und DNS-Rebinding-Prüfung; zyklische `$ref`-Erkennung; Cache mit Hash.
- **HTTP/RPC/GraphQL:** Credentials nur als Secret-Referenz, TLS-Verifikation an, Ziel-Allowlist,
  Response-/Depth-/Complexity-Limits und strukturierte Redaction.
- **gRPC:** Reflection ist nicht garantiert; Descriptor Sets sind der reproduzierbare
  Produktionspfad. Metadaten und Credentials werden nie Teil des Agentenschemas.
- **AsyncAPI:** untrusted Payloads, Deduplizierung, Backpressure, Retention, Consumer-Cursor, DLQ
  und Auditvolumen sind Releaseblocker.
- **A2A:** Delegationsidentität, Budget/Zeit/Rekursion, Loop-Erkennung, Artifact-Rechte und
  Confused-Deputy-Schutz sind Releaseblocker.
- **WASI/Container/Host:** Runtime-Grants sind unabhängig von Connector-RBAC. Eine freigegebene
  Hostfunktion oder ein Mount bleibt ein realer Zugriff und wird separat auditiert.

## Roadmap für einen Solobetreiber

1. **M1 – CLI-Sicherheitsrelease:** Secret-Fixes, Stream-Caps, Environment-/Pfadpolicy, typisierte
   Manifeste, Approval-Default, Lifecycle-/OS-Tests.
2. **M2 – Gateway-CLI:** Suche, Beschreibung, Invocation, Server, Approval, Audit; dokumentierte
   JSON-Verträge und Exitcodes.
3. **M3 – Modellrelease:** ADR-0015/0016 akzeptieren; additive Capability-V1-Adapter und
   Connector-Handshake ohne Drittanbieterinstallation.
4. **M4 – WASI-Spike:** ein signiertes Fixture, WIT-Inventar, deny-all Imports, Limits auf Windows
   und Linux. Erst danach Runtime auswählen.
5. **M5 – Isolation/SDK:** WASI-Pluginpfad, Grants/Audit, Packaging/Rollback; Containeradapter für
   native Prozesse getrennt ausliefern.
6. **M6 – OpenRPC:** Dokumentimport + HTTP JSON-RPC, keine Batch/Notifications in v1.
7. **M7 – gRPC Unary:** Reflection und statische Descriptor Sets; kein Streaming.
8. **M8 – GraphQL:** nur registrierte Queries/Mutations; Mutation approval-default.
9. **M9 – Task/Event:** Persistenz, Cancellation, Cursor, DLQ; danach ein AsyncAPI-
   Referenztransport.
10. **M10 – A2A:** erst nach Delegations-, Budget-, Loop- und Artifact-Nachweisen.

Jeder Meilenstein ist einzeln releasbar. SOAP/WSDL bleibt außerhalb der festen Roadmap.

### Aktueller M4-Stand

Der ausführbare Wasmtime-47-Spike, WIT-Inventar, binäre Component-Reflection, Hash-Pinning,
deny-by-default Imports, Limits und Windows-/Linux-CI sind implementiert. Lokal ist der
Windows-Nachweis grün; der externe Linux-CI-Nachweis sowie Publisher-Signatur und echte
WASI-P2-Host-Grants bleiben als M4-Restarbeiten offen. M5 ist noch nicht begonnen.
