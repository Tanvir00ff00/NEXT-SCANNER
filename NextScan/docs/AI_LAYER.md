# The AI panel — research before building

Researched 2026-09-22. The owner's design: a new rail section below Capture,
Look, Batch and Pages; selecting it changes the layout so the right-hand panel —
where settings live today — becomes a chat with the model. Quick-action icons
sit above it for one-click work on the open document, the model suggests actions
from what it sees, and Settings gains API keys for Gemini, Claude and OpenAI
plus a thinking level.

What follows is what the providers actually accept, and the decisions that fall
out of it.

---

## 1. Thinking level: one control, three dialects

The owner asked for a thinking mode with medium, high and so on. All three
providers have converged on named levels, so one control can drive all of them:

| provider | parameter | levels |
| --- | --- | --- |
| Claude | `output_config: {effort: ...}`, with `thinking: {type: "adaptive"}` | `low` `medium` `high` `xhigh` `max` |
| Gemini | `thinking_level` (3.1 Pro; earlier models `thinking_config`) | `low` `medium` `high` `max` |
| OpenAI | `reasoning_effort` | `low` `medium` `high` |

So the panel shows **Low / Medium / High / Max**, and the provider adapter maps
it. On OpenAI, Max collapses to High, and the UI should say so rather than
pretending the setting took.

**A correction worth recording.** General web sources still describe Claude's
thinking as `thinking: {type: "enabled", budget_tokens: N}`. That is out of
date: `budget_tokens` is **rejected with a 400** on the current Claude models,
and the fixed-budget concept has been replaced by adaptive thinking plus
`effort`. Anything written against the older shape will fail at runtime, not at
compile time — so the adapter is written from the provider's own current
documentation, never from recollection or from a blog.

## 2. The document is the expensive part, and it does not change

In this panel the same scanned page is re-sent with every chat turn. That is the
single biggest cost in the design, and also the easiest to remove.

Prompt caching matches on a **prefix**, and any byte changed anywhere in that
prefix invalidates everything after it. The order rendered is tools, then
system, then messages. So the layout is forced:

    stable    the system prompt, the tool list, the page image,
              the measurements we extracted from it          <- cached
    volatile  the conversation turns, the current question   <- after the breakpoint

Get this backwards — put a timestamp or a per-request id in the system prompt,
or re-serialise the measurements in a different key order — and the cache
silently never hits. The check is `usage.cache_read_input_tokens`: if it stays
zero across turns, something in the prefix is moving.

## 3. Never count tokens by guessing

Claude exposes a token-counting endpoint (`POST /v1/messages/count_tokens`), and
tokenizers differ between providers and between model generations of the same
provider. A count from a local library such as `tiktoken` is wrong for Claude
and wrong for Gemini.

So the token readout in the panel comes from the provider — the counting
endpoint before sending, `usage` after — and is labelled with which model it
was counted for. A number that is quietly for a different tokenizer is worse
than no number.

## 4. History has three separate mechanisms, and they are not the same thing

For a chat that may run all day over one document:

- **Caching** makes resending the prefix cheap. It does not shorten anything.
- **Compaction** (Claude, beta) summarises earlier context as it approaches the
  window. The response carries compaction blocks that must be appended back
  verbatim — appending only the text silently loses the state.
- **Context editing** *clears* old tool results or thinking blocks. It does not
  summarise.

They solve different problems and can be combined. The panel should default to
caching plus compaction, and leave clearing for the tool-heavy case.

## 5. Memory: ours, not the model's

Claude has a memory tool the model can write to. For this application the more
useful memory is the operator's, not the model's: which corrections they have
made before, how they like a document laid out, what this customer's forms
usually look like.

That belongs in our own store, injected into the stable prefix — where it is
cached, inspectable, editable and deletable by the operator. A model-managed
memory is none of those things.

## 6. Where the keys live

API keys are secrets on a shop machine that other people use. Windows provides
`ProtectedData` (DPAPI), which encrypts under the logged-in user's account with
no dependency and no key of our own to lose.

