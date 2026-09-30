package llm_test

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"testing"

	"github.com/msdickinson/vett/sidecar/internal/llm"
)

// repoRoot returns the absolute path of the VETT directory, regardless of
// where `go test` is invoked from. Tests reach up from the test file.
func repoRoot(t *testing.T) string {
	t.Helper()
	_, thisFile, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	return filepath.Clean(filepath.Join(filepath.Dir(thisFile), "..", ".."))
}

// TestOpenHandsSchemasAreValidJSON — sanity: every embedded schema parses.
func TestOpenHandsSchemasAreValidJSON(t *testing.T) {
	schemas := llm.OpenHandsToolSchemas()
	if len(schemas) != 5 {
		t.Fatalf("expected 5 schemas, got %d", len(schemas))
	}
	wantNames := []string{"terminal", "file_editor", "task_tracker", "finish", "think"}
	for i, raw := range schemas {
		var obj map[string]any
		if err := json.Unmarshal(raw, &obj); err != nil {
			t.Fatalf("schema %d: invalid JSON: %v", i, err)
		}
		fn, ok := obj["function"].(map[string]any)
		if !ok {
			t.Fatalf("schema %d: missing function object", i)
		}
		if name, _ := fn["name"].(string); name != wantNames[i] {
			t.Errorf("schema %d: expected name %q, got %q", i, wantNames[i], name)
		}
	}
}

// TestEmbeddedSchemasMatchTestdata — embedded copies must equal the
// testdata/golden-schemas source files byte-for-byte. If they drift, the
// capture was updated in one place and not the other.
func TestEmbeddedSchemasMatchTestdata(t *testing.T) {
	root := repoRoot(t)
	names := []string{"terminal", "file_editor", "task_tracker", "finish", "think"}
	schemas := llm.OpenHandsToolSchemas()
	for i, name := range names {
		path := filepath.Join(root, "testdata", "golden-schemas", name+".json")
		want, err := os.ReadFile(path)
		if err != nil {
			t.Fatalf("read %s: %v", path, err)
		}
		if !bytes.Equal(bytes.TrimRight(want, "\n"), bytes.TrimRight(schemas[i], "\n")) {
			t.Errorf("embedded schema %s differs from %s", name, path)
		}
	}
}

// TestTurn1RequestMatchesRef — the load-bearing mirror test. Build a request
// with the canonical openhands inputs and compare against the captured
// reference wire body. Comparison is structural: we parse both sides, walk
// the tree, and verify deep equality AND object-key ordering.
func TestTurn1RequestMatchesRef(t *testing.T) {
	root := repoRoot(t)

	sysPromptBytes, err := os.ReadFile(filepath.Join(root, "testdata", "ref-system-prompt.txt"))
	if err != nil {
		t.Fatalf("read system prompt: %v", err)
	}
	userMsg := "Hello, can you list the files in the current directory?"

	// Canonical values per openhands-reference-spec.md §2 (metadata.json is
	// the source of truth). The raw capture file has temperature=0.6 and
	// model=openai/qwen3-coder-next because it was taken before the temp
	// was fixed; we compare against the normalized wire golden instead.
	req := llm.BuildRequest(
		"qwen3-coder-next",
		string(sysPromptBytes),
		userMsg,
		llm.OpenHandsToolSchemas(),
		1.0,  // temperature
		0.95, // top_p
	)

	actualBytes, err := llm.MarshalRequest(req)
	if err != nil {
		t.Fatalf("marshal request: %v", err)
	}

	goldenPath := filepath.Join(root, "testdata", "ref-requests", "turn-1-initial.wire.json")
	goldenBytes, err := os.ReadFile(goldenPath)
	if err != nil {
		t.Fatalf("read golden: %v", err)
	}

	actual, err := parseOrdered(actualBytes)
	if err != nil {
		t.Fatalf("parse actual: %v", err)
	}
	golden, err := parseOrdered(goldenBytes)
	if err != nil {
		t.Fatalf("parse golden: %v", err)
	}

	if diff := compareOrdered("", actual, golden); diff != "" {
		t.Fatalf("request differs from golden:\n%s", diff)
	}
}

// orderedValue is either a *orderedObject, []orderedValue, string, float64,
// bool, or nil. Lets us check both structure and key order.
type orderedValue any

type orderedObject struct {
	keys   []string
	values map[string]orderedValue
}

