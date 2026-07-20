# ADR-0011: Secret-Erkennung als hot-swappable Guardrail im Invoker

- **Status:** **Vorgeschlagen** — enthält vier offene Entscheidungen (Abschnitt „Zu entscheiden")
- **Datum:** 2026-07-20
- **Betrifft:** ADR-0008 (Invocation-Kern), FR-24 (Redaction), NFR-01 (Latenz), NFR-04 (Secrets), NFR-10 (Lizenzen)

## Kontext

Der Gateway protokolliert heute alles, verhindert aber inhaltlich nichts. Die vorhandene Redaction
maskiert Secrets **nur fürs Audit-Log** — nicht auf dem Weg zum Empfänger.

Entscheidend ist dabei die Flussrichtung. Der Gateway spricht nie mit einem Modell; er sitzt
zwischen Agent und Werkzeug:

```
Agent (hält das LLM)  ──Argumente──▶  Gateway  ──▶  Upstream-Tool
                      ◀──Ergebnis──            ◀──
```

Daraus folgen **zwei verschiedene Bedrohungen**, die man nicht gleich behandeln darf:

| Richtung | Bedrohung | Heute |
|---|---|---|
| **Ergebnis → Agent** | Ein Tool liefert `.env`, ein K8s-Secret, eine DB-Zeile — der Wert landet im Kontextfenster des Modells und von dort in dessen Logs, Folgeantworten und Folge-Calls. | **ungeschützt** |
| **Argument → Tool** | Ein per Prompt-Injection gesteuerter Agent exfiltriert ein Secret über ein legitimes Tool (Token in ein Issue posten). | **ungeschützt** |

Die erste Richtung ist die wichtigere, weil sie heute komplett offen ist und der Schaden sich
nicht zurücknehmen lässt: Was einmal im Kontext war, ist beim Modellanbieter gewesen.

## Entscheidung (Kern)

Eine **Guardrail-Stufe im `ToolInvoker`** — dem einzigen Weg zu einem Tool-Call (ADR-0008), womit
MCP-, REST- und UI-Fassade in einem Zug abgedeckt sind. Kein neues Produkt, kein zweiter Pfad:
MCP-MCP bleibt ein MCP-Gateway, die Erkennung ist ein Filter darin.

Regeln liegen in der Datenbank, werden gecacht und zur Laufzeit getauscht — dasselbe
Write-Through-Muster wie `RedactionRuleStore` und `ToolDescriptionOverrideStore`, also
**hot-swappable ohne Neustart**, konsistent mit dem Rest des Produkts.

### Pipeline

```
Größenobergrenze prüfen        → darüber gar nicht scannen, Policy entscheidet
  ↓
SearchValues-Multiscan         ~0,4 µs — überspringt im Normalfall alles Weitere
  ↓
je Regel: IndexOf(Keyword)     Ordinal, ~0,4 µs
  ↓
Regex: NonBacktracking + Timeout   nur für Regeln mit Keyword-Treffer
  ↓
Befund → Aktion je nach Richtung und Modus
```

### Regex-Ausführung

`RegexOptions.NonBacktracking` **plus** `matchTimeout`. Gemessen (.NET 10, 50 Regeln, 10 KB JSON):

| Variante | Zeit |
|---|---|
| 50× `Compiled` | 137 µs |
| 50× `NonBacktracking` | **92 µs** |
| 50× `NonBacktracking` + Keyword-Prefilter | **50 µs** |

Zwei Messergebnisse, die gegen die Intuition laufen und das Design bestimmen:

- **NonBacktracking ist hier schneller als `Compiled`**, nicht langsamer. ReDoS-Sicherheit kostet
  also keine Performance. Preis ist Speicher: ~530 KB pro Regel, einmalig bei der Konstruktion,
  plus 106 ms Bauzeit für 50 Regeln — beides gehört zwingend in den Config-Reload, nie in den Request.
- **`OrdinalIgnoreCase` im Vorfilter kostet Faktor 10** (21 µs statt 0,4 µs pro Suche). Vorfilter
  immer `Ordinal`; Case-Insensitivität gehört in den Regex (`(?i)`).

Der Latenzbeitrag liegt bei ~50 µs, also **0,7 % des 7-ms-Budgets** (NFR-01). Skalierung ist linear
in der Eingabegröße — deshalb ist die **Größenobergrenze die einzige wirklich nötige**
Performance-Maßnahme; ein 10-MB-Ergebnis läge extrapoliert bei 50–90 ms.

### Regelbasis

**gitleaks** (MIT, 222 Regeln) als Startpunkt. Zwei Gründe: die Lizenz passt zu NFR-10, und
gitleaks-Muster sind für RE2 geschrieben, das dieselben Konstrukte nicht unterstützt wie
NonBacktracking — sie sind damit **strukturell kompatibel**.

**trufflehog scheidet aus: AGPL-3.0.** Bei einem Netzwerkdienst greift §13, das verträgt sich nicht
mit NFR-10. Das ist eine harte lizenzrechtliche Feststellung, keine Präferenz.

### Befunde dürfen den Fund nicht enthalten

Ein Guardrail-Report speichert **Fingerabdruck (Hash), Regel-Id, Offset und Länge** — niemals den
Klartext-Treffer. Sonst kopiert die Secret-Erkennung Secrets in ein zweites, oft schwächer
geschütztes System. LiteLLM hatte genau diesen Vorfall: Guardrail-Antworten trugen Rohdaten in
Spend-Logs und OTel-Traces, mit anschließender Empfehlung zur Credential-Rotation.

### Probelauf ist Pflicht, nicht Komfort

Jede Regel startet im Modus **Beobachten**: Treffer werden gezählt und auditiert, der Call läuft
durch. Erst nach Sichtung wird scharfgeschaltet. Ohne das ist der erste Falsch-Positiv-Treffer ein
Produktionsausfall — und die Regel wird danach nie wieder eingeschaltet.

## Zu entscheiden

### E1 — Was passiert bei einem Treffer, je Richtung?

Vorschlag, aus der Asymmetrie oben abgeleitet:

| Richtung | Vorschlag | Begründung |
|---|---|---|
| Argument → Tool | **Blockieren** | Ein Fehlalarm kostet einen fehlgeschlagenen Tool-Call. Das Secret verlässt sonst die Organisation. |
| Ergebnis → Agent | **Sichtbar maskieren** | Stilles Ersetzen ist gefährlicher als es aussieht: Der Agent rechnet mit korrupten Daten weiter und scheitert unvorhersehbar. Der Marker muss für das Modell lesbar sein („hier wurde etwas entfernt"). |

Alternative: beides nur melden. Dann bleibt der Gateway bei „protokolliert", was der Ausgangslage
entspricht.

### E2 — Freitext-Regex in der UI: ja oder eingeschränkt?

**Der wichtigste Befund der Recherche, und er kippt eine Annahme.** Microsoft schreibt ausdrücklich:

> „The .NET regular expression engine does not offer protection against untrusted **patterns** […]
> Features such as time-out values and `RegexOptions.NonBacktracking` […] are not intended as a
> security boundary against malicious patterns."

`NonBacktracking` schützt gegen teure **Eingaben**, nicht gegen bösartige **Muster**. Gemessen:
Muster wie `(?:a{1000}){1000}` laufen unter `Compiled`+Timeout durch, weil der Einzelmatch unter
der Schwelle bleibt — bei 50 Regeln × N Requests summiert sich das trotzdem.

Ein Freitextfeld für Regex ist damit faktisch: **„Admin darf Rechenzeit und Speicher im
Gateway-Prozess verbrauchen."** Das ist vertretbar — Admins können ohnehin stdio-Upstreams mit
beliebigem Kommando anlegen (ADR-0005) —, aber es muss bewusst entschieden und dokumentiert sein,
nicht wegtechnisiert werden.

Optionen:

- **(a) Freitext, nur für Admin-Rolle.** Konsistent mit ADR-0005. Validierung beim Speichern
  (Kompilieren, Musterlänge begrenzen, `NotSupportedException` als Formularfehler) und Konstruktion
  außerhalb des Hot Paths.
- **(b) Geführter Editor:** Präfix + Zeichenklasse + Länge als Formularfelder, daraus wird der
  Regex generiert. Genau das empfiehlt die MS-Doku als Alternative. Deckt die meisten
  Secret-Muster ab und schließt die Klasse aus.
- **(c) Beides:** geführt als Standard, Freitext hinter einem ausdrücklichen Schalter.

*Empfehlung: (c).* Der geführte Weg deckt den Normalfall; wer wirklich Freitext braucht, trifft
eine sichtbare Entscheidung.

### E3 — Entropie-Heuristik in v1?

**Empfehlung: nein, oder ausschließlich als Filter hinter einem Regex-Treffer.**

Belege gegen den Standalone-Einsatz — eigene Berechnung, je 2000 Stichproben:

| Typ | Ø Entropie | über Schwelle 3,0 |
|---|---|---|
| Git-Commit-SHA (40 hex) | 3,70 | **100 %** |
| UUIDv4 ohne Bindestriche | 3,70 | **~100 %** |
| SHA-256 | 3,82 | **100 %** |

Der Hex-Zweig ist praktisch ein **Hash- und UUID-Detektor** — genau die Formen, aus denen
Tool-Ergebnisse von Git-, Issue-, CI- und Storage-Servern bestehen. Die Studie *SecretBench*
(818 Repos, 97 479 gelabelte Secrets) misst den Unterschied: Entropie **als Filter** (gitleaks)
46 % Precision, Entropie **als Detektor** (trufflehog) **6 %**. Faktor 7,5.

Für ein Gateway, das im Zweifel blockiert, ist Precision mehr wert als Recall.

### E4 — Was gilt oberhalb der Größenobergrenze?

Ein Ergebnis über der Grenze wird nicht gescannt. Dann gilt entweder „durchlassen, aber
kennzeichnen" oder „blockieren". Ersteres ist ein blinder Fleck, letzteres bricht legitime
Groß-Ergebnisse. *Empfehlung: durchlassen und im Audit als ungeprüft markieren* — kombiniert mit
FR-16 (Ergebnis-Kürzung), das die Größe ohnehin begrenzen kann.

## Konsequenzen

- Der Gateway wird vom reinen Beobachter zur Kontrollinstanz. Das ist der Zweck — und zugleich ein
  neues Ausfallrisiko: Eine falsch gesetzte Regel kann legitime Arbeit blockieren. Deshalb E1 und
  der Pflicht-Probelauf.
- **Der Schutz ist gut, nicht vollständig.** Mustererkennung fängt, was ein Muster hat
  (`AKIA…`, `ghp_…`, `sk-ant-…`, PEM-Blöcke). Ein 32-stelliges Zufallspasswort ist von einer
  Datei-Id nicht unterscheidbar. Das gehört so in die Betriebsdoku, sonst verlässt sich jemand darauf.
- Speicher: ~530 KB je Regel. Bei 222 gitleaks-Regeln wären das ~118 MB — **spricht dafür, mit
  einer kuratierten Teilmenge zu starten** statt den Regelsatz komplett zu laden.
- Bekannte Falsch-Positiv-Treiber, die auf „beobachten" gehören statt auf „blockieren": **JWT**
  (Header und Payload sind nur Base64, öffentliche ID-Tokens matchen identisch) und generische
  32-Hex-Muster.

## Nicht Teil dieser Entscheidung

- **Modellgestützte Inhaltsprüfung.** Der Hot Path liegt bei 7 ms; ein Moderationsaufruf kostet
  das Hundertfache, kann selbst ausfallen und ist nicht nachvollziehbar. Widerspricht dem
  Kernversprechen des Gateways.
- **LLM-Proxy-Funktion.** MCP-MCP routet Tool-Calls, keine Completions. Dafür gibt es gepflegte
  Werkzeuge; zwei Produkte in einem verwässern beides.
