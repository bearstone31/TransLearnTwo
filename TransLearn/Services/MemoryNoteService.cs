// ============================================================================
// MemoryNoteService.cs
// 역할 : "기억노트" — 사용자가 지정한 단어의 번역 결과를 고정한다.
//
//  저장 위치 : %AppData%\TransLearn\memory_note.txt
//  저장 형식 : UTF-8 텍스트. 한 줄에 하나씩,  대상단어<TAB>고정번역
//              예)  max	맥스
//              '#' 로 시작하는 줄은 주석으로 무시된다.
//              탭이 없으면 '|' 또는 공백 구분도 허용한다.
//
//  동작 원리 : 번역기가 대상 단어를 건드리지 못하게 만든 뒤,
//              그 자리에 사용자가 지정한 번역을 확정시킨다.
//              번역 '후' 치환이 아니라 번역 '전' 가공이므로 어순이 깨지지 않는다.
//
//    ● DeepL (태그 방식)  — TryApplyTags / CleanTags
//        Hello max  →  Hello <span translate="no">맥스</span>
//        요청에 TagHandling="html", IgnoreTags=["span"] 를 함께 지정한다.
//
//    ● Google 비공식 endpoint (플레이스홀더 방식) — TryApplyPlaceholders / RestorePlaceholders
//        html 모드가 없으므로 토큰으로 치환했다가 번역 후 되돌린다.
//        Hello max  →  Hello __MN0__  →  안녕 __MN0__  →  안녕 맥스
//
//  진단 : 모든 주요 동작을 출력 창(디버그)에 [MemoryNote] 태그로 기록한다.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TransLearn.Services;

/// <summary>기억노트 항목 하나 (대상 단어 → 고정 번역)</summary>
public sealed class MemoryNoteEntry
{
    public string Source { get; set; } = string.Empty;   // 대상 단어 (예: max)
    public string Target { get; set; } = string.Empty;   // 고정 번역 (예: 맥스)
}

public static class MemoryNoteService
{
    private static readonly object Sync = new();

    private static List<MemoryNoteEntry> _entries = new();
    private static Regex? _combined;
    private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static bool _wholeWordOnly = true;

