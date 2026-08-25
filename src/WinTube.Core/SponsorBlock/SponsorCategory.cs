namespace WinTube.Core.SponsorBlock;

/// The kinds of interruption SponsorBlock distinguishes.
public enum SponsorCategory
{
    Sponsor,
    SelfPromo,
    Interaction,
    Intro,
    Outro,
    Preview,
    Filler,
    MusicOffTopic,
}

public static class SponsorCategories
{
    /// What gets skipped: the four that are unambiguously NOT the video the user chose to
    /// watch. Intro/outro/preview/filler are editorial parts of the video itself — plenty of
    /// people want them — so they are parsed but never requested. No settings UI, as on tvOS.
    public static readonly IReadOnlySet<SponsorCategory> DefaultSkipped = new HashSet<SponsorCategory>
    {
        SponsorCategory.Sponsor,
        SponsorCategory.SelfPromo,
        SponsorCategory.Interaction,
        SponsorCategory.MusicOffTopic,
    };

    /// The API's own identifier, sent verbatim in the categories query parameter.
    public static string ApiName(this SponsorCategory category) => category switch
    {
        SponsorCategory.Sponsor => "sponsor",
        SponsorCategory.SelfPromo => "selfpromo",
        SponsorCategory.Interaction => "interaction",
        SponsorCategory.Intro => "intro",
        SponsorCategory.Outro => "outro",
        SponsorCategory.Preview => "preview",
        SponsorCategory.Filler => "filler",
        SponsorCategory.MusicOffTopic => "music_offtopic",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// Shown in the "skipped" toast.
    public static string DisplayName(this SponsorCategory category) => category switch
    {
        SponsorCategory.Sponsor => "Sponsor",
        SponsorCategory.SelfPromo => "Self-promotion",
        SponsorCategory.Interaction => "Subscribe reminder",
        SponsorCategory.Intro => "Intro",
        SponsorCategory.Outro => "Outro",
        SponsorCategory.Preview => "Recap",
        SponsorCategory.Filler => "Filler",
        SponsorCategory.MusicOffTopic => "Non-music section",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// Null for a category this build doesn't know — the API grows categories, and an
    /// unknown one must be ignored, not crash the parse.
    public static SponsorCategory? FromApiName(string name) => name switch
    {
        "sponsor" => SponsorCategory.Sponsor,
        "selfpromo" => SponsorCategory.SelfPromo,
        "interaction" => SponsorCategory.Interaction,
        "intro" => SponsorCategory.Intro,
        "outro" => SponsorCategory.Outro,
        "preview" => SponsorCategory.Preview,
        "filler" => SponsorCategory.Filler,
        "music_offtopic" => SponsorCategory.MusicOffTopic,
        _ => null,
    };
}
