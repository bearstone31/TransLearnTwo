// ============================================================
// TextQualityFilter.cs
// 역할 : 캡처된 텍스트를 번역·DB 저장 전에 검증하는 품질 필터.
//
// 목적
//   OCR/STT로 수집된 텍스트에는 노이즈(특수문자 뭉침, 숫자만, URL 등)가
//   많이 포함된다. 번역 API를 불필요하게 호출하지 않도록 조기에 폐기해
//   API 비용과 DB 오염을 최소화한다.
//
// 검사 단계 (Evaluate 메서드, 순서대로 적용)
//   1.  빈 텍스트 폐기
//   2.  URL / 파일경로 / 16진 덤프 즉시 폐기
//   3.  단어 수 범위 (MinWords ~ MaxWords)
//   4.  영문자 비율 하한 (MinEnglishRatio, 기본 55%)
//   5.  노이즈 문자 비율 상한 (MaxNoiseRatio, 기본 25%)
//   6.  반복 문자 블록 비율 상한 (MaxRepeatRatio, 기본 40%)
//   7.  모음 비율 하한 (MinVowelRatio, 기본 15%) — 자음 뭉침 OCR 감지
//   8.  평균 단어 길이 상한 (MaxAvgWordLength) — OCR 단어 뭉침 감지
//   9.  알파벳 단어(≥2자) 최소 개수 (MinRealWordCount)
//   10. 공통 영단어 포함 여부 (최소 1개)
//   11. [Sound 전용] 필러 단어 폐기 (um, uh, okay 등)
//   12. [OCR 전용]  기호·숫자만 포함된 줄 폐기
//
// 반환값 : FilterResult (Passed, CleanedText, Reason, ReasonDetail)
// ============================================================
using System.Text.RegularExpressions;

namespace TransLearn.Services;

/// <summary>
/// 캡처된 텍스트를 번역/DB 저장 전에 검증하는 품질 필터.
/// OCR 노이즈, 비완성 문장, 무의미한 토큰을 조기에 폐기해
/// 번역 API 호출 비용과 DB 오염을 최소화한다.
/// </summary>
public sealed class TextQualityFilter
{
    // ── 설정값 (SettingsViewModel 에서 조정 가능하도록 public) ────────────
    public int    MinWords          { get; set; } = 2;    // 최소 단어 수
    public int    MaxWords          { get; set; } = 150;  // 최대 단어 수 (OCR 쓰레기 방지)
    public double MinEnglishRatio   { get; set; } = 0.55; // 영문자 비율 하한
    public double MaxNoiseRatio     { get; set; } = 0.25; // 특수문자·숫자 비율 상한
    public double MinVowelRatio     { get; set; } = 0.15; // 단어 내 모음 비율 하한
    public int    MinRealWordCount  { get; set; } = 2;    // 사전 단어(길이≥2) 최소 수
    public double MaxRepeatRatio    { get; set; } = 0.40; // 반복 문자 비율 상한
    public int    MinWordLength     { get; set; } = 1;    // 허용하는 최소 단어 길이
    public double MaxAvgWordLength  { get; set; } = 20.0; // 평균 단어 길이 상한 (OCR 뭉침 감지)

