package main

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/spf13/cobra"

	vettassets "github.com/msdickinson/vett/sidecar"
)

// vett install — extracts the embedded sidecar binary to
// ~/.vett/bin/<sidecar-filename> so `vett run` or `vett chat` can find
// it from any workspace. Separate from `vett init` which scaffolds a
// workspace.
//
// With --remote <host>, scp's the vett binary itself to <host>:~/.vett/
// bin/vett so a future `vett run --remote <host>` can exec it.
func installCmd() *cobra.Command {
	var remoteHost string
	cmd := &cobra.Command{
		Use:   "install",
		Short: "Extract the embedded sidecar binary (or install on a remote host)",
		Long: `install writes the platform-specific sidecar binary that ships
inside the vett host binary out to ~/.vett/bin/ so that 'vett run' and
'vett chat' can find it without a ./bin copy in the current dir.

With --remote <host>, installs the vett binary itself on the given
SSH host at ~/.vett/bin/vett — the target of future
'vett run --remote <host>' invocations.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			if remoteHost != "" {
				local, err := localLinuxBinary()
				if err != nil {
					return err
				}
				if err := installRemoteVett(remoteHost, local); err != nil {
					return err
				}
				fmt.Printf("✓ vett installed on %s at ~/.vett/bin/vett\n", remoteHost)
				fmt.Println()
				fmt.Println("Next steps:")
				fmt.Printf("  vett run --remote %s --suite X --profile Y ...\n", remoteHost)
				return nil
			}
			installed, err := userSidecarPath()
			if err != nil {
				return err
			}
			bytes, err := vettassets.SidecarBytes()
			if err != nil {
				return fmt.Errorf("read embedded sidecar: %w", err)
			}
			if err := os.MkdirAll(filepath.Dir(installed), 0o755); err != nil {
				return fmt.Errorf("create %s: %w", filepath.Dir(installed), err)
			}
			if err := os.WriteFile(installed, bytes, 0o755); err != nil {
				return fmt.Errorf("write %s: %w", installed, err)
			}
			fmt.Printf("✓ sidecar installed to %s (%d bytes)\n", installed, len(bytes))
			fmt.Println()
			fmt.Println("Next steps:")
			fmt.Println("  cd into any directory you want as a workspace, then:")
			fmt.Println("    vett init              # scaffold profiles/, suites/, results/")
			fmt.Println("    vett profiles          # list built-in + workspace profiles")
			fmt.Println("    vett run --help        # see run flags")
			return nil
		},
	}
	cmd.Flags().StringVar(&remoteHost, "remote", "", "Install vett binary on the given SSH host at ~/.vett/bin/vett")
	return cmd
}

// vett init — scaffolds ./profiles/, ./suites/, ./results/ in the
// current directory and drops a .vett-workspace marker file so users
// can see at a glance which directories are Vett workspaces.
func initCmd() *cobra.Command {
	return &cobra.Command{
		Use:   "init",
		Short: "Scaffold a Vett workspace in the current directory",
		Long: `init creates an empty profiles/, suites/, and results/ tree in the
current directory. This is a user workspace — you can have as many of
these as you want, each in a different folder, each with its own set
of custom profiles, suites, and run outputs.

Built-in profiles and suites (like 'openhands' and 'test-canned') are
always available regardless of whether you run 'vett init' — they ship
inside the binary. Use 'vett init' when you want to create your own
custom profiles or suites alongside them.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			wd, err := os.Getwd()
			if err != nil {
				return err
			}
			for _, sub := range []string{"profiles", "suites", "results"} {
				dir := filepath.Join(wd, sub)
				if err := os.MkdirAll(dir, 0o755); err != nil {
					return fmt.Errorf("create %s: %w", dir, err)
				}
				fmt.Printf("  %s/\n", sub)
			}
			marker := filepath.Join(wd, ".vett-workspace")
			if _, err := os.Stat(marker); os.IsNotExist(err) {
				content := "# Vett workspace\n# This file marks the directory as a Vett workspace.\n# vett run --profile <name> --suite <name> writes outputs to ./results/\n"
				_ = os.WriteFile(marker, []byte(content), 0o644)
			}
			fmt.Printf("\n✓ workspace initialized at %s\n", wd)
			fmt.Println()
			fmt.Println("Built-in profiles available (no files to copy):")
			for _, p := range vettassets.ListProfiles() {
				fmt.Printf("  - %s\n", p)
			}
			fmt.Println()
			fmt.Println("Built-in suites available:")
			for _, s := range vettassets.ListSuites() {
				fmt.Printf("  - %s\n", s)
			}
			fmt.Println()
			fmt.Println("To add a custom profile that extends openhands, create")
			fmt.Println("profiles/<name>.yaml in this directory and edit freely.")
			return nil
		},
	}
}
