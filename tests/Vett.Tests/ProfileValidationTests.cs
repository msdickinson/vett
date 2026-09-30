using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// Profile validation runs at `vett run` start, NOT during parse, so a
/// configured-cleanly profile passes and a misconfigured one fails fast
/// with an actionable message — instead of getting a generic 401 from
/// the LLM endpoint five steps later.
/// </summary>
public class ProfileValidationTests : IDisposable
{
    // Snapshot env so each test gets a clean slate.
    private readonly Dictionary<string, string?> _snapshot = new()
    {
        ["VETT_LLM_ENDPOINT"] = Environment.GetEnvironmentVariable("VETT_LLM_ENDPOINT"),
        ["VETT_LLM_MODEL"] = Environment.GetEnvironmentVariable("VETT_LLM_MODEL"),
        ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
    };

    public void Dispose()
    {
        foreach (var (k, v) in _snapshot) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void Local_Provider_Passes_When_Endpoint_And_Model_Set()
    {
        var p = new Profile { Llm = new LlmConfig { Provider = "local", Endpoint = "http://localhost:8000/v1", Model = "test-model" } };
        // Should not throw.
        Yaml.ValidateProfileForRun(p, "test.yaml");
    }

    [Fact]
    public void Local_Provider_Fails_Without_Endpoint()
    {
        Environment.SetEnvironmentVariable("VETT_LLM_ENDPOINT", null);
        var p = new Profile { Llm = new LlmConfig { Provider = "local", Endpoint = "", Model = "m" } };
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "p.yaml"));
        Assert.Contains("llm.endpoint is required", ex.Message);
        Assert.Contains("VETT_LLM_ENDPOINT", ex.Message);
    }

    [Fact]
    public void Local_Provider_Passes_With_Cli_Endpoint_And_Model()
    {
        // `vett bench --endpoint X --model Y` with neither in the profile nor env
        // used to fail here with "2 configuration errors".
        Environment.SetEnvironmentVariable("VETT_LLM_ENDPOINT", null);
        Environment.SetEnvironmentVariable("VETT_LLM_MODEL", null);
        var p = new Profile { Llm = new LlmConfig { Provider = "local", Endpoint = "", Model = "" } };
        Yaml.ValidateProfileForRun(p, "p.yaml", "http://localhost:8000/v1", "test-model");
    }

    [Fact]
    public void Local_Provider_Picks_Up_Endpoint_From_Env_Var()
    {
        Environment.SetEnvironmentVariable("VETT_LLM_ENDPOINT", "http://runner:8000/v1");
        var p = new Profile { Llm = new LlmConfig { Provider = "local", Model = "m" } };
        Yaml.ValidateProfileForRun(p, "p.yaml"); // no throw
    }

    [Fact]
    public void Cloud_Provider_Fails_Without_ApiKeyEnv()
    {
        var p = new Profile { Llm = new LlmConfig { Provider = "openai", Model = "gpt-4o", ApiKeyEnv = "" } };
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "p.yaml"));
        Assert.Contains("api_key_env is required", ex.Message);
        Assert.Contains("openai", ex.Message);
    }

    [Fact]
    public void Cloud_Provider_Fails_When_ApiKeyEnv_Var_Is_Unset()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        var p = new Profile { Llm = new LlmConfig { Provider = "openai", Model = "gpt-4o", ApiKeyEnv = "OPENAI_API_KEY" } };
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "p.yaml"));
        Assert.Contains("OPENAI_API_KEY", ex.Message);
        Assert.Contains("isn't set", ex.Message);
    }

    [Fact]
    public void Cloud_Provider_Passes_When_Env_Var_Is_Set()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test");
        var p = new Profile { Llm = new LlmConfig { Provider = "openai", Model = "gpt-4o", ApiKeyEnv = "OPENAI_API_KEY" } };
        Yaml.ValidateProfileForRun(p, "p.yaml"); // no throw
    }

    [Fact]
    public void Missing_Model_Fails_With_Both_YAML_Field_And_Env_Var_Empty()
    {
        Environment.SetEnvironmentVariable("VETT_LLM_MODEL", null);
        var p = new Profile { Llm = new LlmConfig { Provider = "local", Endpoint = "http://x:8000/v1", Model = "" } };
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "p.yaml"));
        Assert.Contains("llm.model is required", ex.Message);
    }

    [Fact]
    public void Multiple_Errors_Are_Reported_Together()
    {
        Environment.SetEnvironmentVariable("VETT_LLM_ENDPOINT", null);
        Environment.SetEnvironmentVariable("VETT_LLM_MODEL", null);
        var p = new Profile { Llm = new LlmConfig { Provider = "local", Endpoint = "", Model = "" } };
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "p.yaml"));
        Assert.Contains("2 configuration errors", ex.Message);
        Assert.Contains("llm.endpoint", ex.Message);
        Assert.Contains("llm.model", ex.Message);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("azure")]
    [InlineData("google")]
    public void All_Cloud_Providers_Require_ApiKeyEnv(string provider)
    {
        var p = new Profile { Llm = new LlmConfig { Provider = provider, Model = "x" } };
        var ex = Assert.Throws<InvalidOperationException>(() => Yaml.ValidateProfileForRun(p, "p.yaml"));
        Assert.Contains("api_key_env is required", ex.Message);
        Assert.Contains(provider, ex.Message);
    }
}
