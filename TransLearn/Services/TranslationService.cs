// ============================================================
// TranslationService.cs
// 역할 : 텍스트 번역 서비스. Google 무료 번역 또는 DeepL API를 사용.
//
// 동작
//   Configure() — SettingsViewModel에서 호출, 공급자·API키·문맥크기 설정
//   TranslateAsync(text) — 선택된 공급자로 번역 후 한국어 반환
//     Google 모드: 비공식 endpoint (무료, 속도 제한 있음)
//     DeepL 모드:  DeepL.net SDK (API 키 필요)
//   문맥 번역: 최근 N개 문장을 함께 전송해 의미 연속성 유지
// ============================================================
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using DeepL;

namespace TransLearn.Services;

public enum TranslationProvider { Google, DeepL }

public class TranslationService : IDisposable
{
    private readonly HttpClient _http = new();
    private Translator? _deepL;
    private TranslationProvider _provider = TranslationProvider.Google;

    private readonly Queue<string> _contextWindow = new();
    private int _contextSize = 3;

    // Protected terms (abbreviations, proper nouns)
    private readonly Dictionary<string, string> _protected = new()
    {
        ["API"] = "__API__", ["OCR"] = "__OCR__", ["STT"] = "__STT__",
        ["AI"]  = "__AI__",  ["NLP"] = "__NLP__", ["UI"]  = "__UI__",
    };

    public void Configure(TranslationProvider provider, string? apiKey, int contextSize = 3)
    {
        _provider    = provider;
        _contextSize = contextSize;

        if (provider == TranslationProvider.DeepL && !string.IsNullOrWhiteSpace(apiKey))
        {
            _deepL?.Dispose();
            _deepL = new Translator(apiKey);
        }
    }

    public async Task<string> TranslateAsync(string text, string? targetLang = "KO")
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // 1. Protect terms
        var protected_ = ProtectTerms(text);

        // 2. Build context string
        var context = string.Join(" ", _contextWindow.TakeLast(_contextSize));

        string result;
        try
        {
            result = _provider == TranslationProvider.DeepL && _deepL != null
                ? await TranslateDeepLAsync(protected_, context, targetLang!)
                : await TranslateGoogleAsync(protected_, targetLang!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Translation error: {ex.Message}");
            // Fallback to Google
            result = await TranslateGoogleAsync(protected_, targetLang!);
        }

        // 3. Restore terms
        result = RestoreTerms(result);

        // 4. Update context window
        _contextWindow.Enqueue(text);
        while (_contextWindow.Count > _contextSize)
            _contextWindow.Dequeue();

        return result;
    }

    private async Task<string> TranslateDeepLAsync(string text, string context, string targetLang)
    {
        var opts = new TextTranslateOptions
        {
            Context = string.IsNullOrEmpty(context) ? null : context
        };
        var res = await _deepL!.TranslateTextAsync(text,
            sourceLanguageCode: null,
            targetLanguageCode: targetLang,
            opts);
        return res.Text;
    }

    private async Task<string> TranslateGoogleAsync(string text, string targetLang)
    {
        // Google Translate unofficial endpoint (no key needed for basic use)
        var lang = targetLang.ToLower() == "ko" ? "ko" : targetLang.ToLower();
        var url = $"https://translate.googleapis.com/translate_a/single" +
                  $"?client=gtx&sl=auto&tl={lang}&dt=t&q={HttpUtility.UrlEncode(text)}";
        var resp = await _http.GetStringAsync(url);

        // Parse [[["translated","original",...],...],...]
        var sb = new StringBuilder();
        using var doc = JsonDocument.Parse(resp);
        var arr = doc.RootElement[0];
        foreach (var item in arr.EnumerateArray())
        {
            if (item[0].ValueKind == JsonValueKind.String)
                sb.Append(item[0].GetString());
        }
        return sb.ToString();
    }

    private string ProtectTerms(string text)
    {
        foreach (var (term, ph) in _protected)
            text = text.Replace(term, ph, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private string RestoreTerms(string text)
    {
        foreach (var (term, ph) in _protected)
            text = text.Replace(ph, term);
        return text;
    }

    public void Dispose()
    {
        _deepL?.Dispose();
        _http.Dispose();
    }
}
