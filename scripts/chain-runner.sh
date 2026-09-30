#!/usr/bin/env bash
# Chain runner for the team-chain-calculator suite.
#
# Runs steps 1..10 sequentially against the SAME persistent workspace.
# Between steps, the runner writes the next Step<N>_Tests.cs file
# (since `--workspace-dir` skips seed_files).
#
# Records per-step pass/fail + cumulative metrics. Output a markdown
# report at <OUT_DIR>/chain-summary.md.
#
# Usage:
#   bash scripts/chain-runner.sh [OUT_DIR]
# If OUT_DIR is omitted, defaults to a timestamped dir under
# ./results/chain-runs/.

set -uo pipefail

OUT_DIR="${1:-./results/chain-runs/chain-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT_DIR"
LOG="$OUT_DIR/_chain.log"
ENDPOINT=http://old-gpu-a:8000/v1

# CRITICAL: workspace MUST be outside any existing git repo so vett doesn't
# walk up the directory tree and adopt the parent repo's git boundary.
# Previously this lived inside a large monorepo and the implementer
# saw the entire ~1000-file repo as its workspace — failures looked like
# capability problems but were navigation problems.
WS="${TMPDIR:-/tmp}/vett-chain-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$WS"
cd "$WS"
git init -q .
git config user.email "vett-chain@local"
git config user.name "vett-chain"
cd - >/dev/null

echo "chain start $(date -u +%H:%M:%SZ) workspace=$WS (isolated git repo, outside the host repo)" | tee "$LOG"

# ---- seed the workspace with step 1's scaffold + Step1 tests ----
cat > "$WS/Calculator.csproj" <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>Calc</RootNamespace>
    <AssemblyName>Calc</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup><Using Include="Xunit" /></ItemGroup>
</Project>
PROJ

write_test () {
  local STEP=$1
  cat > "$WS/Step${STEP}_Tests.cs"
}

# ---- Step 1 test ----
write_test 1 <<'T1'
namespace Calc;
public class Step1_Tests
{
    [Fact] public void Add_Two_Positives() => Assert.Equal(5, new Calculator().Add(2, 3));
    [Fact] public void Add_With_Zero()    => Assert.Equal(7, new Calculator().Add(7, 0));
    [Fact] public void Add_With_Negative() => Assert.Equal(-1, new Calculator().Add(2, -3));
}
T1

# ---- Step 2 test (written AFTER step 1 passes) ----
gen_step2_test () {
  write_test 2 <<'T2'
namespace Calc;
public class Step2_Tests
{
    [Fact] public void Subtract_Two_Positives() => Assert.Equal(-1, new Calculator().Subtract(2, 3));
    [Fact] public void Subtract_With_Zero()     => Assert.Equal(7, new Calculator().Subtract(7, 0));
    [Fact] public void Subtract_With_Negative() => Assert.Equal(5, new Calculator().Subtract(2, -3));
}
T2
}

gen_step3_test () {
  write_test 3 <<'T3'
namespace Calc;
public class Step3_Tests
{
    [Fact] public void Multiply_Two_Positives() => Assert.Equal(6, new Calculator().Multiply(2, 3));
    [Fact] public void Multiply_With_Zero()     => Assert.Equal(0, new Calculator().Multiply(7, 0));
    [Fact] public void Multiply_With_Negative() => Assert.Equal(-6, new Calculator().Multiply(2, -3));
}
T3
}

gen_step4_test () {
  write_test 4 <<'T4'
namespace Calc;
public class Step4_Tests
{
    [Fact] public void Divide_Two_Positives()  => Assert.Equal(2, new Calculator().Divide(6, 3));
    [Fact] public void Divide_With_Negative()  => Assert.Equal(-3, new Calculator().Divide(-9, 3));
    [Fact] public void Divide_Truncates()      => Assert.Equal(1, new Calculator().Divide(7, 4));
    [Fact] public void Divide_By_Zero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => new Calculator().Divide(5, 0));
}
T4
}

