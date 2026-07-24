# ADR-0020: WASI-Runtime als Out-of-Process-Rust-Host hinter dem Connector-Vertrag

- **Status:** Akzeptiert
- **Datum:** 2026-07-24
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

M5 soll den WASI-Pluginpfad (ADR-0017) von einem separaten Spike zu einem produktionsfähigen Ausführungspfad machen: signierte WebAssembly-Components mit deny-by-default-Grants laufen isoliert und erscheinen als normale Upstreams im Katalog. Der M4-Ausbau hat das Sicherheitsmodell (Grant-Modell, Ed25519-Publisher-Signatur mit Pinning, Grant-Audit, echtes deny-before-instantiation) in einem Rust/Wasmtime-47-Spike bereits belegt.

Die technische Lage zwingt aber eine Architekturentscheidung: Eine Recherche (2026-07) zeigt, dass das offizielle **`wasmtime-dotnet`-Binding nur Core-WASM-Module und WASI Preview 1 unterstützt — nicht das Component Model / WASI-P2**. Auch die Wasmtime-**C-API** führt Component-Model-Support als offene Lücke. Damit ist eine In-Process-Ausführung von WASI-P2-Components *im* .NET-Gateway auf absehbare Zeit ausgeschlossen. ADR-0018 hat die Runtime-Schnittstelle bereits bewusst vom Protokoll-Connector getrennt, und der Spike ist absichtlich ohne Server-Abhängigkeit gehalten.

**Kernfrage:** Wie führt das .NET-Gateway WASI-P2-Components produktionsreif aus, ohne den einen Governance-Pfad (RBAC → Guardrail → Approval → Rate-Limit → Audit) zu umgehen — obwohl die verfügbaren .NET-/C-API-Wasmtime-Bindings kein Component Model können?

## Anforderungen

### Funktional

- WASI-P2-Components laden, Signatur + Grants prüfen, ausführen und Ergebnis/Fehler/Truncation zurückliefern.
- Als Upstream im aggregierten Katalog erscheinen (Namespacing, Discovery wie jeder andere Transport).
- Grants (Preopens/Netzwerk/Environment/Secrets) und Ausführungslimits (Fuel/Epoch/Memory/Output) durchsetzen.

### Nicht-Funktional

- **Kein Governance-Bypass:** jeder Aufruf läuft durch den bestehenden `IToolInvoker`; die Runtime greift nicht direkt auf DB oder interne Stores zu.
- **Fault-Isolation:** ein abstürzender oder hängender Component darf das Gateway nicht mitreißen.
- **Windows und Linux**, non-root, wartbar für einen Soloentwickler.
- Reife der genutzten Runtime-APIs (Wartungslast über die Zeit).

## Betrachtete Optionen

### Option 0: In-Process via `wasmtime-dotnet`

WASI-P2-Components direkt im .NET-Prozess über das offizielle Binding ausführen.

**Positiv:**
- Kein zweiter Prozess, kein IPC, eine Sprache.

**Negativ:**
- **Technisch nicht möglich:** `wasmtime-dotnet` kann kein Component Model / WASI-P2. Die Option scheitert an der Faktenlage, nicht an einer Abwägung.

### Option 1: Auf .NET-Component-Support warten oder rohe C-API via P/Invoke

Entweder auf Component-Support in `wasmtime-dotnet` warten oder die Wasmtime-C-API direkt per P/Invoke anbinden.

**Positiv:**
- Bliebe langfristig In-Process, wenn der Support kommt.

**Negativ:**
- Die Component-C-API ist selbst unvollständig; eine Eigenanbindung wäre umfangreich und fragil.
- Unklarer Zeithorizont — blockiert M5 auf unbestimmte Zeit.

### Option 2: Out-of-Process Rust-Host hinter versioniertem IPC-Vertrag

Die WASI-Runtime wird ein eigenständiger Rust-Host-Prozess (aus dem M4-Spike gewachsen), den das Gateway über einen versionierten lokalen IPC-Vertrag ansteuert — konsistent mit dem Connector-Vertrag (ADR-0016).

**Positiv:**
- Nutzt die **reife** Rust/Wasmtime-Component-Unterstützung sofort, statt auf .NET zu warten.
- Starke Fault-Isolation durch den eigenen Prozess (killbar, ressourcenbegrenzbar).
- Deckt sich mit ADR-0018 (getrennte Runtime-Schnittstelle) und der bewussten Spike-Trennung.

