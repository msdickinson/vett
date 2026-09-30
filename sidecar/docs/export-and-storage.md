# Vett Run Export, Storage, and Reproduction

This doc captures the full design for how Vett runs are stored, retained, exported into other formats, compressed, and shared as reproduction archives. It's a forward-looking spec for Phase 2-4 features that Phase 1 doesn't implement but MUST not paint itself into a corner against.

**Phase 1 implements only the baseline native trace format.** Everything else here is a future deliverable, but the Phase 1 architecture must support it without retrofit. Specific Phase 1 requirements are called out in §13.

## 1. The fundamental architecture: native is the source of truth

Vett's **native per-instance JSONL trace** (per `trace-format.md`) is the one canonical record. Every other format Vett produces is a **mechanical projection** of the native data. There is no "alternate native format." There is no "format negotiation." There is one source-of-truth schema, and many output projections.

```
Native trace (JSONL per instance + summary.json)
  │
  ├──────► swebench format (predictions JSONL)
  ├──────► openhands format (one line per instance)
  ├──────► swe-agent format (.traj per instance)
  ├──────► flat directory (patches + markdown + csv)
  ├──────► csv / json (analysis-friendly)
  ├──────► markdown report (human readable)
  ├──────► huggingface model card
  ├──────► reproduction archive (with manifest + reproduce.sh)
  └──────► all (zip of every format above)
```

If a format needs information not captured in native, the answer is: **add it to native first, then derive the projection.** Never invent a parallel storage format.

## 2. The two-file pattern: side-by-side summary + trace

Every run produces TWO file groups that live next to each other:

### Summary files (always loaded for browsing — small, fast)
```
results/<run-id>/
├── summary.json            (~5 KB)   — run-level aggregate, hot path for browsing
└── summary.detail.json     (~150-250 KB) — per-instance metadata array
```

**`summary.json`** holds run-level aggregate stats only. It's tiny enough that 1000 of them load in a second when AI Timeline or another tool is showing the run list.

**`summary.detail.json`** holds the per-instance array (instance ID, resolved/failed, iteration count, token usage, duration, patch size, end reason, tool call counts). Loaded only when the user clicks into a specific run's dashboard. Still small enough for instant load.

### Trace files (per-instance, loaded only on drill-in — bigger, lazy)
```
results/<run-id>/
├── instances/
│   ├── astropy__astropy-12907.trace.jsonl   (~1-2 MB raw)
│   ├── astropy__astropy-13033.trace.jsonl
│   └── ...
└── run.trace.jsonl         (run-level events: run_start, instance_start, etc.)
```

Per-instance JSONL with one event per line. Streamable during run, debuggable by hand, parseable by any JSONL reader. Loaded ONLY when the user clicks into a specific instance to see the timeline.

### Why the split matters

This pattern enables the entire tiered storage / browsing / reproducibility story. The summary is the API for high-level work; the trace is the deep-dive content. They're decoupled so you never load all traces just to browse runs.

## 3. Three-tier loading model (for viewers like AI Timeline)

When a viewer opens, it loads in tiers based on what the user is actually doing:

| Tier | What loads | When | Per-run cost |
|---|---|---|---|
| **Tier 1: Run index** | Just `summary.json` for every known run | At viewer launch | ~5 KB × N runs |
| **Tier 2: Run details** | `summary.detail.json` for one run | When user clicks a run | ~150-250 KB |
| **Tier 3: Instance trajectory** | One `<id>.trace.jsonl` file | When user clicks an instance | ~1-2 MB raw |

**Tier 1 is the most loaded and the smallest.** Even at 10,000 runs it's only 50 MB of summaries — a viewer can hold it all in memory. Tier 3 is loaded one at a time, on demand, only when the user is actively investigating.

**Critical: comparison views operate on Tier 1 + Tier 2 data only.** Comparing 5 runs side-by-side never requires loading any trajectories. Filter/search operations also work on Tier 1+2. This keeps the viewer fast even across thousands of runs.

## 4. Sizes at scale — real numbers

Reference from the openhands-full-500 run on the runner box at `~/openhands-eval/benchmarks/results/openhands-full-500-backup-django11119/.../`:

