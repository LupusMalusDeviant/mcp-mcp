# ADR-0018: Native Prozess- und Container-Isolation

- **Status:** Vorgeschlagen
- **Datum:** 2026-07-24

## Kontext

Nicht jede bestehende CLI kann als WebAssembly Component geliefert werden. Direkte Hostprozesse
haben Zugriff auf Kernel, Benutzerrechte und jede versehentlich geerbte Ressource. Shell-freie
Argumentübergabe verhindert Command Injection, bildet aber keine Sandbox.

## Entscheidung

MCPMCP bietet drei explizite Runtime-Modi:

1. **WASI Component:** Default für neue Plugins.
2. **Native Container:** Default für vorhandene, nicht vertrauenswürdige CLI-/stdio-Programme.
3. **Trusted Host Process:** nur mit absolutem kanonischem Pfad, Root-Allowlist, optionalem Hash-Pin
   und ausdrücklicher Admin-Freigabe; PATH-Auflösung nur Development.

Der Container-Modus verwendet pro Upstream einen langlebigen Worker nur, wenn Startupkosten dies
erzwingen; sonst einen Job pro Invocation. Mindestpolicy:

- read-only Root-Filesystem und eigener nicht-root Benutzer;
- alle Linux Capabilities entfernt, no-new-privileges, seccomp/AppArmor soweit verfügbar;
- CPU-, RAM-, PID-, Prozess-, Output- und Ephemeral-Disk-Limits;
- Netzwerk aus, außer expliziter Ziel-Allowlist;
- Mounts nur aus kanonischen read-only/read-write Allowlists;
- Secrets als kurzlebige In-Memory-/File-Descriptor-Injection, nie Image oder persistentes Volume;
- Prozessbaum-Kill, Container-Stop und nachweisbarer Cleanup bei Timeout, Cancellation und Shutdown.

Ohne Container-Runtime darf eine Konfiguration entweder im Trusted-Modus laufen oder wird mit einer
präzisen Readiness-/Validierungsfehlermeldung abgewiesen. Ein stiller Fallback vom Container auf den
Host ist verboten.

## Bereits umgesetzte Host-Basis

Der direkte CLI-Connector begrenzt Streams während des Lesens, trennt stdout/stderr, leert das
Host-Environment, verlangt standardmäßig absolute Pfade, prüft Roots/Links, unterstützt SHA-256-
Pinning, begrenzt Parallelität und beendet Prozessbäume. Das reduziert Risiko, ersetzt aber weder
Container- noch WASI-Isolation.

## Konsequenzen

Windows- und Linux-Container benötigen getrennte Betriebsnachweise. Kubernetes ist kein Zwang; ein
lokaler OCI-kompatibler Runtimeadapter reicht für v1. Die Runtime-Schnittstelle bleibt vom
Protokoll-Connector getrennt, damit stdio, CLI und zukünftige native Connectoren dieselbe Isolation
nutzen.
