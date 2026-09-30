using System.Diagnostics;

namespace Vett.Sandbox;

/// <summary>Starts the Go sidecar as a local subprocess.</summary>
public sealed class LocalSandbox : IDisposable
{
    public RpcClient Rpc { get; }
    public string SessionId { get; }
    private readonly Process _proc;

    private LocalSandbox(Process proc, RpcClient rpc, string sessionId)
    {
        _proc = proc; Rpc = rpc; SessionId = sessionId;
    }

    public static async Task<LocalSandbox> StartAsync(string sidecarPath, string cwd, string session = "local", CancellationToken ct = default)
    {
        var proc = Process.Start(new ProcessStartInfo(sidecarPath)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Failed to start sidecar: {sidecarPath}");

        try
        {
            var rpc = new RpcClient(proc.StandardOutput.BaseStream, proc.StandardInput.BaseStream);
            await rpc.HelloAsync(ct);
            await rpc.SessionCreateAsync(session, cwd, ct);
            return new LocalSandbox(proc, rpc, session);
        }
        catch
        {
            // Init failed — kill the sidecar so we don't leak it.
            try { proc.Kill(); } catch { }
            proc.Dispose();
            throw;
        }
    }

    public void Dispose() { Rpc.Dispose(); try { _proc.Kill(); } catch { } _proc.Dispose(); }
}

/// <summary>Starts the Go sidecar inside a Docker container.</summary>
public sealed class DockerSandbox : IDisposable
{
    /// <summary>
    /// The container is Linux whatever the host is. A Windows or macOS host
    /// has found ITS OWN sidecar, which cannot run in there, so swap in the
    /// Linux build that ships beside it. Any other name (a custom
    /// VETT_SIDECAR_PATH, or already the Linux build) is used as given.
    /// </summary>
    internal static string ContainerSidecar(string hostSidecar)
    {
        const string linux = "vett-sidecar-linux-amd64";
        var name = Path.GetFileName(hostSidecar);
        if (!name.StartsWith("vett-sidecar-windows") && !name.StartsWith("vett-sidecar-darwin"))
            return hostSidecar;
        var sibling = Path.Combine(Path.GetDirectoryName(hostSidecar) ?? ".", linux);
        if (File.Exists(sibling)) return sibling;
        throw new FileNotFoundException(
            $"Docker sandboxes run the Linux sidecar, but only {hostSidecar} was found. Put {linux} next to it.");
    }

    public RpcClient Rpc { get; }
    public string SessionId => "agent";
    private readonly Process _sidecar;
    private string _containerId;

    private DockerSandbox(Process sidecar, RpcClient rpc, string containerId)
    {
        _sidecar = sidecar; Rpc = rpc; _containerId = containerId;
    }

    public static async Task<DockerSandbox> StartAsync(string image, string sidecarPath, bool runAsRoot = false, string home = "/tmp", CancellationToken ct = default)
    {
        var runArgs = new List<string> { "run", "-d", "--rm", "--entrypoint", "/bin/bash", "-e", $"HOME={home}" };
        if (runAsRoot) runArgs.AddRange(["--user", "root"]);
        runArgs.AddRange([image, "-c", "sleep infinity"]);

        sidecarPath = ContainerSidecar(sidecarPath);
        var containerId = (await Docker(runArgs, ct)).Trim();

        try
        {
            const string sidecarContainerPath = "/usr/local/lib/vett-sidecar";
            await Docker(["cp", sidecarPath, $"{containerId}:{sidecarContainerPath}"], ct);
            await Docker(["exec", containerId, "chmod", "+x", sidecarContainerPath], ct);

            var psi = new ProcessStartInfo("docker")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("exec"); psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(containerId); psi.ArgumentList.Add(sidecarContainerPath);

            var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to exec sidecar");
            try
            {
                var rpc = new RpcClient(proc.StandardOutput.BaseStream, proc.StandardInput.BaseStream);
                await rpc.HelloAsync(ct);
                return new DockerSandbox(proc, rpc, containerId);
            }
            catch
            {
                try { proc.Kill(); } catch { }
                proc.Dispose();
                throw;
            }
        }
        catch
        {
            // Container started but later init failed — kill it so it doesn't leak.
            try { await Docker(["kill", containerId], CancellationToken.None); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        Rpc.Dispose();
        try { _sidecar.Kill(); } catch { } _sidecar.Dispose();
        if (_containerId.Length > 0)
        {
            try { Docker(["kill", _containerId], CancellationToken.None).Wait(5000); } catch { }
            _containerId = "";
        }
    }

    private static async Task<string> Docker(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("docker failed");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"docker: {await p.StandardError.ReadToEndAsync(ct)}");
        return stdout;
    }
}
