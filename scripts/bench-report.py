"""Generate a self-contained HTML report from `vett team-bench` text output.

Usage:
    python bench-report.py --output report.html \
        --suite team-smoke-tier1 path/to/t1-output.txt \
        --suite team-smoke-tier2 path/to/t2-output.txt \
        --suite team-method-tier1 path/to/methods-output.txt

Each --suite arg pairs a suite YAML name (resolved against $HOME/.vett/suites/
or suites/) with a captured bench output file. The script enriches
each instance with its description from the suite YAML and emits a single
HTML file with per-suite tables, per-instance pass-rate coloring, wall-time
averages, and inline failure details.

The parser is a deliberate "match-what-vett-prints" loop — there's no JSON
output from `vett team-bench` (yet), so we scrape the human text. Rerun the
script any time you have fresh output to refresh the report.
"""

from __future__ import annotations

import argparse
import html
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

try:
    import yaml  # type: ignore
except ImportError:
    print("error: PyYAML required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)


HEADER_RE = re.compile(
    r"^Suite:\s+(?P<name>\S+)\s+\(tier\s+(?P<tier>\d+)\)\s+profile=(?P<profile>\S+)"
)
# The run header. We capture id + n/m, but the PASS/FAIL outcome may be on the
# same line OR on the next line (vett prints a "warn: timeout..." between
# them when a run hits its wall-clock cap).
#
# The `(run n/m)` suffix is OPTIONAL and its absence is the DEFAULT case:
# TeamBenchCommand.cs:231 builds the label as
#     repeat > 1 ? $"{instance.Id} (run {r + 1}/{repeat})" : instance.Id
# so every `vett team-bench` invocation without an explicit `--repeat N > 1`
# prints a bare `  <id> ... PASS (...)` line. Requiring the suffix made this
# regex match ZERO lines on such a capture: the suite header still parsed, so
# the report rendered the suite under its real name with 0 instances and a
# 0/0 pill, and — because `render_suite` only opens the <details> when
# `passed != total` — a suite whose runs were ALL silently dropped rendered
# COLLAPSED, i.e. in the same visual state as a suite that fully passed.
# Measured on captured fixtures before the fix: a --repeat 1 shaped capture
# parsed to instances=0 passed=0 total=0 and printed "Overall pass rate 0.0%".
RUN_HEAD_RE = re.compile(
    r"^\s{2}(?P<id>\S+)(?:\s+\(run\s+(?P<n>\d+)/(?P<m>\d+)\))?\s+\.\.\."
)
RUN_OUTCOME_RE = re.compile(
    r"(?P<status>PASS|FAIL)\s+"
    r"\((?P<wall>[\d.]+)s,\s+leader=(?P<leader>\d+)\s+iters,\s+members=\[(?P<members>[^\]]*)\]\)"
)
ASSERT_LINE_RE = re.compile(r"^\s{6}([✓✗!])\s(?P<text>.+)$")
SESSION_LOG_RE = re.compile(r"^\s{4}session log:\s+(?P<path>.+)$")
# vett's OWN count of the same runs (TeamBenchCommand.cs:259). This is an
# independent witness to the numbers this script derives by scraping, and it
# is the only thing that can catch the scraper drifting away from the printer
# again. Parsed into SuiteResult.summary_* and reconciled in `reconcile()`.
SUMMARY_TOTAL_RE = re.compile(r"^Summary:\s+(?P<passed>\d+)/(?P<total>\d+)\s+runs passed")


@dataclass
class RunResult:
    n: int
    total: int
    status: str  # "PASS" or "FAIL"
    wall_s: float
    leader_iters: int
    members: str  # raw "implementer-1=3, researcher-1=2" — kept for display
    fail_lines: list[str] = field(default_factory=list)
    session_log: str | None = None


@dataclass
class InstanceResult:
    id: str
    description: str = ""
    runs: list[RunResult] = field(default_factory=list)

    @property
    def passed(self) -> int:
        return sum(1 for r in self.runs if r.status == "PASS")

    @property
    def total(self) -> int:
        return len(self.runs)

    @property
    def avg_wall(self) -> float:
        if not self.runs:
            return 0.0
        return sum(r.wall_s for r in self.runs) / len(self.runs)


@dataclass
class SuiteResult:
    name: str
    tier: int = 0
    profile: str = ""
    instances: list[InstanceResult] = field(default_factory=list)
    # vett's own tally, scraped from its `Summary: P/T runs passed` line.
    # None means the capture had no Summary line (truncated log, run still in
    # flight, or a crash before the summary) — that is NOT the same as zero
    # and must not be reconciled as zero.
    summary_passed: int | None = None
    summary_total: int | None = None

    @property
    def passed(self) -> int:
        return sum(i.passed for i in self.instances)

    @property
    def total(self) -> int:
        return sum(i.total for i in self.instances)


def parse_bench_output(text: str) -> SuiteResult:
    """Parse one captured `vett team-bench` text output into a SuiteResult."""
    suite = SuiteResult(name="(unknown)")
    instances: dict[str, InstanceResult] = {}
    cur_run: RunResult | None = None
    pending_run: tuple[InstanceResult, int, int] | None = None  # (inst, n, m) waiting for outcome

    for raw in text.splitlines():
        line = raw.rstrip()

        m = HEADER_RE.match(line)
        if m:
            suite.name = m.group("name")
            suite.tier = int(m.group("tier"))
            suite.profile = m.group("profile")
            continue

        m = SUMMARY_TOTAL_RE.match(line)
        if m:
            suite.summary_passed = int(m.group("passed"))
            suite.summary_total = int(m.group("total"))
            continue

        head = RUN_HEAD_RE.match(line)
        if head:
            inst = instances.setdefault(head.group("id"), InstanceResult(id=head.group("id")))
            # Absent `(run n/m)` means vett ran this instance exactly once
            # (repeat == 1), not that the run index is unknown.
            n = int(head.group("n")) if head.group("n") else 1
            total = int(head.group("m")) if head.group("m") else 1
            pending_run = (inst, n, total)
            # The outcome may be on this same line OR on the next non-empty line.
            outcome = RUN_OUTCOME_RE.search(line)
            if outcome:
                cur_run = RunResult(
                    n=n, total=total,
                    status=outcome.group("status"),
                    wall_s=float(outcome.group("wall")),
                    leader_iters=int(outcome.group("leader")),
                    members=outcome.group("members"),
                )
                inst.runs.append(cur_run)
                pending_run = None
            continue

        # Outcome on its own line (only happens for timeout-triggered FAILs).
        if pending_run is not None:
            outcome = RUN_OUTCOME_RE.search(line)
            if outcome:
                inst, n, total = pending_run
                cur_run = RunResult(
                    n=n, total=total,
                    status=outcome.group("status"),
                    wall_s=float(outcome.group("wall")),
                    leader_iters=int(outcome.group("leader")),
                    members=outcome.group("members"),
                )
                inst.runs.append(cur_run)
                pending_run = None
                continue

        if cur_run is not None and cur_run.status == "FAIL":
            m = ASSERT_LINE_RE.match(line)
            if m:
                cur_run.fail_lines.append(f"{m.group(1)} {m.group('text')}")
                continue
            m = SESSION_LOG_RE.match(line)
            if m:
                cur_run.session_log = m.group("path")
                continue

    suite.instances = sorted(instances.values(), key=lambda i: i.id)
    return suite


def load_suite_yaml(name_or_path: str) -> dict:
    """Resolve and load a suite YAML by name or absolute path. Returns {} on miss."""
    candidates = [
        Path(name_or_path),
        Path(name_or_path + ".yaml"),
        Path.home() / ".vett" / "suites" / f"{name_or_path}.yaml",
        Path(__file__).resolve().parent.parent / "suites" / f"{name_or_path}.yaml",
    ]
    for p in candidates:
        if p.is_file():
            with p.open(encoding="utf-8") as f:
                return yaml.safe_load(f) or {}
    return {}


def reconcile(suite: SuiteResult) -> list[str]:
    """Compare what we SCRAPED against what vett itself PRINTED.

    The numbers in this report are derived by pattern-matching human output,
    so the report can disagree with the harness whenever the printer changes
    shape and the scraper doesn't. That already happened once: the run-header
    regex required a `(run n/m)` suffix that vett only prints for
    `--repeat N > 1`, so every default-repeat capture parsed to zero runs and
    the report published "Overall pass rate 0.0%" with no error of any kind.

    vett prints its own tally (`Summary: P/T runs passed`,
    TeamBenchCommand.cs:259) from the SAME list of results it printed the
    per-run lines from, which makes it an independent witness. Returns a list
    of human-readable complaints; empty means the two agree.

    A missing Summary line yields a complaint of its own rather than silence:
    "could not check" is not "checked and agreed".
    """
    problems: list[str] = []
    if suite.summary_total is None or suite.summary_passed is None:
        problems.append(
            f"{suite.name}: no `Summary: P/T runs passed` line in the capture, so the "
            f"{suite.total} scraped run(s) are UNRECONCILED (truncated log, or a crash "
            f"before the summary)."
        )
        return problems
    if suite.summary_total != suite.total:
        problems.append(
            f"{suite.name}: vett reported {suite.summary_total} run(s) but this script "
            f"scraped {suite.total}. {suite.summary_total - suite.total} run(s) are "
            f"MISSING from the report — the parser is out of step with vett's output "
            f"format; every rate below is computed on the wrong denominator."
        )
    if suite.summary_passed != suite.passed:
        problems.append(
            f"{suite.name}: vett reported {suite.summary_passed} pass(es) but this "
            f"script scraped {suite.passed}."
        )
    return problems


def enrich_with_yaml(suite: SuiteResult, suite_yaml: dict) -> None:
    """Fill instance descriptions from the suite YAML when present."""
    by_id = {i["id"]: i for i in suite_yaml.get("instances", [])}
    for inst in suite.instances:
        ydef = by_id.get(inst.id)
        if ydef:
            inst.description = (ydef.get("description") or "").strip()


# ---------- HTML rendering -----------------------------------------------


CSS = """
* { box-sizing: border-box; }
body {
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    margin: 0; padding: 24px;
    background: #0d1117; color: #e6edf3;
    line-height: 1.5;
}
h1 { margin: 0 0 4px 0; font-size: 24px; }
.subtitle { color: #8b949e; font-size: 13px; margin-bottom: 24px; }
.grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 12px; margin-bottom: 24px;
}
.card {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    padding: 12px 16px;
}
.card .label { color: #8b949e; font-size: 11px; text-transform: uppercase; letter-spacing: .5px; }
.card .value { font-size: 22px; font-weight: 600; margin-top: 2px; }
.card.pass .value { color: #3fb950; }
.card.fail .value { color: #f85149; }
.card.warn .value { color: #d29922; }

details {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    margin-bottom: 16px;
}
details summary {
    padding: 14px 16px; cursor: pointer; font-weight: 600;
    display: flex; align-items: center; gap: 12px;
    list-style: none;
}
details summary::-webkit-details-marker { display: none; }
details summary::before {
    content: "▶"; font-size: 10px; color: #8b949e;
    transition: transform .1s;
}
details[open] summary::before { transform: rotate(90deg); }
details summary .pill {
    margin-left: auto; padding: 2px 10px; border-radius: 12px;
    font-size: 12px; font-weight: 500;
}
.pill.green { background: rgba(63, 185, 80, .15); color: #3fb950; }
.pill.yellow { background: rgba(210, 153, 34, .15); color: #d29922; }
.pill.red    { background: rgba(248, 81, 73, .15); color: #f85149; }
.pill.gray   { background: rgba(139, 148, 158, .15); color: #8b949e; }

table {
    width: 100%; border-collapse: collapse;
    border-top: 1px solid #30363d;
}
th, td {
    text-align: left; padding: 8px 16px;
    border-bottom: 1px solid #21262d;
    font-size: 13px;
    vertical-align: top;
}
th { color: #8b949e; font-weight: 500; font-size: 11px; text-transform: uppercase; letter-spacing: .5px; }
tr:hover td { background: #1c2128; }

.id { font-family: "JetBrains Mono", "Consolas", monospace; font-weight: 500; }
.desc { color: #8b949e; font-size: 12px; max-width: 480px; }
.runs { font-family: "JetBrains Mono", "Consolas", monospace; }
.runs .r-pass { color: #3fb950; }
.runs .r-fail { color: #f85149; }
.fail-detail {
    margin-top: 6px; padding: 8px 12px;
    background: #0d1117; border-left: 2px solid #f85149;
    font-family: "JetBrains Mono", "Consolas", monospace;
    font-size: 11px; color: #c9d1d9; white-space: pre-wrap;
    border-radius: 0 4px 4px 0;
}
.fail-detail .ok { color: #3fb950; }
.fail-detail .ng { color: #f85149; }
.fail-detail .warn { color: #d29922; }
.session { color: #58a6ff; font-size: 11px; word-break: break-all; }
.muted { color: #8b949e; }
.summary-line { font-size: 12px; color: #8b949e; }
"""


def pill_class(passed: int, total: int) -> str:
    if total == 0:
        return "gray"
    if passed == total:
        return "green"
    if passed > 0:
        return "yellow"
    return "red"


def fmt_assertion_line(line: str) -> str:
    """Color a captured assertion line. line starts with '✓ ', '✗ ', or '! '."""
    if line.startswith("✓ "):
        return f'<span class="ok">{html.escape(line)}</span>'
    if line.startswith("✗ "):
        return f'<span class="ng">{html.escape(line)}</span>'
    if line.startswith("! "):
        return f'<span class="warn">{html.escape(line)}</span>'
    return html.escape(line)


def render_runs(inst: InstanceResult) -> str:
    parts = []
    for r in inst.runs:
        cls = "r-pass" if r.status == "PASS" else "r-fail"
        sym = "✓" if r.status == "PASS" else "✗"
        parts.append(f'<span class="{cls}" title="run {r.n}/{r.total}: {r.status} ({r.wall_s:.1f}s)">{sym}</span>')
    return " ".join(parts)


def render_failures(inst: InstanceResult) -> str:
    fails = [r for r in inst.runs if r.status == "FAIL"]
    if not fails:
        return ""
    blocks = []
    for r in fails:
        body = "\n".join(fmt_assertion_line(l) for l in r.fail_lines) or "(no detail captured)"
        log = f'<div class="session">log: {html.escape(r.session_log)}</div>' if r.session_log else ""
        blocks.append(
            f'<div class="fail-detail"><div class="muted">run {r.n}/{r.total} '
            f'({r.wall_s:.1f}s, leader={r.leader_iters} iters)</div>{body}{log}</div>'
        )
    return "\n".join(blocks)


def render_suite(suite: SuiteResult) -> str:
    pass_pct = (100.0 * suite.passed / suite.total) if suite.total else 0.0
    cls = pill_class(suite.passed, suite.total)
    rows = []
    for inst in suite.instances:
        inst_cls = pill_class(inst.passed, inst.total)
        rows.append(
            f"""
<tr>
  <td class="id">{html.escape(inst.id)}<div class="desc">{html.escape(inst.description) if inst.description else '<span class="muted">(no description)</span>'}</div></td>
  <td><span class="pill {inst_cls}">{inst.passed}/{inst.total}</span></td>
  <td class="runs">{render_runs(inst)}</td>
  <td class="muted">{inst.avg_wall:.0f}s avg</td>
  <td>{render_failures(inst)}</td>
</tr>"""
        )
    return f"""
<details {"open" if suite.passed != suite.total else ""}>
  <summary>
    <span>{html.escape(suite.name)}</span>
    <span class="muted summary-line">tier {suite.tier} · profile {html.escape(suite.profile)} · {len(suite.instances)} instances</span>
    <span class="pill {cls}">{suite.passed}/{suite.total} ({pass_pct:.0f}%)</span>
  </summary>
  <table>
    <thead><tr><th>Instance</th><th>Pass-rate</th><th>Runs</th><th>Wall</th><th>Failure detail</th></tr></thead>
    <tbody>{''.join(rows)}</tbody>
  </table>
</details>
"""


def render_html(suites: list[SuiteResult], generated_at: str) -> str:
    total_pass = sum(s.passed for s in suites)
    total_runs = sum(s.total for s in suites)
    total_inst = sum(len(s.instances) for s in suites)

    cards = [
        f'<div class="card"><div class="label">Suites</div><div class="value">{len(suites)}</div></div>',
        f'<div class="card"><div class="label">Instances</div><div class="value">{total_inst}</div></div>',
        f'<div class="card pass"><div class="label">Runs passed</div><div class="value">{total_pass}/{total_runs}</div></div>',
    ]
    overall_pct = (100.0 * total_pass / total_runs) if total_runs else 0.0
    cards.append(
        f'<div class="card {"pass" if overall_pct == 100 else ("warn" if overall_pct >= 80 else "fail")}">'
        f'<div class="label">Overall pass rate</div><div class="value">{overall_pct:.1f}%</div></div>'
    )

    # The reconciliation has to land in the ARTIFACT, not only on the stderr
    # of whoever happened to run the script: this HTML is what gets read
    # months later, and a rate computed on a short denominator looks exactly
    # like a real one.
    problems = [p for s in suites for p in reconcile(s)]
    banner = ""
    if problems:
        items = "".join(f"<li>{html.escape(p)}</li>" for p in problems)
        banner = (
            '<div style="background:#4a1113;border:2px solid #d33;border-radius:8px;'
            'padding:14px 18px;margin:16px 0;color:#ffd9d9;font-weight:600">'
            'NUMBERS BELOW ARE NOT TRUSTWORTHY — this report disagrees with vett\'s '
            f'own summary:<ul style="font-weight:400">{items}</ul></div>'
        )

    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Vett team-bench report</title>
<style>{CSS}</style>
</head>
<body>
<h1>Vett team-bench report</h1>
<div class="subtitle">Generated {html.escape(generated_at)}. Click a suite header to expand its instance table; rows with failures auto-expand.</div>
{banner}
<div class="grid">{cards[0]}{cards[1]}{cards[2]}{cards[3]}</div>
{''.join(render_suite(s) for s in suites)}
</body>
</html>"""


# ---------- self test ------------------------------------------------------
#
# `python bench-report.py --self-test`
#
# This scraper's failure mode is silence: when the printer changes shape the
# parser matches nothing, and nothing throws. These fixtures are the failure
# test for that. Both are the SAME two runs — one PASS, one FAIL — printed by
# the two label shapes TeamBenchCommand.cs:231 can produce, so they must
# reduce to identical numbers. Keep them byte-faithful to vett's output
# (2-space run indent, 6-space assertion indent, 4-space session-log indent).

_FIXTURE_REPEAT_1 = """Suite: team-smoke-tier1 (tier 1)  profile=coding-team  instances=2 × 1 = 2 runs

  t1-alpha ... PASS  (12.3s, leader=4 iters, members=[implementer-1=3])
  t1-beta ... FAIL  (30.1s, leader=9 iters, members=[implementer-1=7])
      ! timeout after 180s without settle
      ✗ file Program.cs exists — missing
    session log: /tmp/x/session.jsonl

Summary: 1/2 runs passed
"""

_FIXTURE_REPEAT_2 = """Suite: team-smoke-tier1 (tier 1)  profile=coding-team  instances=1 × 2 = 2 runs

  t1-alpha (run 1/2) ... PASS  (12.3s, leader=4 iters, members=[implementer-1=3])
  t1-alpha (run 2/2) ... FAIL  (30.1s, leader=9 iters, members=[implementer-1=7])
      ✗ file Program.cs exists — missing

Summary: 1/2 runs passed
  t1-alpha: 1/2
"""


def self_test() -> int:
    failures: list[str] = []

    def check(label: str, actual, expected) -> None:
        if actual != expected:
            failures.append(f"{label}: expected {expected!r}, got {actual!r}")

    # 1. DEFAULT SHAPE (--repeat 1, no `(run n/m)` suffix). This is the case
    #    that parsed to zero before 2026-08-24.
    s1 = parse_bench_output(_FIXTURE_REPEAT_1)
    check("repeat1.name", s1.name, "team-smoke-tier1")
    check("repeat1.instances", len(s1.instances), 2)
    check("repeat1.total", s1.total, 2)
    check("repeat1.passed", s1.passed, 1)
    check("repeat1.reconcile", reconcile(s1), [])
    beta = [i for i in s1.instances if i.id == "t1-beta"]
    check("repeat1.t1-beta present", len(beta), 1)
    if beta:
        check("repeat1.t1-beta leader_iters", beta[0].runs[0].leader_iters, 9)
        check("repeat1.t1-beta fail_lines", len(beta[0].runs[0].fail_lines), 2)
        check("repeat1.t1-beta session_log",
              beta[0].runs[0].session_log, "/tmp/x/session.jsonl")

    # 2. REPEATED SHAPE — must be unchanged by the fix, and must agree with
    #    (1) run-for-run: same two outcomes, same denominator.
    s2 = parse_bench_output(_FIXTURE_REPEAT_2)
    check("repeat2.instances", len(s2.instances), 1)
    check("repeat2.total", s2.total, 2)
    check("repeat2.passed", s2.passed, 1)
    check("repeat2.reconcile", reconcile(s2), [])
    check("shapes agree on totals", (s1.passed, s1.total), (s2.passed, s2.total))

    # 3. THE GATE MUST FIRE. A green check you wrote is unverified until you
    #    have seen it go red, so this deliberately hides one run line from the
    #    parser (3-space indent instead of 2) and asserts the reconciliation
    #    notices the short denominator instead of publishing 1/1 = 100%.
    damaged = _FIXTURE_REPEAT_1.replace(
        "  t1-beta ... FAIL", "   t1-beta ... FAIL")
    s3 = parse_bench_output(damaged)
    check("damaged.total (parser really did miss it)", s3.total, 1)
    problems = reconcile(s3)
    check("damaged.reconcile fires", len(problems), 1)
    if problems and "MISSING" not in problems[0]:
        failures.append(f"damaged.reconcile wording: {problems[0]!r}")

    # 4. NO SUMMARY LINE is "could not check", not "checked and agreed".
    s4 = parse_bench_output(
        _FIXTURE_REPEAT_1.replace("Summary: 1/2 runs passed", ""))
    check("nosummary.total", s4.total, 2)
    check("nosummary.summary_total is None", s4.summary_total, None)
    nosum = reconcile(s4)
    check("nosummary.reconcile fires", len(nosum), 1)
    if nosum and "UNRECONCILED" not in nosum[0]:
        failures.append(f"nosummary.reconcile wording: {nosum[0]!r}")

    if failures:
        for f in failures:
            print(f"FAIL {f}", file=sys.stderr)
        print(f"self-test: {len(failures)} failure(s)", file=sys.stderr)
        return 1
    print("self-test: all checks passed")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Render a vett team-bench HTML report.")
    ap.add_argument(
        "--suite", action="append", nargs=2, metavar=("SUITE_NAME", "OUTPUT_FILE"),
        help="Pair a suite YAML name (or path) with a captured bench output file. Repeatable.",
    )
    ap.add_argument("--output", "-o", default="bench-report.html", help="Output HTML path.")
    ap.add_argument("--title-stamp", default=None, help="Override the generated-at line.")
    ap.add_argument("--self-test", action="store_true",
                    help="Parse embedded fixtures and verify the scraper. No output written.")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if not args.suite:
        ap.error("at least one --suite NAME FILE pair required")

    import datetime as dt

    suites: list[SuiteResult] = []
    for suite_name, out_path in args.suite:
        if not Path(out_path).is_file():
            print(f"warning: bench output {out_path} not found, skipping", file=sys.stderr)
            continue
        text = Path(out_path).read_text(encoding="utf-8", errors="replace")
        suite = parse_bench_output(text)
        # If the captured output lacked a Suite: header (rare), use the
        # name we were told about as the fallback.
        if suite.name == "(unknown)":
            suite.name = suite_name
        suite_yaml = load_suite_yaml(suite_name)
        if suite_yaml:
            enrich_with_yaml(suite, suite_yaml)
        for problem in reconcile(suite):
            print(f"WARNING: {problem}", file=sys.stderr)
        suites.append(suite)

    if not suites:
        print("error: no suites parsed; nothing to render", file=sys.stderr)
        return 1

    stamp = args.title_stamp or dt.datetime.now().isoformat(timespec="seconds")
    html_doc = render_html(suites, stamp)
    Path(args.output).write_text(html_doc, encoding="utf-8")
    print(f"wrote {args.output} ({len(suites)} suite(s), "
          f"{sum(len(s.instances) for s in suites)} instance(s), "
          f"{sum(s.total for s in suites)} run(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
