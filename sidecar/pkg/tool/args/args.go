// Package args provides typed accessors for tool arguments. LLM tool
// calls arrive as JSON objects decoded into map[string]any; numbers are
// float64, arrays are []any, etc. These helpers return the requested type
// with a default fallback.
package args

// String returns args[key] as a string, or def if missing/wrong type.
func String(args map[string]any, key, def string) string {
	if v, ok := args[key]; ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return def
}

// Bool returns args[key] as a bool, or def if missing/wrong type.
func Bool(args map[string]any, key string, def bool) bool {
	if v, ok := args[key]; ok {
		if b, ok := v.(bool); ok {
			return b
		}
	}
	return def
}

// Int returns args[key] as an int, or def if missing/wrong type.
// JSON numbers decode as float64, so we coerce.
func Int(args map[string]any, key string, def int) int {
	if v, ok := args[key]; ok {
		switch n := v.(type) {
		case float64:
			return int(n)
		case int:
			return n
		case int64:
			return int(n)
		}
	}
	return def
}

// IntArray returns args[key] as a []int, or def if missing/wrong type.
func IntArray(args map[string]any, key string, def []int) []int {
	if v, ok := args[key]; ok {
		if arr, ok := v.([]any); ok {
			out := make([]int, 0, len(arr))
			for _, elem := range arr {
				if f, ok := elem.(float64); ok {
					out = append(out, int(f))
				}
			}
			return out
		}
	}
	return def
}
