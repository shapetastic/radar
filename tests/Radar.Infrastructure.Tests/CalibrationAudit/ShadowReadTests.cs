using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.CalibrationAudit;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 164: the <c>--shadow-read</c> forced-choice second pass. Everything here runs fully offline — the
/// model is a scripted <see cref="IChatClient"/> double, there is no key, no network and no SEC request.
/// </summary>
public sealed class ShadowReadTests : IDisposable
{
    // ------------------------------------------------------------------ scripted model double

    /// <summary>
    /// An offline <see cref="IChatClient"/> that answers per call from a script, captures every assembled
    /// message, and can be told to throw. Deliberately not <c>FakeChatClient</c>: the shadow tests need
    /// per-accession responses and a call counter that survives many calls.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<int, string> _responder;
        private readonly Exception? _throw;

        public ScriptedChatClient(Func<int, string> responder, Exception? throwOnCall = null)
        {
            _responder = responder;
            _throw = throwOnCall;
        }

        public ScriptedChatClient(string response, Exception? throwOnCall = null)
            : this(_ => response, throwOnCall)
        {
        }

        public int CallCount { get; private set; }

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var captured = messages.ToList();
            Calls.Add(captured);
            var index = CallCount;
            CallCount++;

            cancellationToken.ThrowIfCancellationRequested();
            if (_throw is not null)
            {
                throw _throw;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responder(index))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    // ------------------------------------------------------------------ sandbox

    private const string WorksheetHeader =
        "accession,accessionSha256,ticker,cik,companyName,outcome,signalType,direction,confidence,"
        + "strength,novelty,supportingExcerpt,reason,observedAtUtc,comparabilityPolicy,"
        + "comparabilityCapTriggering,comparabilityDiagnosticOnly,cacheVersion,modelIdentity,scopeSegment,cacheFile";

    private const string ManifestHeader =
        "accession,ticker,cik,documentFileName,documentType,exhibitUrl,fullTextSha256,fullTextLength,"
        + "modelInputSha256,modelInputLength,truncated,maxInputLength,outcome,fetchedAtUtc";

    private const string Ticker = "tick";

