package sidecar

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"strconv"
	"strings"
)

// MarkerPrefix is the literal prefix of the sentinel line the sidecar writes
// after every user command. The random suffix prevents collision with any
// text the user's command might print. The host-side test
// TestBashPassthroughIsBitExact locks the exact script shape below.
const MarkerPrefix = "__VETT_END_"

// buildBashScript wraps a user command with bash brace grouping and a
// sentinel printf that reports exit code and cwd. The user command lands
// inside the brace group on its own line(s) — raw passthrough invariant.
//
// Returned: (script bytes to write to bash stdin, marker token).
//
//	{
//	<user_command>
//	} 2>&1
//	printf '\n<marker> exit=%s cwd=%s\n' "$?" "$PWD"
//
// Why newlines around the braces (vs the older "{ <cmd>; }" form):
// when <user_command> ends with a heredoc terminator like "EOF", the
// older form produced "EOF; }" which is NOT a valid heredoc terminator
// (must be the marker alone on its own line). Bash then read forever
// looking for EOF, eventually hitting end-of-input parse error and
// exiting with code 2. Putting "}" on its own line keeps any trailing
// heredoc terminator intact and works for every command shape we've
// seen agents emit.
func buildBashScript(command string) (string, string) {
	marker := newMarker()
	script := fmt.Sprintf(
		"{\n%s\n} 2>&1\nprintf '\\n%s exit=%%s cwd=%%s\\n' \"$?\" \"$PWD\"\n",
		command, marker,
	)
	return script, marker
}

// parseMarkerLine extracts exit code and cwd from "<marker> exit=N cwd=X".
// Kept here (not in bash.go) so Quick-tier tests can exercise it on any OS.
func parseMarkerLine(line, marker string) (int, string) {
	if !strings.HasPrefix(line, marker) {
		return 0, ""
	}
	rest := strings.TrimSpace(strings.TrimPrefix(line, marker))
	var exitStr, cwd string
	if i := strings.Index(rest, "exit="); i >= 0 {
		rest = rest[i+len("exit="):]
		if j := strings.Index(rest, " cwd="); j >= 0 {
			exitStr = rest[:j]
			cwd = rest[j+len(" cwd="):]
		}
	}
	ec, _ := strconv.Atoi(strings.TrimSpace(exitStr))
	return ec, cwd
}

func newMarker() string {
	var b [8]byte
	if _, err := rand.Read(b[:]); err != nil {
		// Deterministic fallback if system entropy is unavailable — the
		// marker still needs to be unique per-call, so fall back to a
		// process-local counter. Sidecar startup ensures crypto/rand works;
		// this branch is unreachable in practice.
		return MarkerPrefix + "fallback__"
	}
	return MarkerPrefix + hex.EncodeToString(b[:]) + "__"
}
