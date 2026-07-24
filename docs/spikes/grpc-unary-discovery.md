# Spike: gRPC Unary Discovery

## Frage und Grenze

Können Reflection und ein statisches `FileDescriptorSet` denselben normalisierten Unary-Katalog
erzeugen? Streaming-Methoden werden erkannt, aber mit einem klaren Unsupported-Status nicht
exportiert.

Reflection liefert die exportierten protobuf-definierten APIs einschließlich referenzierter
Request-/Response-Typen, ist serverseitig aber nicht automatisch aktiviert. Deshalb ist das
statische Descriptor Set ein gleichwertiger Produktionspfad.

## Fixture-Matrix

- Unary mit Scalar, Enum, `oneof`, Map, `repeated`, Timestamp/Duration und Bytes;
- client-, server- und bidirektionales Streaming als Negativfälle;
- Reflection an/aus;
- identisches statisches Descriptor Set;
- Statuscodes mit Details, Deadline und Caller-Cancellation;
- Auth-Metadata als Secret-Referenz;
- große Bytes-Antwort → Artifact statt unbeschränktem Inline-JSON.

## Mapping

Service + Method bilden den nativen Namen. Request und Response werden rekursiv in JSON-Schema
überführt; Protobuf-Feldnummer und vollqualifizierter Typ bleiben als Herkunftsmetadaten erhalten.
Unknown fields werden nicht still erfunden. `oneof` wird als `oneOf`, Maps als Objekt mit
`additionalProperties` und Bytes als Base64 mit hartem Inline-Limit abgebildet.

Go erst, wenn Reflection und Descriptor Set denselben Schemahash liefern und Deadlines/Cancellation
in den Testfixtures nachweisbar sind.

Quelle: [gRPC Reflection](https://grpc.io/docs/guides/reflection/)
