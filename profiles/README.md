# Profiles

A profile binds seats to models. `ds-*` are the live ones; everything else is
either an example, a retirement candidate, or a variant kept for a specific
comparison.

## The live set

Names say what they are, so you can pick one without opening it. `ds` is
DeepSeek; the rest reads left to right as **shape → model → who leads**.

| Profile | Shape | Model(s) | Cost |
|---|---|---|---|
| `ds-solo-flash` | one agent | Flash (local) | free |
| `ds-solo-pro` | one agent | Pro (cloud) | paid |
| `ds-team-flash` | leader + members | Flash everywhere | free |
| `ds-team-flash-escalate` | leader + members | Flash, **plus one Pro seat** | mostly free |
| `ds-team-lead-pro` | leader + members | Pro leads, Flash implements | mixed |
| `ds-team-pro` | leader + members | Pro everywhere | paid |

`ds-team-flash` is the workhorse: free, and the one with the most proving
behind it.

### The two mixed profiles are near-opposites

Easy to confuse by name, so state it plainly — the difference is **what you
are buying Pro for**, and **whether you always pay for it**.

- **`ds-team-lead-pro`** buys Pro for *thinking*. The leader and reviewer run
  Pro; implementer and researcher run Flash. Pro is on the critical path of
  every run, so **you pay on every run**.
- **`ds-team-flash-escalate`** buys Pro for *typing*. Everything starts on
  Flash; a second implementer seat (`implementer-pro`) runs Pro and is only
  reached after a subtask fails the gate twice. On a clean run **Pro is never
  called and the run is free**.

So: pay-always-for-better-judgement, versus pay-only-when-stuck-for-a-better-
attempt. They are not variations on one idea.

## ⛔ Names changed on 2026-08-25 — historical artifacts use the OLD names

The `dsv4-` prefix said which model generation it was and nothing about what
the profile did, which made the set unreadable at a glance. The six live
profiles were renamed:

| Old | New |
|---|---|
| `dsv4-solo-flash` | `ds-solo-flash` |
| `dsv4-solo-pro` | `ds-solo-pro` |
| `dsv4-team-local` | `ds-team-flash` |
| `dsv4-team-escalating` | `ds-team-flash-escalate` |
| `dsv4-team-hybrid` | `ds-team-lead-pro` |
| `dsv4-team-pro` | `ds-team-pro` |

**This was a rename, not a re-configuration** — the bindings, prompts and
caps are unchanged, so results either side of it are comparable and may be
pooled. That is the opposite of the tier6 ruler change (see
`suites/team-games-tier6.yaml`), where results genuinely must not be pooled.

⚠ Everything already written under `data/benchmark-runs/` still says
`dsv4-*`, and was deliberately left alone: those files record what actually
ran, and rewriting them would falsify the record. Read old artifacts through
the table above. The remaining `dsv4-*` files in this directory were NOT
renamed — they are retirement candidates pending a decision, and renaming
them would be wasted work if they are deleted.

## ⛔ Suites were repointed on 2026-08-25 — results are NOT poolable across it

Until this date **every one of the 34 suites named a profile that was absent,
dead, or both**, and nothing complained. Thirty named `coding-team`, which does
not exist in this directory — so it resolved out of `~/.vett/profiles` onto
`old-gpu-a`, a host that has been down for weeks, running the retired AEON
Qwen3.6-27B. `vett validate` stayed green throughout: a suite naming a profile
that resolves *somewhere* is structurally fine.

| Suites | Was | Now |
|---|---|---|
| 30 `team-*` | `coding-team` (absent → dead `old-gpu-a`) | `ds-team-flash` |
| `spec-authoring-tier0`, `-tier0-5` | `spec-authoring-solo` on dead `old-gpu-a` | same profile, **moved to `gpu-1` Flash** |
| `spec-authoring-tier0-oneshot` | `spec-authoring-oneshot` on dead `old-gpu-a` | same profile, **moved to `gpu-1` Flash** |
| `team-python-web-tier2` | `coding-team-v2` (absent → dead `old-gpu-a`) | ⛔ **left broken on purpose** |

