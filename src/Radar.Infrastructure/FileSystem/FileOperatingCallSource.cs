using System.Globalization;
using System.Text.Json;

using Radar.Application.Lifecycle;

namespace Radar.Infrastructure.FileSystem;

/// <summary>The operating-calls file location (spec 184 §2). Resolved by the composition root.</summary>
public sealed record FileOperatingCallSourceOptions(string FilePath);

/// <summary>
/// Reads the committed <c>data/strategy-operating-calls.json</c> (spec 184 §2) — the ONLY runtime input to
/// call resolution. An ABSENT file returns <c>null</c> (an undeclared call layer is a stated report
/// condition, not an error). A PRESENT file is parsed STRICTLY: unknown properties, unknown tokens, a wrong
/// schema version, missing required fields and unparseable timestamps all throw an
/// <see cref="InvalidOperationException"/> naming the file and the violated rule — a typo'd call silently
/// read as "no calls" would hand reader-facing prominence out by accident, the exact fail-open shape
/// specs 174/176 exist to prevent.
/// <para>
/// <b>Two schema versions (spec 186 §3).</b> <c>strategy-operating-calls-v2</c> is current and adds
/// <c>overridesVerdictId</c> — REQUIRED whenever <c>overridesGate</c> is true, because an override now
/// binds to the verdict it overrides BY NAME rather than by post-dating its (filesystem-mtime) instant.
/// <c>strategy-operating-calls-v1</c> stays readable and behaves exactly as before WITHOUT overrides; it
/// simply cannot express one, so a v1 file declaring <c>overridesGate: true</c> fails validation naming the
/// remedy, and <c>overridesVerdictId</c> in a v1 file is an unknown property.
/// </para>
/// </summary>
public sealed class FileOperatingCallSource : IOperatingCallSource
{
    /// <summary>
    /// The LEGACY schema (spec 184). Still readable, but it cannot EXPRESS a gate override: the pre-186
    /// override rule was "the call post-dates the verdict", which spec 186 §3 replaced with identity
    /// binding. A v1 file carrying <c>overridesGate: true</c> is therefore rejected, naming the remedy.
    /// </summary>
    public const string LegacySchemaVersion = "strategy-operating-calls-v1";

    /// <summary>
    /// The CURRENT schema (spec 186 §3): identical to v1 except that a call may carry
    /// <c>overridesVerdictId</c>, which is REQUIRED whenever <c>overridesGate</c> is true.
    /// </summary>
    public const string SupportedSchemaVersion = "strategy-operating-calls-v2";

    /// <summary>Every accepted schema version, newest first (both are read; only v2 can express an override).</summary>
    public static IReadOnlyList<string> AcceptedSchemaVersions { get; } =
    [
        SupportedSchemaVersion,
        LegacySchemaVersion,
    ];

    private readonly FileOperatingCallSourceOptions _options;

