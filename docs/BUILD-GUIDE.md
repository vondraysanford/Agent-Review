# AgentReview — A-to-Z Build Guide (.NET Edition)

A step-by-step path from empty folder to a running multi-agent code-review system with real MCP tool integration. Plan for roughly 3 to 4 focused weekends. Build incrementally: one working MCP server first, then one agent, then the rest, then orchestration. Never wire up all three agents at once.

The core lesson this project teaches: **orchestration is the hard part, not the prompts.** Getting agents to route work, call real tools, and reconcile conflicting outputs reliably is the skill worth demonstrating.

This guide supersedes the original Python plan (`02_AgentReview_A-Z_Guide.md`). The stack is C#/.NET throughout: that keeps the portfolio thesis consistent with DocQuery ("the patterns work in the Microsoft ecosystem") and aims the static analysis at the language I know deepest.

---

## Phase 0 — Prerequisites and Setup (half a weekend)

1. .NET 10 SDK only on this machine; every project targets `net10.0`.
2. Get LLM API access (Azure OpenAI and/or Anthropic) and a GitHub personal access token with repo read scope. Secrets live in gitignored config or user-secrets, never in code, never in images.
3. Initialize the repo from the layout in `docs/README.md`. Commit the README as the build plan before any code.
4. Add packages with exact pinned versions, each one approved before install: the MCP C# SDK, the LLM SDK(s), OpenTelemetry. Semgrep installs as a CLI (also approved first).
5. Skim the MCP spec so the client/server model is fresh: a server exposes tools, agents are clients that call them. The certification covered this; now it gets applied.

**Checkpoint:** one successful LLM API call from a console app, and an authenticated GitHub API call.

---

## Phase 1 — The Static-Analysis MCP Server (1 weekend)

MCP-first, deliberately. A working MCP server is demoable in Claude Code the day it exists. An agent without tools is just a prompt.

1. Create `AgentReview.McpServers.StaticAnalysis` using the official MCP C# SDK.
2. Expose `analyze_csharp(code)`: run Roslyn analyzers against the supplied code and return structured findings (rule id, message, file, line, severity).
3. Expose `run_semgrep(code, ruleset)`: invoke the Semgrep CLI, parse its JSON output, return the same structured shape.
4. Log every tool invocation: arguments in, findings out, duration.
5. Verify inside Claude Code: register the server, list tools, call each one against a snippet with planted issues, and confirm structured findings come back.

**Checkpoint:** Claude Code calls both tools successfully against code you control. Record the demo GIF now while the moment is clean.

**Pitfall specific to this phase:** resist making the server "smart." It runs analyzers and returns facts. Judgment belongs to the agents.

---

## Phase 2 — One Agent, Real Tools (1 weekend)

1. Define the shared finding schema first, as a C# record in `AgentReview.Agents`: issue, file, line, severity, suggestion, source (which analyzer or LLM produced it). Every agent returns exactly this shape. Lock it now; synthesis in Phase 4 depends on it.
2. Build the **Quality Agent**: input is a diff string, output is a list of schema-valid findings. It calls `analyze_csharp` through MCP, adds LLM reasoning over the diff (complexity, naming, duplication), and merges both into one findings list.
3. Wire in the existing GitHub MCP server so the agent can fetch surrounding file context, not just the changed lines.
4. Build a small test harness that runs the agent against 2 or 3 sample diffs committed to the repo. Assert schema validity, log every tool call.

**Checkpoint:** one agent reliably turns a diff into schema-valid findings that are visibly grounded in real tool output (the logs prove the MCP calls happened).

---

## Phase 3 — The Other Two Agents (half a weekend)

Replication is cheap once the pattern exists.

1. **Security Agent**: same structure, focused on injection, secrets, unsafe dependencies, auth mistakes; backed by `run_semgrep` plus LLM review of the diff.
2. **Docs Agent**: identifies missing or outdated documentation and drafts updates; uses the GitHub MCP server for context.
3. Register all three as keyed services (the DocQuery provider pattern) so the orchestrator can resolve them uniformly.

**Checkpoint:** three agents run independently, each returning the identical schema.

---

## Phase 4 — Orchestration and Synthesis (1 weekend)

This is what elevates the project from "three prompts" to "a system."

1. Shared run state: the diff, per-agent findings, the final review. A plain C# class, not a framework.
2. Orchestrator: receive a diff, fan out to the three agents concurrently, collect results as they complete, tolerate one agent failing without sinking the run.
3. Synthesis logic, in order:
   - **Deduplicate** findings that multiple agents flagged (same file, line, and overlapping issue).
   - **Resolve conflicts** with a stated rule first; add an LLM arbiter only if the rule proves insufficient.
   - **Rank by severity** and emit one consolidated, ordered review.
4. Test end-to-end on a diff with planted issues; the consolidated output must be coherent and complete.

**Checkpoint:** one diff in, one deduplicated, severity-ranked review out, assembled from all three agents.

---

## Phase 5 — Observability and Evals (half a weekend, plus scoring time)

Tracing is what makes this look like engineering instead of a demo, and evals are what make the claims credible.

1. OpenTelemetry across orchestrator and agents: per-agent decisions, tool calls, latency, token usage.
2. Structured JSON logs so any run is queryable after the fact.
3. Per-review summary: total latency, total tokens, tool-call success rate, cost in dollars.
4. Eval harness in `evals/`: a set of seeded PRs with planted, labeled bugs. Score per-agent precision and overall agreement with a human review. Fill the README results table with the measured numbers.

**Checkpoint:** for any review, a full trace of what each agent did and what it cost, plus a filled results table.

---

## Phase 6 — API, Compose, Ship (half a weekend)

1. ASP.NET Core minimal API: `/review` accepts a PR URL or raw diff and returns the consolidated review.
2. `docker-compose.yml` brings up the API and the MCP servers as one stack.
3. Commit sample reviews and the demo GIF to the repo. Finalize the README: all boxes honestly checked, results table filled.
4. Optional: the React dashboard rendering the review plus the per-agent trace. Nice, not necessary; the traces and numbers carry the project.

**Final checkpoint:** `docker compose up`, post a PR URL, receive a full multi-agent review, and the README shows real evaluation numbers.

---

## Common Pitfalls

- **Building all three agents before one works.** Get a single agent solid end to end first.
- **Fake tools.** The whole point is real MCP tool calls; never stub them with hardcoded responses.
- **Inconsistent output schemas.** If agents return different shapes, synthesis becomes a nightmare. Lock the record type in Phase 2.
- **No conflict resolution.** Real reviews have disagreements; handling them is what shows orchestration maturity.
- **Skipping tracing.** Without observability the project looks like a toy. The traces are a major part of the value.
- **Framework-first orchestration.** Reaching for an agent framework before plain C# fan-out has been tried hides the very skill this project exists to demonstrate.
- **Unbounded spend.** Token caps per agent and a per-review budget go in before the eval runs, not after the first surprising bill.
- **A smart MCP server.** Analyzers report facts; agents judge. Keep the boundary clean.
