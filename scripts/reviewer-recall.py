"""Multi-strategy reviewer-recall, suite-agnostic.

For every run in a vett bench output, this fires FIVE reviewer
strategies against a fresh-context AEON call and records each one's
verdict + confidence:

  R1  solo-strict        — 1 call, "any doubt = FAIL"
  R2  solo-charitable    — 1 call, "benefit of the doubt"
  R3  solo-test-walker   — 1 call, "mentally evaluate every assertion"
  R4  panel-of-3 majority — 3 calls (strict + charitable + neutral), vote
  R5  adversarial-pair   — 2 calls (verdict, then devil's-advocate retort)

Each reviewer sees:
  - the spec (instance prompt)
  - every file currently in the workspace that the instance seeded
  - the run's harness assertions (so it knows what was being checked)

Per strategy we compute the failure-flagging 2×2:
  TP  actual PASS, strategy said PASS
  FP  actual FAIL, strategy said PASS  — silent fail
  FN  actual PASS, strategy said FAIL  — paranoid
  TN  actual FAIL, strategy said FAIL  — caught a real fail

Run:
  python reviewer-recall.py BENCH_JSON SUITE_YAML [OUT.md]
"""

from __future__ import annotations
import json, os, sys, time, re, urllib.request
from collections import Counter

# 2026-08-25: old-gpu-a was gpu-2's pre-re-lease address (vacated 2026-08-22); AEON is
# switched off. Flash serves from gpu-1 under its own id.
ENDPOINT = "http://gpu-1:8000/v1/chat/completions"
MODEL = "deepseek-v4-flash"


