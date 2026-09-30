"""Aggregate malformed_tool_call + PASS rates from a sweep dir.

Consumes the --json summaries + preserved `_bench-session.jsonl` files
produced by `suite-sweep.sh` (which runs every team-* suite against
prod aeon-serve with `--keep-all-worktrees --json -n 2`).

Outputs a single Markdown report with:
  - Per-suite PASS rate
  - Per-suite malformed_tool_call rate (mal / (mal + tool_call_end))
  - Per-instance breakdown (mal count, tool_call count, rate, PASS bool)
  - Per-taxonomy-dimension slicing (per-value malformed rate)

Usage:
    python sweep-analyze.py path/to/sweep-YYYYMMDDTHHMMSSZ [output.md]
"""

from __future__ import annotations
import json, os, sys
from pathlib import Path
from collections import defaultdict


def parse_session(jsonl_path: str) -> tuple[int, int]:
    """Return (malformed_count, tool_call_end_count) for one run."""
    mal = calls = 0
    try:
        with open(jsonl_path, "r", encoding="utf-8") as f:
            for line in f:
                try:
                    ev = json.loads(line)
                except Exception:
                    continue
                t = ev.get("type", "")
                if t == "malformed_tool_call":
                    mal += 1
                elif t == "tool_call_end":
                    calls += 1
    except FileNotFoundError:
        pass
    return mal, calls


