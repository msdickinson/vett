package runner

import (
	"archive/zip"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

// SWEBenchPrediction is one entry in the SWE-bench submission format.
type SWEBenchPrediction struct {
	InstanceID  string `json:"instance_id"`
	ModelPatch  string `json:"model_patch"`
	ModelName   string `json:"model_name_or_path"`
}

// ExportSWEBenchZip creates a zip file in the SWE-bench evaluation
// format: predictions.jsonl + metadata. This zip can be submitted
// directly to the SWE-bench evaluation harness.
func ExportSWEBenchZip(summary *Summary, outputPath string) error {
	if err := os.MkdirAll(filepath.Dir(outputPath), 0o755); err != nil {
		return fmt.Errorf("create output dir: %w", err)
	}

	f, err := os.Create(outputPath)
	if err != nil {
		return fmt.Errorf("create zip: %w", err)
	}
	defer f.Close()

	w := zip.NewWriter(f)
	defer w.Close()

	// Write predictions.jsonl — one line per instance.
	predWriter, err := w.Create("predictions.jsonl")
	if err != nil {
		return err
	}

	for _, inst := range summary.Instances {
		pred := SWEBenchPrediction{
			InstanceID: inst.InstanceID,
			ModelPatch: inst.Patch,
			ModelName:  summary.Model,
		}
		line, err := json.Marshal(pred)
		if err != nil {
			return fmt.Errorf("marshal prediction %s: %w", inst.InstanceID, err)
		}
		line = append(line, '\n')
		if _, err := predWriter.Write(line); err != nil {
			return err
		}
	}

	// Write metadata.json.
	metaWriter, err := w.Create("metadata.json")
	if err != nil {
		return err
	}

	meta := map[string]any{
		"run_id":          summary.RunID,
		"vett_version":    summary.VettVersion,
		"model":           summary.Model,
		"endpoint":        summary.Endpoint,
		"suite":           summary.Suite,
		"profile":         summary.Profile,
		"instance_count":  summary.InstanceCount,
		"completed":       summary.Completed,
		"resolved":        summary.Resolved,
		"resolution_rate": summary.ResolutionRate,
		"duration_sec":    summary.DurationSec,
	}
	metaJSON, err := json.MarshalIndent(meta, "", "  ")
	if err != nil {
		return err
	}
	if _, err := metaWriter.Write(metaJSON); err != nil {
		return err
	}

	return nil
}
