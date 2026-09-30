using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// A credential is scoped to the host it was issued for. ChatClientFactory.Merge
/// used to inherit `api_key_env` unconditionally, so a team member that repointed
/// itself at a different endpoint carried the BASE profile's key to the new host.
///
/// ⛔ THE LIVE INSTANCE (found 2026-08-26). `ds-team-lead-pro` has an OpenRouter
/// base with `api_key_env: DEEPSEEK_API_KEY`; its `implementer` and `researcher`
/// seats override provider+endpoint to the LAN vLLM at gpu-1:8000. They
/// inherited the key, so the real OpenRouter token went out as an
/// `Authorization: Bearer` header over PLAIN HTTP to a LAN box.
///
/// The profile's own comment (ds-team-lead-pro.yaml:384-385) says
/// "NO api_key_env: the vLLM server is open on the LAN" — the code was
/// contradicting the stated intent at the very seat it applied to.
///
/// ⚠ NOTHING WOULD EVER HAVE CAUGHT THIS AT RUNTIME. vLLM ignores auth and
/// answers 200 with or without a bearer token, so the leak produced no failing
/// observation anywhere. It failed SILENTLY-WELL. That is why the guard has to
/// be a test over the shipped profiles rather than something a live run notices.
/// </summary>
public class CredentialScopingTests
{
    /// <summary>
    /// Same rule the fix uses, restated independently here so the test is not
    /// simply the implementation spelled twice. Destination is the resolved
    /// endpoint, or `provider:<name>` when the endpoint is blank — otherwise an
    /// SDK-default `openai` seat and a `local` seat both look like "" and would
    /// compare equal despite being different hosts.
    /// </summary>
    private static string Dest(string provider, string endpoint)
    {
        var resolved = string.IsNullOrEmpty(endpoint)
            ? ChatClientFactory.DefaultEndpointFor(provider) ?? ""
            : endpoint;
        return string.IsNullOrEmpty(resolved)
            ? "provider:" + (provider ?? "").ToLowerInvariant()
            : resolved.TrimEnd('/').ToLowerInvariant();
    }

