# A2A Multi-Agent Commerce Orchestrator

A .NET 10 proof-of-concept for the [Agent-to-Agent (A2A) protocol](https://a2aproject.github.io/A2A/).
A central **orchestrator** discovers independent **domain agents** over HTTP, routes each user request
to the relevant specialists, dispatches A2A tasks concurrently, and merges the results into a single
answer. All LLM inference runs locally through **Ollama** (`llama3.2`).

See [`docs/architecture.md`](docs/architecture.md) for diagrams and the full request flow.

## How it works

1. **Route** — a router LLM call selects agents purely from their published `AgentCard`s. Zero, one,
   or many agents may be chosen; zero matches falls back to a direct orchestrator answer.
2. **Dispatch** — chosen agents run concurrently. Each is a real A2A server the orchestrator calls
   via `A2AClient.SendMessageAsync`. A down agent degrades gracefully (returns an "unavailable" note).
3. **Aggregate** — an aggregator LLM call merges the specialist outputs into one cohesive answer.

Cross-turn chat history is owned by the orchestrator (`ChatStore`, keyed by `threadId`) and replayed
into the objective sent to agents so follow-up questions stay coherent.

## Project layout

```
src/
  AppHost/            .NET Aspire host — boots agents + orchestrator, injects agent URLs, points at Ollama
  ServiceDefaults/    Shared Aspire wiring — OpenTelemetry (traces/metrics/logs), health, resilience
  Orchestrator/       Live orchestrator: minimal API + chat UI, LLM + DI wiring, and the pipeline
                      (OrchestrationService, AgentRegistry, ChatStore)
  Agent.Assortment/   A2A server: catalog / product / store-coverage specialist
  Agent.SupplyChain/  A2A server: warehouse stock / shipment / velocity specialist
  Shared/             ServiceNames + model id
  Orchestrator.Core/  Dead code — an older copy of the pipeline. Not in the solution, built by nothing.
docs/
  architecture.md     Mermaid diagrams: topology, single-message flow, chat-history flow, tracing
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A running [Ollama](https://ollama.com/) instance with a tool-calling model pulled:
  ```bash
  ollama pull llama3.2
  ```
  Ollama is **not** started by Aspire — run it yourself and point the app at it (see below).

## Configuration

Agents and the orchestrator read two environment variables:

| Variable | Default | Purpose |
|----------|---------|---------|
| `OLLAMA_ENDPOINT` | `http://localhost:11434` | Base URL of the Ollama server. Must support tool calling. |
| `OLLAMA_MODEL` | `llama3.2` | Model id used for routing, aggregation, and per-agent tool loops. |

Under Aspire these are injected by `AppHost`. The AppHost resolves `OLLAMA_ENDPOINT` from
`aspire.config.json` (or the `OLLAMA_ENDPOINT` env var) and propagates it to all services.

The orchestrator also receives the resolved agent base URLs from the AppHost as an array
(`AGENTS__0`, `AGENTS__1`, ...), which `AgentRegistry` binds via `GetSection("AGENTS").Get<string[]>()`.

## Build, test, run

```bash
# Build everything (the solution excludes Orchestrator.Core and the tests project)
dotnet build

# Run the whole system (agents + orchestrator) via Aspire — opens the dashboard
dotnet run --project src/AppHost

# Run just the orchestrator (assumes Ollama + agents already up)
dotnet run --project src/Orchestrator
```

Open the Aspire dashboard link printed on startup to see all services, their endpoints, logs, and the
end-to-end distributed traces (see Observability below).

> **Tests are currently broken.** `tests/Orchestrator.Tests` references the dead `Orchestrator.Core`
> project and is excluded from the solution, so `dotnet build` skips it. To restore tests, repoint the
> test project at `src/Orchestrator` and reconcile the duplicated pipeline first.

### Troubleshooting: "file is locked by AppHost / Orchestrator"

If a build fails with `MSB3021`/`MSB3027` because the output `.exe` is locked, a previous run is still
alive. Stop it before rebuilding:

```bash
tasklist | grep -i apphost           # find the PID
taskkill //PID <pid> //F             # stop it
```

Always stop the running Aspire app (Ctrl+C in its terminal, or from the dashboard) before rebuilding.

## Observability

Every service calls `AddServiceDefaults()`, which wires OpenTelemetry for traces, metrics, and logs.
Under Aspire, one distributed trace tree is produced per `/api/chat` request and viewable in the
dashboard's **Traces** tab:

```
orchestrate.handle (Server)              OrchestrationService — root span for the turn
  chat <model>                           router/aggregator LLM call (Microsoft.Extensions.AI)
    tool call                            FunctionInvokingChatClient tool loop
      dispatch <Agent> (Client)          per-agent A2A call; injects W3C traceparent over HTTP
        <agent>.handle_task (Server)     the remote agent continues the SAME trace
          chat <model> -> tool call      the agent's own LLM + tool loop
```

## Adding a new domain agent

1. Create `src/Agent.<Name>` as an A2A server (copy `Agent.Assortment` as a template).
2. Define its `AgentCard` with accurate `Description`/`Skills` so the router can select it.
3. Implement domain tools + an `IAgentHandler` that runs the tool-calling LLM.
4. Register it in `AppHost.cs` (`AddProject`, wire Ollama env, `WaitFor`) and add it to the
   orchestrator's `AGENTS__<n>` list.
5. Add a `ServiceNames` entry in `src/Shared`.
6. Add a `handle_task` span from an `A2A.Agent.<Name>` `ActivitySource` so the agent shows up in the
   trace tree.

See [`AGENTS.md`](AGENTS.md) for the full conventions and guidance for AI coding agents.