    // ── 캐시된 정규식 ────────────────────────────────────────────────────
    private static readonly Regex _englishChar  = new(@"[a-zA-Z]",        RegexOptions.Compiled);
    private static readonly Regex _vowel        = new(@"[aeiouAEIOU]",    RegexOptions.Compiled);
    private static readonly Regex _noisyChar    = new(@"[^a-zA-Z0-9\s\.,!?;:'""'\-]", RegexOptions.Compiled);
    private static readonly Regex _wordToken    = new(@"\b[a-zA-Z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex _repeatBlock  = new(@"(.)\1{4,}",       RegexOptions.Compiled);  // aaaaa
    private static readonly Regex _onlySymbols  = new(@"^[\W\d_]+$",      RegexOptions.Compiled);
    private static readonly Regex _urlOrPath    = new(@"(https?://|\\\\|[A-Z]:\\)", RegexOptions.Compiled);
    private static readonly Regex _hexDump      = new(@"\b[0-9a-fA-F]{6,}\b", RegexOptions.Compiled);

    // ── 기본 영단어 집합 (매우 흔한 500단어 중 핵심만 포함, 빌드 크기 최소화) ──
    private static readonly HashSet<string> _commonWords = BuildCommonWordSet();

    // ── 공개 API ─────────────────────────────────────────────────────────

    /// <summary>
    /// 텍스트를 검증하고 통과 여부 + 이유를 반환한다.
    /// </summary>
    public FilterResult Evaluate(string text, CaptureSource source)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Reject(RejectReason.Empty, "빈 텍스트");

        // 1. 전처리: 줄바꿈 정규화, 앞뒤 공백 제거
        var normalized = NormalizeWhitespace(text);

        // 2. URL·파일경로·16진 덤프 즉시 폐기
        if (_urlOrPath.IsMatch(normalized))
            return Reject(RejectReason.ContainsUrl, "URL/경로 포함");
        if (_hexDump.IsMatch(normalized))
            return Reject(RejectReason.HexDump, "16진수 덤프");

        var words = TokenizeWords(normalized);

        // 3. 단어 수 범위
        if (words.Length < MinWords)
            return Reject(RejectReason.TooShort, $"단어 {words.Length}개 (최소 {MinWords})");
        if (words.Length > MaxWords)
            return Reject(RejectReason.TooLong, $"단어 {words.Length}개 (최대 {MaxWords})");

        // 4. 영문자 비율
        var engRatio = EnglishCharRatio(normalized);
        if (engRatio < MinEnglishRatio)
            return Reject(RejectReason.LowEnglishRatio, $"영문자 비율 {engRatio:P0} (하한 {MinEnglishRatio:P0})");

        // 5. 특수문자·숫자 노이즈 비율
        var noiseRatio = NoiseRatio(normalized);
        if (noiseRatio > MaxNoiseRatio)
            return Reject(RejectReason.TooNoisy, $"노이즈 비율 {noiseRatio:P0} (상한 {MaxNoiseRatio:P0})");

        // 6. 반복 문자 블록 (aaaaaaa, ###### 등)
        var repeatRatio = RepeatCharRatio(normalized);
        if (repeatRatio > MaxRepeatRatio)
            return Reject(RejectReason.RepetitiveChars, $"반복문자 비율 {repeatRatio:P0}");

        // 7. 모음 비율 (자음만 늘어선 OCR 쓰레기 감지)
        var vowelRatio = VowelRatioInEnglishChars(normalized);
        if (vowelRatio < MinVowelRatio)
            return Reject(RejectReason.NoVowels, $"모음 비율 {vowelRatio:P0} (하한 {MinVowelRatio:P0})");

        // 8. 평균 단어 길이 (OCR이 단어를 뭉쳐 하나의 긴 토큰으로 만드는 경우)
        var avgWordLen = words.Average(w => (double)w.Length);
        if (avgWordLen > MaxAvgWordLength)
            return Reject(RejectReason.WordsTooLong, $"평균 단어 길이 {avgWordLen:F1} (상한 {MaxAvgWordLength})");

        // 9. 알파벳 단어(≥2자) 최소 개수
        var alphaWordCount = _wordToken.Matches(normalized).Count;
        if (alphaWordCount < MinRealWordCount)
            return Reject(RejectReason.TooFewRealWords, $"유효 단어 {alphaWordCount}개 (최소 {MinRealWordCount})");

        // 10. 알려진 영단어 포함 여부 (최소 1개)
        var hasKnownWord = words.Any(w => _commonWords.Contains(w.ToLowerInvariant()));
        if (!hasKnownWord)
            return Reject(RejectReason.NoKnownWords, "알려진 영단어 없음");

        // 11. STT 전용: 너무 짧은 단일 음절 반응 (응, 어, um, uh 등)
        if (source == CaptureSource.Sound && IsFiller(normalized))
            return Reject(RejectReason.FillerWord, "필러 단어 (um/uh/응 등)");

        // 12. OCR 전용: 순수 숫자·기호만 남은 줄 (화면 UI 요소 등)
        if (source == CaptureSource.OCR && _onlySymbols.IsMatch(normalized))
            return Reject(RejectReason.OnlySymbols, "기호/숫자만 포함");

        // ── 모든 검사 통과 ──
        return FilterResult.Pass(normalized);
    }

    // ── 내부 헬퍼 ────────────────────────────────────────────────────────

    private static string NormalizeWhitespace(string text)
        => Regex.Replace(text.Trim(), @"\s+", " ");

