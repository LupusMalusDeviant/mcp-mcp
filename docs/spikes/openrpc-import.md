# Spike: OpenRPC-Import

## Frage und Grenze

Lässt sich ein statisches OpenRPC-Dokument ohne Netzauflösung in stabile Capabilities überführen?
Der Spike führt keine RPCs aus und folgt keinen externen `$ref`.

OpenRPC beschreibt Methoden und Content Descriptors mit JSON-Schema; `rpc.discover` ist ein
optionaler standardisierter Discovery-Aufruf. V1 behandelt statische Dokumente und
`rpc.discover` gleich, nachdem Antwortgröße, Ziel und Schema validiert wurden.

## Mapping

- Method `name` → nativer technischer Name.
- `paramStructure=by-name` → JSON-Objekt; `by-position` → geordnetes Array mit unveränderlicher
  Descriptor-Reihenfolge.
- Content Descriptor `schema` → Eingabeschema; `required` → Required-Liste.
- Result Descriptor → Ausgabeschema.
- JSON-RPC `error.code/message/data` → strukturierter Connectorfehler; `data` wird begrenzt und
  redigiert.
- Request-ID wird vom Connector erzeugt und mit Audit-Correlation verknüpft.

## Security-Fixtures

1. gültiges by-name/by-position-Dokument;
2. doppelte Methodennamen;
3. lokale zyklische `$ref`;
4. externe HTTP-/file-Referenz;
5. Dokument über Größen-/Tiefenlimit;
6. `rpc.discover` mit Redirect auf private/link-local Adresse.

Go erst bei fail-closed Referenzauflösung. Batch und Notifications sind für v1 ausdrücklich
ausgenommen.

Quelle: [OpenRPC Specification](https://spec.open-rpc.org/)
