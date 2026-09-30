# Vett Suite YAML Schema

A suite is a **dataset + a scorer**. It says "here are N tickets to solve" and "here's how to know if a patch solves one of them." Suites live in `suites/*.yaml` and are loaded by `vett run --suite <name>`.

This document is the canonical schema.

## Top-level structure

```yaml
name: swe-bench-verified     # required
description: ...              # optional

loader:                       # required
  type: huggingface
  dataset: princeton-nlp/SWE-bench_Verified
  split: test

instance_filter:              # optional, default: include all
  repos: []                   # empty means all
  exclude_ids: []

rendering:                    # required
  working_dir: /testbed
  base_commit_field: base_commit
  problem_statement_field: problem_statement

sandbox:                      # required
  image_template: swebench/sweb.eval.x86_64.{repo}_1776_{instance}

scorer:                       # required
  type: swe_bench_harness
  config:
    max_workers: 4
    cache_level: instance
    report_dir: ./results/{run_id}/scoring
```

## Required top-level fields

| Field | Type | Description |
|---|---|---|
| `name` | string | Suite identifier. Must be unique. |
| `loader` | object | How to load instances (see "Loaders" below). |
| `rendering` | object | How suite data gets mapped into prompt template variables. |
| `sandbox` | object | How suite data maps to container images. |
| `scorer` | object | How patches are scored (see "Scorers" below). |

## Loaders

### `huggingface`
Loads instances from a HuggingFace dataset.

```yaml
loader:
  type: huggingface
  dataset: princeton-nlp/SWE-bench_Verified
  split: test
  cache_dir: ~/.cache/vett/datasets    # optional
  revision: main                        # optional
```

| Field | Type | Required | Description |
|---|---|---|---|
| `dataset` | string | yes | HuggingFace dataset identifier. |
| `split` | string | no, default `test` | Dataset split. |
| `cache_dir` | string | no | Where to cache downloaded data. Default `~/.cache/vett/datasets`. |
| `revision` | string | no | Dataset revision/commit. Default main. |

### `jsonl`
Loads instances from a local JSONL file.

```yaml
loader:
  type: jsonl
  path: ./testdata/fixtures/canned-instances.jsonl
```

Each line is one instance as a JSON object. Used for tests and custom suites.

### `directory`
Loads instances from a directory of JSON files.

```yaml
loader:
  type: directory
  path: ./suites/my-custom/instances/
```

One file per instance. Useful when instances have large content that would be cumbersome in a single JSONL.

## Instance filter

```yaml
instance_filter:
  repos:
    - astropy
    - django
  exclude_ids:
    - astropy__astropy-12345
  include_ids:
    - astropy__astropy-12907
```

| Field | Type | Default | Description |
|---|---|---|---|
| `repos` | []string | `[]` (all) | Only include instances whose `repo` field is in this list. |
| `exclude_ids` | []string | `[]` | Remove these instance IDs from the run. |
| `include_ids` | []string | `[]` | If non-empty, ONLY run these IDs (overrides repos filter). |

The filter applies AFTER loading but BEFORE any `--instances` flag on the CLI.

## Rendering

How suite instance fields get injected into the profile's user template.

```yaml
rendering:
  working_dir: /testbed
  fields:
    problem_statement: problem_statement
    base_commit: base_commit
    repo: repo
    test_patch: test_patch
```

The `fields` map tells the renderer which instance fields to expose to the user template as `{field_name}` variables. The profile's `user_template` references them:

```
I have access to a repository at {working_dir} with base commit {base_commit}...
```

Values that appear in the template but aren't in the rendering map cause a validation error at run start.

## Sandbox

How suite instances map to Docker image names.

```yaml
sandbox:
  image_template: swebench/sweb.eval.x86_64.{repo}_1776_{instance}
  entrypoint: /bin/bash
  default_cwd: /testbed
```

