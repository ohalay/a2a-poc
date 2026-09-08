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
  Orchestrator/       Minimal API + chat UI (HtmlUi.cs); LLM + DI wiring in Program.cs
  Orchestrator.Core/  OrchestrationService, AgentRegistry, ChatStore — the reusable pipeline
  Agent.Assortment/   A2A server: catalog / product / store-coverage specialist
  Agent.SupplyChain/  A2A server: warehouse stock / shipment / velocity specialist
  Shared/             ServiceNames + model id
tests/
  Orchestrator.Tests/ xUnit tests that exercise the pipeline over a real A2A HTTP transport
docs/
  architecture.md     Mermaid diagrams: topology, single-message flow, chat-history flow
```

## Build, test, run

```bash
# Build everything
dotnet build

# Run the orchestrator unit/integration tests (3 tests: route, multi-agent, direct-answer)
dotnet test tests/Orchestrator.Tests

# Run the whole system (Ollama + agents + orchestrator) via Aspire
dotnet run --project src/AppHost

# Run just the orchestrator (assumes Ollama + agents already up)
dotnet run --project src/Orchestrator
```

Always run `dotnet build` and `dotnet test tests/Orchestrator.Tests` after changes. The full build
compiles all projects including the Aspire AppHost.

## Conventions

- **Language/runtime:** C# on `net10.0`, nullable + implicit usings enabled.
- **Top-level statements:** `Program.cs` files use top-level statements. Because `Program` is in the
  global namespace, remember to add `using <ProjectNamespace>;` when referencing types like `HtmlUi`
  that live in a named namespace (a past build break — see `Orchestrator/Program.cs`).
- **DI first:** register services in `Program.cs`; keep orchestration logic in `Orchestrator.Core` so
  it stays unit-testable without a web host.
- **A2A pattern per agent:** build an `AgentCard` (name, description, skills, capabilities), register
  the handler with `AddA2AAgent<THandler>(card, ...)`, then `MapWellKnownAgentCard(card, "")` and
  `MapA2A("/")`. Domain logic goes in an `IAgentHandler` that runs a tool-calling LLM over private
  domain tools.
- **Agent selection is card-driven:** the router LLM only sees each agent's published AgentCard.
  To make a new capability routable, describe it clearly in the card's `Description` and `Skills`.
- **Graceful degradation:** never let a single down agent crash the orchestrator. `AgentRegistry`
  resolution is lazy/idempotent; dispatch failures return an "unavailable" note.
- **Chat history:** the orchestrator owns cross-turn history in `ChatStore` keyed by `threadId`;
  `OrchestrationService.BuildObjective` embeds recent turns into the task objective. Per-agent task
  history is separate (`AutoAppendHistory = true` + `InMemoryTaskStore`).

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
7. Add/extend tests in `tests/Orchestrator.Tests` (use `TestAgentHost` to spin a fake A2A agent).

## Safety / scope

- Do not commit unless explicitly asked.
- Keep changes minimal and within the vertical slice being worked on.
- This is a local, LLM-backed POC — no external network calls beyond the local Ollama endpoint.
