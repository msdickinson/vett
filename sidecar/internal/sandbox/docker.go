package sandbox

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os/exec"
	"strings"
)

// DockerSandbox owns one running container plus its sidecar subprocess.
// The underlying docker daemon is whatever DOCKER_HOST points at: local
// Unix socket by default, ssh://runner when you're ready to move execution
// to the runner box in a later phase.
type DockerSandbox struct {
	Image       string
	ContainerID string
	User        string
	HomeEnv     string

	Client *Client

	sidecarCmd  *exec.Cmd
	sidecarIn   io.WriteCloser
	sidecarOut  io.ReadCloser
}

// DockerOptions configures Start.
type DockerOptions struct {
	Image           string            // required
	RunAsRoot       bool              // --user root when true
	HomeEnv         string            // HOME env inside container (default /tmp)
	ExtraEnv        map[string]string // additional -e flags
	SidecarHostPath string            // local path to the vett-sidecar binary (required)

	// BindMounts is a list of "host:container[:ro]" style bind mount
	// entries appended as -v flags to docker run. Used for the SSD
	// bind-mount feature that puts /testbed on fast storage and
	// leaves the writable layer's rare writes on HDD. See
	// WorkdirExtractFrom for the paired host-side pre-population.
	BindMounts []BindMount

	// TmpfsMounts is a list of "/path[:size=...]" entries that become
	// --tmpfs flags on docker run. Typical use: --tmpfs /tmp:size=2g
	// to keep pytest/pip temp files entirely in RAM.
	TmpfsMounts []string

	// WorkdirExtractFrom, if non-empty, names a path inside the image
	// whose contents get pre-extracted to the corresponding host-side
	// bind mount target BEFORE the main container starts. This solves
	// the "bind-mounting an empty SSD dir over /testbed makes /testbed
	// empty" problem by docker-cp'ing the baked tree out first.
	//
	// The runner pairs WorkdirExtractFrom="/testbed" with a BindMount
	// entry that targets /testbed on the host side; Start() does the
	// extract, then runs the real container with the bind mount.
	WorkdirExtractFrom string
}

// BindMount is one -v entry for docker run.
type BindMount struct {
	Host      string // host path (must exist)
	Container string // container path
	ReadOnly  bool
}

func (b BindMount) Arg() string {
	s := b.Host + ":" + b.Container
	if b.ReadOnly {
		s += ":ro"
	}
	return s
}

