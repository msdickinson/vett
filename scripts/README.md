# vett scripts

## bench-report.py

Generates a single self-contained HTML page from one or more captured
`vett team-bench` text outputs. Useful for eyeballing pass/fail across
suites without re-running anything.

**Capture an output and run the report:**

```bash
# Capture each suite to a file
vett team-bench team-smoke-tier1 --profile coding-team-v2 --repeat 3 \
  | tee /tmp/t1.txt
vett team-bench team-smoke-tier2 --profile coding-team-v2 --repeat 3 \
  | tee /tmp/t2.txt
vett team-bench team-method-tier1 --profile coding-team-v2 --repeat 3 \
  | tee /tmp/methods.txt

# Render
python scripts/bench-report.py \
  --suite team-smoke-tier1   /tmp/t1.txt \
  --suite team-smoke-tier2   /tmp/t2.txt \
  --suite team-method-tier1  /tmp/methods.txt \
  --output bench-report.html

# Open in your browser
start bench-report.html        # Windows
```

Each `--suite NAME FILE` pair tells the script to (a) parse `FILE` as bench
text output and (b) enrich it with descriptions from the matching suite
YAML (resolved against `~/.vett/suites/` or `suites/`).

The output is one HTML file with embedded CSS — open offline, no server.
Per-suite tables collapse closed when 100% pass and auto-expand when there
are failures, with the assertion failures shown inline and the session log
path linked.

### It reconciles against vett's own tally

Every number in the report is scraped out of human text, so the report can
drift away from the harness whenever the printer changes shape — silently,
because a regex that matches nothing raises nothing. It happened: the
run-header pattern required the `(run n/m)` suffix that `team-bench` only
prints for `--repeat N > 1` (`TeamBenchCommand.cs:231`), so any capture taken
at the **default** `--repeat 1` parsed to **zero** runs. The suite still
appeared, showing `0/0`, collapsed — the same visual state as a clean sweep —
under the headline "Overall pass rate 0.0%". The recipe above always passes
`--repeat 3`, which is why it went unnoticed.

The script now cross-checks its scraped `passed/total` against vett's own
`Summary: P/T runs passed` line. A mismatch, or a capture with no summary
line at all, prints `WARNING:` on stderr **and** puts a red
"NUMBERS BELOW ARE NOT TRUSTWORTHY" banner at the top of the HTML.

```bash
# Failure test for the scraper itself — embedded fixtures, no bench run.
python scripts/bench-report.py --self-test
```
