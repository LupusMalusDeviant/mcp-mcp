# MCP-MCP

[![ci](https://github.com/LupusMalusDeviant/mcp-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/LupusMalusDeviant/mcp-mcp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

**A self-hosted meta-MCP gateway on .NET** — connect one endpoint to your agents, and manage all your MCP servers behind it.

> ✅ **v1.0.** The gateway is complete: it aggregates MCP servers and imported REST APIs, speaks MCP **and** REST, enforces RBAC + rate limits, audits every call, saves context tokens via profiles/meta-tools (≥ 96 % reduction in the reference setup), hot-swaps servers without restart, federates to other gateways, and ships a Blazor admin UI. Security-audited, Dockerized (chiseled, < 300 MB), 200+ tests green on Windows + Linux (persistence also against real PostgreSQL). See [docs/acceptance/v1.md](docs/acceptance/v1.md).

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
Web UI ────────┘                    (one pipeline, no bypass)                          (stdio / HTTP / OpenAPI)
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
- [`docs/adr/`](docs/adr/README.md) — 8 architecture decision records (proxy architecture, token strategy, RBAC model, …)
- [`docs/plans/`](docs/plans/) — implementation plan (Pflichtenheft): work packages with definitions of done, test strategy, coding rules

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M1 "Skeleton talks" | Foundation, upstream connectors, supervisor with crash-restart | ✅ done |
| M2 "Enforcement stands" | Catalog, RBAC, audit, MCP endpoint, hot-swap | ✅ done |
| M3 "Both bridges carry" | REST facade, OpenAPI import | ✅ done |
| M4 "v1.0" | Web UI, hardening, security audit, Docker release | ✅ done |

## License

[MIT](LICENSE)
