"""Analyze the classification benchmark (team-classify-archetypes-tier1).

Reads the bench `--json` summary + preserved `_bench-session.jsonl` files
to compute:
  - Per-archetype accuracy (correct / total)
  - Overall accuracy
  - Confusion matrix (what did the model SAY for each ticket?)
  - List of misclassifications

The bench assertion just checks "expected archetype token appears in
assistant text". A more granular view is whether the model emitted
exactly the right [ARCHETYPE: AX] token — and if not, which one it
actually said.

Usage:
    python classify-analyze.py SUMMARY.json [OUTPUT.md]
"""

from __future__ import annotations
import json, os, re, sys
from pathlib import Path
from collections import defaultdict

ARCHETYPE_RE = re.compile(r"\[ARCHETYPE:\s*A([1-8])\]", re.IGNORECASE)


def expected_archetype(instance_id: str) -> str | None:
    """Pull the expected archetype out of the instance id (cls-aN-X-...)."""
    m = re.match(r"cls-a([1-8])-", instance_id)
    return f"A{m.group(1)}" if m else None


def extract_model_classification(session_log_path: str) -> tuple[str | None, str | None]:
    """Return (predicted_archetype, raw_final_text). predicted is None if the model didn't emit a parseable token at all."""
    if not session_log_path or not os.path.exists(session_log_path):
        return None, None
    last_text = ""
    with open(session_log_path, "r", encoding="utf-8") as f:
        for line in f:
            try:
                ev = json.loads(line)
            except Exception:
                continue
            if ev.get("type") != "llm_response":
                continue
            data = ev.get("data") or {}
            if data.get("thread_id") != "main":
                continue
            content = data.get("content")
            if not isinstance(content, dict):
                continue
            # content is {"role":"assistant","content":[{"type":"text","text":"..."}]}
            parts = content.get("content") or []
            for p in parts:
                if isinstance(p, dict) and p.get("type") == "text":
                    last_text = p.get("text") or last_text
    m = ARCHETYPE_RE.search(last_text)
    pred = f"A{m.group(1).upper()}" if m else None
    return pred, last_text.strip()


def analyze(summary_path: Path) -> str:
    raw = summary_path.read_text(encoding="utf-8")
    brace = raw.find("{")
    if brace > 0:
        raw = raw[brace:]
    summary = json.loads(raw)

    rows = []  # list of (instance, expected, predicted, pass, raw_text)
    for r in summary["runs"]:
        inst = r["instance_id"]
        exp = expected_archetype(inst)
        sp = r.get("session_log_path")
        pred, txt = extract_model_classification(sp) if sp else (None, None)
        rows.append({
            "instance": inst,
            "expected": exp,
            "predicted": pred,
            "pass": r["pass"],
            "raw_tail": (txt or "")[-300:],
        })

    # Overall accuracy = predicted == expected
    correct = sum(1 for r in rows if r["predicted"] == r["expected"])
    total = len(rows)

    # Per-archetype accuracy
    by_arch_correct: dict[str, int] = defaultdict(int)
    by_arch_total: dict[str, int] = defaultdict(int)
    for r in rows:
        by_arch_total[r["expected"]] += 1
        if r["predicted"] == r["expected"]:
            by_arch_correct[r["expected"]] += 1

    # Confusion matrix: expected -> predicted -> count
    confusion: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    for r in rows:
        pred_label = r["predicted"] or "UNPARSED"
        confusion[r["expected"]][pred_label] += 1

    # Render
    out: list[str] = []
    out.append(f"# Classification analysis — {summary_path.name}\n\n")
    out.append(f"**Overall accuracy: {correct}/{total} = {100.0*correct/total:.1f}%**\n\n")

    out.append("## Per-archetype accuracy\n")
    out.append("| Expected | Correct | Total | Accuracy |\n")
    out.append("|---|---|---|---|\n")
    archetypes = [f"A{i}" for i in range(1, 9)]
    for a in archetypes:
        if by_arch_total[a]:
            c, t = by_arch_correct[a], by_arch_total[a]
            out.append(f"| **{a}** | {c} | {t} | {100.0*c/t:.0f}% |\n")
    out.append("\n")

    out.append("## Confusion matrix\n")
    out.append("Rows = expected; columns = what the model said. `UNPARSED` = model didn't emit a parseable `[ARCHETYPE: AX]` token.\n\n")
    cols = sorted({"A1","A2","A3","A4","A5","A6","A7","A8","UNPARSED"})
    out.append("| Expected ↓ / Predicted → | " + " | ".join(cols) + " |\n")
    out.append("|" + ("---|" * (1 + len(cols))) + "\n")
    for a in archetypes:
        if not by_arch_total[a]:
            continue
        cells = [str(confusion[a].get(c, 0)) for c in cols]
        out.append(f"| **{a}** | " + " | ".join(cells) + " |\n")
    out.append("\n")

    out.append("## Misclassifications\n")
    misclass = [r for r in rows if r["predicted"] != r["expected"]]
    if not misclass:
        out.append("None — model classified every instance correctly.\n")
    else:
        for r in misclass:
            out.append(f"- `{r['instance']}` — expected **{r['expected']}**, predicted **{r['predicted'] or 'UNPARSED'}**\n")
            out.append(f"  > {(r['raw_tail'] or '').replace(chr(10), ' ')[:240]}…\n")
    out.append("\n")

    return "".join(out)


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: classify-analyze.py SUMMARY.json [OUTPUT.md]", file=sys.stderr)
        return 2
    p = Path(argv[1])
    out_path = Path(argv[2]) if len(argv) > 2 else p.with_suffix(".ANALYSIS.md")
    md = analyze(p)
    out_path.write_text(md, encoding="utf-8")
    try: sys.stdout.reconfigure(encoding="utf-8")
    except Exception: pass
    print(f"wrote {out_path}")
    print("--- preview ---")
    print(md[:1200])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