def call_aeon(messages, max_tokens=600, temperature=0.2, retries=2):
    """Reads `content`, falls back to `reasoning_content`. Disables
    thinking via `chat_template_kwargs.enable_thinking=false` for
    reliable structured-output parsing on the reviewer task."""
    last_err = None
    for attempt in range(retries + 1):
        try:
            body = json.dumps({
                "model": MODEL, "messages": messages,
                "temperature": temperature, "max_tokens": max_tokens,
                "chat_template_kwargs": {"enable_thinking": False},
            }).encode("utf-8")
            req = urllib.request.Request(
                ENDPOINT, data=body,
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            with urllib.request.urlopen(req, timeout=180) as resp:
                d = json.load(resp)
            msg = d["choices"][0]["message"]
            return msg.get("content") or msg.get("reasoning_content") or msg.get("reasoning") or ""
        except Exception as ex:
            last_err = ex
            if attempt < retries:
                time.sleep(2 * (attempt + 1))
    raise last_err


VERDICT_RX = re.compile(r"VERDICT\s*:\s*(PASS|FAIL)", re.I)
CONF_RX = re.compile(r"CONFIDENCE\s*:\s*(\d{1,3})", re.I)
REASON_RX = re.compile(r"REASON\s*:\s*(.+?)(?:\n|$)", re.I)


def parse(text):
    v = VERDICT_RX.search(text); c = CONF_RX.search(text); r = REASON_RX.search(text)
    return {
        "verdict": v.group(1).upper() if v else None,
        "confidence": int(c.group(1)) if c else None,
        "reason": r.group(1).strip() if r else None,
        "raw": text,
    }


PROMPT_HEADER = (
    "You are reviewing whether the agent's work satisfies a spec. The "
    "harness will independently grade this run via assertions; YOUR job "
    "is to predict whether those assertions pass.\n"
    "Respond with EXACTLY these three lines (no preamble, no extra text):\n\n"
    "VERDICT: PASS|FAIL\n"
    "CONFIDENCE: <integer 0-100>\n"
    "REASON: <one sentence>\n\n"
)

PERSONAS = {
    "strict": (
        "You are a STRICT reviewer. Default to FAIL. Any compile risk, any "
        "edge case the code might miss, any sign that a required change is "
        "missing — FAIL. If a required file is missing, VERDICT is FAIL."
    ),
    "charitable": (
        "You are a CHARITABLE reviewer. Default to PASS. Only return FAIL "
        "if you can point to a specific assertion you are highly confident "
        "would fail. If a required file is missing, VERDICT is FAIL."
    ),
    "neutral": (
        "You are a NEUTRAL reviewer. Weigh the evidence even-handedly. "
        "If a required file is missing, VERDICT is FAIL."
    ),
    "test-walker": (
        "You are a TEST-CASE walker. For each assertion in the harness "
        "list, mentally evaluate whether the produced workspace state "
        "would satisfy it. If ANY assertion would fail, VERDICT is FAIL. "
        "If a required file is missing, VERDICT is FAIL."
    ),
}


def body_block(spec, file_dict, assertions):
    """file_dict: {filename: content_or_None_if_missing}.
       assertions: list of harness assertion descriptions."""
    parts = []
    parts.append("Spec (the agent's task):\n--------\n")
    parts.append(spec)
    parts.append("\n--------\n\n")
    parts.append("Harness assertions for this run:\n--------\n")
    for a in assertions or []:
        parts.append(f"  - {a}\n")
    if not assertions:
        parts.append("  (no assertions listed)\n")
    parts.append("--------\n\n")
    parts.append("Current workspace state:\n--------\n")
    if not file_dict:
        parts.append("(no relevant files found)\n")
    for name, content in file_dict.items():
        if content is None:
            parts.append(f"### `{name}` — **FILE NOT PRESENT IN WORKSPACE**\n\n")
        else:
            ext = os.path.splitext(name)[1].lstrip(".")
            fence = ext if ext else ""
            parts.append(f"### `{name}`\n```{fence}\n{content}\n```\n\n")
    parts.append("--------\n")
    return "".join(parts)


def strat_solo(persona_key, spec, file_dict, assertions):
    sys_msg = PERSONAS[persona_key] + "\n\n" + PROMPT_HEADER
    user_msg = body_block(spec, file_dict, assertions)
    return parse(call_aeon([
        {"role": "system", "content": sys_msg},
        {"role": "user", "content": user_msg},
    ]))


def strat_panel_vote(spec, file_dict, assertions):
    votes = [strat_solo(p, spec, file_dict, assertions)
             for p in ("strict", "charitable", "neutral")]
    verdicts = [v["verdict"] for v in votes if v["verdict"]]
    if not verdicts:
        return {"verdict": None, "confidence": None, "reason": "no parseable votes",
                "raw": json.dumps([v.get("raw","")[:120] for v in votes])}
    counts = Counter(verdicts)
    winner, _ = counts.most_common(1)[0]
    confs = [v["confidence"] for v in votes if v["confidence"] is not None]
    return {
        "verdict": winner,
        "confidence": (round(sum(confs)/len(confs)) if confs else None),
        "reason": f"vote={dict(counts)}",
        "raw": json.dumps([v.get("raw","")[:160] for v in votes]),
    }


def strat_adversarial(spec, file_dict, assertions):
    a = strat_solo("neutral", spec, file_dict, assertions)
    a_verdict = a.get("verdict") or "PASS"
    b_sys = (
        "You are a DEVIL'S ADVOCATE reviewer. Reviewer A has issued the "
        f"verdict {a_verdict}. Your job is to find a specific concrete "
        "failure: name an assertion the agent's work would violate. If "
        "you cannot find one, respond VERDICT: " + a_verdict + " (defer "
        "to A). Otherwise respond VERDICT: FAIL with the counter-example "
        "in REASON.\n\n" + PROMPT_HEADER
    )
    b_text = call_aeon([
        {"role": "system", "content": b_sys},
        {"role": "user", "content": body_block(spec, file_dict, assertions)},
    ])
    b = parse(b_text)
    return {
        "verdict": b.get("verdict") or a_verdict,
        "confidence": b.get("confidence") if b.get("confidence") is not None else a.get("confidence"),
        "reason": f"A={a_verdict}; B={b.get('verdict')}; B-reason: {b.get('reason','')}",
        "raw": f"A: {a.get('raw','')[:200]}\nB: {b.get('raw','')[:200]}",
    }


def strat_concrete_trace(spec, file_dict, assertions):
    """Force the reviewer to pick 3 inputs from the test file and write
    out, step by step, what the produced code returns for each one.
    Concrete execution beats inspection. Designed for the 'modify'
    failure mode where every solo strategy agreed the code 'looks fine'."""
    sys_msg = (
        "You are a STEP-BY-STEP EXECUTION reviewer. Inspection alone is "
        "unreliable on modified code — you MUST trace concrete inputs.\n\n"
        "Procedure (do these in order, do not skip):\n"
        "1. Identify the test file in the workspace.\n"
        "2. Pick THREE specific input cases (prefer the trickiest — "
        "edge cases, large inputs, unicode, etc.).\n"
        "3. For each, write out the EXACT step-by-step execution of the "
        "produced code (variable values after each statement).\n"
        "4. Compare the final return to the test's expected value.\n"
        "5. If ANY of the three diverges from expected, VERDICT is FAIL.\n\n"
        "After the trace, end with EXACTLY these three lines (and nothing else after):\n\n"
        "VERDICT: PASS|FAIL\n"
        "CONFIDENCE: <integer 0-100>\n"
        "REASON: <one sentence>\n"
    )
    return parse(call_aeon([
        {"role": "system", "content": sys_msg},
        {"role": "user", "content": body_block(spec, file_dict, assertions)},
    ], max_tokens=2000))


def strat_diff_aware(spec, file_dict, assertions):
    """The 'modify' archetypes give the reviewer both the seeded original
    AND the modified code (the workspace_files reader includes them).
    This strategy makes the diff explicit: 'here's what changed, did
    that change correctly address the spec?'"""
    sys_msg = (
        "You are a DIFF-FOCUSED reviewer. The workspace shows the agent's "
        "MODIFIED files. You also have the spec and harness assertions.\n\n"
        "Procedure:\n"
        "1. From the spec, identify what behavior was supposed to CHANGE.\n"
        "2. From the modified code, identify what behavior actually CHANGED.\n"
        "3. Ask: does the actual change match the requested change?\n"
        "4. Consider: did the change leave OTHER existing behavior intact?\n"
        "5. If the change is missing, off-target, OR breaks other behavior, FAIL.\n\n"
        "Respond with EXACTLY these three lines:\n\n"
        "VERDICT: PASS|FAIL\nCONFIDENCE: <integer 0-100>\nREASON: <one sentence>\n"
    )
    return parse(call_aeon([
        {"role": "system", "content": sys_msg},
        {"role": "user", "content": body_block(spec, file_dict, assertions)},
    ], max_tokens=1200))


def strat_test_name_walker(spec, file_dict, assertions):
    """List every test/InlineData case by name (or by index), and for
    each predict pass/fail. Aggregate."""
    sys_msg = (
        "You are a PER-TEST PREDICTOR. Reading the test file in the "
        "workspace, list each [Fact] / [Theory] / [InlineData(...)] case "
        "by name or index, and for each one predict PASS or FAIL based "
        "on the produced code. If any single case would fail, the run "
        "fails as a whole.\n\n"
        "Output one line per test like:\n"
        "  test_or_case_X: PASS|FAIL — <one-clause reason>\n\n"
        "After listing every case, end with these three lines exactly:\n\n"
        "VERDICT: PASS|FAIL\nCONFIDENCE: <integer 0-100>\nREASON: <which case(s) drove the verdict>\n"
    )
    return parse(call_aeon([
        {"role": "system", "content": sys_msg},
        {"role": "user", "content": body_block(spec, file_dict, assertions)},
    ], max_tokens=2500))


def strat_dotnet_oracle(spec, file_dict, assertions):
    """Control / ground-truth reviewer: actually run `dotnet test` in
    the workspace and emit VERDICT based on result. NOT a fair external
    reviewer — it's the same check the harness does — but lets us
    confirm the workspace state is what the harness saw, and gives a
    perfect-recall baseline to compare other strategies against."""
    # workspace_dir is encoded into file_dict's existence; reconstruct
    # from any present file's parent (we have no direct handle here).
    # The cleanest path: walk file_dict for any non-None content, then
    # the wd is what the caller passed via session_log_path. But this
    # strategy needs the wd directly, so we read it from a sentinel.
    wd = _CURRENT_WORKSPACE_DIR
    if not wd or not os.path.isdir(wd):
        return {"verdict": None, "confidence": None, "reason": "no workspace dir", "raw": ""}
    import subprocess
    try:
        proc = subprocess.run(
            ["dotnet", "test", "--nologo", "-v", "quiet"],
            cwd=wd, capture_output=True, text=True, timeout=300,
        )
        out = (proc.stdout or "") + "\n" + (proc.stderr or "")
        passed = (proc.returncode == 0) and ("FAIL" not in out.upper().replace("FAILED!", "")[:500])
        # Above heuristic is fragile; a simpler check:
        rc_pass = (proc.returncode == 0)
        return {
            "verdict": "PASS" if rc_pass else "FAIL",
            "confidence": 100,
            "reason": f"dotnet test rc={proc.returncode}",
            "raw": out[-600:],
        }
    except subprocess.TimeoutExpired:
        return {"verdict": "FAIL", "confidence": 100, "reason": "dotnet test timed out", "raw": ""}
    except FileNotFoundError:
        return {"verdict": None, "confidence": None, "reason": "dotnet CLI not available", "raw": ""}
    except Exception as ex:
        return {"verdict": None, "confidence": None, "reason": f"oracle error: {ex}", "raw": ""}


# Module-level sentinel so strat_dotnet_oracle can find the workspace
# without me having to thread `wd` through every strategy signature.
# Set inside main() before each run's strategies are invoked.
_CURRENT_WORKSPACE_DIR: str | None = None


STRATEGIES = {
    "R1-strict": lambda s, f, a: strat_solo("strict", s, f, a),
    "R2-charitable": lambda s, f, a: strat_solo("charitable", s, f, a),
    "R3-test-walker": lambda s, f, a: strat_solo("test-walker", s, f, a),
    "R4-panel-vote": strat_panel_vote,
    "R5-adversarial": strat_adversarial,
    "R6-concrete-trace": strat_concrete_trace,
    "R7-diff-aware": strat_diff_aware,
    "R8-test-name-walker": strat_test_name_walker,
    "R9-dotnet-oracle": strat_dotnet_oracle,
}


def parse_suite(path):
    import yaml
    with open(path, encoding="utf-8") as f:
        return {i["id"]: i for i in yaml.safe_load(f).get("instances", [])}


def workspace_files(wd, instance):
    """Read the current state of every file in the workspace that the
    instance seeded. Returns {filename: content or None_if_missing}."""
    out = {}
    for name in (instance.get("seed_files") or {}).keys():
        p = os.path.join(wd, name)
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                out[name] = f.read()
        except FileNotFoundError:
            out[name] = None
        except Exception as ex:
            out[name] = f"<error reading: {ex}>"
    # Also include any non-seeded .cs/.csproj/.md the agent might have
    # created (e.g. Methods.cs for method-tier1, which is NOT seeded).
    try:
        for fname in os.listdir(wd):
            if fname in out: continue
            if fname.startswith("."): continue
            if fname.endswith((".cs", ".csproj", ".md", ".sln", ".slnx", ".yaml", ".yml", ".json", ".txt", ".cshtml", ".razor")):
                p = os.path.join(wd, fname)
                if not os.path.isfile(p): continue
                try:
                    with open(p, encoding="utf-8", errors="replace") as f:
                        out[fname] = f.read()
                except Exception as ex:
                    out[fname] = f"<error reading: {ex}>"
    except Exception:
        pass
    return out


def assertion_descriptions(run):
    """Pull descriptions from the run's assertion list — gives reviewer
    visibility into what the harness checks."""
    out = []
    for a in run.get("assertions", []) or []:
        d = a.get("description", "")
        if d: out.append(d)
    return out


def main(argv):
    if len(argv) < 3:
        print("usage: reviewer-recall.py BENCH_JSON SUITE_YAML [OUT.md]", file=sys.stderr); return 2
    bench_json, suite_yaml = argv[1], argv[2]
    out_md = argv[3] if len(argv) > 3 else os.path.splitext(bench_json)[0] + ".reviewer-recall.md"
    out_jsonl = os.path.splitext(out_md)[0] + ".jsonl"

    with open(bench_json, encoding="utf-8") as f:
        txt = f.read()
    txt = txt[txt.find("{"):]
    bench = json.loads(txt)
    runs = bench["runs"]
    instances = parse_suite(suite_yaml)

    print(f"reviewing {len(runs)} runs × {len(STRATEGIES)} strategies = {len(runs)*len(STRATEGIES)} reviews", file=sys.stderr)

    open(out_jsonl, "w").close()
    truth = {k: {"tp":0,"fp":0,"fn":0,"tn":0,"unparsed":0} for k in STRATEGIES}
    per_run_rows = []
    t_start = time.time()

    for i, run in enumerate(runs):
        inst = instances.get(run["instance_id"])
        if inst is None: continue
        wd_log = run.get("session_log_path")
        if not wd_log: continue
        wd = os.path.dirname(wd_log)
        if not os.path.isdir(wd):
            print(f"  [{i+1:2d}/{len(runs)}] {run['instance_id']:30s} workspace gone, skip", file=sys.stderr)
            continue
        files = workspace_files(wd, inst)
        asserts = assertion_descriptions(run)

        # R9-dotnet-oracle needs the workspace dir; pass it via module
        # sentinel rather than threading it through every strategy sig.
        global _CURRENT_WORKSPACE_DIR
        _CURRENT_WORKSPACE_DIR = wd

        row = {
            "run_index": run["run_index"], "instance_id": run["instance_id"],
            "actual_pass": run["pass"],
            "files_present": [k for k, v in files.items() if v is not None],
            "files_missing": [k for k, v in files.items() if v is None],
        }
        for skey, fn in STRATEGIES.items():
            t0 = time.time()
            try:
                r = fn(inst.get("prompt", ""), files, asserts)
            except Exception as ex:
                r = {"verdict": None, "confidence": None, "reason": f"call failed: {ex}", "raw": ""}
            r["wall_seconds"] = round(time.time() - t0, 1)
            row[skey] = r
            v = r.get("verdict")
            t = truth[skey]
            if v is None: t["unparsed"] += 1
            elif v == "PASS" and run["pass"]: t["tp"] += 1
            elif v == "PASS" and not run["pass"]: t["fp"] += 1
            elif v == "FAIL" and run["pass"]: t["fn"] += 1
            elif v == "FAIL" and not run["pass"]: t["tn"] += 1
        per_run_rows.append(row)
        with open(out_jsonl, "a", encoding="utf-8") as f:
            f.write(json.dumps(row) + "\n")

        elapsed = time.time() - t_start
        avg = elapsed / (i + 1)
        eta = avg * (len(runs) - i - 1)
        cells = " ".join(f"{k}={row[k].get('verdict')!s:4s}" for k in STRATEGIES)
        print(f"  [{i+1:2d}/{len(runs)}] {run['instance_id']:30s} actual={'PASS' if run['pass'] else 'FAIL'} | {cells} | eta {eta/60:.0f}min", file=sys.stderr)

    # render report
    lines = []
    lines.append(f"# Multi-strategy reviewer recall — `{os.path.basename(bench_json)}`\n\n")
    lines.append(f"Suite: `{os.path.basename(suite_yaml)}`  ")
    lines.append(f"Reviewer model: `{MODEL}`  ")
    lines.append(f"Strategies: {', '.join(STRATEGIES.keys())}\n\n")
    lines.append("## Failure-flagging recall per strategy\n\n")
    lines.append("| Strategy | TP | FP (silent) | FN (paranoid) | TN | Unparsed | **Recall** | Paranoia |\n")
    lines.append("|---|---|---|---|---|---|---|---|\n")
    for skey, t in truth.items():
        denom_r = t["tn"] + t["fp"]
        recall = (t["tn"] / denom_r * 100) if denom_r > 0 else None
        rec_s = f"**{recall:.1f}%**" if recall is not None else "n/a"
        denom_p = t["fn"] + t["tp"]
        paranoia = (t["fn"] / denom_p * 100) if denom_p > 0 else None
        par_s = f"{paranoia:.1f}%" if paranoia is not None else "n/a"
        lines.append(f"| `{skey}` | {t['tp']} | {t['fp']} | {t['fn']} | {t['tn']} | {t['unparsed']} | {rec_s} | {par_s} |\n")
    lines.append("\n")

    lines.append("## Per-run verdicts\n\n")
    header = "| run | instance | actual | files OK | " + " | ".join(STRATEGIES) + " |\n"
    lines.append(header)
    lines.append("|" + "|".join(["---"] * (4 + len(STRATEGIES))) + "|\n")
    for row in per_run_rows:
        cells = [str(row["run_index"]), f"`{row['instance_id']}`",
                 "PASS" if row["actual_pass"] else "**FAIL**",
                 ("yes" if not row["files_missing"] else f"**no** ({','.join(row['files_missing'])})")]
        for s in STRATEGIES:
            v = row[s].get("verdict") or "?"
            match = (v == "PASS") == row["actual_pass"]
            cells.append(("" if match else "**X** ") + v)
        lines.append("| " + " | ".join(cells) + " |\n")

    with open(out_md, "w", encoding="utf-8") as f:
        f.writelines(lines)
    print(f"\nwrote {out_md}", file=sys.stderr)
    for skey, t in truth.items():
        denom = t["tn"] + t["fp"]
        recall = (t["tn"] / denom * 100) if denom > 0 else None
        print(f"  {skey}: TP={t['tp']} FP={t['fp']} FN={t['fn']} TN={t['tn']} unparsed={t['unparsed']} recall={recall}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
