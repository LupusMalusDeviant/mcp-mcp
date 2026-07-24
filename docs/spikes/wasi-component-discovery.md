# Spike: WIT/WASI Component Discovery

## Frage

Kann ein .NET-Host Component-Metadaten unter Windows und Linux stabil inventarisieren, in
`CapabilityDescriptorV1` mappen und mit vollständig verweigerten Host-Capabilities ausführen?

## Ausführbarer Stand

Das getrennte Rust-Projekt `spikes/wasi-component-runtime` pinnt Rust 1.94.0,
Wasmtime 47.0.2 und `wit-parser`/`wit-component` 0.254.0. Es ist keine
Abhängigkeit von Server, Core oder Upstream.

```text
cargo test --locked
cargo run --locked -- probe
cargo run --locked -- discover ../../docs/spikes/fixtures/control-plane.wit connector
```

Der Spike:

- parst das vollständige WIT-Typegraph-Fixture und vergleicht das normalisierte Inventar;
- erzeugt daraus ein echtes WebAssembly Component, lädt und reflektiert dessen Interface,
  Typen und `run`-Export;
- hasht die tatsächlich erzeugten Component-Bytes vor Ausführung;
- instanziiert mit leerem Linker, also ohne Environment, Preopens, Sockets oder Secrets;
- weist nicht freigegebene filesystem-/socket-Imports vor Instanziierung ab;
- beweist Fuel-, Epoch-, Linear-Memory- und Byte-Output-Limits;
- läuft in CI unter Windows und Linux.

## Fixture

`fixtures/control-plane.wit` deckt primitive Typen, Record, Enum, List, Option und Result ab.
`wit-component` erzeugt daraus für den Discovery-Test ein Component derselben World ohne Imports.
Die generierte Dummy-Implementierung dient nur der Typ-/Exportreflexion; der getrennte
`no-import.component.wat`-Smoke-Test führt tatsächlich eine Funktion aus.

## Messbarer Ablauf

1. Runtime-Kandidaten in einem **separaten Spike-Projekt** pinnen; keine Abhängigkeit im Server.
2. Component-Hash vor dem Laden prüfen.
3. World, Exportnamen und Typgraph inventarisieren; unbekannte Pflichttypen fail-closed.
4. Mapping gegen `fixtures/control-plane.expected.json` vergleichen.
5. Eine Funktion ohne Preopens, Sockets, Environment oder Secrets aufrufen.
6. Negativ-Fixtures mit filesystem/socket-Import müssen vor Instanziierung abgewiesen werden.
7. Endlosschleife, Memory-Growth und 1-MB-Ausgabe jeweils durch Fuel/Epoch, Memory- und Output-Cap
   stoppen.
8. Identische Tests auf `windows-latest` und `ubuntu-latest`.

## Lokales Ergebnis 2026-07-24

Windows, Wasmtime 47.0.2:

- WIT-Inventar entspricht `control-plane.expected.json`;
- Component-Hash:
  `b17cb7254db649066f580615f0d608796895bedde4a267f91ed98518ac3ec871`;
- reflektierter Export:
  `mcpmcp:spike/tools@0.1.0.run`;
- Fuel, Epoch-Timeout, 128-KiB-Memory-Cap und 64-KiB-Output-Cap greifen;
- leerer Grant-Satz akzeptiert das importfreie Component;
- filesystem-/socket-Imports werden mit leerem Grant-Satz abgewiesen.

Der Vergleich mit einem gehärteten, bereits geladenen Alpine-OCI-Job ist in
`wasi-vs-container-result-2026-07-24.md` festgehalten.

## Ergänzung 2026-07-24 — M4-Ausbau (Grant-Härtung)

Über die reine Discovery hinaus gehärtet (19 Tests, `cargo fmt --check` und
`cargo clippy -D warnings` grün, lokal Windows):

- **Strukturiertes Grant-Modell (default-deny):** getrennte Grants für Preopens, Netzwerk,
  Environment und Secrets; jeder WASI-P2-Import wird auf seine Kategorie abgebildet und nur bei
  explizitem Grant zugelassen; unbekannte Imports sind fail-closed.
- **Publisher-Signatur:** detached Ed25519 über die Component-Bytes, verifiziert gegen administrativ
  gepinnte Publisher-Public-Keys **vor** dem Laden; manipulierte Bytes, falscher oder ungepinnter
  Schlüssel werden abgewiesen.
- **Grant-Audit-Datensatz:** Modul-SHA-256, Publisher-Key-Id, Runtime-Version und tatsächlich
  erteilte Grants — deterministisch serialisierbar.
- **Echte WASI-P2-Guest-Component:** ein für `wasm32-wasip2` gebautes, als Fixture eingechecktes
  Component (`fixtures/wasi-p2-guest.component.wasm`) — echte Interface-Importe, nicht nur benannt.
  Host-seitig via `wasmtime-wasi 47.0.2`: WASI wird nur bei Grant gelinkt; **ohne Grant schlägt die
  Instanziierung vor jeder Ausführung fehl** (deny-before-instantiation, bewiesen).
- **Negativtests:** Preopen-Eingrenzung lexikalisch **und** fs-kanonisch (Symlink-Ausbruch),
  Socket-Deny, Secret-Deny.

## Go/No-Go

**Go für die nächste Prototypstufe:** Limits greifen lokal deterministisch, Imports sind vor
Instanziierung sichtbar, alle Versionen sind gepinnt, und der M4-Ausbau (Grants, Signatur, Audit,
echte WASI-P2-Component, Negativtests) ist lokal (Windows) belegt.

**Noch kein Produktions-Go:** der externe Linux-CI-Lauf steht noch aus (bislang nur lokal Windows);
das Grant-Gating ist im Spike world-level (per-Interface-Preopen-/Socket-/Secret-Wiring fehlt);
Cache/Rollback und der Connector-Handshake fehlen; der Spike ist weiterhin **kein**
Server-Runtimeadapter. ADR-0017 bleibt deshalb inhaltlich „Vorgeschlagen".
