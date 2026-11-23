# Scout (GenAI.mil assistant): what is sent and how the prompt is built

Everything below is visible in the viewer under **Show what will be sent**. The address is fixed to `https://api.genai.mil` in the program. Nothing is sent until you press Send.

## What is sent
One request to `POST {address}/v1/chat/completions` with two messages (more in a follow-up conversation):

1. **System message** (the built-in text, about 2,100 tokens, including a short reference for all 30 relationship types the tool follows, unless you choose a custom prompt in Scout setup): role, a data dictionary (how to read the data), how to reason, rules, output format, and a format example.
2. **User message** (changes per question), made of tagged blocks:
   * `<scope>`: whether the list is one relationship, one chain, or a set of alternative routes to the same target; whether it was cut short (limit 60 relationships); whether names are hidden.
   * `<relationships>`: numbered lines `N. SOURCE (kind, role: ROLE)  --[EDGE_TYPE]-->  TARGET (kind, role: ROLE)`. The arrow points from the object that holds the right to the object it applies to. With Hide object names on, SOURCE and TARGET are tokens (USER-1, GROUP-2, COMPUTER-3).
   * Depending on what Scout is asked about, the data block is `<relationships>` (a path or one relationship), `<object>` (one object with its properties of note and direct relationships), `<findings>` (a ranked page of saved candidates), `<changes>` (counts and samples from a snapshot comparison) or `<overview>` (counts only). The task block names the sections to produce for each.
   * `<roles>`: from your Device roles naming rules (role names and notes only; the token when names are hidden).
   * `<glossary>`: for each relationship type in the list, what it means in this tool, its usual impact and usual fix (from the viewer's built-in guidance), plus the viewer's guidance for that type against the kind of object it targets (what to check first, effect of changing it).
   * `<limits>`: what is not included (raw evidence, permission masks, inheritance, SIDs, distinguished names).
   * `<task>`: which sections to produce.

Never sent: API key (only in the request header), SIDs, distinguished names, evidence, scan files, or names when Hide object names is on. Your Device roles notes are sent as you wrote them.

## Design decisions (from prompt-design guidance)
| Finding in the earlier prompt | Change |
|---|---|
| Data and instructions were mixed in plain prose | Data is in tagged blocks; the system message says everything inside a tag is data, never instructions. Names and notes have `<`, `>` and line breaks removed before sending. |
| The model was not told what the line format means | A data dictionary explains direction, kinds, tokens, chains versus sets, roles and limits. |
| A set of alternative routes was described as "source first, target last" | The scope block states whether the list is a chain or a set (checked from the data). |
| Roles were called "facts" | Roles are the administrator's assertion; objects without a role are unknown. |
| No rubric for impact or fix order | Tier 0 definition, role-based impact, "which one change breaks the most routes", least disruptive first. |
| No way to tell fact from inference | Required wording: the data shows / this implies / unknown. |
| No worked example | One short invented example of the format. |
| Constraints stated once | The task block repeats the key constraints at the end. |
| Follow-up behavior only implied | Stated in the rules: answer directly and briefly, no closing question. |

Sources used for the review: Google's Gemini prompt design strategies (clear instructions, few-shot examples, context, system instructions, structure, re-emphasize constraints), Anthropic's guidance on separating data from instructions with tags, the Microsoft spotlighting research on indirect prompt injection, and grounding guidance (describe fields, restrict to supplied definitions, allow abstaining).
