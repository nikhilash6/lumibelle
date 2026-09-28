using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.DataProtection;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.Tests", Guid.NewGuid().ToString("D"));
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private FileAiSettingsStore Store => new(new StorageTestEnvironment(_root), new lumibelle.WebSecretProtector(_protection));

    [Fact]
    public async Task EightStepVideoLoraPersistsIndependentlyWithConflictsAndLegacyDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = await Store.SaveAsync(new(), "protected-key", cancellationToken: ct);
        var draft = initial with { H3 = initial.H3 with { Turbo8StepLora = "nested/" + initial.H3.Turbo8StepLora } };
        var saved = await Store.SaveAsync(draft, cancellationToken: ct);
        draft.H3.Turbo8StepLora = "later unsaved edit";
        var reopened = await Store.LoadAsync(ct);
        Assert.Equal(saved.H3.Turbo8StepLora, reopened.H3.Turbo8StepLora);
        Assert.Equal(initial.H3.TurboLora, reopened.H3.TurboLora);
        Assert.Equal("protected-key", await Store.ReadOpenRouterKeyAsync(ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveAsync(initial, cancellationToken: ct));
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        legacy["settings"]!["h3"]!.AsObject().Remove("turbo8StepLora");
        await File.WriteAllTextAsync(path, legacy.ToJsonString(), ct);
        Assert.Equal(new H3Settings().Turbo8StepLora, (await Store.LoadAsync(ct)).H3.Turbo8StepLora);
    }

    [Fact]
    public async Task KleinSettingsPersistSeparatelyAndOldFilesKeepKreaDefault()
    {
        var initial = await Store.SaveAsync(new(), "protected-key", cancellationToken: TestContext.Current.CancellationToken);
        var saved = await Store.SaveAsync(initial with { DefaultImageWorkflow = ImageWorkflow.Flux2Klein9bKv,
            FluxKleinModel = " folder/flux-2-klein-9b-kv-fp8.safetensors ", FluxKleinTextEncoder = " folder/qwen_3_8b_fp8mixed.safetensors ",
            FluxKleinVae = " folder/flux2-vae.safetensors " }, cancellationToken: TestContext.Current.CancellationToken);
        var reopened = await Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, reopened.DefaultImageWorkflow);
        Assert.Equal("folder/flux-2-klein-9b-kv-fp8.safetensors", reopened.FluxKleinModel);
        Assert.Equal("folder/qwen_3_8b_fp8mixed.safetensors", reopened.FluxKleinTextEncoder);
        Assert.Equal("folder/flux2-vae.safetensors", reopened.FluxKleinVae);
        Assert.Equal(initial.ComfyImageModel, reopened.ComfyImageModel);
        Assert.Equal("protected-key", await Store.ReadOpenRouterKeyAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveAsync(initial, cancellationToken: TestContext.Current.CancellationToken));
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        foreach (var key in new[] { "defaultImageWorkflow", "fluxKleinModel", "fluxKleinTextEncoder", "fluxKleinVae" }) legacy["settings"]!.AsObject().Remove(key);
        await File.WriteAllTextAsync(path, legacy.ToJsonString(), TestContext.Current.CancellationToken);
        var old = await Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ImageWorkflow.Krea2, old.DefaultImageWorkflow); Assert.Equal(new AiSettings().FluxKleinModel, old.FluxKleinModel);
        Assert.Equal(initial.ComfyImageModel, old.ComfyImageModel);
    }

    [Fact]
    public async Task SettingsReopenWithoutExposingKeyAndSupportReplacementAndRemoval()
    {
        var defaults = await Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(_root));
        var saved = await Store.SaveAsync(defaults with
        {
            OpenRouterModel = "test/model", ComfyImageEditLora = "  Krea2/krea2_identity_edit_v1_2.safetensors  "
        }, "private-test-key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(saved.HasOpenRouterKey);
        Assert.DoesNotContain("private-test-key", await File.ReadAllTextAsync(Path.Combine(_root, "App_Data", "ai-settings.json"), TestContext.Current.CancellationToken));
        Assert.Equal("private-test-key", await Store.ReadOpenRouterKeyAsync(TestContext.Current.CancellationToken));
        Assert.Equal("test/model", (await Store.LoadAsync(TestContext.Current.CancellationToken)).OpenRouterModel);
        Assert.Equal("Krea2/krea2_identity_edit_v1_2.safetensors",
            (await Store.LoadAsync(TestContext.Current.CancellationToken)).ComfyImageEditLora);
        var next = await Store.SaveAsync(saved, "replacement", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("replacement", await Store.ReadOpenRouterKeyAsync(TestContext.Current.CancellationToken));
        var removed = await Store.SaveAsync(next, removeKey: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(removed.HasOpenRouterKey); Assert.Null(await Store.ReadOpenRouterKeyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BrokenSettingsAndConflictsAreNotOverwritten()
    {
        var saved = await Store.SaveAsync(new(), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveAsync(new(), cancellationToken: TestContext.Current.CancellationToken));
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        await File.WriteAllTextAsync(path, "{", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("{", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnreadableCredentialCanBeReplacedWithoutDiscardingSettings()
    {
        var saved = await Store.SaveAsync(new(), "old-key", cancellationToken: TestContext.Current.CancellationToken);
        var otherAccount = new FileAiSettingsStore(new StorageTestEnvironment(_root), new lumibelle.WebSecretProtector(new EphemeralDataProtectionProvider()));
        await Assert.ThrowsAsync<AiGenerationException>(() => otherAccount.ReadOpenRouterKeyAsync(TestContext.Current.CancellationToken));
        await otherAccount.SaveAsync(saved, "new-key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new-key", await otherAccount.ReadOpenRouterKeyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TextModelVerificationsReopenWithNormalizedUrlAndNewestExactRecord()
    {
        var oldBenchmark = Benchmark(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero), 12.5);
        var newBenchmark = Benchmark(new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero), 18.25, customPrompt: true);
        var older = new ComfyTextModelVerification("HTTP://LOCALHOST:8188/", "0.34.0", "Qwen model.safetensors",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), [oldBenchmark]);
        var newest = older with { VerifiedUtc = older.VerifiedUtc.AddHours(1), Benchmarks = [newBenchmark] };

        var saved = await Store.SaveAsync(new AiSettings { ComfyTextModelVerifications = [older, newest] },
            cancellationToken: TestContext.Current.CancellationToken);
        var reopened = await Store.LoadAsync(TestContext.Current.CancellationToken);

        var verification = Assert.Single(reopened.ComfyTextModelVerifications);
        Assert.Equal("http://localhost:8188", verification.ComfyUrl);
        Assert.Equal("Qwen model.safetensors", verification.Model);
        Assert.Equal(newest.VerifiedUtc, verification.VerifiedUtc);
        Assert.Equal([newBenchmark, oldBenchmark], verification.Benchmarks);
        Assert.Contains("comfyTextModelVerifications", await File.ReadAllTextAsync(Path.Combine(_root, "App_Data", "ai-settings.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal(saved.Revision, reopened.Revision);
    }

    [Fact]
    public async Task SettingsWrittenBeforeVerificationSupportOpenWithAnEmptyCache()
    {
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "settings": { "schemaVersion": 1, "revision": 0 },
              "protectedKey": null
            }
            """, TestContext.Current.CancellationToken);

        var settings = await Store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(settings.ComfyTextModelVerifications);
        Assert.Equal("gemma4_e4b_it_fp8_scaled.safetensors", settings.ComfyModel);
        Assert.Equal("krea2_identity_edit_v1_2.safetensors", settings.ComfyImageEditLora);
    }

    [Fact]
    public async Task InvalidBenchmarkIsRejectedBeforeSettingsAreWritten()
    {
        var invalid = Benchmark(DateTimeOffset.UtcNow, double.NaN);
        var verification = new ComfyTextModelVerification("http://localhost:8188", "0.34.0", "model.safetensors",
            DateTimeOffset.UtcNow, [invalid]);

        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(
            new AiSettings { ComfyTextModelVerifications = [verification] }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("benchmark", error.Message);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task TokenBenchmarkPersistsWhenMemoryStatsAreUnavailable()
    {
        var benchmark = new ComfyTextModelBenchmark(DateTimeOffset.UtcNow,
            null, null, null, null, null, null, null,
            256, 241, 15.75, true, false);
        var verification = new ComfyTextModelVerification("http://localhost:8188", "0.34.0", "model.safetensors",
            DateTimeOffset.UtcNow, [benchmark]);

        await Store.SaveAsync(new AiSettings { ComfyTextModelVerifications = [verification] },
            cancellationToken: TestContext.Current.CancellationToken);
        var reopened = await Store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(benchmark, Assert.Single(Assert.Single(reopened.ComfyTextModelVerifications).Benchmarks!));
    }

    private static ComfyTextModelBenchmark Benchmark(DateTimeOffset measuredUtc, double tokensPerSecond, bool customPrompt = false) =>
        new(measuredUtc, "Test GPU", 0, 20L << 30, 1L << 30, 9L << 30, 256L << 20, 8L << 30,
            256, 256, tokensPerSecond, true, customPrompt);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