| File | Size | What it is |
|---|---|---|
| `output.jsonl` | 624 MB | Full trajectory, one line per instance |
| `output.critic_attempt_*.jsonl` | 635 MB total | Critic retries (openhands-specific, Vett doesn't have this) |
| `output_errors.jsonl` | 4 MB | Error log |
| `output.swebench.jsonl` | 3 MB | Submission file (patches only) |
| `conversations/` | ~900 MB | Per-instance tar.gz archives |
| `logs/` | ~10 MB | Per-instance execution logs |
| **Total** | **2.2 GB** | for 500 instances |

**Vett baseline (no critics, simple per-event JSONL):** ~700 MB for 500 instances uncompressed.

### What 1000 runs would cost at various optimization levels

| Format | 1000 × 500-instance runs | Compressed (gzip) | Compressed (zstd) |
|---|---|---|---|
| Native raw JSONL (Phase 1 baseline) | ~700 GB | ~100 GB | ~80 GB |
| Native + string interning | ~300 GB | ~40 GB | ~30 GB |
| Native + interning + content-addressed observations | ~180 GB | ~25 GB | ~18 GB |
| Hermetic (with Docker images, model weights, binaries) | 30-50 TB | impractical | impractical |

**The practical end state for 1000 runs**: ~25-50 GB on disk if you use native compact + zstd. That's a USB stick. The "TBs" answer is only true if you both keep raw JSONL AND don't compress AND keep hermetic archives for everything — which nobody should do.

### Realistic lifetime estimate

Over a 2-year Vett project, you'd realistically run maybe 300-400 SWE-bench-Verified-sized benchmarks. At the optimized format that's **~10-20 GB total of historical run data** plus whatever you decide to hermetic-archive for specific demos/submissions.

## 5. Compression strategy

### What to compress, when

- **Phase 1**: write raw JSONL during the run, no compression. Streamable, tail-able, debuggable. Disk cost is fine for a few hundred runs.
- **Phase 2**: add a compress-on-completion step that zstds the per-instance JSONL files after the run finishes. Cuts storage 7-8× immediately. Trace files become `<id>.trace.jsonl.zst` — readable with `zstdcat` or any zstd lib.
- **Phase 3+**: introduce optimized formats with string interning and content-addressed observations (see §6).

### gzip vs zstd

Both are lossless. zstd is the better default for new use:
- zstd compresses 5-10% better than gzip on JSONL
- zstd decompresses 3-5× faster than gzip
- zstd is well-supported in Go (`github.com/klauspost/compress/zstd`) and in browsers via WebAssembly modules
- gzip is more universal — every browser handles it natively via `Content-Encoding`

**Vett uses zstd by default** for archival. **Exports can offer gzip** as a `--compress gzip` option for compatibility with tools that don't support zstd.

### Always-lossless guarantee

Compression is a property of the file on disk, not the content. Compressed and uncompressed forms produce identical bytes when decompressed. **All compression in Vett is lossless**, full stop.

## 6. Optimization roadmap (Phase 2+ format improvements)

Your trace can shrink dramatically without losing data via these techniques. Each is **fully lossless and round-trip reversible**.

### v1: raw JSONL (Phase 1)
Per-event lines, every event includes full strings (tool names, paths, observation text). Big but simple.

### v2: string interning (Phase 2 candidate)
Add a `header` event at the top of each trace file containing a string table:

```json
{"event":"header","v":2,"strings":{"1":"terminal","2":"file_editor","3":"agent","4":"/testbed/astropy/modeling/separable.py"}}
{"event":"tool_call_start","tool":1,"session":3,"args":{"path":4}}
```

Tool names, session IDs, repeated file paths, and any string longer than ~40 bytes get an integer ID and are inlined elsewhere. Expanding back is mechanical: substitute IDs for strings.

**Estimated savings:** 30-50% on raw JSONL. A ~700 MB run drops to ~400 MB.

### v3: content-addressed observations (Phase 2-3 candidate)
Tool result strings (which can be 16 KB each for file views) are stored once in a separate section, hashed, and referenced by hash everywhere they're needed:

```json
{"event":"tool_call_end","call_id":"c1","tool":1,"result_ref":"sha256:abc123","result_length":512}
```

When the agent reads the same file twice, the result is stored once and referenced twice. Common in SWE-bench where agents re-list directories or re-grep for symbols.

**Estimated savings:** another 20-40% on top of interning. Combined: 50-75% smaller than raw JSONL before compression.

### v4: delta encoding of message history (Phase 3+ candidate, lower priority)
Store only NEW assistant messages and observations per iteration; readers reconstruct full conversation by accumulating. Saves another 20-30%, complicates random access, lower payoff. **Defer until other optimizations don't suffice.**

### Backwards compatibility rule
**Every event line carries a `v` field.** Phase 1 emits `v: 1` events. Phase 2 may add `v: 2` event types (like `interned_ref`). Phase 1 readers must tolerate unknown event types (skip with warning). Phase 2+ readers must handle both v1 and v2 events on the same trace stream. The format never has a hard cutover — old traces remain readable forever.

## 7. Multi-format export — `vett export`

A future command (Phase 2-4) that produces alternate formats from native trace data. Same source, many sinks.

### Command surface

```
vett export <run-id> [flags]

Flags:
  --format <name>          Output format (see table below)
  --output <path>          Output path or directory
  --compress <algo>        none | gzip | zstd (default: zstd for compact formats, none for human-readable)
  --include <items>        Comma-separated: trace, summary, patches, config, binaries, all
  --filter <expr>          Optional filter on which instances to include
  --verify-lossless        After writing, round-trip back to native and diff. Aborts on mismatch.
  --template <path>        Custom template for human-readable formats
```

### Format catalog

| Format | Shape | Typical size (500 inst) | Phase | Use case |
|---|---|---|---|---|
| `native` | Vett's per-instance JSONL + summaries | ~700 MB raw | Phase 1 | Vett's own storage, backups |
| `native-compact` | v3 format with interning + content-addressing + zstd | ~18-30 MB | Phase 3 | Long-term archives |
| `swebench` | Official SWE-bench predictions JSONL | 2-3 MB | Phase 2 | SWE-bench leaderboard submissions |
| `openhands` | OpenHands `output.jsonl`-compatible | 550-700 MB | Phase 3 | AI Timeline, OpenHands ecosystem |
| `swe-agent` | Directory of `.traj` files | ~500 MB | Phase 3 | SWE-Agent ecosystem |
| `huggingface-card` | Markdown with YAML frontmatter | 5-10 KB | Phase 3+ | HF model cards, leaderboards |
| `flat` | Directory of `.patch` + `.md` + `.csv` | ~5 MB | Phase 2 | Humans browsing manually |
| `csv` | Spreadsheet-friendly file | ~200 KB | Phase 2 | Pandas / Excel analysis |
| `json` | Structured metadata, no trajectories | ~300 KB | Phase 2 | Programmatic consumers |
| `markdown` | Single human-readable report | ~100 KB | Phase 2 | Blog posts, PR reviews |
| `vett-repro` | Reproduction archive (see §8) | ~40-50 MB | Phase 4 | Benchmark submissions |
| `all` | Zip of every format above | ~1-2 GB | Phase 4 | "Just give me everything" |

### Conversion direction matters

- **native → any other format** = always lossless for fields that target format supports. May DROP fields the target doesn't have (openhands-format export drops Vett-only events). Always one-way safe.
- **any format → native** = NOT supported in general. Other formats lack Vett's full event detail. Don't try to reverse-import.
- **Compressed ↔ uncompressed** = always lossless, both directions, by definition.

### Exporters are stateless transformations

Each format is an independent file in `internal/export/formats/`. No format depends on any other format. Adding a new format is ~50-300 lines of Go and one entry in the registry. Removing or modifying a format never affects others.

```go
type Exporter interface {
    Name() string
    Description() string
    Export(ctx context.Context, run *Run, out io.Writer) error
}
```

### Golden test discipline

Every exporter has a golden test that locks its output for a known small Vett run. When you modify an exporter, the test fails if output drifted. You either update the golden (visible diff in PR) or fix the drift. Same pattern as Phase 1's wire format golden tests.

## 8. Reproduction archives — `vett export --format vett-repro`

This is the format that lets someone download a 40 MB file, run one command, and reproduce a benchmark on their own machine.

### Three reproduction levels

| Level | Size | What's bundled | What's pulled at reproduce time |
|---|---|---|---|
| **Lean** (default `vett export`) | ~25-30 MB | trace + config bundle + summary | Vett binary, Docker images, LLM endpoint, model |
| **Portable** (`--repro`) | ~40-50 MB | + Vett binary + sidecar binary | Docker images, LLM endpoint, model |
| **Hermetic** (`--include-all`) | 20-200 GB | + Docker images + Vett binary | LLM endpoint, model (still external) |

**Default is portable.** Lean is for sharing within the Vett ecosystem where the consumer already has Vett installed. Hermetic is for the rare case where you're archiving a benchmark result that needs to survive the disappearance of Docker Hub or the Vett binary's hosting.

The model itself is NEVER bundled — even hermetic doesn't include model weights because they're 60+ GB and require a GPU to run. The reproduction always requires the consumer to provide their own LLM endpoint serving the same model. This is documented clearly in the manifest.

### The reproduction archive structure

```
my-run-20260415.vett-repro.tar.zst
├── manifest.json              ← the recipe, ~20 KB
├── config.bundle.json         ← full Vett config + tool schemas, ~30 KB
├── instances.jsonl            ← exact instances to run, ~500 KB
├── trace.vett.zst             ← original run trajectories, ~20 MB
├── summary.json               ← original results, ~5 KB
├── reproduce.sh               ← one-command entry point
├── vett                       ← bundled Vett binary (linux-amd64)
├── vett-sidecar               ← bundled sidecar binary
└── README.md                  ← human-readable instructions
```

Total: ~40-50 MB. Small enough to upload to a leaderboard, attach to a blog post, or email.

### The manifest

```json
{
  "manifest_version": 1,
  "vett_run_id": "run-20260415-143000",
  "created_at": "2026-04-15T16:00:00Z",
  "description": "Vett openhands profile on SWE-bench Verified, 500 instances",

  "vett": {
    "version": "0.2.0",
    "commit": "abc1234",
    "install_command": "go install github.com/msdickinson/vett/sidecar/cmd/vett@abc1234",
    "release_binary_url": "https://github.com/msdickinson/vett/sidecar/releases/download/v0.2.0/vett-linux-amd64",
    "release_binary_sha256": "def5678...",
    "bundled_in_archive": true,
    "bundled_path": "./vett"
  },

  "sidecar": {
    "version": "0.2.0",
    "sha256": "fed9876...",
    "bundled_in_archive": true,
    "bundled_path": "./vett-sidecar"
  },

  "docker_images": [
    {
      "name": "swebench/sweb.eval.x86_64.astropy_1776_astropy-12907",
      "digest": "sha256:abc123...",
      "pull_command": "docker pull swebench/sweb.eval.x86_64.astropy_1776_astropy-12907@sha256:abc123...",
      "approximate_size_mb": 2800,
      "source": "Docker Hub"
    }
  ],

  "llm": {
    "model": "qwen3-coder-next",
    "endpoint": "USER_PROVIDED",
    "endpoint_notes": "Provide an OpenAI-compatible endpoint serving qwen3-coder-next.",
    "temperature": 1.0,
    "top_p": 0.95,
    "model_source_hint": "Original run used vLLM 0.17.1rc1 serving qwen3-coder-next NVFP4 on Blackwell SM121."
  },

  "dataset": {
    "name": "princeton-nlp/SWE-bench_Verified",
    "split": "test",
    "loader": "huggingface",
    "revision": "main",
    "revision_sha256": "fed654..."
  },

  "profile": { "source": "embedded", "path_in_archive": "./config.bundle.json#profile" },
  "suite": { "source": "embedded", "path_in_archive": "./config.bundle.json#suite" },

  "instances": {
    "source": "embedded",
    "path_in_archive": "./instances.jsonl",
    "count": 500
  },

  "estimated_requirements": {
    "disk_gb_for_images": 140,
    "disk_gb_for_work": 20,
    "ram_gb": 16,
    "gpu": "required for serving the LLM, not required for running the harness",
    "expected_duration_hours": 1.5
  },

  "reproduce_command": "./reproduce.sh"
}
```

Every external dependency is pinned by hash/digest. Docker images come down by digest, not tag, so they're bit-exact even years later if Docker Hub still hosts them.

### The reproduce.sh entry point

```bash
#!/usr/bin/env bash
set -euo pipefail

echo "Vett reproduction: $(jq -r .vett_run_id manifest.json)"
echo "================================================"
cat README.md
read -p "Press Enter to proceed, Ctrl-C to cancel..."

# 1. Verify prerequisites
command -v docker >/dev/null || { echo "ERROR: docker not found"; exit 1; }

# 2. Use bundled Vett, fall back to install
if [ -f ./vett ]; then
  VETT=./vett
  chmod +x ./vett ./vett-sidecar
else
  VETT_VERSION=$(jq -r .vett.commit manifest.json)
  go install "github.com/msdickinson/vett/sidecar/cmd/vett@${VETT_VERSION}"
  VETT="$HOME/go/bin/vett"
fi

# 3. Verify or prompt for LLM endpoint
if [ -z "${VETT_LLM_ENDPOINT:-}" ]; then
  read -p "Enter LLM endpoint URL serving $(jq -r .llm.model manifest.json): " VETT_LLM_ENDPOINT
  export VETT_LLM_ENDPOINT
fi

# 4. Pull Docker images by digest
read -p "Pulling ~140 GB of Docker images. Proceed? [y/N] " confirm
[ "$confirm" = "y" ] || exit 0
jq -r '.docker_images[].pull_command' manifest.json | while read cmd; do eval "$cmd"; done

# 5. Run reproduction
"$VETT" reproduce ./manifest.json --output ./reproduction-output

# 6. Compare against original
"$VETT" compare ./reproduction-output ./trace.vett.zst
```

Consumer experience: download archive, run `./reproduce.sh`, answer one prompt, wait for Docker pulls + run, get a comparison report.

### What this enables: Vett as a benchmark submission format

This is the bigger picture. **No agent benchmark currently has a self-contained reproducible submission format.** SWE-bench takes a JSONL of patches and trusts the submitter. OpenHands ships full trajectory dumps with no reproduction tooling. SWE-Agent ships .traj files with no manifest.

If Vett's `--repro` archive becomes a working pattern, it's a genuine candidate to standardize. Submission to a leaderboard becomes "upload this 40 MB file, the grader runs `./reproduce.sh`, results are verified bit-for-bit." That's a real product story for Vett — not just "yet another benchmark harness" but "the harness that produces verifiable submissions."

This is Phase 4 work. Phase 1 just needs to not paint itself into a corner.

## 9. Lossless verification and round-trip guarantees

Every transformation in Vett is round-trip-checkable. The `vett export --verify-lossless` flag does this automatically:

1. Generate the export
2. Read the export back
3. Compare against the source native data
4. Report the first divergence if any
5. Exit nonzero if anything is lossy that shouldn't be

For the **same-format** round trips (native ↔ native-compact, uncompressed ↔ compressed), the comparison is byte-exact.

For **cross-format** projections (native → openhands-format), the comparison is "all openhands-visible fields match" — Vett-only fields are documented as expected losses and listed in the verify report. You can audit the loss list.

For **hermetic** archives, the image digests are verified by re-pulling Docker images and confirming sha256 matches what the manifest claims.

This means: **even years later, you can prove an archive is a faithful representation of the original run** by running one command. Forensic integrity for free.

## 10. AI Timeline integration

AI Timeline already supports OpenHands `.jsonl` format out of the box. The integration story has two phases:

### Phase 4 (immediate): export to openhands format
`vett export <run-id> --format openhands` produces a AI Timeline-compatible file. Drop it into AI Timeline, it parses, you get the timeline view. Loses Vett-specific events (sub-agent dispatch, middleware events, streaming chunks) but captures the core trajectory.

### Phase 4+ (later): native AI Timeline parser plugin
AI Timeline's plugin system (per its CONTRIBUTING.md) takes ~50-150 lines of TypeScript per parser. A Vett-native parser would read `instances/<id>.trace.jsonl` directly, handle compression (zstd via WASM), and surface all Vett-specific event types in the timeline.

This is a contribution to the AI Timeline project, not part of Vett itself. **Vett ships the native format; AI Timeline ships the parser.** Same shape as how AI Timeline handles every other tool.

## 11. Cross-tool compatibility (TicketForge etc.)

TicketForge stays on PostgreSQL for its live workload (mutable tickets, real-time updates, complex queries — see implementation-notes.md for why). But TicketForge can produce Vett-format archives by reading from Postgres and serializing to the native format.

**Pattern**: TicketForge gets a "Export ticket as Vett archive" feature. Click it on a ticket, it queries Postgres for the ticket's full execution history, constructs a synthetic Vett "run" with one instance (the ticket), serializes to the native format, offers as a download.

This makes the Vett archive format **cross-tool**: it's not "Vett's output format," it's "a shared format that any agent system can produce." TicketForge being one of the first non-Vett producers proves the format is a genuine shared protocol, not a vendor lock-in.

For this to work, the native format needs an **`extensions` object** on instances and runs that producer-specific systems can use to preserve metadata Vett doesn't model. TicketForge puts ticket comments, custom fields, project IDs, etc. in `extensions.ticketforge.*`. Vett ignores them but preserves them across read/write cycles. Other consumers that understand the extension namespace can read it.

This `extensions` field should be in Phase 1's native format from day one as an optional `extensions: {...}` field on `instance_start`, `instance_end`, `run_start`, `run_end` events. Phase 1 doesn't write anything to it; future producers may.

## 12. Storage tier and retention strategy

For a 2-year project at the realistic scale (300-400 runs):

### Hot storage (always on local disk, fast access)
- **Tier 1 summaries** for ALL runs forever (5 KB each × 1000 = 5 MB total — trivial)
- **Tier 2 detail summaries** for ALL runs forever (~250 KB each × 1000 = 250 MB — fine)
- **Tier 3 trajectories** for the last N runs (hot working set)

### Cold storage (compressed, slower access, on the runner box or external)
- **Tier 3 trajectories** for older runs, native-compact format with zstd compression
- **Hermetic archives** for specific important runs (5-20 over project lifetime)

### Promotion / archival
- `vett archive <run-id>` packs a run into native-compact + zstd, optionally moves it to cold storage, leaves the summary in hot storage so the run remains visible in the index.
- `vett restore <run-id>` reverses it: extracts back to expanded native, ready for fast access.
- Optional auto-archive policy: anything older than N days or beyond the most-recent K runs gets archived automatically.

### What gets purged (rare)
Nothing auto-purges. Explicit `vett purge <run-id>` removes a run completely. Default behavior keeps everything.

### The "summary stays even if trace deleted" pattern
A run's summary file is small enough to keep forever. If you eventually delete a trace file to save space, **the summary stays** so the run still shows up in AI Timeline with full metadata. Drilling into deleted trace files surfaces a clear "this run's trajectories were archived/deleted, here's how to recover" message rather than crashing.

## 13. Hard rules for the Phase 1 implementer

The implementation AI doing Phase 1 must NOT implement most of this doc. But Phase 1 must obey these specific rules so future phases can land cleanly:

### Required in Phase 1

1. **Two-file structure must exist.** `summary.json` and `instances/<id>.trace.jsonl` files must be written separately, even if `summary.json` is minimal. The split is the foundational pattern for all future export work. No single combined file.

2. **Every event has a `v` field.** Per `trace-format.md`. Phase 1 emits `v: 1` everywhere. Future versions use higher numbers. Readers must skip unknown event types gracefully.

3. **Every event has a `ts` field.** RFC3339Nano UTC. Required for any later export that needs timing data.

4. **Every JSONL trace file has a `run_id` and `instance_id` discoverable from the events.** So a trace file can be associated with its run/instance even if its directory is moved.

5. **`run_start` and `instance_start` events carry full profile + suite identifiers.** Profile name, profile sha256, suite name, suite sha256, model, endpoint, vett version, sidecar version. So future export commands can reconstruct what the run was.

6. **Optional `extensions: {...}` field on `run_start`, `run_end`, `instance_start`, `instance_end`.** Empty in Phase 1. Reserved for future producers (TicketForge, etc.) and exporters.

7. **`summary.json` includes everything needed to derive `swebench`, `csv`, `json`, `flat`, `markdown` exports without reading the trace files.** Per-instance pass/fail, iteration count, token usage, patch size, end reason, files touched. So later export commands can produce most format outputs from summaries alone.

8. **Path layout under `results/<run-id>/` matches §2.** Don't get clever with directory names. The structure is hardcoded into future viewer code.

9. **No compression in Phase 1.** Files are raw JSONL on disk. Compression is Phase 2+. Streamability and tail-ability matter more than disk savings during the initial proof-of-life period.

### Forbidden in Phase 1

1. **Don't ship a "compact" format.** v1 raw JSONL only. Phase 2 introduces v2 string interning.

2. **Don't try to write multiple format projections during the run.** Native is the only format Vett writes during a run. Exports are post-hoc transformations of native data.

3. **Don't bake any AI Timeline-specific assumptions into the trace format.** AI Timeline will ship its own parser. Vett's job is to be a clean source of truth.

4. **Don't add the `vett export` command in Phase 1.** It's Phase 2-4. Phase 1 just writes native trace files; the export command runs against existing run directories later.

5. **Don't skimp on `summary.json` fields.** It's the API for everything. Be generous about including aggregate stats — they're cheap and future viewers depend on them.

### Permitted in Phase 1 but not required

1. Including `vett_version` and `sidecar_version` in `run_start` (recommended)
2. Including `endpoint` (without credentials) in `run_start` (recommended for reproduction story later)
3. Adding any field to events that is forward-compatible (optional fields readers can ignore)

## 14. Phase plan summary

| Phase | Deliverable | Effort |
|---|---|---|
| **Phase 1** | Native trace format (raw JSONL), summary.json, instance trace files, run trace file | (current) |
| **Phase 2** | `vett export` with cheap formats: `swebench`, `csv`, `json`, `flat`, `markdown`. Compress-on-completion option. | 2-3 days |
| **Phase 3** | Optimized formats: v2 string interning, v3 content-addressed observations. zstd archival. `openhands` and `swe-agent` exports. | 1 week |
| **Phase 4** | `vett-repro` reproduction archive with manifest + reproduce.sh + bundled binaries. `vett reproduce` command. `vett archive` / `vett restore`. The `--all` meta-target. | 1 week |
| **Phase 4+** | AI Timeline native parser plugin (separate project, contributed to AI Timeline's repo, not Vett's). | 2-3 days |
| **Future** | HuggingFace dataset/model card export, HELM format, lm-eval-harness format if demand appears. | per format ~1 day |

## 15. Open questions deferred to later phases

These don't block Phase 1 but should be answered before the relevant later phase:

1. **Storage location for Vett's "global run index"** — `~/.vett/runs/index.jsonl` or per-results-dir indexes? Deferred to Phase 3.
2. **Auto-archive policy** — manual only, or time-based / count-based defaults? Deferred to Phase 3.
3. **Where does the reproduction archive's binary live** — bundled in the tar, or downloaded fresh from a release URL on every reproduce? Deferred to Phase 4. Default: bundled.
4. **AI Timeline plugin development** — contribute upstream, fork, or maintain a parser separately? Deferred to Phase 4+.

## 16. Direct summary for the implementation AI

If you're implementing Phase 1 and reading this for the first time, the takeaways are:

1. **Write two file groups per run:** `summary.json` + `instances/<id>.trace.jsonl` files. They're NOT one combined file.
2. **`summary.json` is the API** for all future browsing and export. Be generous about what it includes — everything needed to derive `swebench`/`csv`/`json`/`flat`/`markdown` exports later.
3. **Every event line has `v: 1` and `ts`.** Forward-compatible event versioning is non-negotiable.
4. **Reserve `extensions: {...}` field** on instance/run lifecycle events. Empty in Phase 1, used by TicketForge and future producers.
5. **No compression yet.** Raw JSONL on disk in Phase 1. Compression is Phase 2.
6. **Don't write multiple formats.** Vett writes native; exports are post-hoc.
7. **Don't ship `vett export` yet.** That's Phase 2-4.
8. **Path layout matters.** `results/<run-id>/summary.json`, `results/<run-id>/instances/<id>.trace.jsonl`. Future code depends on this exact structure.

If Phase 1 obeys all 8 points, Phases 2-4 can land their export and archive features without retrofitting anything in the trace writer or the run output structure.
