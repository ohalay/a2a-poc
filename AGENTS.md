# AGENTS.md

Guidance for AI coding agents working in this repository.

## What this project is

A .NET 10 proof-of-concept for the **Agent-to-Agent (A2A) protocol**. A central orchestrator discovers
independent domain agents over HTTP, routes each user request to the relevant specialists, dispatches
A2A tasks concurrently, and aggregates the results. All LLM inference is local via **Ollama**
(`llama3.2`). See `docs/architecture.md` for diagrams and the full request flow.

## Layout

```
src/
  AppHost/            .NET Aspire host — boots Ollama + agents + orchestrator, injects agent URLs
  ServiceDefaults/    Shared Aspire wiring — OpenTelemetry (traces/metrics/logs), health, resilience
  Orchestrator/       The live orchestrator: minimal API + chat UI (HtmlUi.cs), LLM + DI wiring
                      (Program.cs), and the pipeline — OrchestrationService, AgentRegistry, ChatStore
  Agent.Assortment/   A2A server: catalog / product / store-coverage specialist
  Agent.SupplyChain/  A2A server: warehouse stock / shipment / velocity specialist
  Shared/             ServiceNames + model id
  Orchestrator.Core/  DEAD CODE — an older copy of the pipeline (OrchestrationService, AgentRegistry,
                      ChatStore). Not in the solution, referenced by nothing, not built. The live
                      copies live in Orchestrator/. Both still use `namespace Orchestrator.Core;`.
tests/
  Orchestrator.Tests/ xUnit tests. Currently NOT in the solution and do not build — they reference
                      the removed `Orchestrator.Core` project. See "Build, test, run" below.
docs/
  architecture.md     Mermaid diagrams: topology, single-message flow, chat-history flow
```

## Build, test, run

```bash
# Build everything (solution excludes Orchestrator.Core and the tests project)
dotnet build

# Run the whole system (Ollama + agents + orchestrator) via Aspire
dotnet run --project src/AppHost

# Run just the orchestrator (assumes Ollama + agents already up)
dotnet run --project src/Orchestrator
```

Always run `dotnet build` after changes. The full build compiles all projects in the solution
including the Aspire AppHost.

**Tests are currently broken.** `tests/Orchestrator.Tests` references the dead `Orchestrator.Core`
project and is excluded from `A2APoc.slnx`, so `dotnet build` skips it. To restore tests, repoint the
test project at `src/Orchestrator` (and reconcile the duplicated pipeline — see Layout) before adding
it back to the solution.

## Conventions

- **Language/runtime:** C# on `net10.0`, nullable + implicit usings enabled.
- **Top-level statements:** `Program.cs` files use top-level statements. Because `Program` is in the
  global namespace, remember to add `using <ProjectNamespace>;` when referencing types like `HtmlUi`
  that live in a named namespace (a past build break — see `Orchestrator/Program.cs`).
- **DI first:** register services in `Program.cs`; keep orchestration logic in the `Orchestrator`
  project (`OrchestrationService`, `AgentRegistry`, `ChatStore`). Do not add code to the dead
  `Orchestrator.Core` project.
- **A2A pattern per agent:** build an `AgentCard` (name, description, skills, capabilities), register
  the handler with `AddA2AAgent<THandler>(card, ...)`, then `MapWellKnownAgentCard(card, "")` and
  `MapA2A("/")`. Domain logic goes in an `IAgentHandler` that runs a tool-calling LLM over private
  domain tools.
- **Agent selection is card-driven:** the router LLM only sees each agent's published AgentCard.
  To make a new capability routable, describe it clearly in the card's `Description` and `Skills`.
- **Graceful degradation:** never let a single down agent crash the orchestrator. `AgentRegistry`
  resolution is lazy/idempotent; dispatch failures return an "unavailable" note.
- **Chat history:** the orchestrator owns cross-turn history in `ChatStore` keyed by `threadId`;
  `OrchestrationService.HandleAsync` replays the last 10 turns into the LLM message list. Per-agent
  task history is separate (`AutoAppendHistory = true` + `InMemoryTaskStore`).

## Observability / tracing

OpenTelemetry is wired in `ServiceDefaults/Extensions.cs` and produces one end-to-end trace tree per
request, viewable in the Aspire dashboard:

```
orchestrate.handle (Server)              OrchestrationService — root span for the turn
  chat <model>                           router/aggregator LLM call (Microsoft.Extensions.AI)
    tool call                            FunctionInvokingChatClient tool loop
      dispatch <Agent> (Client)          per-agent A2A call; injects W3C traceparent over HTTP
        <agent>.handle_task (Server)     the remote agent continues the SAME trace
          chat <model> -> tool call      the agent's own LLM + tool loop
```

Conventions that keep this intact:
- **OTel is the OUTERMOST chat-client decorator.** In every `AddChatClient`/`AsBuilder` chain, put
  `UseOpenTelemetry(...)` BEFORE `UseFunctionInvocation()`. The first `Use*` is outermost, so this is
  what makes the `tool call` spans appear. Wrapping a bare `FunctionInvokingChatClient` without an
  OTel layer is exactly why tool spans went missing before.
- **The orchestrator owns its own tool loop.** `OrchestrationService.HandleAsync` builds the
  `FunctionInvokingChatClient`; `Orchestrator/Program.cs` therefore does NOT call
  `UseFunctionInvocation` (that would run the loop twice).
- **Custom spans use `A2A.*` sources**, captured by the `A2A*` wildcard in `ServiceDefaults`:
  `A2A.Orchestrator`, `A2A.Agent.Assortment`, `A2A.Agent.SupplyChain`. Add new agent sources with the
  same `A2A.Agent.<Name>` prefix so they are captured automatically.
- **Trace stitching over A2A** relies on the explicit `dispatch <Agent>` client span being active
  when the A2A client's `HttpClient` sends the request, so HttpClient instrumentation injects
  `traceparent`. Keep the dispatch span around the `SendMessageAsync` call.

## Local LLM configuration

Agents and the orchestrator read `OLLAMA_ENDPOINT` (default `http://localhost:11434`) and
`OLLAMA_MODEL` (default `llama3.2`). Under Aspire these are injected by `AppHost`. The model must
support **tool calling**.

## Adding a new domain agent

1. Create `src/Agent.<Name>` as an A2A server (copy `Agent.Assortment` as a template).
2. Define its `AgentCard` with accurate `Description`/`Skills` so the router can select it.
3. Implement domain tools + an `IAgentHandler` that runs the tool-calling LLM.
4. Register it in `AppHost.cs` (`AddProject`, wire Ollama env, `WaitFor`).
5. Inject its URL into the orchestrator (`WithEnvironment("<NAME>_AGENT_URL", ...)`) and add a matching
   `TryRegister(...)` in `Orchestrator/Program.cs`.
6. Add a `ServiceNames` entry in `src/Shared`.
7. Add a `handle_task` span from an `A2A.Agent.<Name>` ActivitySource in the handler (mirror
   `Agent.Assortment/DomainAgentHandler.cs`) so the agent shows up in the trace tree.

## Safety / scope

- Do not commit unless explicitly asked.
- Keep changes minimal and within the vertical slice being worked on.
- This is a local, LLM-backed POC — no external network calls beyond the local Ollama endpoint.
