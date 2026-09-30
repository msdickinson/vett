using Vett.Capacity;
using Vett.Config;
using Vett.Llm;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The seam between a capability profile and the CLI, pinned.
///
/// EVERY TEST HERE EXISTS BECAUSE A LIVE CHECK FAILED FIRST. The capacity
/// broker had 78 passing tests and was reachable from zero shipped files; when
/// it was finally wired to <see cref="ChatClientFactory.Create"/>, running
/// PREREG-2026-08-28 end to end turned up defects that no unit test could have
/// caught in isolation and that all shared one shape: the endpoint and the
/// model reach the LLM by TWO DIFFERENT ROUTES, and only one of them had been
/// re-pointed at the catalogue.
///
///   C2, first run  -- the profile's own llm.endpoint arrived at the factory
///                     as endpointOverride, because every command folds
///                     flag -> env -> profile into one string before calling.
///                     The factory's "an explicit --endpoint bypasses capacity,
///                     loudly" rule then fired on a flag nobody passed, and the
///                     request went to the deliberately-unroutable endpoint the
///                     check had planted. -> FoldProfileValue.
///   C2, second run -- with the endpoint fixed, the request went out with
///                     ChatOptions.ModelId = "" and the OpenAI client said
///                     "Empty encoded value": the model name travels to
///                     AgentLoop as a bare string, separately from the client
///                     that knows the real answer. -> EffectiveModel.
///   validate       -- a capability profile has no endpoint or model by design,
///                     which is exactly what ValidateProfileForRun rejected.
///                     A FALSE error is worse than a missing one: it tells the
///                     author to add the stale endpoint the capability layer
///                     exists to overrule.
///
/// The live checks are the real proof; these are the regression net, because a
/// fix pass with no test regresses silently.
/// </summary>
public class CapacityWiringTests
{
    // ---- FoldProfileValue: who owns endpoint and model -------------------

    [Fact]
    public void NonCapabilityProfile_KeepsItsOwnEndpointAndModel()
    {
        var c = new LlmConfig { Endpoint = "http://host:8000/v1", Model = "m" };
        Assert.Equal("http://host:8000/v1", ChatClientFactory.FoldProfileValue(c, "", c.Endpoint));
        Assert.Equal("m", ChatClientFactory.FoldProfileValue(c, "", c.Model));
    }

    [Fact]
    public void CapabilityProfile_DiscardsItsOwnEndpointAndModel()
    {
        // The stale-endpoint case, which is the whole point: a profile that
        // names a capability AND carries an endpoint must route by capability.
        var c = new LlmConfig
        {
            Capability = "flash",
            ContextTokens = 60000,
            Endpoint = "http://127.0.0.1:9/v1",
            Model = "THIS-MODEL-DOES-NOT-EXIST",
        };
        Assert.Equal("", ChatClientFactory.FoldProfileValue(c, "", c.Endpoint));
        Assert.Equal("", ChatClientFactory.FoldProfileValue(c, "", c.Model));
    }

    [Fact]
    public void ExplicitFlagStillWins_EvenOnACapabilityProfile()
    {
        // The bypass is deliberate and documented; what was wrong was WHO
        // triggered it. A human's --endpoint must still win (loudly), or there
        // is no way to point a capability profile at a one-off host.
        var c = new LlmConfig { Capability = "flash", ContextTokens = 60000, Endpoint = "http://127.0.0.1:9/v1" };
        Assert.Equal("http://real:8000/v1", ChatClientFactory.FoldProfileValue(c, "http://real:8000/v1", c.Endpoint));
    }

    [Fact]
    public void FoldProfileValue_NullConfigIsNotACapability()
    {
        Assert.Equal("http://host/v1", ChatClientFactory.FoldProfileValue(null, "", "http://host/v1"));
    }

    // ---- EffectiveModel: joining the second route back up ----------------

    [Fact]
    public void EffectiveModel_ReadsTheLeasedClientWhenTheCallerHasNoModel()
    {
        using var leased = new LeasedChatClient(
            new StubChatClient(), NoOpBroker(), leaseId: "test-lease",
            ttl: TimeSpan.FromMinutes(5), model: "deepseek-v4-flash", endpoint: "http://gpu-1:8000/v1");

        Assert.Equal("deepseek-v4-flash", ChatClientFactory.EffectiveModel(leased, ""));
        Assert.Equal("deepseek-v4-flash", ChatClientFactory.EffectiveModel(leased, null));
    }