    private static string[] TokenizeWords(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double EnglishCharRatio(string text)
    {
        if (text.Length == 0) return 0;
        var letters = text.Count(char.IsLetter);
        if (letters == 0) return 0;
        var engLetters = _englishChar.Matches(text).Count;
        return (double)engLetters / letters;
    }

    private static double NoiseRatio(string text)
    {
        if (text.Length == 0) return 0;
        return (double)_noisyChar.Matches(text).Count / text.Length;
    }

    private static double RepeatCharRatio(string text)
    {
        if (text.Length == 0) return 0;
        var repeated = _repeatBlock.Matches(text).Sum(m => m.Length);
        return (double)repeated / text.Length;
    }

    private static double VowelRatioInEnglishChars(string text)
    {
        var eng    = _englishChar.Matches(text).Count;
        if (eng == 0) return 1.0; // 영문자 없으면 이 검사 패스
        var vowels = _vowel.Matches(text).Count;
        return (double)vowels / eng;
    }

    private static bool IsFiller(string text)
    {
        var fillers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "um","uh","ah","oh","hmm","hm","mm","er","eh","mhm","uh-huh",
            "yeah","yep","nope","ok","okay","hi","hey","bye",
            "ugh","wow","oh","ah","oh yeah","uh huh"
        };
        return fillers.Contains(text.Trim());
    }

    private static FilterResult Reject(RejectReason reason, string detail)
        => FilterResult.Fail(reason, detail);

    // ── 공통 영단어 집합 빌드 ────────────────────────────────────────────
    private static HashSet<string> BuildCommonWordSet() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles, pronouns, prepositions (항상 존재)
        "a","an","the","i","you","he","she","it","we","they","me","him","her","us","them",
        "my","your","his","its","our","their","this","that","these","those",
        "is","are","was","were","be","been","being","am",
        "have","has","had","do","does","did","will","would","could","should","can","may","might",
        "in","on","at","to","for","of","with","by","from","up","out","as","into","through",
        "and","but","or","nor","so","yet","if","then","than","when","where","how","what","who",
        // Common content words
        "time","year","people","way","day","man","woman","child","world","life","hand","part","place","case","week",
        "company","system","program","question","work","government","number","night","point","home","water","room",
        "mother","area","money","story","fact","month","lot","right","study","book","eye","job","word","business",
        "issue","side","kind","head","house","service","friend","father","power","hour","game","line","end","among",
        "ever","need","large","often","play","small","number","off","always","move","live","feel","seem","ask","show",
        "try","call","keep","make","take","come","see","think","know","get","give","say","want","look","use",
        "find","tell","become","leave","put","mean","happen","begin","follow","talk","turn","start","show",
        "hear","play","run","move","live","believe","hold","bring","write","stand","happen","meet","lead",
        "read","grow","open","walk","win","offer","remember","love","consider","appear","buy","wait","speak",
        "stop","send","receive","decide","learn","change","watch","help","create","continue","raise","pass","set",
        "explain","hope","develop","carry","break","catch","draw","choose","cause","require","spend","feel",
        "include","continue","set","learn","change","lead","understand","watch","follow","stop","create","speak",
        // Adjectives
        "good","new","first","last","long","great","little","own","other","old","right","big","high","different",
        "small","large","next","early","young","important","public","private","real","best","free","able","sure",
        "true","better","full","easy","clear","recent","certain","open","bad","same","present","available",
        "major","social","hard","strong","human","several","possible","late","possible","white","black",
        // Common nouns (tech/media context)
        "video","audio","text","screen","image","file","data","app","software","computer","phone","internet",
        "language","english","korean","translation","learning","content","media","channel","stream","chat",
        "window","button","menu","setting","option","feature","mode","play","pause","stop","start","record",
    };
}

// ── 결과 타입 ────────────────────────────────────────────────────────────

public sealed record FilterResult
{
    public bool         Passed        { get; init; }
    public string       CleanedText   { get; init; } = "";
    public RejectReason Reason        { get; init; }
    public string       ReasonDetail  { get; init; } = "";

    public static FilterResult Pass(string cleanedText) =>
        new() { Passed = true, CleanedText = cleanedText };

    public static FilterResult Fail(RejectReason reason, string detail) =>
        new() { Passed = false, Reason = reason, ReasonDetail = detail };

    public override string ToString() =>
        Passed ? $"✅ PASS  \"{CleanedText}\"" : $"❌ REJECT [{Reason}] {ReasonDetail}";
}

public enum RejectReason
{
    None,
    Empty,
    TooShort,
    TooLong,
    LowEnglishRatio,
    TooNoisy,
    RepetitiveChars,
    NoVowels,
    WordsTooLong,
    TooFewRealWords,
    NoKnownWords,
    FillerWord,
    OnlySymbols,
    ContainsUrl,
    HexDump,
    Duplicate,
}

public enum CaptureSource { OCR, Sound }