    private readonly List<string> _tempRoots = [];

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string LfHash(string text) => Sha256Hex(text.Replace("\r\n", "\n", StringComparison.Ordinal));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repo root (Radar.sln) above " + AppContext.BaseDirectory);
    }

    private static string CommittedPromptPath() =>
        Path.Combine(RepoRoot(), "scripts", "calibration-audit", "shadow-prompt.md");

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "radar-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    /// <summary>A study archive: worksheet + manifest + legacy-exclusions + the archived model inputs.</summary>
    private sealed class Archive
    {
        private readonly List<string> _worksheet = [];
        private readonly List<string> _manifest = [];
        private readonly List<string> _conflicts = [];

        public Archive(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
        }

        public string Root { get; }

        public void Add(string accession, string outcome, string modelInput, string manifestOutcome = "success",
            string? overrideSha = null, int? overrideLength = null, string sealedDirection = "", string sealedConfidence = "")
        {
            _worksheet.Add(string.Join(",",
                accession, Radar.CalibrationAudit.AccessionHash.HexOf(accession), Ticker, "123", "TestCo",
                outcome, outcome == "DirectionalSignalProduced" ? "GuidanceChange" : string.Empty,
                sealedDirection, sealedConfidence, "2", "0.5", "", "",
                "2026-07-01T00:00:00.0000000Z", "cmpscan-v2", "", "", "3",
                "openai:test-model", "test-scope", "cache.json"));

            _manifest.Add(string.Join(",",
                accession, Ticker, "123", "ex991.htm", "EX-99.1", "https://example.test/ex991.htm",
                Sha256Hex("full:" + accession), modelInput.Length.ToString(),
                overrideSha ?? Sha256Hex(modelInput), (overrideLength ?? modelInput.Length).ToString(),
                "false", "12000", manifestOutcome, "2026-07-01T00:00:00.0000000Z"));

            var path = ExhibitArchiverModelInputPath(Root, accession);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, modelInput);
        }

        public void AddConflict(string accession) => _conflicts.Add(accession);

        public void Write()
        {
            File.WriteAllText(Path.Combine(Root, "worksheet.csv"),
                WorksheetHeader + "\n" + string.Join("\n", _worksheet) + "\n");
            File.WriteAllText(Path.Combine(Root, "exhibit-manifest.csv"),
                ManifestHeader + "\n" + string.Join("\n", _manifest) + "\n");

            var conflicts = new StringBuilder();
            conflicts.AppendLine("accession,reason,legacyOutcome,activeOutcome,outcomeConflict,cacheFile");
            foreach (var accession in _conflicts)
            {
                conflicts.AppendLine($"{accession},legacy-scope,DirectionalSignalProduced,NoDirectionalSignal,true,x.json");
            }

            File.WriteAllText(Path.Combine(Root, "legacy-exclusions.csv"), conflicts.ToString());
        }
    }

    /// <summary>Mirrors <c>ExhibitArchiver.ModelInputPath</c>'s layout (the archive the console wrote).</summary>
    private static string ExhibitArchiverModelInputPath(string root, string accession) =>
        Path.Combine(root, "exhibits-model-input", $"{Ticker}-{accession}.txt");

    private static ShadowReadOptions Options(string exhibitRoot, string outputRoot, bool fresh = false) => new()
    {
        ExhibitRoot = exhibitRoot,
        OutputRoot = outputRoot,
        Fresh = fresh,
        // The fixtures are small on purpose: cohort-count drift is REPORTED, never fatal, so the counts are
        // set to whatever the fixture holds rather than the live 145/153.
        ExpectedDirectional = 0,
        ExpectedNoSignal = 0,
        ExpectedOutcomeConflicts = [],
    };

    private static ShadowReadRunner Runner(IChatClient client, string? promptPath = null) =>
        new(client, ShadowPrompt.Load(promptPath ?? CommittedPromptPath()), "openai:test-model",
            NullLogger<ShadowReadTests>.Instance);

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Response(string direction, decimal confidence = 0.85m, string rationale = "reported revenue grew") =>
        $$"""{"direction":"{{direction}}","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"rationale":"{{rationale}}"}""";

    // ------------------------------------------------------------------ composition: cache isolation

    [Fact]
    public void ShadowComposition_RegistersTheAiSeamAndNothingElse_NoAnalyzedFilingCache()
    {
        var services = ShadowReadRunner.BuildShadowServices(new AiClientOptions
        {
            Provider = "openai",
            Model = "deepseek-ai/DeepSeek-V4-Flash",
            OpenAiBaseUrl = "https://api.deepinfra.test/v1/openai",
            OpenAiApiKey = "not-a-real-key",
        });

        // The AI seam IS registered (the pass calls the model through the production registration).
        Assert.Contains(services, d => d.ServiceType == typeof(IChatClient));

        // ⚠ The production model-scoped analyzed-filing cache is NOT — a shadow read that landed there would
        // be served to the next LIVE baseline run as a cached production read.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAnalyzedFilingCache));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(FileAnalyzedFilingCacheOptions));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IFilingAnalyzer));

        // No type whose name mentions a cache, store, repository, collector or pipeline is registered at all.
        foreach (var descriptor in services)
        {
            var name = descriptor.ServiceType.Name;
            Assert.DoesNotContain("AnalyzedFilingCache", name, StringComparison.Ordinal);
            Assert.DoesNotContain("EvidenceCollector", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Repository", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Pipeline", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ShadowRun_WritesOnlyUnderTheShadowDirectory_AndNeverTouchesTheFilingsCache()
    {
        var root = NewTempRoot();

        // A production data root with a populated filings-cache, exactly as a live install has.
        var cacheRoot = Path.Combine(root, "data", "filings-cache", "openai-deepseek-scope");
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(cacheRoot, "0000000000-26-000001.json"), "{\"cacheVersion\":3}");

        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "Q3 results: revenue grew 40%.");
        archive.Add("0000000001-26-000001", "DirectionalSignalProduced", "Q3 results: guidance cut.",
            sealedDirection: "Positive", sealedConfidence: "0.85");
        archive.Write();

        var outputRoot = Path.Combine(root, "shadow-out");
        var before = SnapshotTree(root);

        var client = new ScriptedChatClient(Response("Improving"));
        var summary = await Runner(client).RunAsync(
            Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);

        Assert.Equal(2, summary.Ok);

        var after = SnapshotTree(root);
        var shadowDir = Path.Combine(outputRoot, "shadow");

        // Nothing that existed before changed, and every NEW path is inside {output-root}/shadow/.
        foreach (var (path, hash) in before)
        {
            Assert.True(after.ContainsKey(path), $"'{path}' disappeared during a read-only shadow run.");
            Assert.Equal(hash, after[path]);
        }

        foreach (var path in after.Keys.Except(before.Keys))
        {
            Assert.StartsWith(shadowDir + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        }

        // Explicitly: the cache directory holds exactly the one file it started with.
        Assert.Equal(
            new[] { "0000000000-26-000001.json" },
            Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
    }

    private static Dictionary<string, string> SnapshotTree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(static f => f, static f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    [Fact]
    public async Task OutputRootInsideTheExhibitRoot_IsRefused()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "text");
        archive.Write();

        var client = new ScriptedChatClient(Response("Neutral"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(archive.Root, "nested")), TimeProvider.System, CancellationToken.None));

        Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    // ------------------------------------------------------------------ manifest verification

    [Fact]
    public async Task TamperedModelInput_FailsNamingTheFile_BeforeAnyModelCall()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "clean input");
        archive.Add("0000000000-26-000002", "NoDirectionalSignal", "tampered input");
        archive.Write();

        // Tamper AFTER the manifest recorded the original hash/length.
        var tamperedPath = ExhibitArchiverModelInputPath(archive.Root, "0000000000-26-000002");
        File.WriteAllText(tamperedPath, "tampered input!!");

        var client = new ScriptedChatClient(Response("Improving"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(root, "out")), TimeProvider.System, CancellationToken.None));

        Assert.Contains(tamperedPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("0000000000-26-000002", ex.Message, StringComparison.Ordinal);

        // BEFORE any model call: not even the clean row was read.
        Assert.Equal(0, client.CallCount);
        Assert.False(Directory.Exists(Path.Combine(root, "out", "shadow")));
    }

    [Fact]
    public async Task ModelInputLengthMismatch_FailsNamingTheFile()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "some input", overrideLength: 999);
        archive.Write();

        var client = new ScriptedChatClient(Response("Improving"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(root, "out")), TimeProvider.System, CancellationToken.None));

        Assert.Contains("modelInputLength", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task NonSuccessManifestRows_AreReportedNeverRead()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "readable input");
        archive.Add("0000000000-26-000002", "NoDirectionalSignal", "short", manifestOutcome: "failed:short-body");
        archive.Write();

        var client = new ScriptedChatClient(Response("Neutral"));
        var summary = await Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(root, "out")), TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, summary.Candidates);
        Assert.Equal(1, summary.NotReadable);
        Assert.Contains(summary.NotReadableDetail, d => d.Contains("0000000000-26-000002", StringComparison.Ordinal));
        Assert.Equal(1, client.CallCount);
    }

    // ------------------------------------------------------------------ the shadow response contract

    [Fact]
    public async Task NeutralResponse_RoundTripsAsNeutral_NeverUnknownOrZero()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "boilerplate announcement");
        archive.Write();

        var outputRoot = Path.Combine(root, "out");
        var client = new ScriptedChatClient(Response("Neutral", 0.77m, "the release reports no results"));
        await Runner(client).RunAsync(Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);

        var record = ShadowRecordStore.TryRead(outputRoot, "0000000000-26-000001");
        Assert.NotNull(record);
        Assert.Equal("ok", record!.Status);
        Assert.Equal("Neutral", record.Direction);   // NOT "Unknown"
        Assert.Equal(0.77m, record.Confidence);      // NOT 0
        Assert.Equal("the release reports no results", record.Rationale);
        Assert.Equal(ShadowCohort.NoSignal, record.Cohort);
    }

    [Theory]
    [InlineData("Improving")]
    [InlineData("Deteriorating")]
    [InlineData("Mixed")]
    [InlineData("Neutral")]
    public void AllFourForcedChoiceTokens_Parse(string token)
    {
        Assert.True(ShadowFilingSentimentParser.TryParse(
            new ShadowFilingSentimentResponse { Direction = token, Confidence = 0.5m, Rationale = "x" },
            "{}", out var sentiment, out _));
        Assert.Equal(token, sentiment.Direction.ToString());
    }

    [Theory]
    [InlineData("Unknown")]     // The production abstain token has no meaning here.
    [InlineData("")]
    [InlineData("0")]           // Numeric text must not parse as the first enum member.
    [InlineData("improvingish")]
    public void UnrecognisedDirectionToken_IsAParseFailure_NeverADirection(string token)
    {
        Assert.False(ShadowFilingSentimentParser.TryParse(
            new ShadowFilingSentimentResponse { Direction = token, Confidence = 0.9m, Rationale = "x" },
            "{}", out _, out var reason));
        Assert.NotEqual(string.Empty, reason);
    }

    [Fact]
    public void Confidence_IsClamped_AndAdviceLanguageIsScrubbed()
    {
        Assert.True(ShadowFilingSentimentParser.TryParse(
            new ShadowFilingSentimentResponse { Direction = "Improving", Confidence = 1.9m, Rationale = "x" },
            "{}", out var high, out _));
        Assert.Equal(1m, high.Confidence);

        Assert.True(ShadowFilingSentimentParser.TryParse(
            new ShadowFilingSentimentResponse { Direction = "Improving", Confidence = -0.4m, Rationale = "x" },
            "{}", out var low, out _));
        Assert.Equal(0m, low.Confidence);

        // The SHARED AdviceLanguageGuard: Radar never surfaces advice language, research artifact included.
        Assert.True(ShadowFilingSentimentParser.TryParse(
            new ShadowFilingSentimentResponse { Direction = "Improving", Confidence = 0.8m, Rationale = "a clear buy here" },
            "{}", out var advice, out _));
        Assert.Equal(string.Empty, advice.Rationale);
        Assert.Equal(ShadowFilingDirection.Improving, advice.Direction);
    }

    [Fact]
    public async Task UnparseableResponse_YieldsParseFailed_WithNoDirectionAndTheRawText()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "some input");
        archive.Write();

        var outputRoot = Path.Combine(root, "out");
        var client = new ScriptedChatClient("I cannot answer that.");
        var summary = await Runner(client).RunAsync(Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, summary.ParseFailed);
        Assert.Equal(0, summary.Ok);

        var record = ShadowRecordStore.TryRead(outputRoot, "0000000000-26-000001");
        Assert.NotNull(record);
        Assert.Equal("parse-failed", record!.Status);
        Assert.Null(record.Direction);
        Assert.Null(record.Confidence);
        Assert.False(string.IsNullOrEmpty(record.Error));
        Assert.Contains("I cannot answer that.", record.RawResponse ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelCallFailure_YieldsCallFailed_NeverADirection()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "some input");
        archive.Write();

        var outputRoot = Path.Combine(root, "out");
        var client = new ScriptedChatClient("{}", throwOnCall: new InvalidOperationException("provider 503"));
        var summary = await Runner(client).RunAsync(Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, summary.CallFailed);

        var record = ShadowRecordStore.TryRead(outputRoot, "0000000000-26-000001");
        Assert.NotNull(record);
        Assert.Equal("call-failed", record!.Status);
        Assert.Null(record.Direction);
        Assert.Null(record.Confidence);
        Assert.Contains("provider 503", record.Error ?? string.Empty, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ prompt provenance

    [Fact]
    public void CommittedPrompt_IsForcedChoice_AndCarriesNoAbstainPath()
    {
        var prompt = ShadowPrompt.Load(CommittedPromptPath());

        foreach (var token in new[] { "Improving", "Deteriorating", "Mixed", "Neutral" })
        {
            Assert.Contains(token, prompt.Instruction, StringComparison.Ordinal);
        }

        Assert.Contains("no abstain option", prompt.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT investment advice", prompt.Instruction, StringComparison.Ordinal);
        Assert.Contains("AS REPORTED", prompt.Instruction, StringComparison.Ordinal);
        Assert.Contains("beat-vs-consensus", prompt.Instruction, StringComparison.Ordinal);
        Assert.Contains("cash burn", prompt.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssembledInstruction_EqualsTheCommittedPromptBytes_AndItsHashIsWhatRecordsCarry()
    {
        var promptPath = CommittedPromptPath();
        var canonical = Encoding.UTF8.GetString(File.ReadAllBytes(promptPath)).Replace("\r\n", "\n", StringComparison.Ordinal);

        var prompt = ShadowPrompt.Load(promptPath);
        Assert.Equal(canonical, prompt.Instruction, StringComparer.Ordinal);
        Assert.Equal(LfHash(canonical), prompt.Sha256);

        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "some input text");
        archive.Write();

        var outputRoot = Path.Combine(root, "out");
        var client = new ScriptedChatClient(Response("Improving"));
        await Runner(client, promptPath).RunAsync(Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);

        // The system message actually sent is byte-for-byte the canonicalized committed file...
        var system = client.Calls.Single().Single(m => m.Role == ChatRole.System);
        Assert.Equal(canonical, system.Text, StringComparer.Ordinal);

        // ...and it REPLACED the production instruction rather than being appended to it.
        Assert.DoesNotContain("or Unknown.", system.Text, StringComparison.Ordinal);
        Assert.NotEqual(ChatFilingAnalyzer.SystemInstruction, system.Text);

        // ...and the recorded hash is a hash of exactly those bytes.
        var record = ShadowRecordStore.TryRead(outputRoot, "0000000000-26-000001");
        Assert.NotNull(record);
        Assert.Equal(Sha256Hex(system.Text), record!.PromptSha256);
        Assert.Equal(prompt.Sha256, record.PromptSha256);
        Assert.Equal("cal-shadow-v1", record.PromptVersion);
    }

    [Fact]
    public async Task ArchivedModelInput_IsSentVerbatim_NeverReTruncated()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        var input = new string('A', 12000) + "TAIL"; // Longer than the recorded 12000 cap.
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", input);
        archive.Write();

        var client = new ScriptedChatClient(Response("Improving"));
        await Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(root, "out")), TimeProvider.System, CancellationToken.None);

        var user = client.Calls.Single().Single(m => m.Role == ChatRole.User);
        Assert.Equal(input, user.Text, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ re-runnability

    [Fact]
    public async Task Rerun_SkipsOkRecords_RetriesFailures_AndFreshOverwritesEverything()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "input one");
        archive.Add("0000000000-26-000002", "NoDirectionalSignal", "input two");
        archive.Write();

        var outputRoot = Path.Combine(root, "out");
        var ordered = new[] { "0000000000-26-000001", "0000000000-26-000002" }
            .OrderBy(Radar.CalibrationAudit.AccessionHash.HexOf, StringComparer.Ordinal)
            .ToList();

        // Run 1: the FIRST accession in hash order parses, the second does not.
        var run1 = new ScriptedChatClient(i => i == 0 ? Response("Improving") : "not json");
        var first = await Runner(run1).RunAsync(Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);
        Assert.Equal(2, run1.CallCount);
        Assert.Equal(1, first.Ok);
        Assert.Equal(1, first.ParseFailed);
        Assert.Equal("ok", ShadowRecordStore.TryRead(outputRoot, ordered[0])!.Status);
        Assert.Equal("parse-failed", ShadowRecordStore.TryRead(outputRoot, ordered[1])!.Status);

        // Run 2: the ok record is SKIPPED, the failure is RETRIED (exactly one call).
        var run2 = new ScriptedChatClient(Response("Deteriorating", 0.6m));
        var second = await Runner(run2).RunAsync(Options(archive.Root, outputRoot), TimeProvider.System, CancellationToken.None);
        Assert.Equal(1, run2.CallCount);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, second.Ok);
        Assert.Equal("Improving", ShadowRecordStore.TryRead(outputRoot, ordered[0])!.Direction);
        Assert.Equal("Deteriorating", ShadowRecordStore.TryRead(outputRoot, ordered[1])!.Direction);

        // Run 3: --fresh re-reads BOTH and overwrites.
        var run3 = new ScriptedChatClient(Response("Mixed", 0.55m));
        var third = await Runner(run3).RunAsync(Options(archive.Root, outputRoot, fresh: true), TimeProvider.System, CancellationToken.None);
        Assert.Equal(2, run3.CallCount);
        Assert.Equal(0, third.Skipped);
        Assert.Equal("Mixed", ShadowRecordStore.TryRead(outputRoot, ordered[0])!.Direction);
        Assert.Equal("Mixed", ShadowRecordStore.TryRead(outputRoot, ordered[1])!.Direction);

        // The summary CSV covers every record, in SHA-256(accession) order.
        var csv = File.ReadAllLines(ShadowRecordStore.SummaryPath(outputRoot));
        Assert.Equal(3, csv.Length);
        Assert.StartsWith("accession,cohort,status,", csv[0], StringComparison.Ordinal);
        Assert.StartsWith(ordered[0], csv[1], StringComparison.Ordinal);
        Assert.StartsWith(ordered[1], csv[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxReads_CapsThisRun_AndTheRestResumeNextRun()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "input one");
        archive.Add("0000000000-26-000002", "NoDirectionalSignal", "input two");
        archive.Write();

        var outputRoot = Path.Combine(root, "out");
        var client = new ScriptedChatClient(Response("Improving"));
        var options = Options(archive.Root, outputRoot) with { MaxReads = 1 };

        var summary = await Runner(client).RunAsync(options, TimeProvider.System, CancellationToken.None);
        Assert.Equal(1, summary.Attempted);
        Assert.Equal(1, client.CallCount);

        var resumed = await Runner(client).RunAsync(options, TimeProvider.System, CancellationToken.None);
        Assert.Equal(1, resumed.Attempted);
        Assert.Equal(1, resumed.Skipped);
        Assert.Equal(2, client.CallCount);
    }

    // ------------------------------------------------------------------ re-assertions

    [Fact]
    public async Task CohortCountsAndOutcomeConflicts_AreReAsserted_AndDriftIsReported()
    {
        var root = NewTempRoot();
        var archive = new Archive(Path.Combine(root, "calibration-audit"));
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "input one");
        archive.Add("0000000001-26-000001", "DirectionalSignalProduced", "input two",
            sealedDirection: "Positive", sealedConfidence: "0.9");
        archive.AddConflict("0000000009-26-000009");
        archive.Write();

        var client = new ScriptedChatClient(Response("Improving"));

        // Holds: the expectations match the fixture exactly.
        var holds = await Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(root, "out-a")) with
            {
                ExpectedDirectional = 1,
                ExpectedNoSignal = 1,
                ExpectedOutcomeConflicts = ["0000000009-26-000009"],
            },
            TimeProvider.System, CancellationToken.None);
        Assert.Empty(holds.Drift);

        // Drifts: both re-assertions fail and BOTH are reported (never silently assumed).
        var drifts = await Runner(client).RunAsync(
            Options(archive.Root, Path.Combine(root, "out-b")) with
            {
                ExpectedDirectional = 145,
                ExpectedNoSignal = 153,
                ExpectedOutcomeConflicts = ["0001628280-26-048253", "0001654954-26-006655"],
            },
            TimeProvider.System, CancellationToken.None);

        Assert.Contains(drifts.Drift, d => d.Contains("COHORT COUNT DRIFT", StringComparison.Ordinal));
        Assert.Contains(drifts.Drift, d => d.Contains("OUTCOME-CONFLICT DRIFT", StringComparison.Ordinal));
        Assert.Contains("0001628280-26-048253", drifts.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CohortToken_ComesFromTheSealedWorksheetOutcome()
    {
        var root = NewTempRoot();
        var archive = new Archive(root);
        archive.Add("0000000000-26-000001", "NoDirectionalSignal", "a");
        archive.Add("0000000001-26-000001", "DirectionalSignalProduced", "b", sealedDirection: "Positive", sealedConfidence: "0.9");
        archive.Write();

        var rows = StudyWorksheetReader.Read(Path.Combine(root, "worksheet.csv"));

        Assert.Equal(2, rows.Count);
        Assert.Equal(ShadowCohort.NoSignal, rows.Single(r => r.Accession == "0000000000-26-000001").Cohort);
        Assert.Equal(ShadowCohort.Directional, rows.Single(r => r.Accession == "0000000001-26-000001").Cohort);
    }

    [Fact]
    public void ShadowRecord_SerializesEveryProvenanceField()
    {
        var root = NewTempRoot();
        var record = new ShadowRecord
        {
            Accession = "0000000000-26-000001",
            Cohort = ShadowCohort.NoSignal,
            Status = ShadowStatus.Ok,
            Direction = "Neutral",
            Confidence = 0.42m,
            Rationale = "no reported results",
            RawResponse = "{}",
            Error = null,
            PromptVersion = ShadowPrompt.Version,
            PromptSha256 = "abc",
            ModelIdentity = "openai:test-model",
            ModelInputSha256 = "def",
            ReadAtUtc = "2026-07-31T00:00:00.0000000Z",
        };

        ShadowRecordStore.Write(root, record);

        using var doc = JsonDocument.Parse(File.ReadAllText(ShadowRecordStore.PathFor(root, record.Accession)));
        foreach (var field in new[]
                 {
                     "accession", "cohort", "status", "direction", "confidence", "rationale", "rawResponse",
                     "error", "promptVersion", "promptSha256", "modelIdentity", "modelInputSha256", "readAtUtc",
                 })
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"shadow record is missing '{field}'.");
        }

        Assert.Equal(record, ShadowRecordStore.TryRead(root, record.Accession));
    }
}
