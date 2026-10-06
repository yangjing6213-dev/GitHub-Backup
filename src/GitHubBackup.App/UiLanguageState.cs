namespace GitHubBackup.App;

internal enum AppUiLanguage { SimplifiedChinese, English }

internal static class UiLanguageState
{
    internal static AppUiLanguage Current { get; set; } = AppUiLanguage.SimplifiedChinese;
    internal static bool IsEnglish => Current == AppUiLanguage.English;
    internal static string Text(string simplifiedChinese, string english) => IsEnglish ? english : simplifiedChinese;
}
