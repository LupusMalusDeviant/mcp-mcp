# Offizielle Gateway-CLI

`mcp-mcp` ist ein separater HTTP-Client. Er verwendet ausschließlich `/healthz`, `/readyz` und die
öffentlichen `/api/v1`-Verträge; er liest weder Datenbank noch interne Stores.

## Konfiguration und Identität

```json
{
  "endpoint": "https://gateway.example/",
  "tokenFile": "C:/secure/mcpmcp.token",
  "identity": "production-operator"
}
```

Die Config wird mit `--config PATH` oder `MCPMCP_CONFIG` gewählt.
`MCPMCP_ENDPOINT` überschreibt den Endpoint. Die wirksame Gateway-Identität stammt immer aus dem
API-Token und damit aus der serverseitigen RBAC-Zuordnung; `identity`/`MCPMCP_IDENTITY` ist nur ein
lokales Profil-Label.

Tokenquellen:

1. `--token-stdin`;
2. `MCPMCP_TOKEN`;
3. `tokenFile` aus der Config.

Ein `--token`-Argument existiert absichtlich nicht, damit Tokens nicht in Prozesslisten oder
Shell-History landen. TLS-Zertifikatsfehler werden nicht ignoriert.

## Befehle

```text
mcp-mcp status
mcp-mcp tools search <query>
mcp-mcp tools describe <tool>
mcp-mcp tools invoke <tool> --json '{...}'
mcp-mcp tools invoke <tool> --file args.json
mcp-mcp tools invoke <tool> --file -
mcp-mcp servers list
mcp-mcp servers add --file server.json
mcp-mcp servers enable <id>
mcp-mcp servers disable <id>
mcp-mcp servers remove <id>
mcp-mcp approvals list
mcp-mcp approvals approve <id>
mcp-mcp approvals deny <id>
mcp-mcp audit tail
```

Globale Optionen stehen vor dem Befehl:

```text
mcp-mcp --json --config gateway.json tools search git
```

`--json` gibt genau ein kompaktes JSON-Dokument auf stdout aus. Menschliche Hinweise und Fehler
gehen nach stderr. Feldnamen und Exitcodes bleiben innerhalb einer Minor-Version kompatibel;
additive JSON-Felder sind erlaubt, Entfernen oder Umdeuten erst mit Major-Version.

## Exitcodes

| Code | Bedeutung |
|---:|---|
| 0 | Erfolg |
| 2 | Syntax, lokale Datei oder ungültiges JSON |
| 3 | nicht authentifiziert oder nicht berechtigt |
| 4 | Objekt/Tool nicht gefunden |
| 5 | Gateway-/Upstream-Fehler |
| 6 | menschliche Freigabe erforderlich |
| 10 | Netzwerk, TLS, I/O oder Abbruch |
