package main

import (
	"strings"
	"testing"
)

func TestImportScriptShape(t *testing.T) {
	script := buildImportScript("princeton-nlp/SWE-bench_Verified", "test", "/tmp/out.jsonl", "")
	for _, want := range []string{
		"from datasets import load_dataset",
		`load_dataset("princeton-nlp/SWE-bench_Verified", split="test")`,
		`open("/tmp/out.jsonl"`,
		"json.dumps",
		"pip install datasets",
	} {
		if !strings.Contains(script, want) {
			t.Errorf("script missing expected fragment: %q\n---\n%s", want, script)
		}
	}
}

func TestImportScriptRevision(t *testing.T) {
	script := buildImportScript("foo/bar", "test", "/tmp/out.jsonl", "abc123")
	if !strings.Contains(script, `revision="abc123"`) {
		t.Errorf("script missing revision arg: %s", script)
	}
}
