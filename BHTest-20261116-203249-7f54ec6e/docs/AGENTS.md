# Tessera AI agents (phases 1 and 2: triage, verifier, explainer)

Optional. Tessera, the collector and the viewer work exactly as before with no AI at all, including on an air-gapped machine. The agent layer sits next to Scout (the existing assistant) and reuses its connection: you press **Connect** in Scout setup with your API key, and the agents use that key, that service and that model list. The key is never saved.

## What phase 1 contains

* **Triage agent.** Reads the saved candidate attack paths, checks the hops with read-only tools, and ranks the candidates (high, medium, low) with a short reason per candidate and a verdict per hop.
* **Tool layer.** Ten read-only tools that wrap the viewer's own query functions (see the list below).
* **Findings store.** A small separate SQLite file next to the main graph, named `<graph>.findings.sqlite`. Agents never write to the main database; it is opened read-only for every tool call.
* **Investigate tab.** Goal box, live activity, findings grid, detail pane, log view (filter, search, export), known/accepted list, and an **AI settings** screen.

* **Verifier agent (phase 2).** Re-checks every claim in a finding against the raw data and marks it confirmed or rejected. It is the only gate to the report.
* **Explainer agent (phase 2).** Turns the stored findings into a plain English story, hop by hop, and names the single fix that breaks the most paths.

Not yet built: hunter and coordinator agents, guarded SELECT, live Active Directory queries, collection diffing, conversational mode. Their settings rows exist so limits can be set ahead of time.

## Verify, explain, report

Triage stores every finding as **unverified**. Use **Verify selected** or **Verify all unverified**. The check has two layers and the code has the last word:

1. Before any model call, the code checks that the saved path exists and that every hop is a real traversable edge in the collected data. If not, the finding is rejected straight away and no tokens are spent.
2. The verifier model then tests each claim (path, hops, account type, disabled state, group size, role) with the read-only tools and returns one check per claim: supported, contradicted or unverifiable. The code decides the verdict: any contradicted claim, or no supported claim at all, means rejected, whatever the model says. The checks are saved with the finding and shown in the detail pane.

A confirmed verdict is tied to the snapshot it was checked against. If triage is re-run after the data changes, the finding goes back to unverified.

**Explain and suggest fix** runs the explainer over the stored findings. It uses `common_hops`, which counts in code which relationship appears in the most stored paths, so the fix is chosen from real overlap. Candidate ids the model lists under "breaks" must be stored findings or they are removed. The result appears in the **Explanation** tab and is kept in the findings file.

**Export verified report...** writes a markdown file containing confirmed findings only (with the verifier's checks) plus the latest explanation. Unverified and rejected findings are counted but never listed. This is the only report the agent layer produces.

Phase 2 adds four tools: `list_findings`, `get_finding`, `edges_between` (direct relationships between two objects, read from the raw edge data) and `common_hops`. The verifier and explainer have their own tool allowlists; triage cannot call them. `node_info` now also shows the collected disabled flag.

## How the model talks to the tools

The service returns free text only (no native tool calling), so the prompt tells the model to reply with exactly one JSON object each turn:

```
{"action": "list_candidates", "args": {"limit": 10}, "reason": "Start with the highest ranked candidates."}
{"final": true, "answer": {"ranked": [ ... ], "notes": "..."}}
```

The loop strips code fences and prose, parses the first complete JSON object, and checks it in code: the action must be on the agent's allowlist and switched on, required arguments must be present, argument types must be right, and there must be exactly one action. A bad reply gets a specific error message and a request to correct it; after the configured number of retries (default 3) the run stops and records the failure. Tool results go back as the next message, labeled as tool output and as data, not instructions. Directory names and descriptions can be written by an attacker, so the prompt tells the model never to follow instructions found in them.

Each agent has a maximum number of steps, a token budget and a per-request timeout. When the step budget runs out, one last request can only be a final answer.

## Triage and shared tools (all read-only)

| Tool | What it returns |
| --- | --- |
| `list_candidates` | Saved candidate paths, best score first, paged (offset, limit, query) |
| `candidate_path` | The recorded hops of one candidate and why it ranked |
| `node_info` | One object: kind, device role, properties of note, relationship counts |
| `find_node` | Search objects by part of a name or id |
| `outgoing`, `incoming` | Relationships that start or end at an object |
| `group_members` | Count and sample of direct members |
| `acl_edges` | Permissions other objects hold over an object |
| `shortest_path` | Shortest recorded path between two objects |
| `tier_zero` | The tier zero objects from your profile, resolved |

Results are compact text with counts and truncated samples, and say when more exist. Device roles from your roles file are attached to object labels automatically.

Safeguards enforced in code, not only in the prompt: the executor refuses unknown tools, tools outside the agent's allowlist, and tools switched off in the settings, even if the model asks for them by name; the main database is opened read-only with a query time limit; findings must name a real saved candidate or they are dropped and logged; live AD is off and has no tool in this version.

## Settings

One screen (Investigate, **AI settings**) over one file, `%LOCALAPPDATA%\Tessera\ai-config.json`. Missing values use defaults, so a partial or old file still loads.

* **General:** turn the AI layer on or off (off removes the Scout and Investigate tabs and the Ask Scout buttons after a restart), optional service address, auth header and scheme, default model, pause between requests, retries per step.
* **Agents:** one row per agent with a model dropdown filled from the service's model list ("use default" falls back to the default model, then to the model chosen in Scout setup), plus max steps, max tokens, per-request timeout, max result size and reply size. The hunter defaults are the loosest. No model names are built in. If a chosen model is no longer offered, a warning shows in the Investigate header and the log, and the default is used. The model is logged on every request.
* **Tools:** switch any tool off for all agents.
* **Environment:** a named profile with your tier zero objects (names, ids or patterns) and free-text notes such as naming conventions. Agents are given these as context.

The service address defaults to the built-in `https://api.genai.mil`. A custom address is accepted only if it is https and ends in `.mil`; anything else is ignored. Size and rate limits for genai.mil are not assumed anywhere in the code; everything is in the config file.

## Findings, known list and logs

Findings are stored per graph, keyed by candidate, so a re-run updates rather than duplicates. **Accept risk** and **False positive** move a finding to the known list with your note; later runs leave it out. The known list lives in the same findings file, so keeping that file with the graph (or on a share) gives a team one list. Open the list from **Known list...** to put an entry back.

The log records every model request and response, tool call and tool result with time, model and Windows user name, in categories you can filter (model_request, model_response, tool_call, tool_result, parse_error, live_ad, error, info) and search. Export writes a tab separated file. The log lives in the findings store; there are no separate log files.

## Privacy note

Scout can replace object names with neutral tokens before sending. The agents cannot, because their tools must look objects up by real name, so tool output (names, kinds, relationship types, device roles) is sent to the service as the run proceeds. Use the agents only where that is acceptable for your environment.

## Changes to existing files

All additive: `GenAiClient` gained optional static settings (address override that must be https and .mil, auth header and scheme, request gap) and a constructor with a timeout; `ScoutSubjects.Properties` became internal; the viewer gained an Investigate tab, an "Investigate this object" button, and hides the Scout tab and Ask Scout buttons when the AI layer is switched off. Nothing in Scout's behavior changed.
