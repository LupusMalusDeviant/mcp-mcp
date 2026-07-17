# Security Policy

## Supported versions

MCP-MCP is in **pre-release development**. There are no supported release versions yet; security fixes land on `main`. Once v1.0 ships, this table will list supported versions.

| Version | Supported |
|---|---|
| `main` (unreleased) | ✅ best effort |

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Use [GitHub private vulnerability reporting](https://github.com/LupusMalusDeviant/mcp-mcp/security/advisories/new) ("Report a vulnerability" under the Security tab). You will get an initial response within **7 days**. Please include reproduction steps and, if possible, the affected component (gateway pipeline, RBAC, audit, upstream connectors, Web UI).

## Security model — what you should know before deploying

MCP-MCP is a security-relevant component by design: it terminates every tool call and holds credentials for all connected upstream servers. The intended posture (see `docs/adr/`, German):

- **Credential concentration:** Upstream credentials are stored encrypted (ASP.NET Data Protection); agent API keys are stored only as hashes. The gateway host is still a high-value target — harden it accordingly (dedicated user, TLS via reverse proxy, restricted network exposure).
- **stdio upstreams run with gateway privileges** (v1, [ADR-0005](docs/adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md)): there is **no sandbox** between the gateway and stdio MCP-server child processes. Only connect MCP servers you trust, exactly as you would when attaching them to an agent directly. Container-per-upstream isolation is a planned v2 feature.
- **Default-deny RBAC:** agents see and reach only what a role explicitly grants. If you observe a tool being visible or callable without a grant, that is a vulnerability — please report it.
- **Audit integrity:** every call (including denied ones) is logged with secret redaction. Bypasses of redaction or of audit logging are vulnerabilities.
- **Untrusted input:** tool descriptions and results from upstream servers are treated as untrusted content (encoding in the UI, no execution). Injection paths through upstream metadata are in scope for reports.

## Out of scope

- Vulnerabilities in connected third-party MCP servers themselves
- Deployments that expose the gateway without TLS/reverse proxy despite the documentation
- Denial of service through deliberately misconfigured self-hosted instances
