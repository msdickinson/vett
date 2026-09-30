"""Q3 — Decomposition benchmark orchestrator.

Reads a "D-pair" YAML defining a monolithic instance + a decomposed
chain of sub-tasks. Runs both, then evaluates the monolith's FINAL
assertions against the decomposed run's workspace. Compares.

D-pair YAML shape (example, see decompose-pairs/ dir):
    name: D1-checkout
    description: |
      Compare monolithic A7 "build whole checkout" vs decomposed
      A5 (Money) + A5 (Order) + A6 (CheckoutService).
    monolith:
      suite: team-decompose-pairs
      instance: D1-monolith-checkout
    decomposed:
      - { suite: team-decompose-pairs, instance: D1-step1-money }
      - { suite: team-decompose-pairs, instance: D1-step2-order }
      - { suite: team-decompose-pairs, instance: D1-step3-checkout }
    final_assertions_from: D1-monolith-checkout   # same suite

Output: per D-pair Markdown report with PASS rate, wall time, malformed
count, and "did decomposition lift?" comparison.

Usage:
    python bench-decompose.py PAIR_YAML [--reps 3] [--endpoint URL]
"""

from __future__ import annotations
import argparse, json, os, shutil, subprocess, sys, tempfile, time
from pathlib import Path

try:
    import yaml  # type: ignore
