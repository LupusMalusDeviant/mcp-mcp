# ADR-0013: Webhook-Trigger — signiert, ein Tool, feste Identität

- **Status:** Akzeptiert
- **Datum:** 2026-07-20
- **Betrifft:** FR-20, ADR-0006 (RBAC), ADR-0008 (Invocation-Kern), ADR-0011 (Guardrail)

## Kontext

FR-20 verlangt, dass eingehende Webhooks Tool-Aufrufe auslösen. Das ist der erste **von außen
unauthentifiziert erreichbare** Eingang des Gateways — bisher ist alles hinter API-Key oder
UI-Cookie. Ein Webhook-Endpunkt, den jeder im Internet aufrufen kann, löst per Definition
Tool-Calls aus; ohne strenge Absicherung ist er ein Fernsteuerungs-Einfallstor.

Das Lastenheft nennt „Tool-**Ketten**". Ketten bringen jedoch ein ganzes neues Konzept mit:
Ergebnis-Weitergabe zwischen Schritten, Abbruchverhalten, Teil-Fehlschläge. Das ist ein eigenes
Orchestrierungs-Feature, kein Trigger.

## Entscheidung

Drei bewusste Einschränkungen, jede sicherheitsgetrieben:

### 1. Authentifizierung: HMAC-SHA256-Signatur pro Webhook

Jeder Webhook hat ein eigenes, beim Anlegen generiertes Secret. Der Absender signiert den
**rohen Request-Body** mit HMAC-SHA256; der Gateway rechnet die Signatur nach und vergleicht sie
**zeitkonstant** (`CryptographicOperations.FixedTimeEquals`). Das ist der Standard von GitHub,
Stripe und Slack.

Dazu **Replay-Schutz**: Der Absender schickt einen Zeitstempel-Header, der in die Signatur
eingeht; Anfragen außerhalb eines Fensters (Vorschlag: 5 min) werden abgewiesen. Ohne das könnte
ein einmal mitgeschnittener signierter Request beliebig oft wiederholt werden.

Kein statisches Bearer-Token: Ein Header, der einmal in einem Log oder Proxy landet, wäre ein
Dauerschlüssel ohne Replay-Schutz.

### 2. Umfang: genau ein Tool-Aufruf pro Webhook

Ein Webhook ist an **ein** Tool gebunden. Die Argumente werden aus dem Webhook-Payload gebildet
(feste plus aus dem Body gemappte Felder). Der Call läuft durch **dieselbe Pipeline** wie jeder
andere — RBAC, Argument-Validierung, Guardrail, Rate-Limit, Audit. Kein Sonderpfad.

Ketten sind **nicht** enthalten. Das ist eine dokumentierte Teilerfüllung von FR-20: Der Trigger
ist da, die Orchestrierung mehrerer Schritte ist v2. Ein Trigger, der die volle Sicherheitspipeline
durchläuft, ist mehr wert als eine Ketten-Engine, die eigene Fehlerpfade mitbringt.

### 3. Identität: fest pro Webhook

Jeder Webhook wird beim Anlegen an **genau eine** Identität gebunden. Deren Grants und Rate-Limits
gelten; im Audit erscheint sie als Aufrufer mit **`CallOrigin.Webhook`**. Ein Webhook kann damit
nie mehr auslösen, als seine Identität ohnehin dürfte — RBAC ist die Grenze, nicht der
Webhook-Code.

## Begründung

- **Die Angriffsfläche ist die RBAC-Grenze, nicht der Endpunkt.** Selbst wer ein Webhook-Secret
  erbeutet, kann nur das eine gebundene Tool im Rahmen der gebundenen Identität auslösen — und das
  landet vollständig im Audit.
- **Signatur über den rohen Body** ist die einzige Variante, die auch dann trägt, wenn ein
  Reverse-Proxy Header umschreibt: Der Body bleibt unangetastet.
- **Ein Tool statt Kette** hält das Feature auf einem prüfbaren Umfang und vermeidet, dass ein
  externer Trigger komplexe mehrstufige Seiteneffekte auslöst, die schwer zu überblicken sind.

## Konsequenzen

- Neuer `CallOrigin.Webhook` (Wert am Ende — persistierte Zahlen). Damit ist im Audit filterbar,
  was von außen getriggert wurde.
- Der Webhook-Endpunkt ist der einzige unauthentifizierte Pfad. Er ist eng: nur `POST`, nur mit
  gültiger Signatur, nur an ein registriertes Webhook-Id-Segment. Fehlende/falsche Signatur → 401,
  ohne Hinweis, ob die Id existiert (kein Enumerations-Leak).
- Das Secret wird einmal beim Anlegen im Klartext angezeigt (wie API-Keys) und danach nur als Hash
  gehalten — nein: HMAC braucht das Secret zum Nachrechnen, es liegt also **DataProtection-
  verschlüsselt** wie Upstream-Credentials, nicht als Hash.
- Rate-Limit der gebundenen Identität greift auch hier — ein Webhook kann nicht schneller feuern,
  als die Identität darf.
- FR-20 gilt als **umgesetzt mit dokumentierter Einschränkung** (Einzel-Tool statt Kette).

## Verworfen

- **Statisches Bearer-Token:** kein Replay-Schutz, ein Leck ist ein Dauerschlüssel.
- **Feste Tool-Ketten jetzt:** eigenes Orchestrierungs-Konzept, das die Angriffsfläche eines
  externen Triggers deutlich vergrößert; als v2 mit eigener ADR sinnvoller.
- **Gemeinsame Webhook-System-Identität:** würde alle Webhooks im Audit und in RBAC ununterscheidbar
  machen — genau die Nachvollziehbarkeit, die ein extern getriggerter Pfad am nötigsten hat.
