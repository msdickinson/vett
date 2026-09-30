# OpenHands fidelity audit

VETT ships an `openhands` profile (`profiles/openhands.yaml`) that is meant to
reproduce the agent loop shipped in the [OpenHands](https://github.com/All-Hands-AI/OpenHands)
SDK (MIT licensed), version 1.14.0, closely enough that a benchmark run
through VETT and a benchmark run through OpenHands itself are comparable.
The profile exists for one reason: VETT's whole value proposition is
*comparing profiles on the same benchmark*, and that comparison is only
meaningful if at least one profile is a known, trustworthy reference point.
`openhands` is that anchor — see [DESIGN.md](DESIGN.md#why-an-openhands-baseline-profile)
for why the anchor matters.

This document is the honest, public account of how close that reproduction
actually gets, based on a direct audit against the OpenHands 1.14.0 source
and against the config of a real local SWE-bench Verified run. It corrects
an earlier internal claim that the profile was a "byte-for-byte mirror."
It is not. One piece of it is byte-identical. Most of the rest is close,
with specific, documented gaps. We would rather publish the gap list than
the marketing claim.

## What was checked

Eight areas, each compared directly against the OpenHands 1.14.0 source and,
where relevant, against a real local reference run's recorded configuration
(used as ground truth in preference to the SDK's abstract defaults, since
that's what actually produced the trajectories we're comparing against):

1. **System prompt** — the rendered prompt template
2. **Tool schemas** — the OpenAI-style function-calling JSON schema for each tool
3. **Tool implementations** — actual runtime behavior of those tools, not just their advertised schema
4. **User message template** — the per-instance prompt built from a SWE-bench problem statement
5. **LLM request payload** — sampling parameters, timeouts, retries, and related request fields
6. **Middleware / condenser chain** — what runs between LLM turns to manage the conversation
7. **Stop conditions** — iteration caps, timeouts, and anything that can end a run early
8. **Tool-call wire format** — the literal JSON shape of tool calls on the wire

## Method

- The system prompt was rendered from OpenHands' actual Jinja2 template with
  the same parameters VETT's target model family would use, then compared
  byte-for-byte (SHA-256 + `diff`) against VETT's static copy of that prompt.
- Tool schemas were generated live from an editable install of the OpenHands
  SDK and diffed against VETT's own schema fixtures.
- Tool implementations were compared by reading VETT's Go/​C# tool code
  against OpenHands' Python executors directly — not just their schemas.
- The LLM request payload was **not captured live on the wire** for this
  pass; source modification and long test runs were explicitly out of
  scope, so this section is inference from a recorded run's configuration
  plus code reading, not an actual request-body diff. That's a real
  limitation, noted below, with a documented method for closing it later
  (log completions on both sides against the same benchmark instance, diff
  the first request body).
- Wire-level tool-call format was checked by reading both clients'
  deserialization code against the inference server's tool-call parser
  configuration, and cross-checked against a stuck-loop-on-malformed-output
  metric that would have shown nonzero if parsing were actually breaking.

## Results

**Bottom line: the system prompt is byte-identical. Almost everything else
has drifted in some specific, bounded way.** That's a real finding, not a
caveat to bury — an earlier internal description of the profile as a
"faithful byte-for-byte mirror" was wrong, and the profile's own
description was corrected to say so.

### System prompt — match

Byte-identical. Both renders came out to the same length with matching
SHA-256 digests. One thing worth being honest about: this match is
partly a coincidence of which model family the profile targets — OpenHands
renders an optional model-specific prompt partial that silently resolves to
empty for this family. Switching the profile to a different model family
could silently break this match without anyone noticing, which is exactly
the kind of fragility a "byte-for-byte" claim should not paper over.

### Tool schemas — 4 of 5 exact, 1 drift

Four of the five built-in tools (`terminal`, `task_tracker`, `finish`,
`think`) have schemas that match OpenHands exactly. The fifth,
`file_editor`, differs in one description field: VETT's schema embeds a
static working-directory string where OpenHands dynamically injects the
live working directory into the description text at request time. Cosmetic,
but a real difference an LLM could in principle notice.

### Tool implementations — 3 of 5 faithful, 2 with real gaps

`think` is byte-exact. `finish` is behaviorally equivalent (a different
internal completion marker, same effect on the loop). `file_editor` is a
faithful port — same size limits, same tab-expansion, same output shape —
with one wording difference in its truncation notice.

The two real gaps:

- **`task_tracker` is a stub.** It accepts input, discards it, and returns a
  constant string. It does not persist any task state. This is consistent
  with an observed pattern in trajectories: the model rarely seems to get
  real value out of calling it, which lines up with the tool doing nothing.
- **`terminal` has a different output envelope** (different field
  order/labeling than OpenHands' own) and is **missing the soft-timeout /
  continued-input semantics** that its own schema advertises but that
  nothing in VETT's executor actually implements.

### User message template — the largest drift found

VETT's SWE-bench user template is a short, minimal prompt. OpenHands ships
a much longer, explicitly phased prompt (roughly 15x longer) that walks the
model through distinct stages of exploring, fixing, and verifying a
SWE-bench instance. This was already a known, flagged gap in the profile's
own source comments before this audit — this pass confirmed it was never
actually closed.

### LLM request payload — several drifts, and one real limitation in how we know that

Caveat first: this section is inference from a recorded configuration plus
source reading, not a captured live request diff, for the reasons noted
above under Method.

What matches: model routing, temperature (`1.0` on both), top-k and
input/output token limits (both default/unset), request timeout (300s),
retry count (5), non-streaming mode, native tool-calling, and seed handling.

What drifts:

- **`top_p`** — OpenHands sends nothing for this field (the server defaults
  to 1.0). VETT explicitly sends `0.95`. This is a real, measurable
  difference in sampling behavior, not cosmetic — the profile's own YAML
  now carries a comment on this exact point and leaves `top_p` unset for
  that reason.
- **Prompt caching** — enabled on OpenHands' side, not implemented in VETT.
- **Reasoning-model fields** (`reasoning_effort` and similar) — set in
  OpenHands' config, not implemented in VETT. Currently harmless, because
  the model this profile targets ignores them — but they'd matter the
  moment the profile points at a reasoning-capable model.

### Middleware / condenser chain — the single biggest gap

OpenHands' SWE-bench configuration runs with a **conversation-summarizing
condenser** enabled by default: once history passes a size threshold, it
summarizes and drops the middle of the conversation, keeping the earliest
context intact. **VETT's `openhands` profile has no equivalent and keeps
every event for the life of the run.** This is flagged as the most likely
explanation for a specific failure pattern seen in earlier trajectories: a
valid patch produced mid-run, followed by errors in later iterations —
consistent with unbounded context growth rather than a model or tooling bug.

### Stop conditions — drift

VETT's `openhands` profile originally shipped with `max_iterations: 100`.
That number turned out to be based on a wrong premise — an earlier internal
note claimed 100 was "the V1 default." It is not: the OpenHands benchmarks
harness's actual default is 500, and the real reference run this profile
is meant to match used 300. The profile has since been corrected to
`max_iterations: 500`, citing the exact line in the OpenHands benchmarks
source as justification (see `profiles/openhands.yaml`). VETT also applies
a per-instance wall-clock cap that OpenHands does not have (OpenHands has
no equivalent — its iteration budget is the only cap); VETT's is documented
as generous enough not to bind in practice. Separately, OpenHands runs an
`AgentFinishedCritic` that can reject a premature `finish` call and force
the model to continue, up to a few times; VETT's `finish` is unconditionally
terminal — there's no equivalent second opinion.

### Tool-call wire format — match

Both stacks produce the same OpenAI-style `tool_calls[]` JSON shape on the
wire — the inference server's tool-call parser fully owns translating the
model's native tool-call syntax into that shape, so this is a property of
the server configuration both stacks share, not something either client
does differently. Neither client sends an explicit stop sequence for it.
Corroborated empirically: a monologue/malformed-tool-call-loop metric that
would have shown a nonzero rate if parsing were breaking has stayed at zero
across every baseline run.

## What this means in practice

"Matches OpenHands within stated bounds" is the honest claim, not
"indistinguishable at the wire level." Concretely:

- If you're comparing pass rates between VETT's `openhands` profile and a
  real OpenHands run on the same model and instances, expect them to be
  close but not necessarily identical, and treat the missing condenser as
  the most likely source of any systematic gap on longer-running instances.
- If you're using `openhands` as VETT's calibration anchor for evaluating
  *other* profiles, the anchor is solid on the parts that matter most for
  that purpose (system prompt, most tool behavior, wire format) and
  explicitly imperfect on context management and stop-condition tuning.
- The profile's own YAML description was updated to state precisely this,
  rather than repeat the earlier "faithful mirror" language.

## Fixing the gaps

Ranked roughly cheapest-to-most-expensive, based on this audit:

1. Fix the `file_editor` schema description to build the working directory
   dynamically instead of hardcoding it.
2. Replace the `terminal` tool's output envelope to match OpenHands' field
   order/labeling, and either implement or drop the soft-timeout semantics
   the schema currently advertises.
3. Port the real phased SWE-bench user template instead of the current
   minimal one.
4. Implement an equivalent conversation-summarizing condenser — the
   highest-value fix, given it's the leading suspect for the observed
   later-iteration failure pattern.
5. Implement `task_tracker` for real, or remove it from the tool list
   rather than ship a stub.
6. Add an equivalent to `AgentFinishedCritic` for profiles that want a
   second opinion before accepting a `finish` call (VETT's own critic
   middleware, described in [DESIGN.md](DESIGN.md#middleware-compaction-permissions-and-runaway-detection),
   is a more general version of the same idea and could be wired in here).

None of these are required for the profile to be useful as a rough
reference point today — they're required for the word "mirror" to mean
what it says.

## Credit

This profile exists because [OpenHands](https://github.com/All-Hands-AI/OpenHands)
publishes its system prompt, tool schemas, and agent loop behavior openly
under the MIT license. Everything in `profiles/openhands.yaml` and the
associated prompt/template fixtures is a reproduction of, or derived from,
OpenHands SDK 1.14.0 — full credit to the OpenHands project and
contributors. Where this document describes a drift or a gap, that's a
statement about VETT's reproduction, not about OpenHands.

## Caveats

- The request-payload comparison (section 5) is inference from a recorded
  configuration and source reading, not a captured live wire diff. A
  concrete method for closing that gap is documented but wasn't run for
  this pass.
- The byte-identical system-prompt result is model-family-specific, as
  described above, and could silently stop being true if the profile is
  retargeted at a different model family.
- The reasoning-model-specific request field gaps are currently harmless
  given the model this profile targets, and only become relevant if that
  changes.
- This audit reflects OpenHands SDK 1.14.0 and one internal reference run's
  configuration at a specific point in time. OpenHands moves; so does this
  profile. Treat this document as a snapshot, not a standing guarantee.
