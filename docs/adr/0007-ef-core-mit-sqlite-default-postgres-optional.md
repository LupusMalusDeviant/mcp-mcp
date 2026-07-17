# ADR-0007: Persistenz — EF Core mit SQLite als Default, PostgreSQL optional

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

Der Gateway persistiert: Server-Konfigurationen (versioniert, FR-10), Identitäten/Rollen/Grants, Profile, Skill-Assets (FR-40) und vor allem das Audit-Log (FR-21 ff.) — letzteres ist schreibintensiv (jeder Call = 1 Insert) und wachstumskritisch (Retention, FR-25). NFR-06 verlangt Betrieb ohne externe Infrastruktur, optional extern für größere Setups. Secrets müssen verschlüsselt liegen (NFR-04).

**Kernfrage:** Welche Persistenz-Technologie trägt Konfiguration und Audit-Log?

## Anforderungen

### Funktional

- Relationale Abfragen fürs Log (Filter über Zeit/Identität/Server/Tool/Status, FR-23).
- Schema-Migrationen ohne Datenverlust (NFR-06).

### Nicht-Funktional

- Zero-Setup-Default (eine Datei im Volume); Insert-Last ~100 Calls/s ohne Blockieren des Hot Path.
- Vertrautheit: Owner arbeitet bereits mit EF Core + SQLite/PostgreSQL (CoreCMS-Muster).

## Betrachtete Optionen

### Option 0: EF Core mit SQLite (Default) und PostgreSQL (opt-in)

Ein EF-Core-Modell, zwei Provider; Auswahl per Connection-String. Audit-Schreibpfad entkoppelt über In-Memory-Channel + Batch-Writer (Log-Insert blockiert nie den Tool-Call).

**Positiv:**
- Deckt beide NFR-06-Modi mit einem Codepfad; erprobtes CoreCMS-Muster des Owners.
- SQLite im WAL-Modus schafft die Ziellast (ein Writer, Batch-Inserts) nachweislich.
- EF-Migrationen lösen die Schema-Evolution; LINQ-Filter für die Log-UI geschenkt.

**Negativ:**
- Zwei-Provider-Disziplin nötig (keine provider-spezifischen SQL-Features ohne Abstraktion; Migrations-Skripte je Provider testen).
- EF-Overhead im Schreibpfad — durch den Batch-Writer neutralisiert, aber Komplexität existiert.

### Option 1: Dokument-/Embedded-Store (LiteDB o. ä.)

**Positiv:**
- Schemafrei, simpel für Config-Objekte.

**Negativ:**
- Log-Analytik (Aggregationen, Zeitfenster, Perzentile) deutlich schwächer als SQL.
- Kein glaubwürdiger Skalierungspfad zu externem Server; kleines Ökosystem/Wartungsrisiko.

### Option 2: JSON-Dateien für Config + separates Log-Backend (z. B. Seq/ClickHouse)

**Positiv:**
- Config als Datei ist git-bar; spezialisiertes Log-Backend ist analytisch stark.

**Negativ:**
- Zwei Persistenz-Systeme = doppelte Betriebs- und Backup-Story; verletzt Zero-Setup.
- Transaktionale Kopplung (Config-Version + Audit-Eintrag „geändert von") über Systemgrenzen hinweg fragil.

## Vorschlag des Autors

Option 0. Sie ist die einzige, die Zero-Setup, SQL-Analytik und den Wachstumspfad in einem System vereint — und sie entspricht dem eingespielten Stack des Owners. Die Zwei-Provider-Disziplin wird per CI abgesichert (Integrationstests laufen gegen beide Provider).

## Entscheidung

**Gewählte Option:** „EF Core mit SQLite-Default, PostgreSQL optional"

Zero-Setup und Owner-Vertrautheit geben den Ausschlag. Die Provider-Disziplin und der entkoppelte Audit-Schreibpfad werden als feste Konstruktionsregeln ins Pflichtenheft übernommen.

## Konsequenzen

### Positiv

- `docker run -v data:/data` genügt für den Vollbetrieb; Migration auf PostgreSQL ist Connection-String + `dotnet ef database update`.
- Log-UI-Filter und Retention-Jobs sind einfache LINQ/SQL-Operationen.

### Negativ

- Audit-Batching bedeutet: bei hartem Crash können die letzten < 1 s Log-Einträge verloren gehen (Flush-Intervall konfigurierbar; bewusst akzeptiert gegenüber Hot-Path-Blockierung).
- SQLite-Grenze bei sehr großen Logs (> ~10 GB) → Retention (FR-25) ist nicht optional, sondern Betriebspflicht.

### Folge-Entscheidungen

- Secret-Verschlüsselung: ASP.NET Data Protection mit persistiertem Key-Ring im Datenvolume (Detail im Pflichtenheft; kein eigenes ADR).
- Audit-Tabelle append-only per Konvention + fehlendem Update-API (kein DB-Trigger-Zwang in v1).

### Review

**Reality-Check geplant für:** 2026-09-15
