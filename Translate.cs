using Anthropic;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;

namespace AutoSubtitleGenerator;

/// <summary>
/// Translate > Auto-translate with Anthropic Claude, then OK, but with this app's own requests
/// (see <see cref="ClaudeTranslator"/>) instead of Subtitle Edit's engine. The model and URL are
/// the ones set in Subtitle Edit; the key comes from the environment.
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

        // Subtitle Edit's engine falls back to its first model when none is set.
        var settings = Se.Settings.AutoTranslate;
        var model = string.IsNullOrWhiteSpace(settings.AnthropicApiModel) ? AnthropicTranslate.Models[0] : settings.AnthropicApiModel.Trim();

        // The language names Subtitle Edit's engine puts in its prompt.
        var languages = ChatGptTranslate.ListLanguages();
        var source = languages.First(p => p.TwoLetterIsoLanguageName == sourceLanguageCode).Name;
        var target = languages.First(p => p.TwoLetterIsoLanguageName == targetLanguageCode).Name;
        Log.Info($"Claude {model}, {source} to {target}");

        var client = new AnthropicClient
        {
            ApiKey = apiKey.Trim(),
            BaseUrl = BaseUrl(settings.AnthropicApiUrl),
            MaxRetries = 5,
        };
        var translator = new ClaudeTranslator(client, model, source, target);

        Log.Progress($"Translating {subtitle.Paragraphs.Count} subtitles");
        var translations = await translator.TranslateAsync(
            subtitle.Paragraphs.Select(p => p.Text).ToList(),
            (done, total) => Log.Progress($"Translating subtitle {done} of {total}"),
            cancellationToken);
        Log.EndProgress();

        Log.Info($"Translated {translations.Length} subtitles in {translator.Requests} requests, " +
                 $"{translator.InputTokens:N0} input and {translator.OutputTokens:N0} output tokens");

        // MainViewModel, on OK: each subtitle takes its translation; time codes stay.
        var translated = new Subtitle(subtitle);
        for (var i = 0; i < translated.Paragraphs.Count; i++)
        {
            translated.Paragraphs[i].Text = translations[i];
        }

        RebreakLongLines(translated, targetLanguageCode);
        return translated;
    }

    /// <summary>
    /// The prompt asks for Subtitle Edit's maximum line length, but the model does not keep to it
    /// closely. A subtitle with a line that is too long is broken again as the "Auto br" button
    /// does it; the others keep the model's line breaks.
    /// </summary>
    private static void RebreakLongLines(Subtitle subtitle, string languageCode)
    {
        var maximumLength = Configuration.Settings.General.SubtitleLineMaximumLength;
        var count = 0;
        foreach (var paragraph in subtitle.Paragraphs)
        {
            if (paragraph.Text.SplitToLines().Any(line => line.CountCharacters(false) > maximumLength))
            {
                var text = Utilities.AutoBreakLine(paragraph.Text, languageCode);
                if (text != paragraph.Text)
                {
                    paragraph.Text = text;
                    count++;
                }
            }
        }

        if (count > 0)
        {
            Log.Info($"Broke {count} subtitles again for lines over {maximumLength} characters");
        }
    }

    /// <summary>Subtitle Edit's setting is the Messages URL; the SDK wants the address in front of /v1.</summary>
    private static string BaseUrl(string? url)
    {
        const string messagesPath = "/v1/messages";
        url = url?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return "https://api.anthropic.com";
        }

        return url.EndsWith(messagesPath, StringComparison.OrdinalIgnoreCase) ? url[..^messagesPath.Length] : url;
    }
}