gen_step5_test () {
  write_test 5 <<'T5'
namespace Calc;
public class Step5_Tests
{
    [Fact] public void Power_BaseZero()     => Assert.Equal(1, new Calculator().Power(2, 0));
    [Fact] public void Power_BasePositive() => Assert.Equal(8, new Calculator().Power(2, 3));
    [Fact] public void Power_BaseOne()      => Assert.Equal(1, new Calculator().Power(1, 100));
    [Fact] public void Power_BaseLarge()    => Assert.Equal(1024, new Calculator().Power(2, 10));
}
T5
}

gen_step6_test () {
  write_test 6 <<'T6'
namespace Calc;
public class Step6_Tests
{
    [Fact] public void LastResult_DefaultsZero() => Assert.Equal(0, new Calculator().LastResult);
    [Fact] public void LastResult_AfterAdd()
    {
        var c = new Calculator();
        c.Add(2, 3);
        Assert.Equal(5, c.LastResult);
    }
    [Fact] public void LastResult_AfterFailedDivide_Unchanged()
    {
        var c = new Calculator();
        c.Add(1, 2);
        try { c.Divide(5, 0); } catch (DivideByZeroException) { }
        Assert.Equal(3, c.LastResult);
    }
    [Fact] public void LastResult_AfterPower()
    {
        var c = new Calculator();
        c.Power(2, 5);
        Assert.Equal(32, c.LastResult);
    }
}
T6
}

gen_step7_test () {
  write_test 7 <<'T7'
namespace Calc;
public class Step7_Tests
{
    [Fact] public void Clear_ResetsLastResult()
    {
        var c = new Calculator();
        c.Add(2, 3);
        c.Clear();
        Assert.Equal(0, c.LastResult);
    }
    [Fact] public void Clear_OnFresh_NoOp()
    {
        var c = new Calculator();
        c.Clear();
        Assert.Equal(0, c.LastResult);
    }
    [Fact] public void After_Clear_NewOpUpdatesLastResult()
    {
        var c = new Calculator();
        c.Add(1, 1);
        c.Clear();
        c.Multiply(3, 4);
        Assert.Equal(12, c.LastResult);
    }
}
T7
}

gen_step8_test () {
  write_test 8 <<'T8'
namespace Calc;
public class Step8_Tests
{
    [Fact] public void History_DefaultsEmpty() => Assert.Empty(new Calculator().History);
    [Fact] public void History_AfterAdd_HasOne()
    {
        var c = new Calculator();
        c.Add(2, 3);
        Assert.Single(c.History);
        Assert.Equal("Add(2,3)=5", c.History[0]);
    }
    [Fact] public void History_FailedDivide_NotRecorded()
    {
        var c = new Calculator();
        c.Add(1, 1);
        try { c.Divide(2, 0); } catch (DivideByZeroException) { }
        Assert.Single(c.History);
    }
    [Fact] public void History_ClearedByClear()
    {
        var c = new Calculator();
        c.Add(1, 2);
        c.Multiply(3, 4);
        c.Clear();
        Assert.Empty(c.History);
    }
}
T8
}

gen_step9_test () {
  write_test 9 <<'T9'
namespace Calc;
public class Step9_Tests
{
    [Fact] public void UndoLast_OnEmpty_NoOp()
    {
        var c = new Calculator();
        c.UndoLast();
        Assert.Empty(c.History);
        Assert.Equal(0, c.LastResult);
    }
    [Fact] public void UndoLast_OneOp_BackToEmpty()
    {
        var c = new Calculator();
        c.Add(2, 3);
        c.UndoLast();
        Assert.Empty(c.History);
        Assert.Equal(0, c.LastResult);
    }
    [Fact] public void UndoLast_TwoOps_LastResultIsPrior()
    {
        var c = new Calculator();
        c.Add(2, 3);       // history: Add(2,3)=5; LastResult=5
        c.Multiply(4, 5);  // history: ..., Multiply(4,5)=20; LastResult=20
        c.UndoLast();
        Assert.Single(c.History);
        Assert.Equal(5, c.LastResult);
    }
    [Fact] public void UndoLast_AfterFailedDivide_StillUndoesLastSuccess()
    {
        var c = new Calculator();
        c.Add(1, 1);
        c.Multiply(2, 3);
        try { c.Divide(7, 0); } catch (DivideByZeroException) { }
        c.UndoLast();
        Assert.Single(c.History);
        Assert.Equal(2, c.LastResult);
    }
}
T9
}

