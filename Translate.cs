using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;
using Nikse.SubtitleEdit.UiLogic.Translate;

namespace AutoSubtitleGenerator;

/// <summary>
/// Translate > Auto-translate with Anthropic Claude, then OK: AutoTranslateViewModel's translate
/// loop is the same as libuilogic's DoAutoTranslate, which is used here as is.
/// </summary>
internal static class Translate
{
    public static async Task<Subtitle> RunAsync(Subtitle subtitle, string sourceLanguageCode, string targetLanguageCode, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Set the ANTHROPIC_API_KEY environment variable to your Anthropic API key.");
        }

        // AutoTranslateViewModel.LoadSettings and SaveSettings put the dialog's values here; the
        // model, URL and prompt come from Subtitle Edit's settings, the key from the environment.
        var settings = Se.Settings.AutoTranslate;
        var tools = Configuration.Settings.Tools;
        tools.AnthropicApiKey = apiKey.Trim();
        tools.AnthropicApiModel = settings.AnthropicApiModel?.Trim() ?? string.Empty;
        tools.AnthropicApiUrl = settings.AnthropicApiUrl?.Trim() ?? string.Empty;
        tools.AnthropicPrompt = settings.AnthropicPrompt;

        using var translator = new AnthropicTranslate();
        var source = translator.GetSupportedSourceLanguages().First(p => p.TwoLetterIsoLanguageName == sourceLanguageCode);
        var target = translator.GetSupportedTargetLanguages().First(p => p.TwoLetterIsoLanguageName == targetLanguageCode);

        // The engine falls back to its first model when none is set.
        var model = string.IsNullOrEmpty(tools.AnthropicApiModel) ? AnthropicTranslate.Models[0] : tools.AnthropicApiModel;
        Log.Info($"Engine: {translator.Name}, model {model}, {source.Name} to {target.Name}");

        // As in a freshly started Subtitle Edit: no merge/split trouble remembered from an earlier file.
        MergeAndSplitHelper.MergeSplitProblems = false;

        var autoTranslate = new DoAutoTranslate
        {
            TranslateEachLineSeparately = settings.IsTranslateEachLineSeparately(translator.Name),
            Progress = (done, total) => Log.Progress($"Translating line {done} of {total}"),
        };
        Log.Progress($"Translating {subtitle.Paragraphs.Count} lines");
        var rows = await autoTranslate.DoTranslate(subtitle, source, target, translator, cancellationToken);
        Log.EndProgress();
        cancellationToken.ThrowIfCancellationRequested();

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