def analyze(sweep_dir: Path) -> str:
    suite_jsons = sorted(p for p in sweep_dir.glob("*.json"))
    if not suite_jsons:
        return f"No JSON files under {sweep_dir}\n"

    suite_records: dict[str, dict] = {}
    by_dim: dict[str, dict[str, list[tuple[int, int]]]] = defaultdict(lambda: defaultdict(list))
    # by_dim[dimension_name][value] = list of (mal, calls) tuples across runs

    for jp in suite_jsons:
        try:
            with open(jp, "r", encoding="utf-8") as f:
                content = f.read().strip()
            if not content:
                suite_records[jp.stem] = {"error": "empty json (run probably hung or killed)"}
                continue
            # Harness bug: timeout warnings go to stdout in --json mode and
            # prefix the actual JSON document. Strip everything before the
            # first `{` to recover.
            brace = content.find("{")
            if brace > 0:
                content = content[brace:]
            elif brace < 0:
                suite_records[jp.stem] = {"error": "no JSON object found (likely killed before write)"}
                continue
            d = json.loads(content)
        except Exception as e:
            suite_records[jp.stem] = {"error": f"parse: {e}"}
            continue

        suite_name = d.get("suite_name", jp.stem)
        total_pass = d.get("total_passed", 0)
        total_runs = d.get("total_runs", 0)
        runs = d.get("runs", [])

        per_run_rows = []
        agg_mal = agg_calls = 0
        for r in runs:
            sp = r.get("session_log_path")
            mal, calls = parse_session(sp) if sp else (0, 0)
            agg_mal += mal
            agg_calls += calls
            rate = (mal / (mal + calls) * 100) if (mal + calls) > 0 else 0.0
            sa = r.get("self_assessment") or {}
            per_run_rows.append({
                "instance_id": r["instance_id"],
                "run_index": r["run_index"],
                "pass": r["pass"],
                "wall": r.get("wall_clock_seconds", 0.0),
                "tool_calls": calls,
                "malformed": mal,
                "rate_pct": rate,
                "taxonomy": r.get("taxonomy") or {},
                "self_captured": sa.get("captured"),
                "self_confidence": sa.get("confidence"),
                "self_predicted_pass": sa.get("predicted_pass"),
                "self_reasoning": sa.get("reasoning"),
            })
            # Per-dimension aggregation
            for dim, val in (r.get("taxonomy") or {}).items():
                by_dim[dim][val].append((mal, calls))

        agg_rate = (agg_mal / (agg_mal + agg_calls) * 100) if (agg_mal + agg_calls) > 0 else 0.0
        suite_records[suite_name] = {
            "pass_rate": (total_pass, total_runs),
            "mal": agg_mal,
            "calls": agg_calls,
            "rate_pct": agg_rate,
            "duration_sec": d.get("total_duration_seconds", 0.0),
            "runs": per_run_rows,
        }

    # Render markdown
    out: list[str] = []
    out.append(f"# Sweep analysis — `{sweep_dir.name}`\n")
    out.append(f"Source: `{sweep_dir}`\n\n")

    out.append("## Per-suite summary\n")
    out.append("| Suite | PASS | malformed | tool_calls | rate | wall (min) |\n")
    out.append("|---|---|---|---|---|---|\n")
    total_mal = total_calls = total_pass_all = total_runs_all = 0
    for name, rec in sorted(suite_records.items()):
        if "error" in rec:
            out.append(f"| `{name}` | — | — | — | — | {rec['error']} |\n")
            continue
        p, t = rec["pass_rate"]
        total_pass_all += p; total_runs_all += t
        total_mal += rec["mal"]; total_calls += rec["calls"]
        out.append(
            f"| `{name}` | {p}/{t} | {rec['mal']} | {rec['calls']} | "
            f"{rec['rate_pct']:.2f}% | {rec['duration_sec']/60:.1f} |\n"
        )
    agg_rate = (total_mal / (total_mal + total_calls) * 100) if (total_mal + total_calls) > 0 else 0.0
    out.append(
        f"| **AGGREGATE** | **{total_pass_all}/{total_runs_all}** | **{total_mal}** | "
        f"**{total_calls}** | **{agg_rate:.2f}%** | |\n\n"
    )

    out.append("## Per-instance malformed events\n")
    out.append("Runs with at least one `malformed_tool_call` event:\n\n")
    out.append("| Suite | Instance | Run | PASS | malformed | tool_calls | rate |\n")
    out.append("|---|---|---|---|---|---|---|\n")
    for name, rec in sorted(suite_records.items()):
        if "error" in rec: continue
        for row in rec["runs"]:
            if row["malformed"] == 0: continue
            out.append(
                f"| `{name}` | `{row['instance_id']}` | {row['run_index']} | "
                f"{'✓' if row['pass'] else '✗'} | {row['malformed']} | "
                f"{row['tool_calls']} | {row['rate_pct']:.2f}% |\n"
            )
    out.append("\n")

    out.append("## Taxonomy slicing — malformed rate by dimension\n")
    for dim in sorted(by_dim.keys()):
        out.append(f"### {dim}\n")
        out.append("| value | runs | malformed | tool_calls | rate |\n")
        out.append("|---|---|---|---|---|\n")
        for val in sorted(by_dim[dim].keys()):
            tuples = by_dim[dim][val]
            mal = sum(t[0] for t in tuples)
            calls = sum(t[1] for t in tuples)
            r = (mal / (mal + calls) * 100) if (mal + calls) > 0 else 0.0
            out.append(f"| `{val}` | {len(tuples)} | {mal} | {calls} | {r:.2f}% |\n")
        out.append("\n")

    # ============================================================
    # Q4 self-assessment / calibration (only when present in runs)
    # ============================================================
    # 2x2 per suite: actual PASS/FAIL × predicted PASS/FAIL
    # plus failure-flagging recall = TN / (TN + FP) — the hero metric.
    sa_suites = []
    for name, rec in sorted(suite_records.items()):
        if "error" in rec: continue
        sa_rows = [r for r in rec["runs"] if r.get("self_captured")]
        if not sa_rows: continue
        tp = fp = fn = tn = 0
        confs = []
        for r in sa_rows:
            actual = r["pass"]
            pred = r.get("self_predicted_pass")
            if pred is None: continue
            if actual and pred: tp += 1
            elif actual and not pred: fn += 1
            elif not actual and pred: fp += 1
            else: tn += 1
            if r.get("self_confidence") is not None:
                confs.append(r["self_confidence"])
        flagging_recall = (tn / (tn + fp) * 100) if (tn + fp) > 0 else None
        sa_suites.append({
            "name": name, "tp": tp, "fp": fp, "fn": fn, "tn": tn,
            "recall": flagging_recall,
            "avg_conf": sum(confs) / len(confs) if confs else None,
            "n_captured": len(sa_rows),
        })

    if sa_suites:
        out.append("## Q4 — Self-assessment & failure-flagging recall\n")
        out.append(
            "Per-suite 2×2 of actual outcome × agent's predicted outcome. "
            "**Failure-flagging recall = TN / (TN + FP)** — of runs the harness scored FAIL, what fraction did the agent flag as failures. <97% = HARD FAIL per the asymmetric-cost principle.\n\n"
        )
        out.append("| Suite | n captured | avg conf | TP | FP (silent fail) | FN (paranoid) | TN | Failure-flag recall |\n")
        out.append("|---|---|---|---|---|---|---|---|\n")
        for s in sa_suites:
            rec = f"{s['recall']:.1f}%" if s["recall"] is not None else "n/a"
            avg = f"{s['avg_conf']:.0f}" if s["avg_conf"] is not None else "—"
            out.append(
                f"| `{s['name']}` | {s['n_captured']} | {avg} | {s['tp']} | "
                f"{s['fp']} | {s['fn']} | {s['tn']} | **{rec}** |\n"
            )

        # Aggregate
        tp = sum(s["tp"] for s in sa_suites)
        fp = sum(s["fp"] for s in sa_suites)
        fn = sum(s["fn"] for s in sa_suites)
        tn = sum(s["tn"] for s in sa_suites)
        agg_recall = (tn / (tn + fp) * 100) if (tn + fp) > 0 else None
        rec_str = f"**{agg_recall:.1f}%**" if agg_recall is not None else "n/a"
        out.append(
            f"| **AGGREGATE** | {tp+fp+fn+tn} | | **{tp}** | **{fp}** | "
            f"**{fn}** | **{tn}** | {rec_str} |\n\n"
        )

        # Reliability buckets — bucket runs by confidence, show empirical pass rate
        buckets = [(0,50),(50,60),(60,70),(70,80),(80,90),(90,101)]
        rows_with_conf = [
            r for rec in suite_records.values() if "error" not in rec
            for r in rec["runs"] if r.get("self_confidence") is not None
        ]
        if rows_with_conf:
            out.append("### Reliability — confidence bucket vs empirical pass rate\n")
            out.append("| Confidence bucket | runs | actual passes | empirical pass % |\n")
            out.append("|---|---|---|---|\n")
            for lo, hi in buckets:
                bucket = [r for r in rows_with_conf if lo <= r["self_confidence"] < hi]
                if not bucket: continue
                p = sum(1 for r in bucket if r["pass"])
                out.append(f"| {lo}–{hi-1 if hi<101 else 100} | {len(bucket)} | {p} | {p*100/len(bucket):.0f}% |\n")
            out.append("\n*Well-calibrated agent: empirical pass % should track the bucket midpoint.*\n\n")
    else:
        out.append("## Q4 — Self-assessment\n_(No self-assessment data in this sweep — pre-Tier-1B binary, or capture skipped.)_\n\n")

    return "".join(out)


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: sweep-analyze.py SWEEP_DIR [OUTPUT.md]", file=sys.stderr)
        return 2
    sweep_dir = Path(argv[1])
    out_path = Path(argv[2]) if len(argv) > 2 else sweep_dir / "ANALYSIS.md"
    md = analyze(sweep_dir)
    out_path.write_text(md, encoding="utf-8")
    # Force stdout to UTF-8 so the preview's checkmarks / em-dashes don't
    # crash on Windows' default cp1252 console encoding.
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    print(f"wrote {out_path}")
    print("--- preview ---")
    print(md[:1500])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
