# A2A Multi-Agent Commerce Orchestrator

A .NET 10 proof-of-concept for the [Agent-to-Agent (A2A) protocol](https://a2aproject.github.io/A2A/).
A central **orchestrator** discovers independent **domain agents** over HTTP, exposes each one as a
callable tool to a single LLM loop, and lets the model call whichever specialists it needs (in
parallel) before synthesizing one answer. All LLM inference runs locally through **Ollama**
(`llama3.2`).

See [`docs/architecture.md`](docs/architecture.md) for diagrams and the full request flow.

## How it works

The orchestrator does **not** run a separate route → dispatch → aggregate pipeline. Instead it hands
every available agent to one function-calling LLM loop and lets the model do the routing, dispatch,
and synthesis itself:

1. **Agents as tools** — each discovered agent's `AgentCard` (name, description, skills) becomes an
   `AIFunction` on the chat client. The tool description tells the model what the specialist covers.
2. **One tool-calling loop** — a `FunctionInvokingChatClient` (up to 10 iterations, concurrent
   invocation allowed) lets the LLM decide which agent tools to call, calls them, and can call several
   in one turn. Each tool invocation is a real A2A call via `A2AClient.SendMessageAsync`. A down agent
   degrades gracefully — the tool returns an `(agent unavailable: ...)` string instead of throwing.
3. **Synthesis** — the same loop merges the tool results into one cohesive plain-text answer. The
   system prompt forbids inventing data and instructs the model to call *every* relevant domain.

Cross-turn chat history is owned by the orchestrator (`ChatStore`, keyed by `threadId`). The last 10
turns are replayed into the LLM message list as real chat messages so follow-up questions stay
coherent. Each A2A agent separately keeps its own per-context task history
(`AutoAppendHistory = true` + `InMemoryTaskStore`).

## Project layout

```
src/
  AppHost/            .NET Aspire host — boots agents + orchestrator, injects agent URLs, points at Ollama
  ServiceDefaults/    Shared Aspire wiring — OpenTelemetry (traces/metrics/logs), health, resilience
  Orchestrator/       Live orchestrator: minimal API + chat UI, LLM + DI wiring, and the pipeline
                      (OrchestrationService, AgentRegistry, ChatStore)
  Agent.Assortment/   A2A server: catalog / product / active-assortment specialist (AssortmentSpecialist)
  Agent.SupplyChain/  A2A server: warehouse stock / shipment / velocity specialist (SupplyChainAnalyst)
  Shared/             ServiceNames + model id
docs/
  architecture.md     Mermaid diagrams: topology, single-message flow, chat-history flow, tracing
```

There is **no `tests/` directory** in the repository at present.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (or Podman) to run the Ollama container.

### Start Ollama first (separate container, before Aspire)

Ollama is **not** managed by Aspire — you must start it yourself and have it running **before** you
launch the AppHost. Run it as its own container and pull a tool-calling model:

```bash
# 1. Start the Ollama container (maps the API port to the host)
docker run -d --name ollama -p 11434:11434 -v ollama:/root/.ollama ollama/ollama

# 2. Pull a tool-calling model into that container
docker exec -it ollama ollama pull llama3.2

# 3. Verify it's reachable before starting Aspire
curl http://localhost:11434/api/tags
```

Then point the app at it via `OLLAMA_ENDPOINT` (see Configuration) and only after that run
`dotnet run --project src/AppHost`. If Ollama isn't up first, the agents and orchestrator will fail
their first LLM call.

> If you already run Ollama natively (not in a container), that works too — just make sure it's
> listening and `OLLAMA_ENDPOINT` matches its address before starting Aspire.

## Configuration

Agents and the orchestrator read two environment variables:

| Variable | Default | Purpose |
|----------|---------|---------|
| `OLLAMA_ENDPOINT` | `http://localhost:11434` (services) | Base URL of the Ollama server. Must support tool calling. |
| `OLLAMA_MODEL` | `llama3.2` | Model id used for the orchestrator loop and per-agent tool loops. |

Under Aspire, `AppHost` resolves `OLLAMA_ENDPOINT` (from `aspire.config.json`, then the
`OLLAMA_ENDPOINT` env var, then its own fallback) and injects it plus `OLLAMA_MODEL` into all three
services. The checked-in `aspire.config.json` points `OLLAMA_ENDPOINT` at a local port — adjust it to
match your Ollama instance. Each service also has its own hard-coded fallback default it uses when run
standalone (the orchestrator and agents fall back to `http://localhost:11434`).

The orchestrator receives the resolved agent base URLs from the AppHost as an array
(`AGENTS__0`, `AGENTS__1`, ...). `AgentRegistry` binds them via
`configuration.GetSection("AGENTS").Get<string[]>()`, then resolves each `AgentCard` through
`A2ACardResolver` using a dedicated `agent-direct` HttpClient (5-minute timeout, no service-discovery
or resilience handler, so a cold agent's first request isn't aborted).

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /` | Chat UI (served from `HtmlUi.Page`). |
| `POST /api/chat` | Body `{ "threadId"?: string, "message": string }`. Returns `{ threadId, answer }`. Omit `threadId` to start a new thread. |
| `GET /api/agents` | Lists the resolved agents (name, description, skills, documentation URL). |

## Build, test, run

> **Start the Ollama container first** (see Prerequisites). Aspire does not start Ollama, so it must
> already be running and reachable at `OLLAMA_ENDPOINT` before you run the AppHost.

```bash
# Build everything (the solution builds all projects under src/; there is no test project)
dotnet build

# Run the whole system (agents + orchestrator) via Aspire — opens the dashboard
dotnet run --project src/AppHost

# Run just the orchestrator (assumes Ollama + agents already up)
dotnet run --project src/Orchestrator
```

Open the Aspire dashboard link printed on startup to see all services, their endpoints, logs, and the
end-to-end distributed traces (see Observability below).

> **No automated tests.** There is currently no `tests/` directory or test project in the repo.

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
`ServiceDefaults` registers three trace sources: the app's own name, the `A2A*` wildcard (the A2A SDK's
`A2A` / `A2A.AspNetCore` transport spans plus any custom `A2A.*` source), and
`Experimental.Microsoft.Extensions.AI` (LLM chat + automatic tool-call spans). Under Aspire, one
distributed trace tree is produced per `/api/chat` request and viewable in the dashboard's **Traces**
tab:

```
POST /api/chat (Server)                  auto-instrumented ASP.NET Core span
  chat <model>                           the orchestrator's tool-calling LLM loop (Extensions.AI)
    orchestrate_tools                    FunctionInvokingChatClient wrapping the tool loop
      chat <model> -> HTTP POST 200      the LLM call that decides which agent tools to invoke
      execute_tool SupplyChainAnalyst    one span per agent tool the model calls
        RPC /SendMessage                 A2A client call
          HTTP POST 200                  W3C traceparent injected over HTTP
            POST / (Server)              the remote agent continues the SAME trace
              HandleA2ARequest           the agent's A2A request handler (+ its own chat/tool spans)
      execute_tool AssortmentSpecialist  (runs concurrently with the other agent)
        RPC /SendMessage -> POST /       ...same shape, into the assortment-agent
      chat <model>                       the final synthesis LLM call
```

Here is a real trace tree for one `/api/chat` turn in the Aspire dashboard — the orchestrator's LLM
loop calling both agents (`SupplyChainAnalyst` and `AssortmentSpecialist`) concurrently over A2A, each
continuing the same trace into the remote agent, then a final synthesis `chat` call:

![Aspire distributed trace tree for a /api/chat request](docs/images/trace-tree.png)

Notes:
- The tool loop surfaces as an `orchestrate_tools` span with an `execute_tool <AgentName>` child per
  agent the model invokes; each drills into `RPC /SendMessage` → `HTTP POST 200` → the remote agent's
  `POST /` / `HandleA2ARequest`.
- The orchestrator's only **custom** span is `orchestrate.history` (from the `A2A.Orchestrator`
  `ActivitySource`), tagged with total and replayed turn counts. There are no `orchestrate.handle`,
  `orchestrate.aggregate`, or explicit `dispatch <Agent>` spans — dispatch is a plain log line inside
  the tool function.
- Tool/LLM spans come from `Experimental.Microsoft.Extensions.AI`, enabled because each chat client is
  built with `.UseOpenTelemetry(o => o.EnableSensitiveData = true)`.
- Trace stitching across the A2A HTTP hop comes from the A2A SDK spans + HttpClient instrumentation
  propagating `traceparent`, so the remote agent's ASP.NET Core span joins the same trace.
- The domain agents do **not** currently emit custom spans (no `handle_task` `ActivitySource`) — they
  only log. Their contribution to the trace is the ASP.NET Core inbound span plus their own `chat` /
  tool spans.

## Adding a new domain agent

1. Create `src/Agent.<Name>` as an A2A server (copy `Agent.Assortment` as a template).
2. Define its `AgentCard` with an accurate `Description`/`Skills` — this text is what the orchestrator
   LLM sees as the tool description, so it drives selection.
3. Implement domain tools + an `IAgentHandler` that runs the tool-calling LLM.
4. Register it in `AppHost.cs` (`AddProject`, wire Ollama env, `WaitFor`) and add it to the
   orchestrator's `AGENTS__<n>` list (`WithEnvironment("AGENTS__<n>", agent.GetEndpoint("http"))`).
5. Add a `ServiceNames` entry in `src/Shared`.

See [`AGENTS.md`](AGENTS.md) for the full conventions and guidance for AI coding agents.