    [Fact]
    public void EffectiveModel_LeavesAnExplicitModelAlone()
    {
        using var leased = new LeasedChatClient(
            new StubChatClient(), NoOpBroker(), leaseId: "test-lease",
            ttl: TimeSpan.FromMinutes(5), model: "deepseek-v4-flash", endpoint: "http://gpu-1:8000/v1");

        Assert.Equal("explicit", ChatClientFactory.EffectiveModel(leased, "explicit"));
    }

    [Fact]
    public void EffectiveModel_IsANoOpOnAPlainClient()
    {
        // Every non-capability path goes through here too. It must not invent
        // a model for a client that never took a lease.
        using var plain = new StubChatClient();
        Assert.Equal("", ChatClientFactory.EffectiveModel(plain, ""));
        Assert.Equal("m", ChatClientFactory.EffectiveModel(plain, "m"));
    }

    // ---- validate: the guard must not reject what the layer below serves --

    [Fact]
    public void Validate_AcceptsACapabilityProfileWithNoEndpointOrModel()
    {
        // Parsed from YAML rather than built with an object initializer, so the
        // test also pins that `capability:` and `context_tokens:` deserialize
        // at all -- a profile that silently dropped them would bind by endpoint
        // and this file would still be green.
        var p = Yaml.ParseProfile("""
            name: p
            system_prompt: hi
            llm: {provider: local, capability: flash, context_tokens: 60000}
            """);
        Assert.Equal("flash", p.Llm.Capability);
        Assert.Equal(60000, p.Llm.ContextTokens);

        Yaml.ValidateProfileForRun(p, "cap.yaml");   // must not throw
    }

