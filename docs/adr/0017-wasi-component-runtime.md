# ADR-0017: WASI Component Runtime als bevorzugter isolierter Pluginpfad

- **Status:** Vorgeschlagen; Hostauswahl nach Spike
- **Datum:** 2026-07-24

## Kontext

WebAssembly stellt eine Speicher- und Kontrollflussgrenze bereit, ist aber nicht automatisch sicher.
Module können den Host nur über freigegebene APIs erreichen; genau diese Imports, Preopens, Sockets
und Secrets bilden damit die entscheidende Policyfläche. WIT beschreibt typisierte Verträge,
Interfaces und Worlds, aber nicht deren Sicherheitsverhalten.

Grundlagen:

- [WebAssembly Security](https://webassembly.org/docs/security/)
- [WIT Overview](https://component-model.bytecodealliance.org/design/wit.html)

## Entscheidung

MCPMCP bevorzugt WebAssembly Components für neue lokale Tools und externe Connectoren. Ein Component
erhält standardmäßig:

- kein Netzwerk;
- keine Dateisystem-Preopens;
- kein Host-Environment;
- keine Uhr, Zufallsquelle oder Secret-Capability, sofern nicht deklariert und freigegeben;
- begrenzten Speicher, Fuel/Epoch-Deadline, Output und Parallelität.

Der Host liest WIT-Exports zur Discovery und bildet WIT-Typen möglichst verlustarm auf den
Capability-Katalog ab. Imports werden vor Instanziierung gegen den Connector-Grant geprüft.
`list<u8>` wird als begrenzte Binärdaten oder Artifact behandelt; `result<T,E>` bleibt ein
strukturierter Result-/Fehlervertrag. Resources, Futures und Streams werden erst aktiviert, wenn
Ownership, Cancellation und das Task-/Event-Modell implementiert sind.

Jedes Modul wird über SHA-256 identifiziert. Produktion verlangt erlaubten Herausgeber oder
administrativ gepinnten Hash; Cache-Keys enthalten Runtime-, Component- und Grant-Version.
Erteilte Host-Capabilities werden bei Start und Invocation auditiert.

## Begrenzter Spike

Der Spike unter `docs/spikes/wasi-component-discovery.md` prüft nur:

1. Component laden und WIT-World/Exports inventarisieren;
2. primitive Typen, Records, Varianten, Listen, Options und Results mappen;
3. ohne Imports ausführen;
4. verweigerte filesystem-/socket-Imports nachweisen;
5. Fuel, Speicher, Timeout und Output-Cap messen;
6. dasselbe Fixture unter Windows und Linux ausführen.

Er implementiert weder Installation noch Netzwerk noch Secret-Injection. Runtime-Pakete werden erst
nach gemessener API-Stabilität und Wartungslast ausgewählt.

## Spike-Ergebnis 2026-07-24

Das separate Projekt `spikes/wasi-component-runtime` pinnt Wasmtime 47.0.2 und Rust 1.94.0. Es
erzeugt aus dem WIT-Fixture ein binäres Component, reflektiert dessen versionierte Exports und
weist nicht gewährte Imports vor Instanziierung ab. Tests belegen lokal unter Windows Fuel,
Epoch-Timeout, Linear-Memory- und Byte-Output-Limits. CI führt denselben Nachweis auf Windows und
Linux aus.

Ein lokaler Startup-Floor-Vergleich lag für das kurzlebige WASI-Component im Median bei 7,16 ms,
für einen gehärteten Alpine-Containerjob bei 232,40 ms. Diese Zahlen sind keine
Sicherheitsäquivalenz und kein Anwendungsbenchmark.

Der Spike ist ein Go für die nächste Prototypstufe, aber kein Produktions-Go: Publisher-Signatur,
echte WASI-P2-Preopen-/Socket-Grants, Grant-Audit, Cache und Rollback fehlen weiterhin. Der
ADR-Status bleibt daher „Vorgeschlagen“.

### M4-Ausbau 2026-07-24

Der Spike wurde gehärtet (Belege in `docs/spikes/wasi-component-discovery.md`, 19 Tests grün).
Jetzt vorhanden: strukturiertes Grant-Modell (default-deny) für Preopens/Netzwerk/Environment/
Secrets, detached Ed25519-Publisher-Signatur gegen administrativ gepinnte Keys, Grant-Audit-Datensatz
(Modulhash/Publisher/Runtime/erteilte Grants) und eine echte `wasm32-wasip2`-Guest-Component mit
`wasmtime-wasi`-Host, die WASI nur bei Grant linkt (deny-before-instantiation bewiesen); dazu
Traversal-/Symlink-/Socket-/Secret-Negativtests.

Offen für Produktion: externer Linux-CI-Nachweis (bislang nur lokal Windows); per-Interface-Grant-
Gating (der Spike gated world-level); Cache/Rollback; Connector-Handshake; Server-Runtimeadapter.
Der ADR-Status bleibt daher „Vorgeschlagen"; die Hostauswahl (Wasmtime 47) ist durch den Spike aber
bestätigt.

## Konsequenzen

WASI ist bevorzugte Isolation, kein Ersatz für Governance oder Container. Ein Component mit
freigegebenem Host-Dateisystem oder Netzwerk kann weiterhin erhebliche Seiteneffekte verursachen.
Native Programme bleiben über ADR-0018 unterstützt.
