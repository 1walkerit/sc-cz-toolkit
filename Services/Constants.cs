namespace ScCestinator.Services;

public static class Constants
{
    public static readonly string[] SupportedStarCitizenBranches =
    {
        "LIVE",
        "PTU",
        "EPTU"
    };

    public const string GitHubLocalizationUrl =
        "https://raw.githubusercontent.com/JarredSC/Star-Citizen-CZ-lokalizace/main/Localization/english/global.ini";

    public const string GitHubLatestReleaseApi =
        "https://api.github.com/repos/1walkerit/sc-cz-toolkit/releases/latest";
}