    [Fact]
    public void Validate_StillRequiresEndpointAndModel_WithoutACapability()
    {
        // The positive control on the test above. Without it, that test would
        // pass just as confidently if the two checks had been deleted outright.
        var p = Yaml.ParseProfile("""
            name: p
            system_prompt: hi
            llm: {provider: local}
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "nocap.yaml"));
        Assert.Contains("llm.endpoint is required", ex.Message);
        Assert.Contains("llm.model is required", ex.Message);
    }

    [Fact]
    public void Validate_RejectsACapabilityWithNoContextTokens()
    {
        // Exempting the two checks above without adding this one would trade a
        // false error for a MISSING one: the config would validate clean and
        // then throw at first client construction, after the run had started.
        var p = Yaml.ParseProfile("""
            name: p
            system_prompt: hi
            llm: {provider: local, capability: flash}
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "nowindow.yaml"));
        Assert.Contains("context_tokens", ex.Message);
    }

    [Fact]
    public void BindsThroughCapacity_IsTheOneRuleEveryGuardConsults()
    {
        Assert.False(ChatClientFactory.BindsThroughCapacity(null));
        Assert.False(ChatClientFactory.BindsThroughCapacity(new LlmConfig()));
        Assert.False(ChatClientFactory.BindsThroughCapacity(new LlmConfig { Capability = "   " }));
        Assert.True(ChatClientFactory.BindsThroughCapacity(new LlmConfig { Capability = "flash" }));
    }

    // ---- ResolveClient: the same rule, one layer down --------------------

    /// <summary>
    /// ⭐ A SEAT THAT OVERRIDES `capability:` MUST SEND THE CATALOGUE'S MODEL,
    /// NOT THE BASE PROFILE'S.
    ///
    /// This is the third place the endpoint and the model came apart, and the
    /// nastiest: the member gets a real PRO client (correct endpoint, correct
    /// lease) while `resolvedModel` fell through to the base FLASH name. Not an
    /// empty string -- a confident, wrong one. It is also the shape Mark's
    /// \"team leader gets more than the workers\" ask produces directly, so it
    /// would have fired on the first mixed-capability team run.
    /// </summary>
    [Fact]
    public void ResolveClient_ACapabilityMemberTakesTheCataloguesModel_NotTheBases()
    {
        using var fx = new CatalogueFixture();

        var baseCfg = new LlmConfig { Capability = "flash", ContextTokens = 1000 };
        var memberCfg = new LlmConfig { Capability = "pro", ContextTokens = 1000 };
        using var baseClient = new StubChatClient();

        var (client, model) = Vett.Agent.TeamCoordinator.ResolveClient(
            baseCfg, baseClient, "flash-model", memberCfg);

        Assert.NotSame(baseClient, client);
        Assert.Equal("pro-model", model);
        (client as IDisposable)?.Dispose();
    }

    /// <summary>
    /// An EXPLICIT per-seat `model:` still outranks the catalogue, matching the
    /// --model flag's precedence at the CLI. Without this the fix above would
    /// have replaced one override-dropping bug with another.
    /// </summary>
    [Fact]
    public void ResolveClient_AnExplicitSeatModelStillWins()
    {
        using var fx = new CatalogueFixture();

        var baseCfg = new LlmConfig { Capability = "flash", ContextTokens = 1000 };
        var memberCfg = new LlmConfig { Capability = "pro", ContextTokens = 1000, Model = "pinned-by-hand" };
        using var baseClient = new StubChatClient();

        var (client, model) = Vett.Agent.TeamCoordinator.ResolveClient(
            baseCfg, baseClient, "flash-model", memberCfg);

        Assert.Equal("pinned-by-hand", model);
        (client as IDisposable)?.Dispose();
    }

    /// <summary>
    /// THE NEGATIVE CONTROL ON BOTH OF THE ABOVE. A seat that overrides nothing
    /// client-affecting must still get the BASE client and the BASE model,
    /// object-identical. Every non-capability profile in the repo takes this
    /// path, so a regression here would change all 18 shipped profiles while the
    /// two tests above stayed green.
    /// </summary>
    [Fact]
    public void ResolveClient_APlainSeatStillInheritsTheBaseClientAndModel()
    {
        var baseCfg = new LlmConfig { Endpoint = "http://host:8000/v1", Model = "m" };
        var memberCfg = new LlmConfig { Temperature = 0.2 };
        using var baseClient = new StubChatClient();

        var (client, model) = Vett.Agent.TeamCoordinator.ResolveClient(
            baseCfg, baseClient, "m", memberCfg);

        Assert.Same(baseClient, client);
        Assert.Equal("m", model);
    }

    // ---- helpers ---------------------------------------------------------

    private static CapacityBroker NoOpBroker()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vett-wiring-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        return new CapacityBroker(dir);
    }

    /// <summary>
    /// A two-capability catalogue and a scratch ledger, installed on
    /// CapacityBinding's static overrides and REMOVED on dispose.
    ///
    /// ⚠ THESE ARE PROCESS-WIDE STATICS AND XUNIT RUNS CLASSES IN PARALLEL.
    /// Safe here only because ChatClientFactory consults the capacity path at
    /// all only when BindsThroughCapacity(config) is true, and no other test in
    /// this project builds a client from a config that names a capability
    /// (swept 2026-08-28). If one ever does, both classes need the same xunit
    /// collection -- a unique collection name would NOT fix it, since
    /// parallelism is between collections.
    /// </summary>
    private sealed class CatalogueFixture : IDisposable
    {
        private readonly string _dir;

        public CatalogueFixture()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vett-wiring-cat-" + Guid.NewGuid().ToString("n")[..8]);
            CapacityBinding.BrokerOverride = new CapacityBroker(_dir);
            CapacityBinding.CatalogueOverride = new CapabilityCatalogue
            {
                Capabilities =
                {
                    Cap("flash", "local-flash", "http://127.0.0.1:18001/v1", "flash-model"),
                    Cap("pro", "local-pro", "http://127.0.0.1:18002/v1", "pro-model"),
                },
            };
        }

        // Both LOCAL on purpose: a cloud provider would pull a budget and an
        // api_key_env into a test that is about model-name plumbing, and a
        // missing key would fail it for the wrong reason.
        private static Capability Cap(string name, string provider, string endpoint, string model) => new()
        {
            Name = name,
            Providers =
            {
                new CapabilityProvider
                {
                    Name = provider,
                    LocalityRaw = "local",
                    Endpoint = endpoint,
                    Model = model,
                    ServedWindowTokens = 65536,
                    PoolTokens = 100000,
                },
            },
        };

        public void Dispose()
        {
            CapacityBinding.BrokerOverride = null;
            CapacityBinding.CatalogueOverride = null;
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    private sealed class StubChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("stub");

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("stub");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
