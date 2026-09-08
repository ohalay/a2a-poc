# AGENTS.md

Guidance for AI coding agents working in this repository.

## What this project is

A .NET 10 proof-of-concept for the **Agent-to-Agent (A2A) protocol**. A central orchestrator discovers
independent domain agents over HTTP, exposes each one as a callable tool to a single function-calling
LLM loop, and lets the model call whichever specialists it needs (in parallel) before synthesizing one
answer. All LLM inference is local via **Ollama** (`llama3.2`). See `docs/architecture.md` for diagrams
and the full request flow.

There is **no** explicit route → dispatch → aggregate pipeline: `OrchestrationService.HandleAsync`
builds one `FunctionInvokingChatClient` with every agent wrapped as an `AIFunction` tool, and the LLM
does the routing, dispatch, and synthesis in a single loop.

## Layout

```
src/
  AppHost/            .NET Aspire host — boots agents + orchestrator, injects agent URLs + Ollama env.
                      Does NOT start Ollama (externally managed).
  ServiceDefaults/    Shared Aspire wiring — OpenTelemetry (traces/metrics/logs), health, resilience
  Orchestrator/       The live orchestrator: minimal API + chat UI (HtmlUi.cs), LLM + DI wiring
                      (Program.cs), and the pipeline — OrchestrationService, AgentRegistry, ChatStore
  Agent.Assortment/   A2A server: catalog / product / active-assortment specialist (card: AssortmentSpecialist)
  Agent.SupplyChain/  A2A server: warehouse stock / shipment / velocity specialist (card: SupplyChainAnalyst)
  Shared/             ServiceNames + model id
docs/
  architecture.md     Mermaid diagrams: topology, single-message flow, chat-history flow, tracing
```

There is **no `tests/` directory** in the repository. `Orchestrator/` uses `namespace Orchestrator;`
throughout.

## Build, test, run

Ollama is **not** started by Aspire. Start it as a separate container (and pull a tool-calling model)
**before** running the AppHost:

```bash
docker run -d --name ollama -p 11434:11434 -v ollama:/root/.ollama ollama/ollama
docker exec -it ollama ollama pull llama3.2
```

```bash
# Build everything (the solution builds all projects under src/; there is no test project)
dotnet build

# Run the whole system (agents + orchestrator) via Aspire
dotnet run --project src/AppHost

# Run just the orchestrator (assumes Ollama + agents already up)
dotnet run --project src/Orchestrator
```

Always run `dotnet build` after changes. The full build compiles all projects in `A2APoc.slnx`
(AppHost, ServiceDefaults, Orchestrator, both agents, Shared).

**No automated tests exist.** There is no `tests/` directory or test project. If you add tests, target
`src/Orchestrator` and add the test project to `A2APoc.slnx`.

## How the orchestrator actually works

`OrchestrationService.HandleAsync`:
1. `registry.GetAgents()` → each `RemoteAgent(Card, A2AClient)` becomes an `AIFunction` via `ToTool`.
   The tool's name is `Card.Name` and its description is `Card.Description` + skill names — this text is
   what the LLM uses to route.
2. Builds the message list: system prompt + `thread.Turns.TakeLast(10)` replayed as real
   `user`/`assistant` `ChatMessage`s + the new user message. Appends the user turn to the thread.
3. Wraps a `FunctionInvokingChatClient` (`MaximumIterationsPerRequest = 10`,
   `AllowConcurrentInvocation = true`) with `.UseOpenTelemetry(...)` and runs one
   `GetResponseAsync` — the loop calls agent tools (each a real `A2AClient.SendMessageAsync`) and
   synthesizes the final answer.

The `/api/chat` endpoint appends the assistant turn after `HandleAsync` returns.

## Conventions

- **Language/runtime:** C# on `net10.0`, nullable + implicit usings enabled.
- **Top-level statements:** `Program.cs` files use top-level statements. Because `Program` is in the
  global namespace, remember to add `using <ProjectNamespace>;` when referencing types like `HtmlUi`
  that live in a named namespace. All `Orchestrator/` types live in `namespace Orchestrator;`.
- **DI first:** register services in `Program.cs`; keep orchestration logic in the `Orchestrator`
  project (`OrchestrationService`, `AgentRegistry`, `ChatStore`).
- **A2A pattern per agent:** build an `AgentCard` (name, description, skills, capabilities), register
  the handler with `AddA2AAgent<THandler>(card, options => options.AutoAppendHistory = true)`, then
  `MapWellKnownAgentCard(card, "")` and `MapA2A("/")`. Domain logic goes in an `IAgentHandler` that
  runs a tool-calling LLM over private domain tools.
- **Agent selection is card-driven:** the orchestrator LLM only sees each agent's `Card.Name`,
  `Card.Description`, and skill names (turned into a tool). To make a new capability routable, describe
  it clearly in the card's `Description` and `Skills`.
