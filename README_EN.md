# LLM — Online Chat & Agent Plugin for Unity

**English** | [中文](README.md)

Wraps any OpenAI-compatible endpoint (`/chat/completions`) into a Unity-side **chat layer + agent kernel**: streaming output, tool calling, async actions, long-term memory, context injection and threshold-based compression. Shared by in-game NPCs, voice assistants and editor tools.

**Five things only**: streaming, tools, actions, memory fact slots, context compression. **Not included**: RAG / vector search, ASR & TTS, animation backends (all three live in the gameplay layer).

---

## Assemblies & dependencies

| Assembly | Folder | References |
| --- | --- | --- |
| `LLM.Runtime` | `Runtime/` | `UniTask`, `Lin.Runtime.Prefs` (state persistence only) |
| `LLM.Editor` | `Editor/` | `LLM.Runtime`, `UniTask` |
| `LLM.Tests` / `LLM.Tests.Editor` | `Tests/` | assemblies under test |

| Dependency | Where it is used | Where to get it |
| --- | --- | --- |
| [UniTask](https://github.com/Cysharp/UniTask) | every async and streaming callback | UPM git URL: `https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask` |
| [ZString](https://github.com/Cysharp/ZString#unity) | `Cysharp.Text` allocation-free building, the plugin's string-concat convention | NuGet package `ZString` (this project uses 2.6.0, under `Assets/Packages/ZString.2.6.0`) |
| [Unity-PlayerPrefsHelper](https://github.com/yaosenlin975-art/Unity-PlayerPrefsHelper) | persistence: the `Lin.Runtime.Prefs` namespace used by `PrefsAgentStateStore` / `PrefsNpcAffinityStore` | package `com.lin.runtime-prefs-helper`, embedded or local UPM; it declares `com.unity.nuget.newtonsoft-json` itself |
| `com.unity.nuget.newtonsoft-json` | argument JSON schemas for tools and actions | UPM, normally pulled in by the row above |

⚠ All four are **compile-time** requirements: `LLM.Runtime.asmdef` hard-references `Lin.Runtime.Prefs`, so a missing package fails the whole assembly even if you set `AgentStateStore` to `None` (that only stops reads and writes).

- **Logging** uses the bundled `LLM.Runtime.Log` — no framework logger. In Player builds every log call (arguments included) is compiled away; add `LLM_LOG` to `Scripting Define Symbols` to get output back. Editor builds always log.
- Clock: `AgentCore` is not a MonoBehaviour; wall clock is pumped by `AgentHost.Update` every frame.

---

## Three minutes to a first answer

1. `Assets → Create → LLM → Provider Config`, fill `Model` / `BaseUrl`. Leave `apiKey` empty to read the `LLM_API_KEY` environment variable.
2. `Assets → Create → LLM → Global Config`, save as `Assets/Resources/LLM/LLMGlobalConfig.asset`, drag the provider asset into `ProviderConfig`, set `AgentStateStore` / `NpcAffinity`. This step is optional: on every editor load (and domain reload) a missing asset is created at that path and you get a prompt to wire `ProviderConfig`. The auto-created asset sets both `AgentStateStore` and `NpcAffinity` to `None` — an empty shell must not start writing into PlayerPrefs on its own, tick `Prefs` when you want persistence. A build cannot create assets, so there it stays a hard error.
3. Assemble once:

```csharp
LLMRuntimeSettings.Install();   // registers providers + installs state stores; already called by AgentHost.Activate and the sandbox
```

4. Ask something:

```csharp
using Cysharp.Threading.Tasks;
using LLM.Runtime;

private LLMSession session;

public async UniTask Run()
{
    session = new LLMSession("npc_merchant", "You are the village shopkeeper. Answer briefly.");
    session.AddContext("Player gold: 120");           // injected into every turn

    string answer = await session.AskAsync("How much for that sword?", OnChunk,
        ct: this.GetCancellationTokenOnDestroy());
}

private void OnChunk(LLMStreamChunk chunk)
{
    if (!string.IsNullOrEmpty(chunk.ContentDelta))
        panel.AppendText(chunk.ContentDelta);         // streaming delta; pass null for non-streaming
}
```

5. No code at all: menu `Lin/LLM/Agent 沙盒` — pick a profile asset and chat, with per-turn injections and tool round-trips visible.

---

## 1 · LLM Provider

A provider is one usable channel: how a single request leaves the process. It knows nothing about sessions or context.

```csharp
public interface ILLMProvider
{
    string ProviderName { get; }
    bool IsAvailable { get; }              // empty key on the default base URL = unavailable; self-hosted gateways pass by changing BaseUrl
    bool SupportsStreaming { get; }
    bool SupportsToolCalling { get; }

    UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct);
    UniTask CompleteStreamAsync(LLMRequest request, Action<LLMStreamChunk> onChunk, CancellationToken ct);
    int EstimateTokens(string text);
}
```

**Config & registration.** `LLMProviderConfig_SO` (`Create → LLM → Provider Config`) carries **connection parameters only**: key, `Model`, `BaseUrl`. `Temperature`, `MaxTokens` and `ToolChoice` are not here — generation parameters are configurable in exactly one place, the profile asset `AgentProfile_SO`. Any OpenAI-compatible service (own gateway, Azure, domestic models) only needs a different `BaseUrl`.

**Primary / fallback.** Create a second asset and hook it to `FallbackProviderConfig`; `RegisterToDispatcher()` registers it as `<name>_fallback` and marks it as the safety channel. If the primary throws (cancellation excluded), the very same request is replayed on the fallback; only when there is no fallback does the exception reach the caller.

**Composition root.** `LLMRuntimeSettings.Install()` reads `Resources/LLM/LLMGlobalConfig` and registers only while no provider exists — idempotent. You can also skip it:

```csharp
var dispatcher = LLMDispatcher.GetInstance();
dispatcher.RegisterProvider(new OpenAIProvider(apiKey, "gpt-4o", "https://api.openai.com/v1", "openai"));
dispatcher.SetDefaultProvider("openai");
```

**Raw request (no session, no agent).**

```csharp
var request = new LLMRequest
{
    SessionId = "item_summary",                     // groups request logs only
    SystemPrompt = "Compress the input to 20 characters.",
    Messages = { new LLMMessage("user", rawText) },
    MaxTokens = 64
};
request.AddContextBlock("Language: Simplified Chinese");   // appended after SystemPrompt, stable order = prefix cache hits

LLMResponse resp = await LLMDispatcher.GetInstance().EnqueueAsync(request, ct);
// resp.Content / resp.PromptTokens / resp.CacheHitTokens
```

Streaming: `EnqueueStreamAsync(request, onChunk, ct)`. Every request (including the fallback one and failed ones) raises `OnRequestCompleted`, which the `Lin/LLM/请求日志` window lists with latency and tokens. ⚠ Real streaming usage arrives in a **separate `choices: []` event** (a chunk with `IsDone = false` carrying only tokens) — cache across chunks instead of waiting for the `IsDone` one.

**Adding a provider kind.** Derive from `LLMProviderConfigBase_SO` and answer two questions: how to build it (`CreateProvider(name)`) and its registration name (`DefaultProviderName`). The global config only knows the base class, so new providers never touch the composition root.

**Key safety.** Never commit a key inside an asset: everything under `Resources/` ships in the build, and anything in git is public. Use `LLM_API_KEY` during development.

---

## 2 · AgentTool (read-only queries)

A tool is a **synchronous, side-effect-free query returning a string**. The return value is handed back as `role=tool`, so it is the entire reality the model sees.

```csharp
using LLM.Runtime;

public static class ShopTools
{
    [AgentTool("get_price", "Price of an item, in gold.")]
    public static string GetPrice(string itemId)
    {
        var item = Shop.Find(itemId);
        return item is null ? "The shop does not stock that." : item.Price.ToString();
    }
}
```

Constraints: `public static`, returns `string`, arguments limited to `int / float / bool / string` and their nullable forms (the JSON Schema is reflected from the signature; parameter names become fields). Non-conforming methods are skipped during scanning with a `Log.Warning` that says why — they never disappear silently.

**Global vs member.**

| | Declared as | Visible to | Registration |
| --- | --- | --- | --- |
| Global tool | `public static` + `[AgentTool]` | every agent, gated per entry | inside `LLM.Runtime`: automatic; **your own assembly must be registered explicitly** |
| Member tool | `public` instance method + `[AgentTool]` on a component inside the `AgentHost` hierarchy | that agent only (private `AgentToolSet`) | none, the host scans its hierarchy on `Activate` |

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
private static void RegisterTools()
{
    AgentToolRegistry.Register(typeof(ShopTools).Assembly);
    AgentActionRegistry.Register(typeof(ShopTools).Assembly);   // same for static actions
}
```

The Inspector's "scan & sync" button only enumerates candidates while editing — do not treat it as composition.

**Attribute fields (runaway protection, arbitrated by LoopGuard).**

| Field | Meaning |
| --- | --- |
| `RepeatLimit` | how many identical-signature calls before a warning. `0` = inherit `GlobalRepeatLimit`, `-1` = off for this tool |
| `NameRepeatLimit` | per-turn cap for the tool name (counts argument changes). `0` = inherit `PerNameToolCallLimit`, `-1` = off |
| `Idempotent` | default `true`. `false` = side effects: the second identical call is always blocked, threshold pinned to 1, no way to disable |

Warn / Block feedback always instructs the model to answer briefly from what it already has instead of retrying, so a blocked model does not go silent. Unknown tool names come back as `[Tool Error] Unknown tool`, which never dead-ends the loop.

**Per-instance gating.** `AgentHost` keeps an override-style toggle list: absent = enabled, present = its `Enabled`. Both the declaration layer (disabled tools stay out of `request.Tools`) and the execution layer (rejected again) read it, so a closed tool is invisible *and* uncallable. At runtime: `host.SetToolEnabled("get_price", false)`, effective from the next turn.

**Three injection points** on `LLMSession`: `ToolExecutor` replaces how a `tool_call` lands (async, interception, timeouts live there; `AbortTurn` skips the remaining calls of the batch without backfilling them); `ExtraTools` adds declarations outside the registry (kernel actions travel here); `AskAsync(..., writeHistory: false)` lets the caller decide whether the turn is stored. `EnableTools = false` disables declaration and execution together.

---

## 3 · AgentAction (side effects)

Actions keep their own registry, deliberately separate: they return `UniTask<AgentActionResult>`, may await, may be interrupted and may take a resource lock — none of which survives the tool side's "returns string" check.

```csharp
[AgentAction("adjust_affinity", "Adjust affinity for what already happened this turn. Do not call without a solid reason.",
    Idempotent = false)]
public UniTask<AgentActionResult> AdjustAffinity(AgentActionContext context, int delta, string reason)
    => AdjustFromModel(delta, reason, context.Agent.RoundSerial);
```

Constraints: `public static` (global) or `public` instance method (member, on the host hierarchy), returns `UniTask<AgentActionResult>`, remaining arguments as for tools. A first parameter of type `AgentActionContext` is injected by the runner and kept out of the schema — `ctx.Agent / ctx.Profile / ctx.SessionId / ctx.CancellationToken` come from there.

**`say` is a reserved parameter name.** Non-idempotent actions get a required `say` field appended to their schema: the model must state first what it is about to do, and the executor plays it out (host receives it through `IAgentOutput.OnSay` / `AgentHost.SayPresented`) *before* the action starts. Declaring `say` yourself is rejected during scanning.

**Attribute fields.**

| Field | Meaning |
| --- | --- |
| `Idempotent` | default `false` (actions have side effects): adds required `say`, blocks the second identical call |
| `Repeatable` | side-effecting but safe to repeat: keeps `say`, uses the per-turn NameCap/L2 instead of a one-shot quota |
| `LongRunning` | escort / performance / travel: uses `LongActionTimeoutSeconds` and does not burn the turn's wall clock while running |
| `Interruptible` | whether a new input may stop it, default `true` |
| `LockKey` | cross-agent resource lock template, must look like `resourceType:{parameter}`, e.g. `item:{itemId}`. Keys coarse enough to lock a whole class are rejected during scanning |
| `RepeatLimit` / `NameRepeatLimit` | same two knobs as tools |

**Timeouts & budget** (all from `AgentProfile_SO`): `TurnDeadlineSeconds` is the wall clock of one turn (LLM round-trips + lock waiting + execution of non-`LongRunning` actions); when it expires the turn closes as `Timeout`. A single action uses `ActionTimeoutSeconds`, `LongRunning` ones `LongActionTimeoutSeconds`. A contended lock waits `LockWaitSeconds`; `0` fails immediately. **There is no tool-round cap** — cost and runaway control rest on the wall clock plus the repeat limits above.

Failure backfill: `AgentActionResult.Failure("Missing NavMeshAgent.")` reaches the model as `[Action Failed] Missing NavMeshAgent.`. The whole runner is swappable: `new AgentCore(..., runner: myRunner)` — `IAgentActionRunner` has a single method; default `ReflectionActionRunner`, pass `NullActionRunner` for pure conversation.

Execution routing order: **member action → member tool → global action → global tool**.

---

## 4 · Putting an agent in a scene

1. `Create → LLM → AgentProfile_SO` (NPCs: `LLM/NPC Profile`) and write the persona. Generation parameters `Temperature` / `MaxTokens`, turn and runaway limits, memory slot cap, `FallbackLines` all live on this asset. An empty `ProfileKey` is derived from the persona content with a project-wide collision check, and afterwards decoupled from it.
2. Add `AgentHost` to a scene object, assign the profile, fill **`instanceId`** — only instances with a stable `instanceId` read and write persistence (fact slots, conversation rounds, affinity); empty runs as a temporary instance and says so in the log.
3. Subscribe to output:

```csharp
host.TokenReceived += delta => bubble.AppendText(delta);      // streaming answer text
host.SayPresented += (actionId, say) => PlaySubtitle(say);     // line spoken before an action
host.TurnFinished += (answer, outcome) => Debug.Log(outcome);   // Completed / Stale / Timeout / Failed / LoopAborted
host.ActiveChanged += active => panel.SetActive(active);

host.SetCoreSnapshot("Backpack: 3 items, quest: find the key"); // low-frequency world state
host.Trigger("The player clicked Talk");                         // starts a turn
host.Notify("The player left the shop");                         // world event, marked as such in the prompt
```

4. World state injected per turn comes from `host.SetCoreSnapshot(...)`: `AgentHost` implements `IWorldContextProvider` itself and returns `coreSnapshot` plus the affinity line. Only if you skip `AgentHost` do you implement that interface. Keep it low-frequency (identity, goals, relationship level); fast-changing values belong in query tools. `QueryableHint` is the single guidance slot on the profile — when non-empty it injects one "You may query: …" line.
5. Without `AgentHost`: `new AgentCore(profile, instanceId, ctx, output, runner, toolGate, toolSet)`, call `core.Tick()` yourself each frame and `Dispose()` on teardown.
6. The `AgentHost` Inspector has a read-only persisted-memory block: with `AgentStateStore = Prefs` and a stable `instanceId` it lists the fact slots stored under `{ProfileKey}#{instanceId}`. Conversation history is not shown and nothing is editable.

---

## Built-ins

| Kind | Names | Carrier |
| --- | --- | --- |
| Global tool | `get_current_time` | `AgentTimeTools` (static) |
| Global action | `write_fact` / `forget_fact` / `search_past_conversation` | `AgentMemory` (self-registered by the kernel) |
| Member tool | `get_agent_transform` | `AgentTransformTools` |
| Member tool / action | `get_navigation_status` / `move_to` / `move_to_player` / `stop_navigation` | `AgentNavigationTools` (`[RequireComponent(NavMeshAgent)]`) |
| Member tool / action | `get_animation_state`, `list_animation_states` / `play_animation_state`, `set_animation_parameter`, `set_animation_speed` | `AgentAnimationTools`; knows only `IAgentAnimationDriver`. The driver lives in the gameplay layer — without it all five reply "animation driver not mounted" |
| Member tool / action | `get_affinity` / `adjust_affinity` | `NpcAffinityController` |

`move_to_player` finds the object tagged `Player`, projects its feet onto the NavMesh and stops `playerStopDistance` (default 1.5 m) back along "player → agent"; the snap radius is `destinationSnapRadius` (default 2 m). Fact slots are injected into the system prompt in full every turn, so the model never needs a "list memories" tool; `search_past_conversation` searches only rounds outside the current history window plus this session's compression archive — no vector search.

## Context & compression

`CompressionConfig_SO` (`Create → LLM → CompressionConfig_SO`) goes on the profile's `CompressionConfig`:

- `ContextWindowTokens > 0` selects token mode with three thresholds `SoftCompactRatio` / `CompactRatio` / `ForceCompactRatio`; `= 0` falls back to round mode (`SlidingWindowRounds × CompressThresholdMultiplier`).
- Token counts are estimates (roughly 1 token per CJK character, 4 characters per token otherwise), calibrated afterwards by the real `prompt_tokens` from responses.
- In the soft band, no summary is produced — `PruneStaleLargeContent` only replaces folded-away replies larger than `MinPruneBytes` with placeholders. ⚠ A no-op in round mode.
- The fold keeps the newest tail up to `TailTokenBudget`; "summary + first short question" stays anchored forever; below `MinFoldTokens` nothing is folded; after two consecutive compactions auto-compression parks itself.
- Summaries are LLM generated, **tried once**; timeout or failure degrades to mechanical folding. To run compression on a small model, put its registration name in `CompressionProviderName` (empty = default provider). Raw rounds may be archived under `persistentDataPath/LLMArchive/<session>/`.

## NPC presentation layer (optional)

`NpcAgentProfile_SO` (identity / personality / speech style / goals & values / knowledge boundary / `InitialAffinity`), `NpcAffinityController` (affinity and relationship level, also callable from gameplay via `AdjustFromGame`), and `NpcGestureCatalog_SO` + `NpcPerformanceProfile_SO` + `NpcSpeechPerformanceController` (local streaming direction: clause splitting, expression, gesture — never sent into the prompt). Animation and gesture backends implement `IAgentAnimationDriver` / `INpcGestureDriver` in the gameplay layer; this plugin references no animation type.

## Editor entry points

- `Lin/LLM/Agent 沙盒` — pick a profile and chat; per-turn injections and declared tools/actions visible.
- `Lin/LLM/请求日志` — latency, token usage, cache hits and tool-call summary per request.
- `AgentHost` Inspector — three tool groups (actions / shared tools / own tools), scan & sync, quick-add member tool components, read-only persisted memory.

## Known trade-offs

- One `LLMSession` allows a single in-flight request; a second call throws.
- `[AgentTool]` supports synchronous methods only; anything async is an `[AgentAction]`.
- No incremental JSON validation while streaming: `AgentToolCallAssembler` emits a call only once its arguments close, so tool results always arrive after the last token.
- Outside Play mode, and in the sandbox, nothing pumps the wall clock (`Tick` is driven only by `AgentHost.Update`), so long actions in the editor are never cut off by timeout.
- Only simple types can enter a schema; serialize structures into a string argument and parse them yourself.

## Design docs

Layering and the per-decision records (`agent-kernel-design.md`, `agent-tool-model-design.md`, `agent-tool-gating-design.md`, `agent-animation-actions-design.md`, `decisions/ADR-0xx-*.md`, …) currently exist only in the framework project's copy of this plugin (`Unity-Framework/Assets/Plugins/LLM/Docs/`); they are not shipped in this folder. Write the ADR before changing architecture, not just the code.