**Negativ:**
- **Rust wird eine Produktions-Runtime-Abhängigkeit:** Cross-Platform-Build/Packaging (Win+Linux), Ops und zweisprachiger Betrieb.
- IPC-Overhead und ein Vertrag, der versioniert und kompatibel gehalten werden muss.
- Lebenszyklus des Host-Prozesses (Start/Restart/Supervision, analog ADR-0005).

## Vorschlag des Autors

Option 2. Option 0 ist faktisch vom Tisch, Option 1 verlagert das Risiko nur in eine unbestimmte Zukunft mit hoher Eigenbaulast. Der Out-of-Process-Rust-Host ist die einzige Variante, die die bereits bewiesene Runtime *jetzt* nutzbar macht — und der Preis (zweiter Prozess, IPC, Rust im Betrieb) ist genau die Isolationsgrenze, die ADR-0018 ohnehin fordert. Die zusätzliche Angriffs-/Betriebsfläche wird durch den unveränderten Governance-Pfad im .NET-Gateway eingerahmt: Der Host bekommt ausschließlich bereits autorisierte, gefilterte Aufrufe.

## Entscheidung

**Gewählte Option:** „Out-of-Process Rust-Host hinter versioniertem IPC-Vertrag"

Die WASI-Runtime läuft als eigenständiger Rust-Host-Prozess und wird vom .NET-Gateway über einen versionierten lokalen IPC-Vertrag angesteuert. Jeder Aufruf läuft weiterhin durch den `IToolInvoker` (RBAC/Guardrail/Approval/Rate-Limit/Audit); der Host setzt seinerseits Signaturprüfung, Grants und Ausführungslimits durch und greift nie direkt auf Gateway-DB oder -Stores zu. Bewusst in Kauf genommen: Rust als Produktions-Abhängigkeit und die Pflege eines IPC-Vertrags.

## Konsequenzen

### Positiv

- Die reife Rust/Wasmtime-Component-Unterstützung wird sofort produktiv nutzbar.
- Prozess-Isolation als zusätzliche Sicherheits- und Stabilitätsgrenze.
- Konsistent mit ADR-0016 (Connector-Vertrag) und ADR-0018 (getrennte Runtime-Schnittstelle) — keine Sonderarchitektur.

### Negativ

- Rust im Produktions-Build: Cross-Platform-Packaging, CI und Ops werden aufwändiger.
- Ein IPC-Vertrag muss versioniert, kompatibel und getestet gehalten werden.
- Ein weiterer beaufsichtigter Prozess (Start/Restart/Health).

### Folge-Entscheidungen

- Konkrete IPC-Form (stdio-Framing vs. lokaler Socket) und Serialisierung des Vertrags.
- Packaging/Distribution des Rust-Hosts neben dem .NET-Image (ein Container mit beidem vs. getrennt).
- Persistenter Publisher-Trust-Store (DataProtection) und Modul-Cache/Rollback auf .NET- oder Host-Seite.
- Fein-granulares Grant-Mapping (per-Preopen/-Socket/-Env/-Secret) im Host statt world-level.

### Review

**Reality-Check geplant für:** 2026-09-04

## Weitere Informationen

### Scope

Betrifft den WASI-Ausführungspfad (M5). Der native CLI-Transport (ADR-0014) und der Container-Isolationspfad (ADR-0018) bleiben unberührt. Diese Entscheidung ist die Architektur*grenze*; die IPC-Vertragsdetails und die Umsetzungsschritte gehören in den M5-Plan (`docs/plans/0003`), nicht in dieses ADR.

### Referenzen

- [ADR-0016](./0016-versionierter-connector-plugin-vertrag.md) — der Vertrag, dem der IPC-Kanal folgt.
- [ADR-0017](./0017-wasi-component-runtime.md) — die WASI-Runtime-Entscheidung, die dies produktionsreif macht.
- [ADR-0018](./0018-native-prozess-und-container-isolation.md) — die getrennte Runtime-Schnittstelle, in die sich der Host einfügt.
- [wasmtime-dotnet](https://github.com/bytecodealliance/wasmtime-dotnet) — .NET-Binding, Core-Module/WASI-P1, ohne Component Model.
- [WASI & Component Model — Status](https://eunomia.dev/blog/2025/02/16/wasi-and-the-webassembly-component-model-current-status/) — kein .NET-Component-Host-Support in Sicht; `componentize-dotnet` baut Components, hostet sie nicht.
