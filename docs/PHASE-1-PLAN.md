# Phase 1 Plan — Static-Analysis MCP Server

Working plan for Phase 1 of the build (see `README.md` for the full phase list). Nothing here is built yet. Checkboxes get ticked only when the item works end-to-end, verified in a terminal or in Claude Code.

## Goal

A working MCP server, `AgentReview.McpServers.StaticAnalysis`, exposing two tools:

- `analyze_csharp(code)` — runs Roslyn against the supplied C# code, returns structured findings.
- `run_semgrep(code, ruleset)` — runs the Semgrep CLI, parses its JSON output, returns the same structured shape.

**Checkpoint:** Claude Code lists both tools, calls each against a snippet with planted issues, and gets structured findings back. Demo GIF recorded while the moment is clean.

**Guardrail:** the server stays dumb. It runs analyzers and returns facts (rule id, message, line, severity). No LLM calls, no judgment, no summarizing. That belongs to the agents in Phase 2.

## Out of scope for Phase 1

- Agents, orchestration, LLM providers, API keys of any kind.
- The shared `Finding` record in `AgentReview.Agents` (locked in Phase 2). The server returns its own plain result shape; the Phase 2 schema will map from it.
- The GitHub MCP server, Docker, the ASP.NET API.

## Installs needing approval

⚠️ Per the working agreement, each of these gets a separate approval prompt before install. Versions below were checked against the registries on 2026-07-31; re-verify at install time.

**NuGet (nuget.org):**

| Package | Version | Publisher / source |
|---|---|---|
| `ModelContextProtocol` | 2.0.0 | Microsoft + Anthropic — github.com/modelcontextprotocol/csharp-sdk |
| `Microsoft.Extensions.Hosting` | 10.0.10 | Microsoft — github.com/dotnet/runtime |
| `Microsoft.CodeAnalysis.CSharp` | 5.6.0 | Microsoft — github.com/dotnet/roslyn |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.6.0 | Microsoft — github.com/dotnet/roslyn |
| `Microsoft.CodeAnalysis.NetAnalyzers` | 10.0.302 | Microsoft — github.com/dotnet/roslyn-analyzers (11.x is preview; stay on 10.x stable) |

**CLI (Homebrew):**

| Tool | Version | Source |
|---|---|---|
| `semgrep` | 1.172.0 | formula `semgrep` — github.com/semgrep/semgrep |

Note: `ModelContextProtocol` went 2.0 recently. Verify tool-registration API shapes against the official csharp-sdk README at build time rather than assuming the 1.x idiom.

## Design decisions

**Transport: stdio.** Claude Code launches the server as a child process. No ports, no HTTP, so the AirPlay port issue does not apply in this phase. Registered in Claude Code via `.mcp.json` (project scope) with the command `dotnet run --project src/AgentReview.McpServers.StaticAnalysis`.

**`analyze_csharp` implementation, two layers:**

1. Compiler diagnostics first: parse with `CSharpSyntaxTree`, build a `CSharpCompilation` against the default reference set, collect syntax and semantic diagnostics. This needs no analyzer loading and gives real findings (unused variables, unreachable code, type errors) on day one.
2. Analyzer rules second: load the NetAnalyzers assemblies via `AnalyzerFileReference` and run `CompilationWithAnalyzers` to add CA-rule findings (disposal, security, performance). If loading packaged analyzer DLLs at runtime turns out to be fiddly, layer 1 alone still satisfies the checkpoint; layer 2 becomes its own checklist item.

**`run_semgrep` implementation:** write the code to a temp file with a `.cs` extension, shell out to `semgrep scan --json --config <ruleset>`, parse the JSON `results` array, delete the temp file. `ruleset` defaults to a registry pack that works anonymously (start with `p/default`, confirm `p/csharp` availability at build time). Registry configs need network; if offline behavior matters later, we vendor a small local rules file into the repo, but that is not a Phase 1 requirement.

**Result shape (server-local, not the Phase 2 schema):**

```csharp
record AnalysisFinding(string RuleId, string Message, int Line, int Column, string Severity, string Source);
```

