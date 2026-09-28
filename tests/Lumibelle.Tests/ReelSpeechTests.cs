using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ReelSpeechTests
{
    public static IEnumerable<object[]> Passages() => ReelSpeech.Languages.SelectMany(language =>
        new[] { ReelSpeechLength.Short, ReelSpeechLength.Medium, ReelSpeechLength.Long, ReelSpeechLength.Extended }
            .Select(length => new object[] { language.Name, length }));

    [Theory]
    [MemberData(nameof(Passages))]
    public void EveryBuiltInPassageIsEditableExactDialogueNotSceneProse(string language, ReelSpeechLength length)
    {
        var draft = ReferenceReelTests.Recipe();
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterVoiceReference);
        draft.Language = language; draft.Duration = ReelSpeech.NominalSeconds(length);
        draft.Speech = new() { Length = length };
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance";
        var previousWords = draft.Line;
        var suggestion = Assert.IsType<ReelSpeechSuggestion>(ReelSpeech.Suggest(draft));
        Assert.Equal(previousWords, draft.Line); Assert.NotEmpty(suggestion.Text);
        Assert.DoesNotContain("<d>", suggestion.Text); Assert.DoesNotContain("subject_definitions:", suggestion.Text);
        Assert.True(ReelSpeech.UseSuggestion(draft, suggestion));
        Assert.Equal(suggestion.Text, draft.Line); Assert.Equal("Authored prompt", draft.Prompt);
        Assert.Equal("Authored guidance", draft.UseGuidance); Assert.Null(draft.CheckedInputs);
        var pair = ReferenceReels.Preset(draft);
        ReferenceReels.ValidatePair(pair, draft);
        Assert.Equal($"[{language}] {suggestion.Text}", Assert.Single(Regex.Matches(pair.Prompt, @"<d>(.*?)</d>", RegexOptions.Singleline).Cast<Match>()).Groups[1].Value);
        Assert.Contains(ReelSpeech.RangeDelivery, pair.Prompt);
        Assert.Contains("not a measured speech duration", pair.Prompt);
        Assert.DoesNotContain("<Audio 1>", pair.Prompt);
        Assert.Contains(language, pair.UseGuidance);
    }

    [Fact]
    public void UpstreamSupportIsNotAWhitelistAndDoesNotRewriteAliases()
    {
        Assert.Equal(11, ReelSpeech.Languages.Count(l => l.UpstreamStable));
        Assert.Equal(12, ReelSpeech.Languages.Count);
        Assert.False(ReelSpeech.FindLanguage("sv-SE")!.UpstreamStable);
        Assert.Equal("Chinese", ReelSpeech.FindLanguage("zh-CN")!.Name);
        Assert.Equal("Japanese", ReelSpeech.FindLanguage("日本語")!.Name);
        var draft = ReferenceReelTests.Recipe(); draft.Speech = new(); draft.Duration = 10;
        draft.Language = "fr-FR";
        Assert.Equal("fr-FR", ReelSpeech.Suggest(draft)!.Language);
        draft.Language = "Finnish"; draft.Line = "Siinä sinä olet. Oletko valmis? Hyvä, kokeillaan.";
        Assert.Null(ReelSpeech.Suggest(draft));
        ReferenceReels.ValidatePair(ReferenceReels.Preset(draft), draft);
        Assert.Equal("Finnish", draft.Language); Assert.StartsWith("Siinä", draft.Line);
        Assert.Contains("Experimental", ReelSpeech.SupportNotice(draft.Language));
    }

    [Theory]
    [InlineData(5, ReelSpeechLength.Short, 124)]
    [InlineData(7.9, ReelSpeechLength.Medium, 192)]
    [InlineData(8, ReelSpeechLength.Medium, 192)]
    [InlineData(10, ReelSpeechLength.Long, 243)]
    [InlineData(15, ReelSpeechLength.Extended, 362)]
    public void SuggestedLengthUsesTheActualFrameGrid(double duration, ReelSpeechLength expected, int frames)
    {
        var draft = ReferenceReelTests.Recipe(); draft.Duration = duration; draft.Speech = new();
        var suggestion = ReelSpeech.Suggest(draft)!;
        Assert.Equal(expected, suggestion.Length); Assert.Equal(frames / 24d, suggestion.GeneratedSeconds);
        Assert.Equal(duration, draft.Duration);
    }

    [Fact]
    public void ShortClipsAndUnsupportedLanguagesNeverFallBackToEnglish()
    {
        var draft = ReferenceReelTests.Recipe(); draft.Speech = new(); draft.Duration = 2;
        Assert.Null(ReelSpeech.Suggest(draft));
        draft.Duration = 10; draft.Language = "Klingon"; draft.Line = "My explicitly authored words.";
        Assert.Null(ReelSpeech.Suggest(draft)); Assert.Equal("My explicitly authored words.", draft.Line);
        draft.Language = "Swedish";
        Assert.StartsWith("Jaha", ReelSpeech.Suggest(draft)!.Text);
        Assert.Equal("My explicitly authored words.", draft.Line);
    }

    [Theory]
    [InlineData("language")] [InlineData("duration")] [InlineData("line")] [InlineData("selection")] [InlineData("silent")]
    public void AnOldPreviewCannotReplaceAChangedDraft(string change)
    {
        var draft = ReferenceReelTests.Recipe(); draft.Speech = new(); draft.Duration = 10;
        var old = ReelSpeech.Suggest(draft)!;
        switch (change)
        {
            case "language": draft.Language = "French"; break;
            case "duration": draft.Duration = 8; break;
            case "line": draft.Line = "New author text."; break;
            case "selection": draft.Speech = draft.Speech with { Length = ReelSpeechLength.Short }; break;
            case "silent": draft.VoiceMode = ReelVoiceMode.Silent; break;
        }
        var before = ReferenceReels.Fingerprint(draft);
        Assert.Throws<WorkspaceStoreException>(() => ReelSpeech.UseSuggestion(draft, old));
        Assert.Equal(before, ReferenceReels.Fingerprint(draft));
    }

    [Fact]
    public void OptionalMetadataPreservesOldFingerprintsAndNewSettingsAreDeepCopied()
    {
        var draft = ReferenceReelTests.Recipe();
        var serialized = JsonSerializer.SerializeToNode(draft, AtomicJsonFile.Options)!.AsObject();
        Assert.False(serialized.ContainsKey("speech"));
        var before = ReferenceReels.Fingerprint(draft);
        Assert.Equal(before, ReferenceReels.Fingerprint(serialized.Deserialize<ReferenceReelDraft>(AtomicJsonFile.Options)!));
        draft.Speech = new(); Assert.NotEqual(before, ReferenceReels.Fingerprint(draft));
        var copy = draft.Copy(); copy.Speech = copy.Speech! with { Length = ReelSpeechLength.Short };
        Assert.Equal(ReelSpeechLength.Automatic, draft.Speech.Length);
        Assert.NotEqual(ReferenceReels.InputsFingerprint(draft), ReferenceReels.InputsFingerprint(copy));
        draft.Speech = null; Assert.Equal(before, ReferenceReels.Fingerprint(draft));
    }

    [Fact]
    public void InvalidMetadataIsRejectedButCustomLanguageIsNot()
    {
        var draft = ReferenceReelTests.Recipe();
        foreach (var invalid in new[] { new ReelSpeechSettings { CatalogueVersion = 99 },
            new ReelSpeechSettings { Preset = (ReelSpeechPreset)999 }, new ReelSpeechSettings { Length = (ReelSpeechLength)999 } })
        {
            draft.Speech = invalid;
            Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft));
        }
        draft.Speech = new() { Preset = ReelSpeechPreset.CustomText }; draft.Language = "Finnish";
        ReferenceReels.Validate(draft);
        var environment = ReferenceReels.NewDraft(new() { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment });
        environment.Speech = new();
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(environment));
    }

    [Fact]
    public void VoicePresetIsOptInAndPreservesTextPicturesAndRememberedRecording()
    {
        var draft = ReferenceReelTests.Recipe(ReelFraming.ThreeAngles, ReelVoiceMode.ExistingRecording);
        var voice = draft.Voice; var pictures = draft.Images; var words = draft.Line;
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance";
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode);
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterVoiceReference);
        Assert.Equal(20, (int)ReelFraming.CharacterVoiceReference); Assert.Equal(19, (int)ReelFraming.CharacterFacePriority);
        Assert.Equal(10, draft.Duration); Assert.Equal(ReelVoiceMode.ExistingRecording, draft.VoiceMode);
        Assert.Same(voice, draft.Voice); Assert.Same(pictures, draft.Images); Assert.Equal(words, draft.Line);
        Assert.Equal("Authored prompt", draft.Prompt); Assert.Equal("Authored guidance", draft.UseGuidance);
        Assert.Null(draft.CaptureArticulation); Assert.Null(draft.CaptureCloseUp);
        Assert.Equal(ReelSpeechPreset.ConversationalRange, draft.Speech!.Preset);
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterFacePriority);
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode); Assert.Equal(words, draft.Line); Assert.Same(voice, draft.Voice);
    }

    [Theory]
    [InlineData(ReelVoiceMode.NewVoice)] [InlineData(ReelVoiceMode.ExistingRecording)] [InlineData(ReelVoiceMode.Silent)]
    public void ComposerKeepsEnglishProseAndExactSpokenLanguageWithIndependentVoiceIdentity(ReelVoiceMode mode)
    {
        var draft = ReferenceReelTests.Recipe(ReelFraming.ThreeAngles, ReelVoiceMode.ExistingRecording);
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterVoiceReference);
        draft.Language = "French"; draft.Line = "Tout est prêt ? Bien, prends ton temps.";
        draft.VoiceDescription = "Warm, low voice; relaxed delivery."; draft.VoiceMode = mode;
        var before = ReferenceReels.Fingerprint(draft);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, before,
            new(draft.AssetId, "Riley", "", "", null, "General", "", ""), []);
        var messages = ReferenceReels.Messages(request, []);
        using var json = JsonDocument.Parse(messages[1].Text!);
        var captured = json.RootElement.GetProperty("request").GetProperty("draft");
        var pair = ReferenceReels.Preset(draft);
        Assert.Contains("English", messages[0].Text);
        Assert.Contains("head-and-shoulders", json.RootElement.GetProperty("presetViews").GetString());
        Assert.Single(Regex.Matches(pair.Prompt, @"\[Shot \d+\]").Cast<Match>());
        if (mode == ReelVoiceMode.Silent)
        {
            Assert.False(captured.TryGetProperty("speech", out _));
            Assert.False(captured.TryGetProperty("voiceDescription", out _));
            Assert.DoesNotContain("<d>", pair.Prompt); Assert.DoesNotContain("<Audio 1>", pair.Prompt);
            Assert.DoesNotContain(ReelSpeech.RangeDelivery, pair.Prompt);
        }
        else
        {
            Assert.Equal(draft.Line, captured.GetProperty("line").GetString());
            Assert.Contains($"<d>[French] {draft.Line}</d>", pair.Prompt);
            Assert.Contains(draft.VoiceDescription, pair.Prompt);
            Assert.Equal(mode == ReelVoiceMode.ExistingRecording, pair.Prompt.Contains("<Audio 1>"));
            Assert.Contains("never translate", messages[0].Text);
        }
        Assert.Equal(before, ReferenceReels.Fingerprint(draft));
    }

    [Theory]
    [InlineData(ReelFraming.ContinuousTurn)] [InlineData(ReelFraming.CharacterCapture)]
    [InlineData(ReelFraming.CharacterSeatedToStanding)] [InlineData(ReelFraming.CharacterVoiceReference)]
    public void SpeechIsIndependentOfVisualPlanAndNeverSubstitutesCatalogueWords(ReelFraming framing)
    {
        var draft = ReferenceReelTests.Recipe(); ReferenceReels.SelectCharacterPreset(draft, framing);
        draft.VoiceMode = ReelVoiceMode.NewVoice; draft.Speech = new();
        draft.Line = "This is my own line, not a catalogue passage.";
        var pair = ReferenceReels.Preset(draft);
        Assert.Contains(ReelSpeech.RangeDelivery, pair.Prompt); Assert.Contains(draft.Line, pair.Prompt);
        Assert.DoesNotContain("Oh, there you are", pair.Prompt);
        Assert.Contains(ReferenceReels.Views(draft), pair.Prompt);
        draft.Speech = draft.Speech with { Preset = ReelSpeechPreset.CustomText };
        Assert.DoesNotContain(ReelSpeech.RangeDelivery, ReferenceReels.Preset(draft).Prompt);
        draft.VoiceMode = ReelVoiceMode.Silent;
        Assert.DoesNotContain("<d>", ReferenceReels.Preset(draft).Prompt);
    }

    [Theory]
    [InlineData(ReelVoiceMode.NewVoice)] [InlineData(ReelVoiceMode.ExistingRecording)]
    public void TenSecondSpeechReachesSingleAndSharedH3GraphsWithoutChangingSeeds(ReelVoiceMode mode)
    {
        var draft = ReferenceReelTests.Recipe(ReelFraming.ThreeAngles, mode);
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterVoiceReference);
        draft.Language = "Swedish"; ReelSpeech.UseSuggestion(draft, ReelSpeech.Suggest(draft)!);
        var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        var shot = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(Guid.NewGuid(), 0, shot, pair.Prompt, H3Policy.Fingerprint(shot), "http://comfy.test", settings,
            size.Width, size.Height, H3Policy.Frames(draft.Duration), ReferenceReels.Profile)
        {
            Reel = new(draft, new(draft.AssetId, "Riley", "", "", null, "General", "", "")), OutputPolicy = new(false),
            Preset = H3Presets.Capture(shot, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)),
            Sampling = H3Policy.Sampling(shot, settings)
        };
        var inputs = new List<AiVideoInput> { new("image.png", false, 4, new('A', 64)) };
        if (mode == ReelVoiceMode.ExistingRecording) inputs.Add(new("voice.wav", true, 4, new('B', 64)));
        var request = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, inputs); AiVideoJobPolicy.Validate(request);
        var prepared = inputs.Select(i => new PreparedVideoInput(i.FileName, i.Audio)).ToArray();
        object Build(long seed, string id) => ComfyH3Video.BuildWorkflow(snapshot, seed, id, prepared);
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), 2, 123).Candidates;
        foreach (var workflow in new[] { Build(123, "single"), ComfyMultiTakeWorkflow.Build(candidates, c => Build(c.Seed, c.Id.ToString("D")), "batch") })
        {
            var graph = JsonSerializer.SerializeToElement(workflow).GetProperty("prompt");
            var encoder = Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "MiniMaxH3ReferenceToVideo").Value.GetProperty("inputs");
            Assert.Equal(243, encoder.GetProperty("length").GetInt32()); Assert.Equal(pair.Prompt, encoder.GetProperty("prompt").GetString());
            Assert.Equal(mode == ReelVoiceMode.ExistingRecording, encoder.TryGetProperty("ref_audios.ref_audio_0", out _));
        }
        Assert.Equal(new long[] { 123, 124 }, candidates.Select(c => c.Seed));
    }
}