# Step 10 has no new tests — it's the refactor regression test.

# ---- run one chain step ----
run_step () {
  local N=$1
  local INSTANCE_ID=$2
  local STDERR="$OUT_DIR/step-${N}.stderr.log"
  local JSON="$OUT_DIR/step-${N}.json"
  echo "[step-$N] start $(date -u +%H:%M:%SZ) instance=$INSTANCE_ID" | tee -a "$LOG"
  vett team-bench team-chain-calculator -i "$INSTANCE_ID" \
    --endpoint "$ENDPOINT" --keep-all-worktrees --json \
    --workspace-dir "$WS" \
    > "$JSON" 2> "$STDERR"
  local RC=$?
  local SUMMARY=$(grep "^Summary:" "$STDERR" | tail -1)
  local LINE=$(grep -E "${INSTANCE_ID}.*\.\.\." "$STDERR" | grep -v "(run " | tail -1)
  echo "[step-$N] DONE rc=$RC $SUMMARY" | tee -a "$LOG"
  echo "[step-$N] $LINE" | tee -a "$LOG"
  # parse PASS/FAIL out of the line
  if echo "$LINE" | grep -q "PASS"; then
    echo "$N PASS" >> "$OUT_DIR/_results.txt"
    return 0
  else
    echo "$N FAIL" >> "$OUT_DIR/_results.txt"
    return 1
  fi
}

# ---- run the chain ----
run_step 1 step-1-add || { echo "[chain] step 1 failed, aborting" | tee -a "$LOG"; exit 1; }
gen_step2_test
run_step 2 step-2-subtract || echo "[chain] step 2 failed, continuing" | tee -a "$LOG"
gen_step3_test
run_step 3 step-3-multiply || echo "[chain] step 3 failed, continuing" | tee -a "$LOG"
gen_step4_test
run_step 4 step-4-divide || echo "[chain] step 4 failed, continuing" | tee -a "$LOG"
gen_step5_test
run_step 5 step-5-power || echo "[chain] step 5 failed, continuing" | tee -a "$LOG"
gen_step6_test
run_step 6 step-6-memory || echo "[chain] step 6 failed, continuing" | tee -a "$LOG"
gen_step7_test
run_step 7 step-7-clear || echo "[chain] step 7 failed, continuing" | tee -a "$LOG"
gen_step8_test
run_step 8 step-8-history || echo "[chain] step 8 failed, continuing" | tee -a "$LOG"
gen_step9_test
run_step 9 step-9-undo || echo "[chain] step 9 failed, continuing" | tee -a "$LOG"
# Step 10 — no new tests, just regression
run_step 10 step-10-refactor || echo "[chain] step 10 failed, continuing" | tee -a "$LOG"

# ---- summary ----
PASS_COUNT=$(grep -c "PASS" "$OUT_DIR/_results.txt" 2>/dev/null || echo 0)
TOTAL_COUNT=$(wc -l < "$OUT_DIR/_results.txt" 2>/dev/null || echo 0)
FIRST_FAIL=$(grep "FAIL" "$OUT_DIR/_results.txt" 2>/dev/null | head -1 | awk '{print $1}')

cat > "$OUT_DIR/chain-summary.md" <<MD
# Chain run — $(date -u +%Y-%m-%dT%H:%MZ)

Workspace: \`$WS\`

## Per-step results

\`\`\`
$(cat "$OUT_DIR/_results.txt" 2>/dev/null)
\`\`\`

## Summary

- Steps PASS: $PASS_COUNT / $TOTAL_COUNT
- First fail: ${FIRST_FAIL:-none}
- Full chain completion: $([ "$PASS_COUNT" = "10" ] && echo "YES" || echo "NO")
MD

echo "" | tee -a "$LOG"
echo "chain done $(date -u +%H:%M:%SZ) — $PASS_COUNT/$TOTAL_COUNT steps passed; first fail: ${FIRST_FAIL:-none}" | tee -a "$LOG"
