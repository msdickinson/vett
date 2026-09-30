# VETT

[![CI](https://github.com/msdickinson/vett/actions/workflows/ci.yml/badge.svg)](https://github.com/msdickinson/vett/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

**A .NET harness for running AI coding agents on your own hardware: benchmark them, chat with them, or run them as a team.**

VETT runs a model against [SWE-bench](https://www.swebench.com/) (or any
JSONL suite of tasks), drives an interactive chat session on your own
codebase, or coordinates a lead agent and workers through a shared task
board. Each agent works through a small Go sidecar inside a Docker sandbox.
Profiles, tools, middleware and suites are plain YAML.

It is the engine behind our own agent work, built and used on two NVIDIA
DGX Sparks. It is shared as-is: it works, it is tested, and it is still
changing. Why it is built the way it is: **[docs/DESIGN.md](docs/DESIGN.md)**.

## What VETT adds

VETT's `openhands` profile reproduces the
[OpenHands](https://github.com/OpenHands/software-agent-sdk) SDK 1.14.0
agent as a baseline, so results can be compared. The system prompt is
byte-identical; the known gaps elsewhere are listed in
[docs/openhands-fidelity.md](docs/openhands-fidelity.md). Credit and
license are in [NOTICE](NOTICE). Everything else is VETT's own:

- **Benchmark runner and grader.** Runs a suite with concurrency and
  sandbox lifecycle handled, writes SWE-bench `predictions.jsonl`, and can
  grade the gold patch first to flag instances whose tests can't be trusted.
- **Agent teams.** A lead plans and dispatches work to workers through a
  shared task board; each dispatch gets its own git worktree, and work lands
  only when its checks pass. A task is done only with evidence, not on the
  agent's word.
- **Multi-endpoint scheduling.** Spread one run over several model servers,
  pinning each conversation to one server so its prefix cache stays warm. A
  capacity broker lets several tools share the same servers.
- **Go sandbox sidecar.** One static binary copied into any Linux image
  (SWE-bench images included) and driven over JSON-RPC, so nothing has to be
  installed in the image.
- **YAML plugins and middleware.** Add a tool with a YAML manifest and a
  script in any language. Middleware covers compaction, retries, stuck
  detection, runaway caps and output truncation.
- **Strict config.** Profiles and suites are validated at load; a missing
  key is an error, never a silent default.
- **Live event stream.** `--live-port` serves Server-Sent Events that
  [AI Timeline](https://github.com/msdickinson/ai-timeline) shows live.
- **`vett chat`.** The same agent loop, interactive, on your local repo.

## Results

On the 25 easiest SWE-bench Verified instances (`suites/swe-bench-easiest25.yaml`),
`openhands` profile, models served with vLLM on NVIDIA DGX Sparks, graded
with the official SWE-bench harness. One run per row.

| Model | Setup | Resolved | Wall time |
|---|---|---|---|
| DeepSeek V4 Flash (current) | Two Sparks (TP=2), 5 at a time, 500 iterations | 23/25 (92%) | 54 m (agent phase) |
| Qwen3-Coder-Next FP8 (earlier) | One Spark, 5 at a time, 100 iterations | 21/25 (84%) | 3 h 44 m |
| Qwen3-Coder-Next FP8 (earlier) | One Spark, 5 at a time, 200 iterations | 22/25 (88%) | 5 h 11 m |
| Qwen3-Coder-Next FP8 (earlier) | Two Sparks, 10 at a time, 100 iterations | 20/25 (80%) | 1 h 51 m |

How the 25 were picked: SWE-bench Verified's "<15 min fix" difficulty
band, then the shortest problem statements. Sampling: temperature 1.0,
top_p unset (vLLM default), no fixed seed, so reruns differ.

These are the easiest 25, not the full benchmark, and single runs vary.
Treat them as "the harness works end to end", not as a leaderboard score.
[AGENTS.md](AGENTS.md) has the rules that keep these numbers honest (for
example, never tuning on held-out tasks).

## Install

Needs:
- **.NET 10 SDK** (pinned in `global.json`)
- **Go** (version in `sidecar/go.mod`) to build the sidecar, or download
  the prebuilt tool from [Releases](https://github.com/msdickinson/vett/releases)
- **Docker** for sandboxed benchmark runs (not needed for `vett chat`)
- An **OpenAI-compatible model endpoint**: vLLM, Ollama, or a hosted API

From a release: download `VettBench.*.nupkg`, then

```bash
dotnet tool install --global --add-source <folder-with-the-nupkg> VettBench
vett --help
```

From source:

```bash
cd sidecar && make sidecar-all && cd ..    # Windows: sidecar\build.ps1
dotnet pack src/Vett/Vett.csproj -c Release -o nupkg
dotnet tool install --global --add-source ./nupkg VettBench
vett --help
```

The tool carries the sidecar binaries. To use your own build, set
`VETT_SIDECAR_PATH`.

## Configure

| Setting | Set with | Needed for |
|---|---|---|
| Model endpoint | `VETT_LLM_ENDPOINT`, or `llm.endpoint` in the profile | local providers |
| Model name | `VETT_LLM_MODEL`, or `llm.model` in the profile | always |
| API key | the env var named by `llm.api_key_env` | hosted APIs |

```bash
export VETT_LLM_ENDPOINT=http://localhost:8000/v1
export VETT_LLM_MODEL=your-model-name
vett validate --profile openhands    # tells you exactly what is missing
```

## Run a benchmark

```bash
# 25 easiest SWE-bench Verified instances with the OpenHands baseline
vett run --suite swe-bench-easiest25 --profile openhands

# Stream events live (AI Timeline can subscribe)
vett run --suite swe-bench-easiest25 --profile openhands --live-port 5151

# Two model servers; each instance stays on one to keep its prefix cache warm
vett run --suite swe-bench-easiest25 --profile openhands \
  --endpoint http://server-a:8000/v1 --endpoint http://server-b:8000/v1 \
  --concurrency 10
```

Output goes to `results/run-<timestamp>/` (or `--output`): a summary, the
SWE-bench `predictions.jsonl`, and a full event stream per instance.

## Chat

```bash
vett chat --cwd .
```

An interactive session on your own machine, using the same agent loop you
benchmark (no Docker needed).

## Teams

Team profiles give a lead agent tools to plan, dispatch and review work.
Start from `defaults/profiles/team-example.yaml`; the `team-*` suites in
`suites/` exercise teams on tasks from a one-method edit up to building a
small game. The other profiles in `profiles/` are the configurations we run
ourselves, kept as worked examples; their comments record why each exists.

## Tests

```bash
dotnet test Vett.slnx
cd sidecar && go test ./...
```

About 1,600 .NET tests plus the Go suite; CI runs both on every push.

## Layout

```
src/Vett/          .NET CLI: Agent/, Capacity/, Tools/, Sandbox/, Cli/, Config/, Llm/, Live/
sidecar/           Go sandbox binary, plus the original design specs in sidecar/docs/
profiles/          profiles we run (worked examples)
defaults/          built-in profiles, tools, middleware, schemas
suites/            benchmark and team suites
pipelines/, bench-profiles/, capabilities/, schemas/   more config
scripts/           analysis helpers for run output
docs/              DESIGN.md, openhands-fidelity.md
tests/             xUnit
```

## Contributing

Issues and PRs are welcome. Read [AGENTS.md](AGENTS.md) first: it lists
the rules that keep results trustworthy (keep the `openhands`
baseline as OpenHands does it, never tune on held-out tasks). Plugin tools need only a YAML
manifest in `<workdir>/plugins/` and a script; see `defaults/tools/*.yaml`.

## License

MIT, see [LICENSE](LICENSE). Third-party notices in [NOTICE](NOTICE).