    public FileOperatingCallSource(FileOperatingCallSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FilePath);
        _options = options;
    }

    public async Task<StrategyOperatingCallsFile?> ReadAsync(CancellationToken ct)
    {
        var path = _options.FilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            await using var stream = File.OpenRead(path);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Fail(path, "the file exists but could not be read or parsed as JSON — fix or remove it; "
                + "an unreadable calls file must never silently read as \"no calls\"", ex);
        }

        using (document)
        {
            return Parse(path, document.RootElement);
        }
    }

    private static StrategyOperatingCallsFile Parse(string path, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Fail(path, "the root must be a JSON object");
        }

        // The schema version is resolved BEFORE the calls, in its own pass: which properties a call may
        // carry depends on it, and JSON object order is not a contract (spec 186 §3).
        var schemaVersion = ResolveSchemaVersion(path, root);
        var version = string.Equals(schemaVersion, SupportedSchemaVersion, StringComparison.Ordinal)
            ? CallsSchema.V2
            : CallsSchema.V1;

        var stopAll = false;
        var calls = new List<StrategyOperatingCall>();

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "schemaVersion":
                    break; // already resolved and validated above
                case "globalCall":
                    var token = RequireString(path, property, "globalCall");
                    if (!string.Equals(token, "StopAll", StringComparison.Ordinal))
                    {
                        throw Fail(path, $"globalCall carries unknown token '{token}' — the only valid "
                            + "global call is 'StopAll'");
                    }

                    stopAll = true;
                    break;
                case "calls":
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw Fail(path, "'calls' must be an array of call objects");
                    }

                    var index = 0;
                    foreach (var element in property.Value.EnumerateArray())
                    {
                        calls.Add(ParseCall(path, element, index, version));
                        index++;
                    }

                    break;
                default:
                    throw Fail(path, $"unknown property '{property.Name}' at the root — the schema allows "
                        + "schemaVersion, globalCall and calls only (a typo'd property would silently "
                        + "change which call wins)");
            }
        }

        return new StrategyOperatingCallsFile(path, schemaVersion, stopAll, calls);
    }

    /// <summary>The two accepted schema shapes; only <see cref="V2"/> can express a gate override.</summary>
    private enum CallsSchema
    {
        V1,
        V2,
    }

    private static string ResolveSchemaVersion(string path, JsonElement root)
    {
        string? schemaVersion = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
            {
                schemaVersion = RequireString(path, property, "schemaVersion");
            }
        }

        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            throw Fail(path, "schemaVersion is missing");
        }

        if (!AcceptedSchemaVersions.Contains(schemaVersion, StringComparer.Ordinal))
        {
            throw Fail(path, $"schemaVersion '{schemaVersion}' is not supported — this reader understands "
                + string.Join(" and ", AcceptedSchemaVersions.Select(v => $"'{v}'")) + " only");
        }

        return schemaVersion;
    }

    private static StrategyOperatingCall ParseCall(
        string path, JsonElement element, int index, CallsSchema schema)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Fail(path, $"calls[{index}] must be an object");
        }

        string? strategy = null;
        OperatingCall? call = null;
        DateTimeOffset? asOfUtc = null;
        string? basis = null;
        OperatingCallActor? actor = null;
        var overridesGate = false;
        string? overridesVerdictId = null;
        DateTimeOffset? reviewByUtc = null;
        string? resolutionRule = null;
        OperatingCallResolution? resolution = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "strategy":
                    strategy = RequireString(path, property, $"calls[{index}].strategy");
                    break;
                case "call":
                    call = ParseCallToken(path, RequireString(path, property, $"calls[{index}].call"), index);
                    break;
                case "asOfUtc":
                    asOfUtc = ParseUtc(path, property, $"calls[{index}].asOfUtc");
                    break;
                case "basis":
                    basis = RequireString(path, property, $"calls[{index}].basis");
                    break;
                case "actor":
                    var actorToken = RequireString(path, property, $"calls[{index}].actor");
                    actor = actorToken switch
                    {
                        "human" => OperatingCallActor.Human,
                        "gate-default" => OperatingCallActor.GateDefault,
                        _ => throw Fail(path, $"calls[{index}].actor carries unknown token '{actorToken}' — "
                            + "valid actors: human, gate-default"),
                    };
                    break;
                case "overridesGate":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw Fail(path, $"calls[{index}].overridesGate must be a boolean");
                    }

                    overridesGate = property.Value.GetBoolean();
                    break;
                case "overridesVerdictId" when schema == CallsSchema.V2:
                    overridesVerdictId = RequireString(
                        path, property, $"calls[{index}].overridesVerdictId");
                    break;
                case "reviewByUtc":
                    reviewByUtc = ParseUtc(path, property, $"calls[{index}].reviewByUtc");
                    break;
                case "resolutionRule":
                    resolutionRule = RequireString(path, property, $"calls[{index}].resolutionRule");
                    break;
                case "resolution":
                    resolution = ParseResolution(path, property.Value, index);
                    break;
                default:
                    // `overridesVerdictId` is a v2-only property, so in a v1 file it falls through to here
                    // and is rejected as unknown — exactly the strict rule every other stray key meets.
                    throw Fail(path, $"calls[{index}] carries unknown property '{property.Name}' — the "
                        + "schema allows strategy, call, asOfUtc, basis, actor, overridesGate, reviewByUtc, "
                        + "resolutionRule and resolution"
                        + (schema == CallsSchema.V2 ? ", and overridesVerdictId only" : " only"));
            }
        }

        if (strategy is null || call is null || asOfUtc is null || basis is null || actor is null
            || reviewByUtc is null)
        {
            throw Fail(path, $"calls[{index}] is missing a required field — every call needs strategy, "
                + "call, asOfUtc, basis, actor and reviewByUtc");
        }

        // Spec 186 §3. An override BINDS to the verdict it overrides, by name — v1 cannot express that
        // binding (its rule was the deleted "the call post-dates the verdict"), and in v2 the binding is
        // mandatory. Both failures name the file and the remedy; an unbound override would be an override
        // that quietly applies to whatever verdict happens to be on disk next.
        if (overridesGate && schema == CallsSchema.V1)
        {
            throw Fail(path, $"calls[{index}] ('{strategy}') declares overridesGate: true under "
                + $"schemaVersion '{LegacySchemaVersion}', which cannot express which verdict is being "
                + $"overridden — migrate to {SupportedSchemaVersion} and bind the override to a verdict id "
                + "(the gateVerdictId column of data/efficacy/strategy-paired-comparison.csv, also stated "
                + "in the .md artifact)");
        }

        if (overridesGate && overridesVerdictId is null)
        {
            throw Fail(path, $"calls[{index}] ('{strategy}') declares overridesGate: true without "
                + "overridesVerdictId — an override binds to the verdict it overrides BY NAME, never by "
                + "timestamp; set overridesVerdictId to the gateVerdictId of the verdict being overridden "
                + "(data/efficacy/strategy-paired-comparison.csv)");
        }

        if (!overridesGate && overridesVerdictId is not null)
        {
            throw Fail(path, $"calls[{index}] ('{strategy}') carries overridesVerdictId without "
                + "overridesGate: true — a bound verdict id that overrides nothing reads as an override and "
                + "is not one; declare overridesGate: true, or remove the binding");
        }

        return new StrategyOperatingCall(
            strategy, call.Value, asOfUtc.Value, basis, actor.Value, overridesGate, reviewByUtc.Value,
            resolutionRule, resolution, overridesVerdictId);
    }

    private static OperatingCallResolution ParseResolution(string path, JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Fail(path, $"calls[{index}].resolution must be an object");
        }

        OperatingCallOutcome? outcome = null;
        DateTimeOffset? resolvedAtUtc = null;
        string? evidenceRef = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "outcome":
                    var token = RequireString(path, property, $"calls[{index}].resolution.outcome");
                    outcome = token switch
                    {
                        "Right" => OperatingCallOutcome.Right,
                        "Wrong" => OperatingCallOutcome.Wrong,
                        "Unresolved" => OperatingCallOutcome.Unresolved,
                        _ => throw Fail(path, $"calls[{index}].resolution.outcome carries unknown token "
                            + $"'{token}' — valid outcomes: Right, Wrong, Unresolved"),
                    };
                    break;
                case "resolvedAtUtc":
                    resolvedAtUtc = ParseUtc(path, property, $"calls[{index}].resolution.resolvedAtUtc");
                    break;
                case "evidenceRef":
                    evidenceRef = RequireString(path, property, $"calls[{index}].resolution.evidenceRef");
                    break;
                default:
                    throw Fail(path, $"calls[{index}].resolution carries unknown property "
                        + $"'{property.Name}' — the schema allows outcome, resolvedAtUtc and evidenceRef only");
            }
        }

        if (outcome is null || resolvedAtUtc is null || evidenceRef is null)
        {
            throw Fail(path, $"calls[{index}].resolution is missing a required field — a resolution needs "
                + "outcome, resolvedAtUtc and evidenceRef (a judgement without an evidence reference is an "
                + "assertion, not a record)");
        }

        return new OperatingCallResolution(outcome.Value, resolvedAtUtc.Value, evidenceRef);
    }

    private static OperatingCall ParseCallToken(string path, string token, int index) => token switch
    {
        "Lead" => OperatingCall.Lead,
        "Trial" => OperatingCall.Trial,
        "DoNotLead" => OperatingCall.DoNotLead,
        "Stop" => OperatingCall.Stop,
        _ => throw Fail(path, $"calls[{index}].call carries unknown token '{token}' — valid calls: Lead, "
            + "Trial, DoNotLead, Stop (StopAll is a globalCall, never a per-strategy call)"),
    };

    private static string RequireString(string path, JsonProperty property, string field)
    {
        if (property.Value.ValueKind != JsonValueKind.String
            || property.Value.GetString() is not { } value
            || string.IsNullOrWhiteSpace(value))
        {
            throw Fail(path, $"{field} must be a non-empty string");
        }

        return value;
    }

    private static DateTimeOffset ParseUtc(string path, JsonProperty property, string field)
    {
        var text = RequireString(path, property, field);
        if (!DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value))
        {
            throw Fail(path, $"{field} '{text}' is not a parseable UTC instant");
        }

        return value;
    }

    private static InvalidOperationException Fail(string path, string rule, Exception? inner = null) =>
        new($"Operating-calls file '{path}': {rule}.", inner);
}