// Start boots a container via `docker run -d`, copies the sidecar binary
// into it, then `docker exec -i`s it and returns a ready-to-use sandbox
// with the Hello handshake already complete.
func Start(ctx context.Context, opts DockerOptions) (*DockerSandbox, error) {
	if opts.Image == "" {
		return nil, errors.New("sandbox: Image is required")
	}
	if opts.SidecarHostPath == "" {
		return nil, errors.New("sandbox: SidecarHostPath is required")
	}
	if opts.HomeEnv == "" {
		opts.HomeEnv = "/tmp"
	}

	// If the profile requested a pre-extract (SSD bind-mount for the
	// workdir), docker-cp the baked tree out of the image to the host
	// bind-mount target BEFORE the main docker run. Without this, the
	// bind mount would overlay an empty host dir onto the workdir and
	// the agent would start with no code to work on.
	if opts.WorkdirExtractFrom != "" {
		if err := preExtractWorkdir(ctx, opts); err != nil {
			return nil, fmt.Errorf("pre-extract workdir: %w", err)
		}
	}

	runArgs := []string{
		"run", "-d", "--rm",
		"--entrypoint", "/bin/bash",
		"-e", "HOME=" + opts.HomeEnv,
	}
	if opts.RunAsRoot {
		runArgs = append(runArgs, "--user", "root")
	}
	for k, v := range opts.ExtraEnv {
		runArgs = append(runArgs, "-e", k+"="+v)
	}
	for _, bm := range opts.BindMounts {
		runArgs = append(runArgs, "-v", bm.Arg())
	}
	for _, tm := range opts.TmpfsMounts {
		runArgs = append(runArgs, "--tmpfs", tm)
	}
	runArgs = append(runArgs, opts.Image, "-c", "sleep infinity")

	out, err := dockerRun(ctx, runArgs...)
	if err != nil {
		return nil, translateDockerErr("docker run", err, out, opts.Image)
	}
	containerID := strings.TrimSpace(string(out))
	if containerID == "" {
		return nil, fmt.Errorf("docker run: empty container id; output=%q", string(out))
	}

	sandbox := &DockerSandbox{
		Image:       opts.Image,
		ContainerID: containerID,
		HomeEnv:     opts.HomeEnv,
	}

	// docker cp sidecar into a path that does NOT collide with any
	// tmpfs mount. /tmp is a common tmpfs target (--tmpfs /tmp), and
	// docker cp'ing into a path where tmpfs is mounted at runtime
	// writes to the underlying layer — invisible at exec time. Use
	// /usr/local/lib/vett-sidecar instead, which is writable under
	// --user root and never gets overlaid by conventional mounts.
	const sidecarContainerPath = "/usr/local/lib/vett-sidecar"
	if _, err := dockerRun(ctx, "cp", opts.SidecarHostPath, containerID+":"+sidecarContainerPath); err != nil {
		_ = sandbox.Kill(context.Background())
		return nil, fmt.Errorf("docker cp sidecar: %w", err)
	}
	if _, err := dockerRun(ctx, "exec", containerID, "chmod", "+x", sidecarContainerPath); err != nil {
		_ = sandbox.Kill(context.Background())
		return nil, fmt.Errorf("docker chmod sidecar: %w", err)
	}

	// docker exec -i to launch the sidecar and keep stdin/stdout attached.
	cmd := exec.CommandContext(ctx, dockerBin(), "exec", "-i", containerID, sidecarContainerPath)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		_ = sandbox.Kill(context.Background())
		return nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		_ = sandbox.Kill(context.Background())
		return nil, err
	}
	cmd.Stderr = nil // keep the sidecar's stderr quiet; real errors come back over RPC
	if err := cmd.Start(); err != nil {
		_ = sandbox.Kill(context.Background())
		return nil, fmt.Errorf("docker exec sidecar: %w", err)
	}
	sandbox.sidecarCmd = cmd
	sandbox.sidecarIn = stdin
	sandbox.sidecarOut = stdout
	sandbox.Client = NewClient(stdout, stdin)

	// Mandatory hello handshake.
	if _, err := sandbox.Client.Hello(ctx, "0.1.0-dev"); err != nil {
		_ = sandbox.Kill(context.Background())
		return nil, fmt.Errorf("sidecar hello: %w", err)
	}
	return sandbox, nil
}

// Close shuts down the sidecar cleanly and kills the container.
func (d *DockerSandbox) Close(ctx context.Context) error {
	if d.sidecarIn != nil {
		_ = d.sidecarIn.Close()
	}
	if d.sidecarCmd != nil {
		_ = d.sidecarCmd.Wait()
	}
	return d.Kill(ctx)
}

// Kill forcibly removes the container.
func (d *DockerSandbox) Kill(ctx context.Context) error {
	if d.ContainerID == "" {
		return nil
	}
	_, err := dockerRun(ctx, "kill", d.ContainerID)
	d.ContainerID = ""
	return err
}

// dockerRun runs a docker CLI subcommand and returns combined output.
func dockerRun(ctx context.Context, args ...string) ([]byte, error) {
	cmd := exec.CommandContext(ctx, dockerBin(), args...)
	var out bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &out
	err := cmd.Run()
	return out.Bytes(), err
}

// dockerBin is overridable for tests. Defaults to "docker".
var dockerBin = func() string { return "docker" }