Keys are entered in Settings, stored encrypted, never written to the settings
`.ini`, and never logged — including in the diagnostics folder, which is the
easy mistake.

## 7a. What a chat interface actually looks like

Researched 2026-09-22, after the first version of this panel was built by
invention and rejected. Five were compared side by side: Claude Code, the Claude
side panel, ChatGPT, Gemini and Copilot Chat in VS Code.

They agree, and the agreement is the finding:

| | where the model and effort live |
| --- | --- |
| Claude Code | under the box, bottom right — `Opus 5  High` |
| ChatGPT | in the box's bottom row — `GPT-6 Astra  Medium ⌄` |
| Copilot Chat | in the box's bottom row — `⫽ Agent   Models` |
| Claude side panel | in the box's bottom row — `Opus 5 ⌄` |
| Gemini | top left of the header — `Flash  Extended ⌄` |

**None of them puts it on a settings page.** The model and the effort are
chosen per question, so they sit where the question is written. Anthropic's own
help says it plainly: the menu next to the send button controls the model, the
effort and whether it thinks.

Three more things all of them do:

* the input and its controls are **one rounded container**, not a box with
  buttons beside it;
* the empty state is a greeting and **named suggestions** — `/check-doc`,
  `/copy-edit` — not a toolbar of icons;
* the header carries new-conversation and history, and nothing else.

What belongs on our settings page is therefore the API key alone: it is the one
part that belongs to the account rather than to the turn.

## 7b. The model list is fetched, never written down

Also the owner's correction. A list of model names in our source is wrong by the
next release, and worse, it hides whatever the account can actually reach.

All three providers list their own:

| provider | call |
| --- | --- |
| Claude | `client.Models.List()` → `ModelInfo.ID`, `DisplayName` |
| OpenAI | `OpenAIModelClient.GetModelsAsync()` → `OpenAIModel.Id` |
| Gemini | `client.Models.ListAsync()` → `Model.Name`, `SupportedActions` |

Gemini says which actions each model supports, so the filter there is the
provider's own answer. OpenAI's endpoint returns embeddings, speech and image
models with no capability flag at all, so that filter is a guess about names and
is written down as one. Anthropic's endpoint returns only message models, so
nothing is filtered.

The list is cached against the key, not the provider: a new key is a different
account and may not reach the same models.

## 7c. An open question for the owner: SDK or raw HTTP

Each provider ships an official SDK. This project has no NuGet packages at all —
the ONNX Runtime is bound by hand over its C API rather than taking a
dependency, and the build is `csc.exe` with no project file.

Two ways forward:

- **Raw HTTP** — `HttpClient` plus the `Json.cs` already in the repository. One
  adapter shape for all three providers, no dependency, consistent with how
  everything else here is built. More work per feature, and we own the drift
  when an API changes.
- **Official SDKs** — less code and typed surfaces, but it means NuGet, three
  packages, and a build that no longer runs from a single compiler invocation.

Raw HTTP fits the project as it stands, and three providers behind one interface
argues for it too. But it is the owner's call, because it is the first real
crack in the no-dependency rule.

**Decided 2026-09-22: the official SDKs.** The AI layer is the one part of this
repository with dependencies, it is built by the .NET SDK rather than by csc,
and its output is kept in `bini` behind a single reference. See STATUS 20.

---

## What gets built, in order

1. **Provider adapter** — one interface, three implementations: send messages,
   stream the reply, report usage, map the thinking level. Keys via DPAPI.
2. **The panel** — rail section, chat, streaming display, thinking-level
   control, token readout.
3. **Quick actions** — the icons. Each is a stored prompt plus the document
   context, so adding one is a data change rather than a code change.
4. **Suggestions** — a cheap first pass over the open document proposes which
   actions apply, shown as icons.

Steps 3 and 4 depend on the document pipeline in `DOCUMENT_PIPELINE.md`: the
measurements are what makes the model's job easy, and they are the same
measurements either way.
