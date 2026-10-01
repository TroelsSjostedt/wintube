using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class SubtitleSelectionTests
{
    [Fact]
    public void Choose_PrefersManualOverAsr_AndMatchesRegionalVariants()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en&caps=asr", "en", "English (auto)", true),
            new("https://t?lang=en-US", "en-US", "English (US)", false),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.False(choice!.IsTranslation);
        Assert.Equal("en-US", choice.Language);
        Assert.Equal("https://t?lang=en-US&fmt=vtt", choice.Url);
    }

    [Fact]
    public void Choose_FallsBackToTranslation_OnlyForEnglishPreference()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=ja", "ja", "Japanese", false) };
        var en = SubtitleSelection.Choose(tracks, "en");
        Assert.True(en!.IsTranslation);
        Assert.EndsWith("&tlang=en", en.Url);
        Assert.Equal("en", en.Language);
        Assert.Null(SubtitleSelection.Choose(tracks, "da"));   // no Danish track, no auto-translate
    }

    [Fact]
    public void Choose_NullPreference_ReturnsNull()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=en", "en", "English", false) };
        Assert.Null(SubtitleSelection.Choose(tracks, null));
    }

    [Fact]
    public void Choose_EmptyTracks_ReturnsNull()
    {
        var tracks = new List<CaptionTrack>();
        Assert.Null(SubtitleSelection.Choose(tracks, "en"));
    }

    [Fact]
    public void Choose_ExactMatchBeatsRegionalVariant_RegardlessOfTrackOrder()
    {
        // Preference "en": the regional en-GB is listed first, the exact "en" last.
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en-GB", "en-GB", "English (UK)", false),
            new("https://t?lang=en", "en", "English", false),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.Equal("en", choice!.Language);
        Assert.Equal("https://t?lang=en&fmt=vtt", choice.Url);
    }

    [Fact]
    public void Choose_ExactRegionalMatch_PicksItAmongSiblings()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en-US", "en-US", "English (US)", false),
            new("https://t?lang=en-GB", "en-GB", "English (UK)", false),
            new("https://t?lang=en", "en", "English", false),
        };
        var choice = SubtitleSelection.Choose(tracks, "en-GB");
        Assert.Equal("en-GB", choice!.Language);
        Assert.Equal("https://t?lang=en-GB&fmt=vtt", choice.Url);
    }

    [Fact]
    public void Choose_ExactBeatsSamePrimarySubtag_RegardlessOfTrackOrder()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en", "en", "English", false),
            new("https://t?lang=en-US", "en-US", "English (US)", false),
            new("https://t?lang=en-GB", "en-GB", "English (UK)", false),
        };
        Assert.Equal("en-GB", SubtitleSelection.Choose(tracks, "en-GB")!.Language);
    }

    [Fact]
    public void Choose_RegionalPreference_FallsBackToBareLanguageTrack()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=en", "en", "English", false) };
        var choice = SubtitleSelection.Choose(tracks, "en-GB");
        Assert.False(choice!.IsTranslation);
        Assert.Equal("en", choice.Language);
    }

    [Fact]
    public void Choose_RegionalPreference_FallsBackToSiblingRegion()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=en-US", "en-US", "English (US)", false) };
        Assert.Equal("en-US", SubtitleSelection.Choose(tracks, "en-GB")!.Language);
    }

    [Fact]
    public void Choose_RegionalPreference_TranslatesWhenNoEnglishTrack()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=ja", "ja", "Japanese", false) };
        var choice = SubtitleSelection.Choose(tracks, "en-GB");
        Assert.True(choice!.IsTranslation);
        Assert.EndsWith("&tlang=en", choice.Url);
    }

    [Fact]
    public void Choose_RegionalPreference_NonEnglish_NeverTranslates()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=ja", "ja", "Japanese", false) };
        Assert.Null(SubtitleSelection.Choose(tracks, "pt-BR"));
    }

    [Fact]
    public void Choose_RegionalPreference_MatchesBareLanguageAsrTrack()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=pt&caps=asr", "pt", "Portuguese (auto)", true) };
        var choice = SubtitleSelection.Choose(tracks, "pt-BR");
        Assert.Equal("pt", choice!.Language);
        Assert.False(choice.IsTranslation);
    }

    [Fact]
    public void Choose_ManualBeatsAsr_EvenWhenAsrIsTheBetterMatchTier()
    {
        // Documented order: manual before ASR, then match tier within each.
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en-US", "en-US", "English (US)", false),
            new("https://t?lang=en&caps=asr", "en", "English (auto)", true),
        };
        Assert.Equal("English (US)", SubtitleSelection.Choose(tracks, "en")!.Label);
    }

    [Fact]
    public void Choose_WithinAsr_ExactBeatsRegional()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en-US&caps=asr", "en-US", "English US (auto)", true),
            new("https://t?lang=en&caps=asr", "en", "English (auto)", true),
        };
        Assert.Equal("English (auto)", SubtitleSelection.Choose(tracks, "en")!.Label);
    }

    [Fact]
    public void Choose_SamePrimarySubtag_DoesNotMatchLongerPrefix()
    {
        // "en" must not match "eng" or "enm"; primary subtags compare whole.
        var tracks = new List<CaptionTrack> { new("https://t?lang=enm", "enm", "Middle English", false) };
        var choice = SubtitleSelection.Choose(tracks, "en-GB");
        Assert.True(choice!.IsTranslation);   // no match, so only the translation rung is left
    }

    [Fact]
    public void Choose_PrefixMatchForRegionalVariant()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en-US", "en-US", "English (US)", false),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.Equal("en-US", choice!.Language);
    }

    [Fact]
    public void Choose_PrefersManualOverAutoGenerated()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en", "en", "English (auto)", true),
            new("https://t?lang=en", "en", "English", false),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.Equal("English", choice!.Label);
    }

    [Fact]
    public void Choose_FallsBackToAsrWhenNoManualTrack()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=en", "en", "English (auto)", true),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.Equal("English (auto)", choice!.Label);
        Assert.Equal("https://t?lang=en&fmt=vtt", choice.Url);
    }

    [Fact]
    public void Choose_TranslationUsesFirstNonAutoTrackOrFirstTrack()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=ja-asr", "ja", "Japanese (auto)", true),
            new("https://t?lang=ja", "ja", "Japanese", false),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.True(choice!.IsTranslation);
        Assert.Equal("https://t?lang=ja&fmt=vtt&tlang=en", choice.Url);
        Assert.Equal("en", choice.Language);
    }

    [Fact]
    public void Choose_TranslationWithAllAutoTracks_UsesFirstTrack()
    {
        var tracks = new List<CaptionTrack>
        {
            new("https://t?lang=ja-asr", "ja", "Japanese (auto)", true),
            new("https://t?lang=ko-asr", "ko", "Korean (auto)", true),
        };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.True(choice!.IsTranslation);
        Assert.Equal("https://t?lang=ja-asr&fmt=vtt&tlang=en", choice.Url);
        Assert.Equal("en", choice.Language);
    }

    [Fact]
    public void Choose_TranslationLabelIsTranslateToEnglish()
    {
        var tracks = new List<CaptionTrack> { new("https://t?lang=ja", "ja", "Japanese", false) };
        var choice = SubtitleSelection.Choose(tracks, "en");
        Assert.Equal("Translate to English", choice!.Label);
        Assert.Equal("en", choice.Language);
    }

    [Fact]
    public void OnMuteChanged_OffWithMute_ReturnsAutoMute()
    {
        Assert.Equal(SubtitleMode.AutoMute, SubtitleMuteMachine.OnMuteChanged(SubtitleMode.Off, true));
    }

    [Fact]
    public void OnMuteChanged_AutoMuteWithUnmute_ReturnsOff()
    {
        Assert.Equal(SubtitleMode.Off, SubtitleMuteMachine.OnMuteChanged(SubtitleMode.AutoMute, false));
    }

    [Fact]
    public void OnMuteChanged_ManualWithUnmute_StaysManual()
    {
        Assert.Equal(SubtitleMode.Manual, SubtitleMuteMachine.OnMuteChanged(SubtitleMode.Manual, false));
    }

    [Fact]
    public void OnMuteChanged_ManualWithMute_StaysManual()
    {
        Assert.Equal(SubtitleMode.Manual, SubtitleMuteMachine.OnMuteChanged(SubtitleMode.Manual, true));
    }

    [Fact]
    public void OnMuteChanged_OffWithoutMute_StaysOff()
    {
        Assert.Equal(SubtitleMode.Off, SubtitleMuteMachine.OnMuteChanged(SubtitleMode.Off, false));
    }

    [Fact]
    public void OnMuteChanged_AutoMuteWithMute_StaysAutoMute()
    {
        Assert.Equal(SubtitleMode.AutoMute, SubtitleMuteMachine.OnMuteChanged(SubtitleMode.AutoMute, true));
    }

    [Fact]
    public void OnManualPick_PickedTrack_ReturnsManual()
    {
        Assert.Equal(SubtitleMode.Manual, SubtitleMuteMachine.OnManualPick(false));
    }

    [Fact]
    public void OnManualPick_PickedOff_ReturnsOff()
    {
        Assert.Equal(SubtitleMode.Off, SubtitleMuteMachine.OnManualPick(true));
    }
}
