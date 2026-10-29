using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;
using Nikse.SubtitleEdit.UiLogic.Translate;

namespace AutoSubtitleGenerator;

/// <summary>
/// Translate > Auto-translate with DeepL, then OK: AutoTranslateViewModel's translate loop is
/// the same as libuilogic's DoAutoTranslate, which is used here as is.
/// </summary>
internal static class Translate
{
    public static async Task<Subtitle> RunAsync(Subtitle subtitle, string sourceLanguageCode, string targetLanguageCode, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Set the DEEPL_API_KEY environment variable to your DeepL API key.");
        }

        // AutoTranslateViewModel.SaveSettings puts the dialog's values here; the URL and formality
        // come from Subtitle Edit's settings, the key from the environment.
        var tools = Configuration.Settings.Tools;
        tools.AutoTranslateDeepLUrl = Se.Settings.AutoTranslate.DeepLUrl.Trim();
        tools.AutoTranslateDeepLApiKey = apiKey.Trim();
        tools.AutoTranslateDeepLFormality = Se.Settings.AutoTranslate.DeepLFormality;

        var translator = new DeepLTranslate();
        var source = translator.GetSupportedSourceLanguages().First(p => p.Code == sourceLanguageCode);
        var target = translator.GetSupportedTargetLanguages().First(p => p.Code == targetLanguageCode);
        Log.Info($"Engine: {translator.Name}, {source.Name} to {target.Name}, formality {tools.AutoTranslateDeepLFormality}");

        // As in a freshly started Subtitle Edit: no merge/split trouble remembered from an earlier file.
        MergeAndSplitHelper.MergeSplitProblems = false;

        var autoTranslate = new DoAutoTranslate
        {
            TranslateEachLineSeparately = Se.Settings.AutoTranslate.IsTranslateEachLineSeparately(translator.Name),
        };
        Log.Progress($"Translating {subtitle.Paragraphs.Count} lines");
        var rows = await autoTranslate.DoTranslate(subtitle, source, target, translator, cancellationToken);
        Log.EndProgress();

        // MainViewModel, on OK: a row takes its translation when there is one; time codes stay.
        var translated = new Subtitle(subtitle);
        var count = 0;
        for (var i = 0; i < translated.Paragraphs.Count && i < rows.Count; i++)
        {
            if (!string.IsNullOrEmpty(rows[i].TranslatedText))
            {
                translated.Paragraphs[i].Text = rows[i].TranslatedText;
                count++;
            }
        }

        if (count < translated.Paragraphs.Count)
        {
            throw new InvalidOperationException($"Only {count} of {translated.Paragraphs.Count} lines were translated.");
        }

        Log.Info($"Translated {count} lines");
        return translated;
    }
}
