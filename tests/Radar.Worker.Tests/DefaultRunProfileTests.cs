using System.Text.Json;

namespace Radar.Worker.Tests;

/// <summary>
/// Guards the committed baseline run profile (<c>scripts/run-profiles/default.json</c>) — the canonical record
/// of HOW we run a live measurement. These are pure file/JSON assertions: no Worker is started, no HTTP or AI
/// call is made, and no API key is read (only the env-var NAME the profile declares is asserted).
/// </summary>
public sealed class DefaultRunProfileTests
{
    private static JsonElement DefaultProfileAi()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DefaultProfilePath()));
        return doc.RootElement.GetProperty("Radar").GetProperty("Ai").Clone();
    }

    /// <summary>
    /// Walks up from the test binary to the repo root (the first ancestor carrying
    /// <c>scripts/run-profiles/default.json</c>) so the test does not depend on the working directory.
    /// </summary>
    private static string DefaultProfilePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "run-profiles", "default.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate scripts/run-profiles/default.json from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void DefaultProfile_UsesDeepInfraDeepSeekEarningsReader()
    {
        // Spec 119: the baseline earnings read is DeepSeek-V4-Flash on DeepInfra via the OpenAI-compatible
        // provider (spec 118). Pinned so the baseline model cannot drift silently — the model is folded into the
        // AI-ON scoring fingerprint by value, so a change here is a comparability event, not a detail.
        var ai = DefaultProfileAi();

        Assert.Equal("openai", ai.GetProperty("Provider").GetString());
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", ai.GetProperty("Model").GetString());

        var openAi = ai.GetProperty("OpenAi");
        Assert.Equal("https://api.deepinfra.com/v1/openai", openAi.GetProperty("BaseUrl").GetString());
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", openAi.GetProperty("Model").GetString());
        Assert.Equal("DEEPINFRA_API_KEY", openAi.GetProperty("ApiKeyEnvVar").GetString());
    }

    [Fact]
    public void DefaultProfile_DeclaresOnlyTheKeyEnvVarName_NeverAKeyValue()
    {
        // Secret hygiene (same precedent as the SEC User-Agent): the committed profile may name the environment
        // variable but must never carry an inline key. Assert there is no ApiKey-style property anywhere under
        // Radar:Ai other than the env-var NAME field.
        var ai = DefaultProfileAi();
        var openAi = ai.GetProperty("OpenAi");

        Assert.False(openAi.TryGetProperty("ApiKey", out _));
        Assert.False(ai.TryGetProperty("ApiKey", out _));
        Assert.Equal(
            JsonValueKind.Undefined,
            ai.TryGetProperty("Anthropic", out var anthropic) ? anthropic.ValueKind : JsonValueKind.Undefined);
    }
}