⚠ **Unlike the profile rename, this IS a re-configuration.** The 186 recorded
runs under `coding-team` in `data/benchmark-runs/` ran on AEON Qwen3.6-27B in
**May 2026**; anything run now runs on DeepSeek V4 Flash. **Never pool the two.**
The discontinuity is not caused by this repoint — it was caused by the host dying
and the model being retired, which closed that corpus in May. The repoint only
moves the suites off the corpse and gives the boundary a name.

`team-python-web-tier2` is untouched because *both* its A/B arms are dead;
repointing only its default would read as repaired while measuring nothing.
Reviving it is a scoping call. `tests/Vett.Tests/SuiteProfileBindingTests.cs`
now fails if any other suite drifts back into this state, and also fails if that
exemption stops being necessary — so it cannot quietly become permanent.

## ⛔ `aeon-mtp` is TWO DIFFERENT MODELS depending on the host

- On dead `old-gpu-a` it was **AEON Qwen3.6-27B**.
- On live `gpu-1` it is a back-compat **alias for DeepSeek-V4-Flash**
  (`/v1/models` reports both `aeon-mtp` and `deepseek-v4-flash` with
  `root: deepseek-ai/DeepSeek-V4-Flash-DSpark`).

So repairing a dead profile by moving **only the endpoint** leaves it naming
AEON, running DeepSeek, and validating green. Both lines have to move. Measured
2026-08-25: no existing artifact is contaminated — every recorded `aeon-mtp` run
paired with `old-gpu-a` — but the trap is live for anything run from now on. **When
reading old results, the model name alone does not identify the model; pair it
with the endpoint.**

## ⛔ Three stores resolve, and the first one wins

`Profile.cs` looks in this order:

1. `<cwd>/profiles` ← **this directory, when you run from the repo root**
2. `~/.vett/profiles`
3. `<install-dir>/profiles`

**cwd wins.** Runs launched from the repo get these files. Anything launched
from elsewhere falls through to `~/.vett/profiles`, which is a SEPARATE,
SHARED store that is not synced from here.

⚠ Store 3 is not a stale copy of store 1 — it is a **snapshot taken when the
tool was built**. `Vett.csproj` copies `profiles/**` and `suites/**` into the
output (`CopyToOutputDirectory`), so they ship inside `vett` itself. The
installed `vett` is from **2026-07-13**, so it carries July's profiles and
July's suites, i.e. the `dsv4-*` names and the `coding-team` bindings. Editing
this directory does not change them; only republishing the tool does.

⚠ Measured 2026-08-25: nine `dsv4-*` profiles in `~/.vett/profiles` point at
`old-gpu-b:8000`, which is dead — they are stale copies from before the
move to `gpu-1`. A fix written here is not a fix present there. Check with:

```
cd ~/.vett && vett validate --check-endpoints
```

## Checking a profile actually works

`vett validate` checks SHAPE only — a profile naming a switched-off host or a
model the server does not serve parses perfectly and earns a clean tick. That
is not hypothetical: 31 suites pointed at a dead `old-gpu-a` for weeks
while validating green.

```
vett validate --check-endpoints
```

asks each seat's endpoint whether it is up and actually serves the named
model. Every call is `GET /models` — a catalogue read, never a completion —
so it costs nothing even on a metered provider.

Read its output with two things in mind:

- **"reachable" is necessary, not sufficient.** It says the seat can connect,
  never that the run will succeed.
- **"no endpoint declared" is a third state**, printed as a warning and NOT
  given a tick. Nothing was asked, so nothing is known.

The footer prints how many *distinct* endpoints were probed. If that number
starts approaching the profile count, the catalogue cache has stopped being
shared and the verdicts are no longer trustworthy — see `EndpointProbe`.
