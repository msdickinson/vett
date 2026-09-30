# Minimal Vett smoke-test fixture image.
#
# Goal: a Docker image the Vett end-to-end pipeline can run against to
# prove sandbox + sidecar + agent loop + tool dispatch + patch
# extraction all work together. Trivially solvable by any small
# code-capable LLM — the agent reads mathlib.py, sees that `add`
# returns a - b instead of a + b, fixes it with file_editor, and
# calls finish.
#
# Build: docker build -t vett-test:add-001 testdata/fixtures/add-001/
FROM python:3.11-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir pytest

# /testbed is the default_cwd in profiles/openhands.yaml.
WORKDIR /testbed

COPY mathlib.py test_mathlib.py .gitignore /testbed/

RUN git init -b main \
 && git config user.email "test@vett.local" \
 && git config user.name  "vett-fixture" \
 && git add -A \
 && git commit -m "initial buggy version"