| Field | Type | Required | Description |
|---|---|---|---|
| `image_template` | string | yes | Template with `{repo}` and `{instance}` placeholders. |
| `entrypoint` | string | no, default `/bin/bash` | Container entrypoint override. |
| `default_cwd` | string | no, default `/testbed` | Initial working directory for the default session. |

The image template uses the instance's `repo` and `instance_id` fields to construct the full image name. For `astropy__astropy-12907`, `repo=astropy`, `instance=astropy__astropy-12907`, producing `swebench/sweb.eval.x86_64.astropy_1776_astropy__astropy-12907`.

## Scorers

### `swe_bench_harness`
Uses the official SWE-bench test harness to score patches.

```yaml
scorer:
  type: swe_bench_harness
  config:
    dataset: princeton-nlp/SWE-bench_Verified
    max_workers: 4
    cache_level: instance
    timeout_seconds: 1800
```

| Field | Type | Required | Description |
|---|---|---|---|
| `dataset` | string | yes | Dataset name for the harness to look up test specs. |
| `max_workers` | int | no, default 4 | Parallel scoring workers. |
| `cache_level` | string | no, default `instance` | One of `base`, `env`, `instance`. |
| `timeout_seconds` | int | no, default 1800 | Per-instance scoring timeout. |

The scorer assembles a predictions file from the run's patches and runs `python -m swebench.harness.run_evaluation`. Requires Python + swebench package installed somewhere reachable. Vett's `vett verify` command also uses this scorer.

### `pass_fail`
Minimal scorer: a patch "passes" if it's non-empty, "fails" otherwise. Used for suites that don't have a test harness yet.

```yaml
scorer:
  type: pass_fail
```

No config required. Mostly useful for development.

### `script`
Runs an external script to score each patch. The script receives instance metadata and the patch on stdin and must print a JSON result.

```yaml
scorer:
  type: script
  config:
    command: ./my-scorer.sh
    timeout_seconds: 600
```

Interface:
- stdin: JSON with `{instance, patch}`
- stdout: JSON with `{resolved: bool, details: ...}`
- exit code 0 = scoring succeeded (regardless of pass/fail); non-zero = scoring failed

## Validation at load time

When Vett loads a suite, it validates:

1. `name` is unique in the search path
2. `loader.type` is a known loader type
3. `rendering.fields` map is non-empty and all values reference real instance fields
4. `sandbox.image_template` contains `{instance}` (required) and optionally `{repo}`
5. `scorer.type` is a known scorer type
6. Loader-specific and scorer-specific config is valid

## Search path

Suites are loaded from:

1. `./suites/` (relative to cwd)
2. `~/.vett/suites/` (or `$VETT_CONFIG_DIR/suites/`)
3. Embedded suites (baked into the `vett` binary)

Also: `vett run --suite ./my-suite.yaml` to use an explicit path.

## Example: the canonical swe-bench-verified suite

```yaml
name: swe-bench-verified
description: |
  SWE-bench Verified: 500 manually-validated GitHub issues from popular
  Python repositories. Each instance has a base commit, a failing test
  set, and a reference patch.

loader:
  type: huggingface
  dataset: princeton-nlp/SWE-bench_Verified
  split: test

rendering:
  working_dir: /testbed
  fields:
    problem_statement: problem_statement
    base_commit: base_commit
    repo: repo

sandbox:
  image_template: swebench/sweb.eval.x86_64.{repo}_1776_{instance}
  default_cwd: /testbed

scorer:
  type: swe_bench_harness
  config:
    dataset: princeton-nlp/SWE-bench_Verified
    max_workers: 4
    cache_level: instance
    timeout_seconds: 1800
```

## Example: a test-only fixture suite

```yaml
name: test-canned
description: Fixture suite for unit/integration tests. Three canned instances.

loader:
  type: jsonl
  path: ./testdata/fixtures/canned-instances.jsonl

rendering:
  working_dir: /workspace
  fields:
    problem_statement: problem_statement

sandbox:
  image_template: vett-test:{instance}

scorer:
  type: pass_fail
```