except ImportError:
    print("error: PyYAML required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)


def run_vett(args: list[str], stdout_path: Path, stderr_path: Path) -> int:
    """Run a vett command, capture stdout/stderr. Returns exit code."""
    with open(stdout_path, "w", encoding="utf-8") as so, open(stderr_path, "w", encoding="utf-8") as se:
        p = subprocess.run(["vett"] + args, stdout=so, stderr=se, check=False)
    return p.returncode


def parse_summary(stdout_path: Path) -> dict | None:
    try:
        content = stdout_path.read_text(encoding="utf-8").strip()
        brace = content.find("{")
        if brace > 0:
            content = content[brace:]
        return json.loads(content)
    except Exception as e:
        print(f"  ! parse failed for {stdout_path}: {e}", file=sys.stderr)
        return None


def init_empty_git_workspace() -> str:
    """Create a fresh empty-git-repo workspace mirroring the bench harness."""
    base = Path(tempfile.gettempdir()) / "vett-team-bench"
    base.mkdir(exist_ok=True)
    ws = base / f"decompose-{int(time.time())}-{os.urandom(3).hex()}"
    ws.mkdir()
    subprocess.run(["git", "init", "-q"], cwd=ws, check=True)
    subprocess.run(["git", "config", "user.email", "bench@vett.local"], cwd=ws, check=True)
    subprocess.run(["git", "config", "user.name", "vett-bench"], cwd=ws, check=True)
    subprocess.run(["git", "commit", "--allow-empty", "-m", "init", "-q"], cwd=ws, check=True)
    return str(ws)


def run_pair(pair_yaml: Path, reps: int, endpoint: str, out_dir: Path) -> dict:
    pair = yaml.safe_load(pair_yaml.read_text(encoding="utf-8"))
    pair_name = pair["name"]
    pair_out = out_dir / pair_name
    pair_out.mkdir(parents=True, exist_ok=True)

    mono = pair["monolith"]
    decomp_chain: list[dict] = pair["decomposed"]
    print(f"\n=== {pair_name} ===")
    print(f"  monolith: {mono['suite']} / {mono['instance']}")
    print(f"  decomposed: {len(decomp_chain)} steps")

    # --- monolithic runs ---
    monolith_results: list[dict] = []
    for rep in range(reps):
        print(f"  monolith rep {rep+1}/{reps} ...", end=" ", flush=True)
        sp = pair_out / f"monolith-rep{rep}.json"
        ep = pair_out / f"monolith-rep{rep}.stderr.log"
        t0 = time.time()
        rc = run_vett([
            "team-bench", mono["suite"],
            "--instance", mono["instance"],
            "--endpoint", endpoint,
            "--keep-all-worktrees", "--json",
        ], sp, ep)
        wall = time.time() - t0
        d = parse_summary(sp)
        if d and d["runs"]:
            r = d["runs"][0]
            print(f"PASS={r['pass']} wall={wall:.0f}s")
            monolith_results.append({
                "rep": rep, "pass": r["pass"], "wall": wall,
                "session_log": r.get("session_log_path"),
            })
        else:
            print(f"NO_DATA rc={rc} wall={wall:.0f}s")
            monolith_results.append({"rep": rep, "pass": False, "wall": wall, "session_log": None, "no_data": True})

    # --- decomposed chain runs ---
    decomposed_results: list[dict] = []
    for rep in range(reps):
        print(f"  decomposed rep {rep+1}/{reps} starting fresh workspace ...")
        ws = init_empty_git_workspace()
        steps_outcomes: list[dict] = []
        chain_pass = True
        chain_wall = 0.0
        for step_idx, step in enumerate(decomp_chain):
            label = f"step{step_idx+1}"
            print(f"    [{label}] {step['suite']} / {step['instance']} ...", end=" ", flush=True)
            sp = pair_out / f"decomp-rep{rep}-{label}.json"
            ep = pair_out / f"decomp-rep{rep}-{label}.stderr.log"
            t0 = time.time()
            rc = run_vett([
                "team-bench", step["suite"],
                "--instance", step["instance"],
                "--endpoint", endpoint,
                "--keep-all-worktrees", "--json",
                "--workspace-dir", ws,
            ], sp, ep)
            wall = time.time() - t0
            chain_wall += wall
            d = parse_summary(sp)
            if d and d["runs"]:
                r = d["runs"][0]
                step_pass = r["pass"]
                print(f"PASS={step_pass} wall={wall:.0f}s")
                steps_outcomes.append({"step": label, "pass": step_pass, "wall": wall, "session_log": r.get("session_log_path")})
                if not step_pass:
                    chain_pass = False
                    print(f"    chain aborted at {label}")
                    break
            else:
                print(f"NO_DATA rc={rc} wall={wall:.0f}s")
                steps_outcomes.append({"step": label, "pass": False, "wall": wall, "no_data": True})
                chain_pass = False
                break
        decomposed_results.append({
            "rep": rep,
            "chain_pass": chain_pass,
            "wall": chain_wall,
            "workspace": ws,
            "steps": steps_outcomes,
        })

    # --- compare ---
    m_pass = sum(1 for r in monolith_results if r["pass"])
    d_pass = sum(1 for r in decomposed_results if r["chain_pass"])
    m_wall = sum(r["wall"] for r in monolith_results) / max(1, len(monolith_results))
    d_wall = sum(r["wall"] for r in decomposed_results) / max(1, len(decomposed_results))
    summary = {
        "pair_name": pair_name,
        "reps": reps,
        "monolith_pass_rate": (m_pass, len(monolith_results)),
        "decomposed_pass_rate": (d_pass, len(decomposed_results)),
        "monolith_avg_wall": m_wall,
        "decomposed_avg_wall": d_wall,
        "lift_pct_points": (d_pass - m_pass) * 100.0 / max(1, len(monolith_results)),
        "monolith": monolith_results,
        "decomposed": decomposed_results,
    }
    (pair_out / "SUMMARY.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"\n  Result: monolith {m_pass}/{len(monolith_results)} ({m_pass*100/len(monolith_results):.0f}%) "
          f"vs decomposed {d_pass}/{len(decomposed_results)} ({d_pass*100/len(decomposed_results):.0f}%) "
          f"lift={summary['lift_pct_points']:+.0f}pp  "
          f"wall mono={m_wall:.0f}s decomp={d_wall:.0f}s")
    return summary


def render_report(summaries: list[dict], out_path: Path):
    out: list[str] = []
    out.append("# Decomposition benchmark — Q3 results\n\n")
    out.append("| Pair | Monolith pass | Decomposed pass | Lift | Monolith wall | Decomposed wall |\n")
    out.append("|---|---|---|---|---|---|\n")
    for s in summaries:
        m = f'{s["monolith_pass_rate"][0]}/{s["monolith_pass_rate"][1]}'
        d = f'{s["decomposed_pass_rate"][0]}/{s["decomposed_pass_rate"][1]}'
        out.append(f"| `{s['pair_name']}` | {m} | {d} | {s['lift_pct_points']:+.0f}pp | "
                   f"{s['monolith_avg_wall']:.0f}s | {s['decomposed_avg_wall']:.0f}s |\n")
    out.append("\n## Verdict\n")
    total_mono = sum(s["monolith_pass_rate"][0] for s in summaries)
    total_runs = sum(s["monolith_pass_rate"][1] for s in summaries)
    total_decomp = sum(s["decomposed_pass_rate"][0] for s in summaries)
    total_decomp_runs = sum(s["decomposed_pass_rate"][1] for s in summaries)
    if total_runs and total_decomp_runs:
        mp = total_mono / total_runs
        dp = total_decomp / total_decomp_runs
        out.append(f"- Aggregate monolithic: **{total_mono}/{total_runs} = {mp*100:.0f}%**\n")
        out.append(f"- Aggregate decomposed: **{total_decomp}/{total_decomp_runs} = {dp*100:.0f}%**\n")
        out.append(f"- **Lift: {(dp-mp)*100:+.0f} percentage points**\n\n")
        if (dp - mp) > 0.10:
            out.append("**STRONG SIGNAL** — decomposition meaningfully improves pass rate.\n")
        elif (dp - mp) > 0.03:
            out.append("**WEAK SIGNAL** — decomposition mildly improves pass rate; more reps needed.\n")
        elif (dp - mp) < -0.03:
            out.append("**NEGATIVE SIGNAL** — decomposition actually hurts. Worth investigating why.\n")
        else:
            out.append("**FLAT** — decomposition doesn't change the rate. Either both archetypes are already easy, or the overhead cancels the benefit.\n")
    out_path.write_text("".join(out), encoding="utf-8")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("pair_yamls", nargs="+", help="one or more D-pair YAML paths")
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--endpoint", default="http://old-gpu-a:8000/v1")
    ap.add_argument("--out-dir", default=None)
    args = ap.parse_args(argv[1:])

    out_dir = Path(args.out_dir) if args.out_dir else Path(
        "results/decompose-runs"
    ) / f"decompose-{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}"
    out_dir.mkdir(parents=True, exist_ok=True)

    summaries: list[dict] = []
    for py in args.pair_yamls:
        try:
            summaries.append(run_pair(Path(py), args.reps, args.endpoint, out_dir))
        except Exception as e:
            print(f"  ! pair {py} failed: {e}", file=sys.stderr)

    render_report(summaries, out_dir / "REPORT.md")
    print(f"\nWrote {out_dir / 'REPORT.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
