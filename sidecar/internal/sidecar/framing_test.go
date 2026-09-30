package sidecar

import (
	"strings"
	"testing"
)

// TestBashPassthroughIsBitExact — the load-bearing invariant for the
// sidecar's bash passthrough. Every user command must land inside the
// brace-group wrapper byte-for-byte unchanged, on its own line(s).
//
// 2026-04-25: framing changed from "{ <cmd>; } 2>&1" to a multi-line
// "{\n<cmd>\n} 2>&1" so that user commands ending with a heredoc
// terminator (e.g. `python3 << 'EOF' ... EOF`) work. The old form
// produced "EOF; }" which is not a heredoc terminator — bash hung
// looking for EOF and exited with code 2 on parse error, killing the
// session multiple times per SWE-bench instance.
func TestBashPassthroughIsBitExact(t *testing.T) {
	tricky := []string{
		`ls`,
		`pytest -v`,
		`echo 'single quotes'`,
		`echo "double quotes"`,
		`echo $HOME`,
		`echo \\backslash`,
		`echo $(pwd)`,
		"echo \\`backtick\\`",
		`python -c 'print("hello")'`,
		`grep -rn 'def foo(' .`,
		// embedded newline — multi-line command
		"for i in 1 2 3; do\n  echo $i\ndone",
		// heredoc — the case that broke the old framing
		"python3 << 'EOF'\nprint('hi')\nEOF",
		// empty string
		``,
		// weird unicode
		`echo "héllo → wörld"`,
	}
	for _, cmd := range tricky {
		script, marker := buildBashScript(cmd)
		// Invariant 1: marker appears exactly once in the script, in the
		// printf at the end.
		if strings.Count(script, marker) != 1 {
			t.Errorf("marker %q should appear exactly once in script, got %d\nscript:\n%s",
				marker, strings.Count(script, marker), script)
		}
		// Invariant 2: the user command appears verbatim in the script,
		// between "{\n" and "\n} 2>&1\n".
		openTok := "{\n"
		closeTok := "\n} 2>&1\n"
		openIdx := strings.Index(script, openTok)
		closeIdx := strings.Index(script, closeTok)
		if openIdx < 0 || closeIdx < 0 || openIdx >= closeIdx {
			t.Fatalf("could not locate {\\n ... \\n} 2>&1 wrapper\nscript:\n%s", script)
		}
		extracted := script[openIdx+len(openTok) : closeIdx]
		if extracted != cmd {
			t.Errorf("command round-trip failed:\n  sent: %q\n  seen in script: %q", cmd, extracted)
		}
		// Invariant 3: the printf sentinel line follows the grouping line.
		wrapperEnd := closeIdx + len(closeTok)
		printfLine := script[wrapperEnd:]
		if !strings.HasPrefix(printfLine, "printf '\\n"+marker+" exit=%s cwd=%s\\n' \"$?\" \"$PWD\"\n") {
			t.Errorf("printf sentinel line malformed:\n  got: %q", printfLine)
		}
	}
}

// TestMarkerUniqueness ensures two back-to-back calls produce different
// markers (guards against a future refactor that caches a single marker).
func TestMarkerUniqueness(t *testing.T) {
	_, m1 := buildBashScript("echo a")
	_, m2 := buildBashScript("echo b")
	if m1 == m2 {
		t.Errorf("expected distinct markers, got %q both times", m1)
	}
}

// TestParseMarkerLine verifies the round-trip between the printf sentinel
// and the parsed (exit, cwd) tuple.
func TestParseMarkerLine(t *testing.T) {
	marker := "__VETT_END_cafebabe__"
	line := marker + " exit=0 cwd=/testbed"
	ec, cwd := parseMarkerLine(line, marker)
	if ec != 0 || cwd != "/testbed" {
		t.Errorf("expected (0,/testbed), got (%d,%q)", ec, cwd)
	}
	// Non-zero exit, nested path with spaces? Openhands cwds don't
	// usually have spaces, but let's be robust to at least a plain path.
	line2 := marker + " exit=7 cwd=/home/user/proj"
	ec2, cwd2 := parseMarkerLine(line2, marker)
	if ec2 != 7 || cwd2 != "/home/user/proj" {
		t.Errorf("expected (7,/home/user/proj), got (%d,%q)", ec2, cwd2)
	}
}
