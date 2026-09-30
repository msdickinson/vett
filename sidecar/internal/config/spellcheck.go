package config

import (
	"fmt"
	"strings"
)

// Known valid top-level keys for profiles and suites.
var validProfileKeys = map[string]bool{
	"name": true, "description": true, "self_reported_confidence": true,
	"sandbox": true, "llm": true, "system_prompt": true,
	"system_prompt_file": true, "user_template": true,
	"user_template_file": true, "tools": true, "middleware": true,
	"max_iterations": true, "timeout_minutes": true, "team": true,
}

var validSandboxKeys = map[string]bool{
	"type": true, "run_as_root": true, "home": true,
	"image_prefix": true, "default_cwd": true,
	"workdir_backing": true, "tmpfs_mounts": true,
}

var validLLMKeys = map[string]bool{
	"model": true, "endpoint": true, "temperature": true,
	"top_p": true, "max_tokens": true, "seed": true,
	"parallel_tool_calls": true, "request_timeout_seconds": true,
	"num_retries": true,
}

// CheckMisspellings scans a map of YAML keys and returns warnings for
// any that look like misspellings of known keys.
func CheckMisspellings(keys []string, validKeys map[string]bool) []string {
	var warnings []string
	for _, key := range keys {
		if validKeys[key] {
			continue
		}
		// Find closest match.
		best := ""
		bestDist := 999
		for valid := range validKeys {
			d := levenshtein(key, valid)
			if d < bestDist {
				bestDist = d
				best = valid
			}
		}
		if bestDist <= 2 && best != "" {
			warnings = append(warnings,
				fmt.Sprintf("unknown key %q — did you mean %q?", key, best))
		}
	}
	return warnings
}

// levenshtein computes the edit distance between two strings.
func levenshtein(a, b string) int {
	a = strings.ToLower(a)
	b = strings.ToLower(b)

	if len(a) == 0 {
		return len(b)
	}
	if len(b) == 0 {
		return len(a)
	}

	prev := make([]int, len(b)+1)
	curr := make([]int, len(b)+1)

	for j := range prev {
		prev[j] = j
	}

	for i := 1; i <= len(a); i++ {
		curr[0] = i
		for j := 1; j <= len(b); j++ {
			cost := 1
			if a[i-1] == b[j-1] {
				cost = 0
			}
			curr[j] = min3(
				prev[j]+1,
				curr[j-1]+1,
				prev[j-1]+cost,
			)
		}
		prev, curr = curr, prev
	}
	return prev[len(b)]
}

func min3(a, b, c int) int {
	if a < b {
		if a < c {
			return a
		}
		return c
	}
	if b < c {
		return b
	}
	return c
}
