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
//   기억노트: 사용자가 등록한 단어를 지정된 번역으로 고정 (MemoryNoteService)
//     DeepL  → TagHandling="html" + <span translate="no"> 방식
//     Google → __MN0__ 플레이스홀더 방식 (비공식 endpoint는 html 모드 미지원)
// ============================================================
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using DeepL;
using System.Text.RegularExpressions;

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
        ["API"] = "__API__",
        ["OCR"] = "__OCR__",
        ["STT"] = "__STT__",
        ["AI"] = "__AI__",
        ["NLP"] = "__NLP__",
        ["UI"] = "__UI__",
    };

    public void Configure(TranslationProvider provider, string? apiKey, int contextSize = 3)
    {
        _provider = provider;
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

        // 문맥 문자열 (최근 N개 문장)
        var context = string.Join(" ", _contextWindow.TakeLast(_contextSize));

        string result;
        try
        {
            result = _provider == TranslationProvider.DeepL && _deepL != null
                ? await TranslateDeepLAsync(text, context, targetLang!)
                : await TranslateGoogleAsync(text, targetLang!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Translation error: {ex.Message}");
            // Fallback to Google — 가공되지 않은 원문에서 다시 시작한다
            result = await TranslateGoogleAsync(text, targetLang!);
        }

        // 문맥 윈도우 갱신
        _contextWindow.Enqueue(text);
        while (_contextWindow.Count > _contextSize)
            _contextWindow.Dequeue();

        return result;
    }

    // ------------------------------------------------------------------
    // DeepL — 태그 방식
    // ------------------------------------------------------------------
    private async Task<string> TranslateDeepLAsync(string text, string context, string targetLang)
    {
        // 1. 약어 보호
        var body = ProtectTerms(text);

        // 2. 기억노트 적용 (교정할 단어가 있을 때만 html 모드로 전환)
        var useNote = MemoryNoteService.TryApplyTags(body, out body);

        var opts = new TextTranslateOptions
        {
            Context = string.IsNullOrEmpty(context) ? null : context
        };

        if (useNote)
        {
            opts.TagHandling = "html";
            opts.IgnoreTags.Add("span");
        }

        var res = await _deepL!.TranslateTextAsync(body,
            sourceLanguageCode: null,
            targetLanguageCode: targetLang,
            opts);

        var result = res.Text;

        // 3. 태그·엔티티 정리
        if (useNote) result = MemoryNoteService.CleanTags(result);

        // 4. 약어 복원 + 안전망
        result = RestoreTerms(result);
        return MemoryNoteService.ApplyFallback(result);
    }

    // ------------------------------------------------------------------
    // Google — 플레이스홀더 방식
    // ------------------------------------------------------------------
    private async Task<string> TranslateGoogleAsync(string text, string targetLang)
    {
        // 1. 약어 보호
        var body = ProtectTerms(text);

        // 2. 기억노트 적용 (등록 단어를 __MN0__ 토큰으로 치환)
        var useNote = MemoryNoteService.TryApplyPlaceholders(body, out body, out var restoreMap);

        // Google Translate unofficial endpoint (no key needed for basic use)
        var lang = targetLang.ToLower() == "ko" ? "ko" : targetLang.ToLower();
        var url = $"https://translate.googleapis.com/translate_a/single" +
                  $"?client=gtx&sl=auto&tl={lang}&dt=t&q={HttpUtility.UrlEncode(body)}";
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

        var result = sb.ToString();

        // 3. 토큰 복원
        if (useNote) result = MemoryNoteService.RestorePlaceholders(result, restoreMap);

        // 4. 약어 복원 + 안전망
        result = RestoreTerms(result);
        return MemoryNoteService.ApplyFallback(result);
    }

    private string ProtectTerms(string text)
    {
        foreach (var (term, ph) in _protected)
            text = Regex.Replace(text, $@"\b{Regex.Escape(term)}\b", ph,
                                 RegexOptions.IgnoreCase);
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
