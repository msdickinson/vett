package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/spf13/cobra"
)

// vett import-swebench — downloads a HuggingFace dataset once (via a
// Python shell-out) and writes it to a local JSONL file that Vett's
// native jsonl loader can read forever after. The Python dep is
// import-time only; runtime Vett stays pure Go.
//
// Per docs/export-and-storage.md and the user's preference: no parquet
// Go library, no runtime Python. A one-shot shell-out is the cheap
// path to "Vett can run SWE-bench Verified" without taking on a
// permanent dependency.
func importSwebenchCmd() *cobra.Command {
	var (
		dataset  string
		split    string
		outPath  string
		revision string
	)
	cmd := &cobra.Command{
		Use:   "import-swebench",
		Short: "Download a HuggingFace SWE-bench dataset and flatten to jsonl",
		Long: `import-swebench downloads a HuggingFace dataset (default
princeton-nlp/SWE-bench_Verified) once and writes it as newline-
delimited JSON that Vett's jsonl loader reads natively. After the
first run, Vett doesn't need Python for the dataset anymore — the
jsonl file is the source of truth.

Requires: python3 and the 'datasets' library available on PATH. If
'datasets' isn't installed, the command prints a pip install hint
and exits non-zero.

The default output path is ~/.vett/datasets/<dataset>-<split>.jsonl
so the canonical workspace suite (suites/swe-bench-verified.yaml)
can point at it via a loader.path relative to the user's home.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			if outPath == "" {
				home, err := os.UserHomeDir()
				if err != nil {
					return err
				}
				base := strings.ReplaceAll(dataset, "/", "__")
				outPath = filepath.Join(home, ".vett", "datasets", base+"-"+split+".jsonl")
			}
			if err := os.MkdirAll(filepath.Dir(outPath), 0o755); err != nil {
				return fmt.Errorf("create output dir: %w", err)
			}
			script := buildImportScript(dataset, split, outPath, revision)
			py := exec.Command(resolvePython(), "-c", script)
			py.Stdout = os.Stdout
			py.Stderr = os.Stderr
			if err := py.Run(); err != nil {
				return fmt.Errorf("python import failed (install 'datasets' in %q or system python3): %w",
					resolvePython(), err)
			}
			return nil
		},
	}
	cmd.Flags().StringVar(&dataset, "dataset", "princeton-nlp/SWE-bench_Verified", "HuggingFace dataset identifier")
	cmd.Flags().StringVar(&split, "split", "test", "Dataset split (train/test/validation)")
	cmd.Flags().StringVar(&outPath, "out", "", "Output jsonl path (default: ~/.vett/datasets/<dataset>-<split>.jsonl)")
	cmd.Flags().StringVar(&revision, "revision", "", "Dataset revision/commit (optional)")
	return cmd
}

// resolvePython picks the Python interpreter to run the import script
// with. Prefers ~/.vett/venv/bin/python (where `vett` users are
// encouraged to pip-install datasets in isolation) and falls back to
// system python3 on PATH. Returns the default "python3" if neither is
// usable so errors surface clearly.
func resolvePython() string {
	if home, err := os.UserHomeDir(); err == nil {
		venvPython := filepath.Join(home, ".vett", "venv", "bin", "python")
		if _, err := os.Stat(venvPython); err == nil {
			return venvPython
		}
		// Windows venv layout differs.
		venvPythonWin := filepath.Join(home, ".vett", "venv", "Scripts", "python.exe")
		if _, err := os.Stat(venvPythonWin); err == nil {
			return venvPythonWin
		}
	}
	return "python3"
}

// buildImportScript returns the Python one-liner that loads the dataset
// and writes each row as a JSON line. Kept as a separate function so
// tests can verify its shape without actually running Python.
func buildImportScript(dataset, split, outPath, revision string) string {
	revArg := ""
	if revision != "" {
		revArg = fmt.Sprintf(", revision=%q", revision)
	}
	return fmt.Sprintf(`
import json, sys
try:
    from datasets import load_dataset
except ImportError:
    sys.stderr.write("vett import-swebench requires the 'datasets' Python library.\nInstall with: pip install datasets\n")
    sys.exit(2)
ds = load_dataset(%q, split=%q%s)
count = 0
with open(%q, "w", encoding="utf-8", newline="\n") as f:
    for row in ds:
        # HuggingFace datasets sometimes hold numpy types; json.dumps
        # needs plain Python. default=str catches the leftovers.
        f.write(json.dumps(row, ensure_ascii=False, default=str) + "\n")
        count += 1
sys.stdout.write(f"vett import-swebench: wrote {count} instances to %s\n")
`, dataset, split, revArg, outPath, outPath)
}
