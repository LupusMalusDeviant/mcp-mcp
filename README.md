# MCP-MCP

[![ci](https://github.com/LupusMalusDeviant/mcp-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/LupusMalusDeviant/mcp-mcp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

**A self-hosted meta-MCP gateway on .NET** — connect one endpoint to your agents, and manage all your MCP servers behind it.

> **[Release v0.5.0](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.5.0)** — the first valid release. Feature-complete and thoroughly tested (**317 tests**, 86.9 % core coverage, against SQLite *and* real PostgreSQL), but **never run in production**. The version is deliberately below 1.0: it works and is proven by tests, not yet by operational uptime. 1.0 follows once there's real-world runtime behind it.

> [!CAUTION]
> **The earlier v1.0.0 and v1.1.0 tags are retracted — do not use them.**
> On the lazy path (`invoke_tool`) they wrote tool arguments to the audit log **unredacted**, so
> credentials passed through that path ended up in the database in plaintext. If you ran either
> release, check the `AuditEvents` table, rotate anything exposed, and delete the affected rows.
> That v0.5.0 carries the *lower* number is intentional — the 1.x line was never a valid release,
> and 0.5 describes the maturity more honestly. See the [threat model](docs/security/threat-model.md).

> **What it does.** The gateway aggregates MCP servers and imported REST APIs, speaks MCP **and** REST, enforces RBAC + rate limits, audits every call, saves context tokens via profiles/meta-tools (≥ 96 % reduction in the reference setup), hot-swaps servers without restart, federates to other gateways, and ships a Blazor admin UI. It **scans tool arguments and results for credentials** and blocks them before they reach the upstream or the model's context window ([ADR-0011](docs/adr/0011-secret-erkennung-als-guardrail.md)); it gates chosen tools behind **human approval** ([ADR-0012](docs/adr/0012-approval-flows-asynchron.md)) and accepts **signed webhook triggers** ([ADR-0013](docs/adr/0013-webhook-trigger.md)). Dockerized (< 300 MB, non-root, amd64 **and** arm64), [formal NFR-01 benchmark](docs/acceptance/performance.md) on reference hardware: **p95 = 7.3 ms** per call, ~6400 calls/s, 0 errors under 20 sessions / 100 in-flight.
>
> Pattern-based secret detection catches what has a pattern (`AKIA…`, `ghp_…`, PEM blocks); a random 32-char password is indistinguishable from a file id. The guardrail is an additional layer, not a substitute for keeping secrets out of tool results.
>
> A security audit was performed before v1.0, but it missed the redaction defect above — the gap was
> only found later by an independent requirement-versus-code review. Treat the audit as one input,
> not a clean bill of health.

## The problem

Every agent × every MCP server = a config entry, a credential copy, and a pile of tool schemas eating your context window. No central log answers *which agent called which tool with which arguments*, no access control separates read-only agents from write-capable ones, and every server change means restarting agent sessions.

## What MCP-MCP does about it

MCP-MCP is a reverse proxy for the Model Context Protocol: to your agents it is a single MCP server, to your MCP servers it is a single client. Every call flows through one enforcement pipeline — which is what makes the features below possible *by construction*, not by convention:

| Feature | How |
|---|---|
| 🔌 **One endpoint per agent** | All upstream servers aggregated behind one Streamable-HTTP endpoint, tools namespaced `server__tool` |
| 🔄 **Hot-swappable servers** | Add/remove/reconfigure upstreams at runtime; connected agents get `tools/list_changed`, no restarts |
| 🪙 **Token saving** | Per-agent profiles: pin frequently used tools with full schemas, expose the long tail via `search_tools` / `describe_tool` / `invoke_tool` meta-tools (target: ≥ 80 % schema-token reduction) |
| 🌉 **API ↔ MCP bridge** | Every tool is also callable via REST (`POST /api/v1/tools/{name}/invoke`, generated OpenAPI 3.1); existing REST APIs can be imported from an OpenAPI spec and appear as MCP tools |
| 📜 **Full audit log** | Who / what / when / result for every call — including denied ones — with secret redaction before persistence |
| 🔐 **RBAC** | Per-agent API keys, roles with server/tool/action-level grants, default-deny, visibility follows permission |
| 🖥️ **Web UI** | Blazor admin panel: server management, tool explorer, RBAC, live dashboard, log search, token cockpit |
| 📦 **Central skill distribution** | Versioned text assets (skills/prompts/instructions) served to all agents as MCP prompts/resources |

## Architecture at a glance

```
Agents (MCP) ──┐
REST clients ──┼──►  AuthN ─► RBAC ─► validation ─► routing ─► timeout ─► audit  ──►  upstream servers
Web UI ────────┘                    (one pipeline, no bypass)                          (stdio / HTTP / OpenAPI / CLI)
```

Built on the [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk). Runs as a single Docker container (or bare `dotnet run`), SQLite by default, PostgreSQL optional.

## Quickstart (Docker)

```bash
docker compose up -d
docker compose logs mcpmcp     # read the bootstrap credentials — shown ONCE, on first start
```

First start prints an agent **API key** (`mcpk_…`) and a **UI password** for user `admin`. Then:

```bash
# Web UI: http://localhost:8080  (login: admin + the printed password)

# Connect an agent:
claude mcp add --transport http mcpmcp http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

Add upstream MCP servers, roles and profiles from the UI or the REST API — no config files. Always run behind a TLS reverse proxy in production (see [docs/operations.md](docs/operations.md)).

## Building from source (developers)

```bash
git clone https://github.com/LupusMalusDeviant/mcp-mcp.git
cd mcp-mcp
dotnet build
dotnet test
dotnet run --project src/McpMcp.Server   # http://localhost:5000
```

Requires the .NET 10 SDK. The integration tests spawn reference MCP servers (`tests/McpMcp.TestServers/*`) as real stdio/HTTP processes.

## Documentation

The full design documentation lives in [`docs/`](docs/) — written in **German**:

- [`docs/prd/`](docs/prd/) — requirements (Lastenheft): 41 functional requirements, NFRs, acceptance criteria
- [`docs/adr/`](docs/adr/README.md) — 14 architecture decision records (proxy architecture, token strategy, RBAC model, CLI transport, …)
- [`docs/plans/`](docs/plans/) — implementation plan (Pflichtenheft): work packages with definitions of done, test strategy, coding rules

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M1 "Skeleton talks" | Foundation, upstream connectors, supervisor with crash-restart | ✅ done |
| M2 "Enforcement stands" | Catalog, RBAC, audit, MCP endpoint, hot-swap | ✅ done |
| M3 "Both bridges carry" | REST facade, OpenAPI import | ✅ done |
| M4 "v1.0" | Web UI, hardening, security audit, Docker release | ✅ done |
| M5 "Gap closure" | 23 planned-but-missing items found by independent requirement-versus-code audits, incl. the redaction defect | ✅ done |
| M6 "Guardrails" | Secret detection in the invoker — blocks credentials in tool arguments and results, runtime-editable rules ([ADR-0011](docs/adr/0011-secret-erkennung-als-guardrail.md)) | ✅ done |
| M7 "All optional reqs" | Every Kann-requirement decided: approval flows ([ADR-0012](docs/adr/0012-approval-flows-asynchron.md)) and signed webhook triggers ([ADR-0013](docs/adr/0013-webhook-trigger.md)) built; FR-04 documented as a deviation | ✅ done |
| M8 "v0.5.0 release" | [Acceptance against the actual state](docs/acceptance/v1.2.md), then the first valid release — deliberately < 1.0 pending operational uptime | ✅ [released](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.5.0) |
| CLI transport | CLI programs as a fourth upstream transport — shell-free execution (`ArgumentList`), fixed-binary allowlist, governed by the same pipeline ([ADR-0014](docs/adr/0014-cli-programme-als-upstream-transport.md)). UI form + structured per-command schemas still open | 🧪 prototype (on `main`, not in a release) |
| M9 "1.0" | Real-world operation over time — the one thing tests can't provide | ⏳ open |

## License

[MIT](LICENSE)