`Source` is `"roslyn"` or `"semgrep"`. Both tools return `AnalysisFinding[]` serialized as JSON.

**Logging:** built-in `ILogger`, JSON console formatter, written to stderr so it never corrupts the stdio protocol stream. Every tool invocation logs arguments in, finding count out, and duration.

## Session-sized checklist

One item per session, small diffs, `dotnet build` green before every commit.

- [x] **1. Repo + scaffold.** `git init`, commit `README.md` and docs as the build plan. Create the solution, `src/AgentReview.McpServers.StaticAnalysis` (console app, `net10.0`), `.gitignore`. Approve and install `ModelContextProtocol` + `Microsoft.Extensions.Hosting`, pinned. Server starts, responds to MCP `initialize` over stdio. Verify: register in Claude Code, `/mcp` shows the server connected with zero tools. *Verified 2026-08-02: `initialize` answered over stdio (protocol 2025-06-18, logs on stderr), `/mcp` shows the server connected. Bonus beyond plan: xUnit test project with a zero-tools smoke test.*
- [x] **2. `analyze_csharp`, compiler diagnostics.** Approve and install the two Roslyn packages, pinned. Implement layer 1, return `AnalysisFinding[]`. Verify in Claude Code against a snippet with planted issues (unused variable, unreachable code). *Verified 2026-08-03: called from inside Claude Code over MCP; planted snippet returned CS0219 (line 5) and CS0162 (line 7) as structured findings. 5 unit tests green.*
- [x] **3. `analyze_csharp`, analyzer rules.** Approve and install `Microsoft.CodeAnalysis.NetAnalyzers`, pinned. Load via `AnalyzerFileReference`, merge CA findings into the same result list. Verify a planted `IDisposable` leak produces a CA finding. *Verified 2026-08-03: called from inside Claude Code; planted FileStream leak returned CA2000 at the right position. Assembly-level rules (CA1014/1016/1017/1050) suppressed as snippet-inapplicable. 6 unit tests green. Note: CA2000 only catches abandoned locals, not leaks via return/throw paths; eval fixtures must use detectable shapes.*
- [x] **4. `run_semgrep`.** Approve and install the Semgrep CLI via Homebrew. Implement the tool, parse JSON output, handle the CLI-missing case with a clear error result. Verify against a snippet with a planted hardcoded secret or SQL-injection pattern. *Verified 2026-08-04: called from inside Claude Code; planted SQL injection returned csharp-sqli as Error with position. Install deviated from plan: PyPI (`pip install --user semgrep==1.172.0`, user-approved) because Homebrew hit a /usr/local permissions wall; the server finds the CLI via SEMGREP_PATH, PATH, or pip user-site conventions. Registry note: `p/default` and `p/csharp` both work anonymously and catch the SQLi; no standard pack (including `p/secrets`) flags the connection-string password, so SQLi is the reliable planted issue for demos and evals. 12 unit tests green.*
- [ ] **5. Polish + demo.** Invocation logging on both tools, README checkboxes updated with verification notes, demo GIF of Claude Code calling both tools committed.

## Verification snippet (planted issues)

Keep a small fixture in the repo for repeatable demos, roughly:

```csharp
public class Demo
{
    public void Run(string userInput)
    {
        int unused = 42;                                   // Roslyn: unused variable
        var conn = new SqlConnection("Server=x;Password=hunter2"); // Semgrep: hardcoded secret
        var cmd = new SqlCommand("SELECT * FROM t WHERE id = " + userInput, conn); // Semgrep: SQL injection
        return;
        Console.WriteLine("never runs");                   // Roslyn: unreachable code
    }
}
```

## Risks

- **MCP SDK 2.0 API drift** — mitigations: read the current csharp-sdk samples before writing the host; item 1 is deliberately tiny so surprises surface early.
- **Runtime analyzer loading** (item 3) is the fiddliest part — mitigation: it is isolated in its own item and the checkpoint does not depend on it.
- **Semgrep registry needs network** — acceptable for a local dev tool; noted for later if offline runs matter.