- **Graceful degradation:** never let a single down agent crash the orchestrator. `AgentRegistry`
  resolution skips agents whose card probe fails (logged as a warning); each agent tool catches
  exceptions and returns `"(agent unavailable: …)"` instead of throwing.
- **Chat history:** the orchestrator owns cross-turn history in `ChatStore` keyed by `threadId`;
  `HandleAsync` replays the last 10 turns as real chat messages. Agents receive only the per-call
  `request` string the model chose, not the whole conversation. Per-agent task history is separate
  (`AutoAppendHistory = true` + `InMemoryTaskStore`).

## Observability / tracing

OpenTelemetry is wired in `ServiceDefaults/Extensions.cs`. It registers three trace sources: the app
name, `A2A*` (wildcard — the A2A SDK's `A2A`/`A2A.AspNetCore` spans plus any custom `A2A.*` source),
and `Experimental.Microsoft.Extensions.AI` (LLM chat + tool spans). One end-to-end trace tree is
produced per request, viewable in the Aspire dashboard:

```
POST /api/chat (Server)                  auto-instrumented ASP.NET Core span
  orchestrate.history (Internal)         A2A.Orchestrator — the ONLY custom orchestrator span
  chat <model>                           the tool-calling LLM loop (Experimental.Microsoft.Extensions.AI)
    <tool call spans>                    FunctionInvokingChatClient invoking each agent tool
      <A2A client + HttpClient spans>    per-agent A2A call; injects W3C traceparent over HTTP
        POST / (Server)                  the remote agent continues the SAME trace
          chat <model>                   the agent's own LLM + tool loop
```

Conventions that keep this intact:
- **OTel is the OUTERMOST chat-client decorator.** In `OrchestrationService`, the chat client is a
  `FunctionInvokingChatClient` wrapped by `.UseOpenTelemetry(...)` (the first `Use*` is outermost), so
  OTel observes the whole tool loop. Wrapping a bare `FunctionInvokingChatClient` without an OTel layer
  is why tool spans went missing before.
- **The orchestrator owns its own tool loop.** `OrchestrationService.HandleAsync` builds the
  `FunctionInvokingChatClient`; `Orchestrator/Program.cs` therefore does NOT call
  `UseFunctionInvocation` (that would run the loop twice). The agents' own `Program.cs` files DO call
  `UseFunctionInvocation`, because they rely on the automatic tool loop.
- **The orchestrator emits only one custom span:** `orchestrate.history` (from `A2A.Orchestrator`),
  tagged with `a2a.history.total_turns` / `a2a.history.replayed_turns`. There is no
  `orchestrate.handle`, `orchestrate.aggregate`, or explicit `dispatch <Agent>` span — dispatch is a
  plain log line inside the agent tool function.
- **The domain agents emit no custom spans** — they only log. To add an `A2A.Agent.<Name>.handle_task`
  span, create an `ActivitySource` with an `A2A` prefix in the handler; the `A2A*` wildcard captures
  it automatically.
- **Trace stitching over A2A** comes from the A2A SDK client spans + HttpClient instrumentation
  propagating `traceparent`, so the remote agent's inbound span joins the same trace.

## Local LLM configuration

Agents and the orchestrator read `OLLAMA_ENDPOINT` and `OLLAMA_MODEL` (default `llama3.2`). Each
service falls back to `http://localhost:11434` when run standalone. Under Aspire, `AppHost` resolves
`OLLAMA_ENDPOINT` (from `aspire.config.json`, then the env var, then its own fallback) and injects it
into all three services. The checked-in `aspire.config.json` points at a specific local port — adjust
it to your Ollama instance. The model must support **tool calling**.

## Adding a new domain agent

1. Create `src/Agent.<Name>` as an A2A server (copy `Agent.Assortment` as a template).
2. Define its `AgentCard` with an accurate `Description`/`Skills` — that text becomes the tool
   description the orchestrator LLM routes on.
3. Implement domain tools + an `IAgentHandler` that runs the tool-calling LLM.
4. Register it in `AppHost.cs` (`AddProject`, wire Ollama env, `WaitFor`).
5. Inject its URL into the orchestrator by adding the next `AGENTS__<n>` entry in `AppHost.cs`
   (`WithEnvironment("AGENTS__<n>", agent.GetEndpoint("http"))`). `AgentRegistry` reads the whole
   `AGENTS` array — no orchestrator code change is needed.
6. Add a `ServiceNames` entry in `src/Shared`.
7. (Optional) Add a custom `handle_task` span from an `A2A.Agent.<Name>` `ActivitySource` in the
   handler so the agent shows up in the trace tree with its own span (none of the current agents do
   this yet).

## Safety / scope

- Do not commit unless explicitly asked.
- Keep changes minimal and within the vertical slice being worked on.
- This is a local, LLM-backed POC — no external network calls beyond the local Ollama endpoint.