func parseOrdered(b []byte) (orderedValue, error) {
	dec := json.NewDecoder(bytes.NewReader(b))
	dec.UseNumber()
	tok, err := dec.Token()
	if err != nil {
		return nil, err
	}
	return parseOrderedToken(dec, tok)
}

func parseOrderedToken(dec *json.Decoder, tok json.Token) (orderedValue, error) {
	switch v := tok.(type) {
	case json.Delim:
		switch v {
		case '{':
			obj := &orderedObject{values: map[string]orderedValue{}}
			for dec.More() {
				keyTok, err := dec.Token()
				if err != nil {
					return nil, err
				}
				key := keyTok.(string)
				valTok, err := dec.Token()
				if err != nil {
					return nil, err
				}
				val, err := parseOrderedToken(dec, valTok)
				if err != nil {
					return nil, err
				}
				obj.keys = append(obj.keys, key)
				obj.values[key] = val
			}
			if _, err := dec.Token(); err != nil { // consume '}'
				return nil, err
			}
			return obj, nil
		case '[':
			arr := []orderedValue{}
			for dec.More() {
				elemTok, err := dec.Token()
				if err != nil {
					return nil, err
				}
				elem, err := parseOrderedToken(dec, elemTok)
				if err != nil {
					return nil, err
				}
				arr = append(arr, elem)
			}
			if _, err := dec.Token(); err != nil { // consume ']'
				return nil, err
			}
			return arr, nil
		}
	}
	// leaf: string, json.Number, bool, nil
	if n, ok := tok.(json.Number); ok {
		// Normalize numeric representation: use float64. This is how
		// openhands-sdk's Python json emits numbers too.
		f, err := n.Float64()
		if err != nil {
			return nil, err
		}
		return f, nil
	}
	return tok, nil
}

func compareOrdered(path string, a, b orderedValue) string {
	switch av := a.(type) {
	case *orderedObject:
		bv, ok := b.(*orderedObject)
		if !ok {
			return path + ": actual is object, golden is " + typeName(b)
		}
		if len(av.keys) != len(bv.keys) {
			return path + ": key count differs (actual=" + joinKeys(av.keys) + " golden=" + joinKeys(bv.keys) + ")"
		}
		for i := range av.keys {
			if av.keys[i] != bv.keys[i] {
				return path + ": key-order mismatch at index " + itoa(i) + " (actual=" + av.keys[i] + " golden=" + bv.keys[i] + ")"
			}
		}
		for _, k := range av.keys {
			if diff := compareOrdered(path+"."+k, av.values[k], bv.values[k]); diff != "" {
				return diff
			}
		}
		return ""
	case []orderedValue:
		bv, ok := b.([]orderedValue)
		if !ok {
			return path + ": actual is array, golden is " + typeName(b)
		}
		if len(av) != len(bv) {
			return path + ": array length differs (actual=" + itoa(len(av)) + " golden=" + itoa(len(bv)) + ")"
		}
		for i := range av {
			if diff := compareOrdered(path+"["+itoa(i)+"]", av[i], bv[i]); diff != "" {
				return diff
			}
		}
		return ""
	default:
		if !reflect.DeepEqual(a, b) {
			return path + ": leaf differs (actual=" + valRepr(a) + " golden=" + valRepr(b) + ")"
		}
		return ""
	}
}

func typeName(v orderedValue) string {
	switch v.(type) {
	case *orderedObject:
		return "object"
	case []orderedValue:
		return "array"
	case string:
		return "string"
	case float64:
		return "number"
	case bool:
		return "bool"
	case nil:
		return "null"
	}
	return "unknown"
}

func joinKeys(ks []string) string {
	out := "["
	for i, k := range ks {
		if i > 0 {
			out += ","
		}
		out += k
	}
	return out + "]"
}

func itoa(i int) string {
	return jsonNumberString(i)
}

func jsonNumberString(i int) string {
	if i == 0 {
		return "0"
	}
	neg := false
	if i < 0 {
		neg = true
		i = -i
	}
	var buf [20]byte
	pos := len(buf)
	for i > 0 {
		pos--
		buf[pos] = byte('0' + i%10)
		i /= 10
	}
	if neg {
		pos--
		buf[pos] = '-'
	}
	return string(buf[pos:])
}

func valRepr(v orderedValue) string {
	switch x := v.(type) {
	case string:
		if len(x) > 80 {
			return "\"" + x[:80] + "...\""
		}
		return "\"" + x + "\""
	}
	b, _ := json.Marshal(v)
	return string(b)
}