    private static string ProfilesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var c = Path.Combine(d.FullName, "profiles");
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "ds-team-flash.yaml"))) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("could not locate the repo's profiles/ directory");
    }

    // ---- the rule, three ways ---------------------------------------------

    [Fact]
    public void A_member_that_repoints_to_another_host_does_not_inherit_the_key()
    {
        var b = new LlmConfig { Provider = "local", Endpoint = "https://openrouter.ai/api/v1", Model = "pro", ApiKeyEnv = "DEEPSEEK_API_KEY" };
        var m = new LlmConfig { Endpoint = "http://gpu-1:8000/v1", Model = "flash" };

        var merged = ChatClientFactory.Merge(b, m);

        Assert.Equal("", merged.ApiKeyEnv);
        Assert.Equal("http://gpu-1:8000/v1", merged.Endpoint);   // the repoint itself still works
    }

    /// <summary>
    /// The other side. Without this, "always return empty" would pass the test
    /// above and silently break every seat that legitimately shares the base's
    /// credential — which is how `ds-team-pro` is built.
    /// </summary>
    [Fact]
    public void A_member_on_the_same_host_still_inherits_the_key()
    {
        var b = new LlmConfig { Provider = "local", Endpoint = "https://openrouter.ai/api/v1", Model = "pro", ApiKeyEnv = "DEEPSEEK_API_KEY" };
        var m = new LlmConfig { Model = "deepseek/deepseek-v4-pro" };      // model-only override

        Assert.Equal("DEEPSEEK_API_KEY", ChatClientFactory.Merge(b, m).ApiKeyEnv);
    }

    /// <summary>
    /// An explicitly declared key is the author's informed choice and travels
    /// wherever they pointed it. This is what keeps `ds-team-flash-escalate`'s
    /// `implementer-pro` seat working: a local base, a cloud seat, and its own
    /// `api_key_env` line.
    /// </summary>
    [Fact]
    public void An_explicit_member_key_wins_even_across_hosts()
    {
        var b = new LlmConfig { Provider = "local", Endpoint = "http://gpu-1:8000/v1", Model = "flash" };
        var m = new LlmConfig { Endpoint = "https://openrouter.ai/api/v1", Model = "pro", ApiKeyEnv = "DEEPSEEK_API_KEY" };

        Assert.Equal("DEEPSEEK_API_KEY", ChatClientFactory.Merge(b, m).ApiKeyEnv);
    }

    // ---- and it must hold over the profiles that actually ship -------------

    /// <summary>
    /// The hand-built cases above are about Merge. This one is about the fleet:
    /// it walks every shipped profile's every seat and asserts no INHERITED
    /// credential reaches a host the base did not name.
    ///
    /// Both counters are asserted non-zero. Without the cross-host counter the
    /// test could pass because no profile repoints a seat (vacuous); without the
    /// inheriting counter it could pass because the key was dropped everywhere
    /// (a fix that breaks ds-team-pro). The ledger is printed either way, so a
    /// failure says which population it was measured over.
    /// </summary>
    [Fact]
    public void No_shipped_seat_inherits_a_credential_across_hosts()
    {
        int crossHost = 0, sameHostInherited = 0, explicitKeys = 0, seats = 0;
        var skipped = new List<string>();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(ProfilesDir(), "*.yaml"))
        {
            Profile p;
            try { p = Yaml.LoadProfile(file); }
            catch (Exception ex) { skipped.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}"); continue; }
            if (p.Team is null || p.Llm is null) continue;

            var baseDest = Dest(p.Llm.Provider, p.Llm.Endpoint);

            var all = new List<MemberConfig>(p.Team.Members ?? []);
            if (p.Team.Leader is not null) all.Add(p.Team.Leader);

            foreach (var seat in all)
            {
                if (seat.Llm is null) continue;
                seats++;

                var merged = ChatClientFactory.Merge(p.Llm, seat.Llm);
                var seatDest = Dest(merged.Provider, merged.Endpoint);
                var declaredOwnKey = !string.IsNullOrEmpty(seat.Llm.ApiKeyEnv);

                if (declaredOwnKey) { explicitKeys++; continue; }

                if (seatDest != baseDest)
                {
                    crossHost++;
                    if (!string.IsNullOrEmpty(merged.ApiKeyEnv))
                        violations.Add($"{Path.GetFileName(file)}::{seat.Name} inherited '{merged.ApiKeyEnv}' "
                                     + $"to {seatDest} (base host {baseDest})");
                }
                else if (!string.IsNullOrEmpty(merged.ApiKeyEnv))
                {
                    sameHostInherited++;
                }
            }
        }

        var ledger = $"seats={seats} crossHost={crossHost} sameHostInherited={sameHostInherited} "
                   + $"explicit={explicitKeys} unparseable={skipped.Count}"
                   + (skipped.Count > 0 ? $" [{string.Join("; ", skipped)}]" : "");

        Assert.True(violations.Count == 0,
            $"a credential followed a seat to a host the base never named:\n  {string.Join("\n  ", violations)}\n{ledger}");
        Assert.True(crossHost > 0, $"no shipped seat repoints to another host, so this guard is vacuous. {ledger}");
        Assert.True(sameHostInherited > 0, $"no shipped seat inherits a key at all — the rule is over-broad. {ledger}");
    }

    /// <summary>
    /// The harm, stated directly rather than as a property: an INHERITED key must
    /// never leave over cleartext. An EXPLICIT one may — an auth-enabled server on
    /// a trusted LAN is a real configuration, and that is the author's call. Only
    /// the silent case is banned.
    /// </summary>
    [Fact]
    public void No_inherited_credential_is_sent_over_plain_http()
    {
        var bad = new List<string>();
        foreach (var file in Directory.GetFiles(ProfilesDir(), "*.yaml"))
        {
            Profile p;
            try { p = Yaml.LoadProfile(file); } catch { continue; }
            if (p.Team is null || p.Llm is null) continue;

            foreach (var seat in (p.Team.Members ?? []).Concat(p.Team.Leader is null ? [] : new[] { p.Team.Leader }))
            {
                if (seat.Llm is null || !string.IsNullOrEmpty(seat.Llm.ApiKeyEnv)) continue;
                var merged = ChatClientFactory.Merge(p.Llm, seat.Llm);
                if (!string.IsNullOrEmpty(merged.ApiKeyEnv) &&
                    merged.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    bad.Add($"{Path.GetFileName(file)}::{seat.Name} -> {merged.Endpoint} carrying '{merged.ApiKeyEnv}'");
            }
        }

        Assert.True(bad.Count == 0, "inherited credential over cleartext:\n  " + string.Join("\n  ", bad));
    }
}