    /// <summary>기억노트 txt 파일 경로</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TransLearn",
        "memory_note.txt");

    /// <summary>기능 on/off (환경 설정 체크박스와 연결)</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// true  : 완벽히 일치하는 단어만 교정 (max 는 잡고 maximum 은 통과)
    /// false : 문자열이 포함되기만 하면 교정
    /// 한글·한자처럼 띄어쓰기 경계가 없는 언어를 대상 단어로 쓸 땐 false 가 필요하다.
    /// </summary>
    public static bool WholeWordOnly
    {
        get => _wholeWordOnly;
        set
        {
            if (_wholeWordOnly == value) return;
            _wholeWordOnly = value;
            lock (Sync) Rebuild();
        }
    }

    /// <summary>등록된 항목 수</summary>
    public static int Count
    {
        get { EnsureLoaded(); lock (Sync) return _entries.Count; }
    }

    private static void Log(string msg) => Debug.WriteLine($"[MemoryNote] {msg}");

    // ------------------------------------------------------------------
    // 불러오기 / 저장 / 가져오기
    // ------------------------------------------------------------------

    private static void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    /// <summary>AppData 에 저장된 기억노트를 읽어온다.</summary>
    public static void Load()
    {
        var list = new List<MemoryNoteEntry>();

        try
        {
            if (File.Exists(FilePath))
            {
                list = Parse(ReadAllLinesSmart(FilePath));
                Log($"로드 완료 — {list.Count}개  ({FilePath})");
                foreach (var e in list)
                    Log($"  · '{e.Source}' → '{e.Target}'");

                if (list.Count == 0)
                    Log("!! 파일은 있는데 읽어들인 항목이 0개입니다. 구분자(탭)를 확인하세요.");
            }
            else
            {
                Log($"파일 없음 — {FilePath}");
            }
        }
        catch (Exception ex)
        {
            Log($"로드 실패: {ex.Message}");
        }

        lock (Sync)
        {
            _entries = list;
            Rebuild();
            _loaded = true;
        }
    }

    /// <summary>기억노트를 AppData 의 txt 파일에 저장한다. (빈 줄·중복 자동 정리)</summary>
    public static void Save(IEnumerable<MemoryNoteEntry> entries)
    {
        var clean = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Source) && !string.IsNullOrWhiteSpace(e.Target))
            .Select(e => new MemoryNoteEntry { Source = Clean(e.Source), Target = Clean(e.Target) })
            .Where(e => e.Source.Length > 0 && e.Target.Length > 0)
            .GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# TransLearn 기억노트");
        sb.AppendLine("# 형식: 대상단어<TAB>고정번역   (한 줄에 하나씩)");
        sb.AppendLine("# 예:   max\t맥스");
        sb.AppendLine();
        foreach (var e in clean)
            sb.Append(e.Source).Append('\t').AppendLine(e.Target);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // BOM 없는 UTF-8 로 저장한다 (BOM 이 붙으면 첫 단어가 매칭되지 않는다)
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));

        lock (Sync)
        {
            _entries = clean;
            Rebuild();
            _loaded = true;
        }

        Log($"저장 완료 — {clean.Count}개");
    }

    /// <summary>사용자가 고른 외부 txt 파일을 기억노트로 가져온다. 기존 내용은 덮어쓴다.</summary>
    /// <returns>가져온 항목 수</returns>
    public static int ImportFrom(string sourcePath)
    {
        var list = Parse(ReadAllLinesSmart(sourcePath));
        Log($"가져오기 — {sourcePath} 에서 {list.Count}개");
        Save(list);                 // AppData 로 복사 저장 + 정규식 재빌드까지 처리됨
        return list.Count;
    }

    /// <summary>UI 바인딩용 사본</summary>
    public static List<MemoryNoteEntry> GetAll()
    {
        EnsureLoaded();
        lock (Sync)
            return _entries
                .Select(e => new MemoryNoteEntry { Source = e.Source, Target = e.Target })
                .ToList();
    }

    // ------------------------------------------------------------------
    // 파일 파싱
    // ------------------------------------------------------------------

    /// <summary>앞뒤 공백과 BOM(U+FEFF), 제로폭 문자를 제거한다.</summary>
    private static string Clean(string s)
        => s.Trim().Trim('\uFEFF', '\u200B', '\u00A0').Trim();

    private static List<MemoryNoteEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<MemoryNoteEntry>();
        var lineNo = 0;

        foreach (var raw in lines)
        {
            lineNo++;
            var line = Clean(raw);
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts;

            if (line.Contains('\t'))
                parts = line.Split('\t', 2);
            else if (line.Contains('|'))
                parts = line.Split('|', 2);
            else if (line.Contains("=>", StringComparison.Ordinal))
                parts = line.Split("=>", 2, StringSplitOptions.None);
            else
                // 탭을 못 넣은 경우를 대비한 최후 수단: 첫 번째 공백 덩어리에서 자른다.
                // (대상 단어에 공백이 들어간다면 반드시 탭을 써야 한다)
                parts = Regex.Split(line, @"\s+", RegexOptions.None).Length >= 2
                    ? new[]
                      {
                          Regex.Match(line, @"^\S+").Value,
                          Regex.Replace(line, @"^\S+\s+", "")
                      }
                    : new[] { line };

            if (parts.Length < 2)
            {
                Log($"  건너뜀 ({lineNo}행): 구분자 없음 — \"{line}\"");
                continue;
            }

            var src = Clean(parts[0]);
            var dst = Clean(parts[1]);
            if (src.Length == 0 || dst.Length == 0)
            {
                Log($"  건너뜀 ({lineNo}행): 빈 값 — \"{line}\"");
                continue;
            }

            list.Add(new MemoryNoteEntry { Source = src, Target = dst });
        }

        return list;
    }

    /// <summary>UTF-8 우선. 깨지면 CP949(ANSI)로 재시도한다.</summary>
    private static string[] ReadAllLinesSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);

        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return strict.GetString(bytes).Split('\n');
        }
        catch (Exception)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Log("UTF-8 디코딩 실패 → CP949 로 재시도");
                return Encoding.GetEncoding(949).GetString(bytes).Split('\n');
            }
            catch (Exception)
            {
                return Encoding.UTF8.GetString(bytes).Split('\n');
            }
        }
    }

    // ------------------------------------------------------------------
    // 정규식 빌드
    // ------------------------------------------------------------------

    private static void Rebuild()
    {
        _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
            _map[e.Source] = e.Target;

        if (_entries.Count == 0)
        {
            _combined = null;
            return;
        }

        // 긴 단어를 먼저 매칭해야 "max power" 가 "max" 에 먼저 잡히지 않는다.
        var alts = _entries
            .OrderByDescending(e => e.Source.Length)
            .Select(e => _wholeWordOnly ? WithWordBoundary(e.Source) : Regex.Escape(e.Source));

        var pattern = string.Join("|", alts);

        try
        {
            _combined = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
            Log($"정규식 준비됨 — {pattern}");
        }
        catch (Exception ex)
        {
            Log($"정규식 생성 실패: {ex.Message}");
            _combined = null;
        }
    }

    /// <summary>
    /// 영문·숫자로 시작/끝나는 단어에만 \b 를 붙인다.
    /// 한글·한자는 \b 가 제대로 동작하지 않으므로 경계 없이 매칭한다.
    /// </summary>
    private static string WithWordBoundary(string word)
    {
        var pattern = Regex.Escape(word);
        if (IsAsciiWordChar(word[0])) pattern = @"\b" + pattern;
        if (IsAsciiWordChar(word[^1])) pattern += @"\b";
        return pattern;
    }

    private static bool IsAsciiWordChar(char c)
        => c < 128 && (char.IsLetterOrDigit(c) || c == '_');

    private static bool TryGetMatcher(out Regex rx, out Dictionary<string, string> map)
    {
        EnsureLoaded();
        lock (Sync)
        {
            map = _map;
            rx = _combined!;
            return _combined is not null;
        }
    }

    // ------------------------------------------------------------------
    // ① DeepL 용 : 태그 방식
    // ------------------------------------------------------------------

    /// <summary>
    /// DeepL 요청 직전에 호출. 등록 단어를 &lt;span translate="no"&gt;고정번역&lt;/span&gt; 으로 바꾼다.
    /// 반환값이 true 일 때만 TagHandling="html", IgnoreTags 에 "span" 을 넣을 것.
    /// </summary>
    public static bool TryApplyTags(string sourceText, out string prepared)
    {
        prepared = sourceText;

        if (!Enabled || string.IsNullOrWhiteSpace(sourceText)) return false;

        if (!TryGetMatcher(out var rx, out var map))
        {
            Log("적용 안 함 — 등록된 단어가 0개");
            return false;
        }

        if (!rx.IsMatch(sourceText))
        {
            Log($"적용 안 함 — 문장에 등록 단어 없음: \"{Preview(sourceText)}\"");
            return false;
        }

        // html 모드로 보내므로 원문의 <, &, > 가 태그로 오해되지 않게 먼저 이스케이프.
        // 이스케이프는 영문·한글 글자를 바꾸지 않으므로 이후 매칭에 영향이 없다.
        var escaped = HtmlEscape(sourceText);

        // 줄바꿈이 html 모드에서 사라지지 않도록 <br> 로 보존
        escaped = escaped.Replace("\r\n", "\n").Replace("\n", "<br>");

        prepared = rx.Replace(escaped, m =>
            map.TryGetValue(m.Value, out var target)
                ? $"<span translate=\"no\">{HtmlEscape(target)}</span>"
                : m.Value);

        Log($"[DeepL] 적용됨\n    전: {Preview(sourceText)}\n    후: {Preview(prepared)}");
        return true;
    }

    /// <summary>DeepL 응답에서 태그를 제거하고 줄바꿈·엔티티를 복원한다.</summary>
    public static string CleanTags(string translatedText)
    {
        if (string.IsNullOrEmpty(translatedText)) return translatedText;

        var s = Regex.Replace(translatedText, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</?span[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        return HtmlUnescape(s);
    }

    // ------------------------------------------------------------------
    // ② Google 용 : 플레이스홀더 방식
    // ------------------------------------------------------------------

    /// <summary>
    /// Google 요청 직전에 호출. 등록 단어를 __MN0__ 같은 토큰으로 치환한다.
    /// 번역이 끝나면 반드시 RestorePlaceholders 로 되돌려야 한다.
    /// </summary>
    public static bool TryApplyPlaceholders(
        string sourceText, out string prepared, out Dictionary<string, string> restoreMap)
    {
        prepared = sourceText;
        restoreMap = new Dictionary<string, string>();

        if (!Enabled || string.IsNullOrWhiteSpace(sourceText)) return false;

        if (!TryGetMatcher(out var rx, out var map))
        {
            Log("적용 안 함 — 등록된 단어가 0개");
            return false;
        }

        if (!rx.IsMatch(sourceText))
        {
            Log($"적용 안 함 — 문장에 등록 단어 없음: \"{Preview(sourceText)}\"");
            return false;
        }

        var slots = new Dictionary<string, string>();   // 고정번역 → 슬롯번호
        var local = restoreMap;
        var index = 0;

        prepared = rx.Replace(sourceText, m =>
        {
            if (!map.TryGetValue(m.Value, out var target)) return m.Value;

            if (!slots.TryGetValue(target, out var slot))
            {
                slot = index.ToString();
                slots[target] = slot;
                local[slot] = target;
                index++;
            }
            return $"__MN{slot}__";
        });

        Log($"[Google] 적용됨\n    전: {Preview(sourceText)}\n    후: {Preview(prepared)}");
        return restoreMap.Count > 0;
    }

    /// <summary>
    /// 번역 결과의 __MN0__ 토큰을 고정 번역으로 되돌린다.
    /// 번역기가 토큰 주변에 공백을 넣거나 밑줄 개수를 바꾸는 경우까지 허용한다.
    /// </summary>
    public static string RestorePlaceholders(string translatedText, Dictionary<string, string> restoreMap)
    {
        if (string.IsNullOrEmpty(translatedText) || restoreMap.Count == 0) return translatedText;

        return Regex.Replace(translatedText, @"_{1,4}\s*MN\s*(\d+)\s*_{1,4}",
            m => restoreMap.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value,
            RegexOptions.IgnoreCase);
    }

    // ------------------------------------------------------------------
    // 공통 안전망
    // ------------------------------------------------------------------

    /// <summary>
    /// 번역 '후' 안전망. 번역기가 대상 단어를 그대로 흘려보낸 경우에만 고정 번역으로 교체한다.
    /// 이미 번역된 결과는 건드리지 않으므로 오작동 위험이 낮다.
    /// </summary>
    public static string ApplyFallback(string translatedText)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(translatedText)) return translatedText;
        if (!TryGetMatcher(out var rx, out var map)) return translatedText;

        return rx.Replace(translatedText,
            m => map.TryGetValue(m.Value, out var t) ? t : m.Value);
    }

    // ------------------------------------------------------------------
    // 보조
    // ------------------------------------------------------------------

    private static string Preview(string s)
    {
        s = s.Replace("\r", "").Replace("\n", "⏎");
        return s.Length <= 120 ? s : s[..120] + "…";
    }

    private static string HtmlEscape(string s)
        => s.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    private static string HtmlUnescape(string s)
        => s.Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&amp;", "&");   // & 는 반드시 마지막에
}
