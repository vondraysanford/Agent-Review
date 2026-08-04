# AgentReview — Multi-Agent Code Review in C#/.NET with MCP

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/Language-C%23-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![MCP](https://img.shields.io/badge/MCP-Model%20Context%20Protocol-blue)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Status: Planning](https://img.shields.io/badge/Status-Planning-orange)](#build-plan)

A multi-agent system where specialized AI agents collaborate to review pull requests. An orchestrator routes a code diff to independent agents (one for code quality, one for security, one for documentation), then synthesizes their findings into a single ranked review. Agents reach real tools (Roslyn analyzers, Semgrep, the GitHub API) through the Model Context Protocol.

Built in C#/.NET. That choice is the point: agentic tooling is overwhelmingly Python, and this project proves the same patterns work in the Microsoft ecosystem. It is the sibling of [DocQuery](https://github.com/vondraysanford/docquery), which made the same argument for RAG.

> **✅ Status: planning.** Nothing below is built yet. This README is the build plan.

**The checkboxes in this README are an honesty contract. No box gets checked until the feature works end-to-end, verified in a terminal or a running app.**

## Why This Project

Single-prompt LLM calls are a solved problem. The harder skill is getting multiple agents to coordinate reliably: routing work, calling real tools, and reconciling conflicting outputs. This project demonstrates that skill, and it turns an MCP certification into a working artifact instead of a resume line.

It also has a professional anchor: I built AI-driven pull-request and change-request automation used daily by a division at my current employer. AgentReview is the public, from-scratch version of that idea.

## What It Does (when complete)

- Accepts a GitHub pull request URL or a raw diff.
- Dispatches the diff to three specialized agents running concurrently:
  - **Quality Agent** — flags complexity, naming, duplication, and maintainability issues.
  - **Security Agent** — checks for injection risks, secret leakage, unsafe dependencies, and auth mistakes.
  - **Docs Agent** — identifies missing or outdated documentation and drafts updates.
- Each agent reaches real tools via MCP: a custom static-analysis MCP server (Roslyn + Semgrep) and the GitHub MCP server for surrounding file context.
- An orchestrator deduplicates findings, resolves conflicts, ranks by severity, and emits one consolidated review.
- Full observability: every agent's decisions, tool calls, latency, and token usage are traced.

## Tech Stack

**Agent Orchestration**
- C#/.NET 10 — plain orchestration first (concurrent fan-out, keyed DI for agent registration), the same pattern that powers DocQuery's provider routing. A framework (Semantic Kernel / Microsoft agent tooling) gets adopted only if it earns its place.
- Swappable LLM providers behind one interface, reusing the DocQuery pattern: Azure OpenAI and/or Anthropic API, with local Ollama as a dev option. Nothing network-related hardcoded.

**Tool Integration (MCP)**
- Official MCP C# SDK — building the custom server and the agent-side clients.
- **Static-analysis MCP server (custom, the centerpiece)** — wraps Roslyn analyzers for C# diffs and Semgrep for polyglot security rules, exposed as MCP tools.
- GitHub MCP server (existing) — repository and PR context beyond the changed lines.

**Observability**
- OpenTelemetry — first-class in .NET, per-agent traces of decisions, tool calls, latency, tokens.
- Structured JSON logging so every run is queryable.

**Serving**
- ASP.NET Core minimal API — a `/review` endpoint taking a PR URL or raw diff.
- Docker Compose — orchestrator plus MCP servers as one stack.
- React + Vite dashboard (optional, last).

## Architecture

```
                       PR URL / diff
                            │
                            ▼
                     ┌─────────────┐
                     │ Orchestrator│  (C# fan-out + synthesis)
                     └──────┬──────┘
            ┌───────────────┼────────────────┐
            ▼               ▼                ▼
      Quality Agent   Security Agent     Docs Agent
            │               │                │
            ▼               ▼                ▼
        MCP tools       MCP tools        MCP tools
    (Roslyn, GitHub) (Semgrep, Roslyn)   (GitHub)
            └───────────────┼────────────────┘
                            ▼
                  Synthesis + ranking
                            │
                            ▼
                  Consolidated review  ──►  API response / dashboard
```

## Build Plan

Phases are sized for weekends. One checklist item at a time; small, explainable commits.

### Phase 1 — Static-Analysis MCP Server (the MCP-first phase)

The first artifact is a working MCP server, not an agent. A server drops into Claude Code on day one and is demoable by itself; a tool-less agent is just a prompt.

- [x] `AgentReview.McpServers.StaticAnalysis` project: MCP server via the official C# SDK (verified 2026-08-02: answers `initialize` over stdio, connects in Claude Code via `.mcp.json`, zero tools yet)
- [x] `analyze_csharp(code)` tool backed by Roslyn analyzers, returning structured findings (verified 2026-08-03 in Claude Code: compiler diagnostics and NetAnalyzers CA rules, planted issues return CS0219, CS0162, and CA2000 with positions)
- [x] `run_semgrep(code, ruleset)` tool for polyglot security rules (verified 2026-08-04 in Claude Code: planted SQL injection returns csharp-sqli with position; ruleset defaults to p/default)
- [ ] Verified end-to-end inside Claude Code: tools listed, calls succeed, findings come back structured
- [ ] Demo GIF of Claude Code using the server

### Phase 2 — One Agent, Real Tools

- [ ] Shared finding schema as a C# record (issue, file, line, severity, suggestion); every agent returns this shape, locked early
- [ ] Quality Agent: takes a diff, calls the static-analysis MCP tools, returns schema-valid findings grounded in analyzer output
- [ ] GitHub MCP server wired in for surrounding file context
- [ ] Test harness with 2 or 3 controlled sample diffs; every tool call logged

### Phase 3 — The Other Two Agents

- [ ] Security Agent (Semgrep-backed, plus LLM reasoning over the diff)
- [ ] Docs Agent (GitHub-context-backed)
- [ ] All three run independently and return the identical schema

### Phase 4 — Orchestration and Synthesis

- [ ] Orchestrator fans one diff out to all three agents concurrently
- [ ] Synthesis: deduplicate overlapping findings, resolve conflicts with a stated rule or an LLM arbiter, rank by severity
- [ ] End-to-end test on a diff with planted issues produces one coherent, ordered review

### Phase 5 — Observability and Evals

- [ ] OpenTelemetry tracing: per-agent decisions, tool calls, latency, token usage
- [ ] Run summary per review: total latency, total tokens, tool-call success rate, cost
- [ ] Eval harness: seeded PRs with planted bugs; report per-agent precision and agreement with a human review
- [ ] Results table in this README filled with measured numbers

### Phase 6 — API and Ship

- [ ] ASP.NET Core `/review` endpoint (PR URL or raw diff)
- [ ] `docker compose up` brings up orchestrator and MCP servers together
- [ ] Sample reviews committed to the repo; demo GIF in this README
- [ ] Optional React dashboard rendering the review and the per-agent trace

## Results To Report (measured, not estimated)

| Metric | Value |
|---|---|
| Agreement rate vs. human review on a labeled PR set | _pending_ |
| Per-agent precision on planted bugs | _pending_ |
| End-to-end review latency | _pending_ |
| Tokens and cost per review | _pending_ |
| MCP tool-call success rate | _pending_ |

## Demo Decision (made up front)

There is no public review endpoint in v1, deliberately. A public API that runs three LLM agents against arbitrary PRs is a cost and abuse magnet. The public proof is: the demo GIF, committed sample reviews, and the eval numbers above. A likely later step: a GitHub Action that runs AgentReview on this repository's own pull requests, which is a live demo with a naturally bounded cost.

## Cost Guardrails

Token caps per agent enforced in code, a per-review budget the orchestrator refuses to exceed, and cost per review reported in the results table. Cloud spend gets a stated budget with alerts before Phase 2 begins, the same discipline that kept DocQuery's four phases near one dollar.

## Repository Layout (target)

```
agent-review/
├── src/
│   ├── AgentReview.Orchestrator/        # fan-out, synthesis, ranking
│   ├── AgentReview.Agents/              # quality, security, docs agents + shared schema
│   ├── AgentReview.McpServers.StaticAnalysis/  # Roslyn + Semgrep MCP server
│   └── AgentReview.Api/                 # ASP.NET Core /review endpoint
├── tests/
│   └── AgentReview.Tests/               # schema, synthesis, and harness tests
├── evals/                               # seeded PRs, labels, scoring scripts
├── dashboard/                           # React review viewer (optional, last)
├── docker-compose.yml
├── docs/
└── README.md
```

## License

MIT