// translateDockerErr converts common docker CLI failure modes into
// actionable, user-facing error messages per implementation-notes.md's
// image management policy (never auto-pull).
func translateDockerErr(stage string, err error, out []byte, image string) error {
	s := string(out)
	if strings.Contains(s, "No such image") || strings.Contains(s, "manifest unknown") || strings.Contains(s, "pull access denied") {
		return fmt.Errorf("%s: image %q not available locally. Run: docker pull %s", stage, image, image)
	}
	if strings.Contains(s, "Cannot connect to the Docker daemon") || strings.Contains(s, "error during connect") {
		return fmt.Errorf("%s: docker daemon is not reachable (DOCKER_HOST=%q). Start Docker Desktop or set DOCKER_HOST", stage, dockerHostFromEnv())
	}
	return fmt.Errorf("%s: %w (output: %s)", stage, err, strings.TrimSpace(s))
}

// preExtractWorkdir implements the "docker create + docker cp + docker
// rm" dance that pulls the baked contents of a directory out of an
// image so we can bind-mount a host-side copy over it at run time.
// Used by the SSD bind-mount feature to put /testbed on fast storage
// without losing the image's original /testbed contents.
//
// Finds the BindMount entry whose Container matches WorkdirExtractFrom
// and extracts to the Host side of that mount. No-op if no matching
// mount is declared.
func preExtractWorkdir(ctx context.Context, opts DockerOptions) error {
	var target *BindMount
	for i := range opts.BindMounts {
		if opts.BindMounts[i].Container == opts.WorkdirExtractFrom {
			target = &opts.BindMounts[i]
			break
		}
	}
	if target == nil {
		return fmt.Errorf("WorkdirExtractFrom=%q has no matching BindMount", opts.WorkdirExtractFrom)
	}
	// If the host dir already has content, skip extraction — idempotent.
	if entries, err := readDirShallow(target.Host); err == nil && len(entries) > 0 {
		return nil
	}
	// docker create <image> — creates a non-running container so we
	// can docker cp out of it.
	createArgs := []string{"create"}
	if opts.RunAsRoot {
		createArgs = append(createArgs, "--user", "root")
	}
	createArgs = append(createArgs, "--entrypoint", "/bin/sh", opts.Image, "-c", "true")
	createOut, err := dockerRun(ctx, createArgs...)
	if err != nil {
		return translateDockerErr("docker create", err, createOut, opts.Image)
	}
	tmpID := strings.TrimSpace(string(createOut))
	if tmpID == "" {
		return fmt.Errorf("docker create returned empty container id")
	}
	defer dockerRun(context.Background(), "rm", "-f", tmpID)
	// docker cp <tmpID>:<source>/. <host>/
	// The trailing /. on the source means "contents of", so the host
	// dir receives the contents directly rather than a nested copy.
	srcSpec := tmpID + ":" + opts.WorkdirExtractFrom + "/."
	cpOut, err := dockerRun(ctx, "cp", srcSpec, target.Host)
	if err != nil {
		return fmt.Errorf("docker cp %s → %s: %w (output: %s)",
			srcSpec, target.Host, err, strings.TrimSpace(string(cpOut)))
	}
	return nil
}

// readDirShallow returns the immediate children of dir. Returns an
// empty slice and nil error if dir exists but is empty. Error for
// anything else.
func readDirShallow(dir string) ([]string, error) {
	entries, err := osReadDir(dir)
	if err != nil {
		return nil, err
	}
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		names = append(names, e.Name())
	}
	return names, nil
}

// osReadDir is a test hook.
var osReadDir = osReadDirFn

func dockerHostFromEnv() string {
	// Report DOCKER_HOST if set so the error message is actionable in
	// both local and remote configurations.
	return getenv("DOCKER_HOST")
}

// getenv is a tiny wrapper so tests can stub it.
var getenv = func(key string) string {
	return osGetenv(key)
}

func osGetenv(key string) string {
	// indirection kept out of the main file so tests can stub dockerBin
	// and getenv without dragging in os everywhere.
	return osEnvLookup(key)
}
