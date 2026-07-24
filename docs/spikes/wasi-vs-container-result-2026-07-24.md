# Spike-Ergebnis: WASI Component versus nativer Container

- **Datum:** 2026-07-24
- **Host:** Windows mit Linux-Container-Engine
- **WASI:** Wasmtime 47.0.2, Rust 1.94.0
- **Container:** Docker 29.6.1
- **Image:** `alpine:3.22.1`
- **Image-ID/Digest:** `sha256:4bcff63911fcb4448bd4fdacec207030997caf25e9bea4045fa6c8c44de311d1`
- **Samples:** 7

## Vergleich

| Kurzlebiger Job | Minimum | Median | p95/Maximum |
|---|---:|---:|---:|
| WASI: Engine + Component kompilieren, instanziieren, Funktion aufrufen | 6,59 ms | 7,16 ms | 9,55 ms |
| OCI: gehärteten Container starten und `/bin/true` ausführen | 217,52 ms | 232,40 ms | 244,73 ms |

WASI lief ohne Imports, mit Fuel, Epoch-Deadline und Speicherlimit. Der Container lief mit:

- `network=none`;
- read-only Root-Filesystem;
- allen Linux Capabilities entfernt;
- `no-new-privileges`;
- PID-Limit 16;
- RAM-Limit 64 MiB;
- CPU-Limit 0,5;
- UID/GID 65532.

## Interpretation

Der Messwert ist ausschließlich eine **Startup-Untergrenze** auf diesem Host. Er vergleicht weder
Anwendungsdurchsatz noch gleichwertige Sicherheitsgrenzen: WebAssembly hängt von freigegebenen
Hostfunktionen ab, während ein Container weiterhin eine größere Kernel-/Runtime-Angriffsfläche
besitzt. Der deutliche Startzeitunterschied stützt WASI als bevorzugten Pfad für neue portable
Plugins; er ersetzt nicht den Containerpfad für vorhandene native Programme.

Der Befehl ist reproduzierbar:

```text
cargo run --locked -- compare-container alpine:3.22.1 7
```

Der geplante CI-Lauf verwendet den Image-Digest und 15 Samples. Ergebnisse anderer Hosts dürfen
nicht mit diesen lokalen Zahlen vermischt werden.

## Entscheidung

- **Go:** Connector-Handshake und Grant-Modell auf dem WASI-Prototyp weiterentwickeln.
- **Weiter erforderlich:** Publisher-Signatur, WASI-P2-Preopen-/Socket-Negativtests, Audit der
  Grants, Cache/Rollback und externer Linux-CI-Nachweis.
- **Container bleibt erforderlich:** native CLI-/stdio-Programme erhalten keinen stillen
  Host-Fallback und brauchen weiterhin den separaten ADR-0018-Adapter.
