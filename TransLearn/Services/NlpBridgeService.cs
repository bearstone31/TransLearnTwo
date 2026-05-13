// ============================================================
// NlpBridgeService.cs
// ============================================================
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Globalization;
using TransLearn.Models;

namespace TransLearn.Services;

public class NlpBridgeService
{
    private readonly string _scriptPath;
    private readonly bool _isExe;
    private string? _pythonExe;

    public static string? CustomPythonPath
    {
        get => SecureKeyStorage.Load("python_path");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                SecureKeyStorage.Delete("python_path");
            else
                SecureKeyStorage.Save("python_path", value);
        }
    }

    public NlpBridgeService()
    {
        var basePython = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Python");

        // onedir 번들 exe 우선
        var onedirExe = Path.Combine(basePython, "nlp_analyzer", "nlp_analyzer.exe");
        // 단일 exe 차선
        var singleExe = Path.Combine(basePython, "nlp_analyzer.exe");
        // py 폴백
        var pyPath = Path.Combine(basePython, "nlp_analyzer.py");

        if (File.Exists(onedirExe))
        {
            _scriptPath = onedirExe;
            _isExe = true;
            Debug.WriteLine($"[NLP] onedir exe 사용: {_scriptPath}");
        }
        else if (File.Exists(singleExe))
        {
            _scriptPath = singleExe;
            _isExe = true;
            Debug.WriteLine($"[NLP] single exe 사용: {_scriptPath}");
        }
        else
        {
            _scriptPath = pyPath;
            _isExe = false;
            Debug.WriteLine($"[NLP] py 파일 사용: {_scriptPath}");
        }

        _pythonExe = ResolvePython();
    }

    private static string? ResolvePython()
    {
        var custom = CustomPythonPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (TryPython(custom, out var ver))
            {
                Debug.WriteLine($"[NLP] Custom Python OK: {custom} ({ver})");
                return custom;
            }
            Debug.WriteLine($"[NLP] Custom path failed: {custom}");
        }

        foreach (var exe in new[] { "python", "py", "python3" })
        {
            if (TryPython(exe, out var ver))
            {
                Debug.WriteLine($"[NLP] Found Python on PATH: {exe} ({ver})");
                return exe;
            }
        }
        Debug.WriteLine("[NLP] Python not found. Using C# fallback.");
        return null;
    }

    private static bool TryPython(string exe, out string version)
    {
        version = "";
        try
        {
            var psi = new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0)
            {
                version = (p.StandardOutput.ReadToEnd() +
                           p.StandardError.ReadToEnd()).Trim();
                return true;
            }
        }
        catch { }
        return false;
    }

    public string? PythonExe => _pythonExe;

    public bool IsAvailable =>
        (_isExe && File.Exists(_scriptPath)) ||
        (!_isExe && _pythonExe != null && File.Exists(_scriptPath));

    public void Reload() => _pythonExe = ResolvePython();

    public string StatusSummary()
    {
        if (_isExe && File.Exists(_scriptPath))
            return $"✅ nlp_analyzer.exe 감지됨 (Python 불필요)";
        if (_pythonExe == null)
            return "❌ Python 미감지 — C# 기본 분석 사용 중";
        if (!File.Exists(_scriptPath))
            return $"⚠️ 스크립트 없음: {_scriptPath}";
        TryPython(_pythonExe, out var ver);
        return $"✅ Python {ver} ({_pythonExe})";
    }

    private bool CheckSpacy()
    {
        if (_pythonExe == null) return false;
        try
        {
            var psi = new ProcessStartInfo(_pythonExe,
                "-c \"import spacy; spacy.load('en_core_web_sm')\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── 문장 분석 ────────────────────────────────────────────────────────
    public async Task<List<WordEntry>> AnalyzeSentencesAsync(IEnumerable<string> sentences)
    {
        var sentList = sentences.ToList();
        if (sentList.Count == 0) return new();

        if (!IsAvailable)
            return CSharpFallbackAnalyze(sentList);

        var output = await RunScriptAsync("analyze", sentList.Count, sentList);
        if (string.IsNullOrWhiteSpace(output))
            return CSharpFallbackAnalyze(sentList);

        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(output.Trim());
            if (raw == null || raw.Count == 0)
                return CSharpFallbackAnalyze(sentList);

            return raw.Select(e => new WordEntry
            {
                Lemma = e.GetProperty("lemma").GetString() ?? "",
                Pos = e.GetProperty("pos").GetString() ?? "",
                Frequency = e.GetProperty("frequency").GetInt32(),
                GdexScore = e.GetProperty("gdex_score").GetDouble(),
                ExampleSentence = e.GetProperty("example_sentence").GetString() ?? "",
            }).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NLP] parse error: {ex.Message}");
            return CSharpFallbackAnalyze(sentList);
        }
    }

    // ── 퀴즈 생성 ────────────────────────────────────────────────────────
    public async Task<List<QuizItem>> GenerateQuizAsync(
        List<WordEntry> words,
        int count = 10,
        double difficulty = 0.5)
    {
        if (words.Count < 4) return new();
        if (!IsAvailable) return CSharpFallbackQuiz(words, words, count);

        var payload = words.Select(w => new
        {
            word_id = w.Id,
            lemma = w.Lemma,
            pos = w.Pos,
            frequency = w.Frequency,
            gdex_score = w.GdexScore,
            example_sentence = w.ExampleSentence,
        });

        var diffStr = difficulty.ToString("F2", CultureInfo.InvariantCulture);
        var output = await RunScriptAsync($"quiz {count} {diffStr}", words.Count, payload);

        if (string.IsNullOrWhiteSpace(output))
            return CSharpFallbackQuiz(words, words, count);

        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(output.Trim());
            if (raw == null || raw.Count == 0)
                return CSharpFallbackQuiz(words, words, count);

            return raw.Select(e =>
            {
                long wid = 0;
                if (e.TryGetProperty("word_id", out var wp) &&
                    wp.ValueKind == JsonValueKind.Number)
                    wid = wp.GetInt64();

                var lemma = e.GetProperty("correct").GetString() ?? "";

                if (wid == 0)
                    wid = words.FirstOrDefault(w =>
                        string.Equals(w.Lemma, lemma, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;

                var wordEntry = words.FirstOrDefault(w =>
                    w.Id == wid ||
                    string.Equals(w.Lemma, lemma, StringComparison.OrdinalIgnoreCase));
                var translation = wordEntry?.ExampleTranslated ?? "";

                Debug.WriteLine($"[NLP] 퀴즈 단어: {lemma} | wid: {wid} | 번역문: '{translation}'");

                return new QuizItem
                {
                    WordId = wid,
                    Question = e.GetProperty("question").GetString() ?? "",
                    Sentence = e.GetProperty("sentence").GetString() ?? "",
                    Translation = translation,
                    Correct = lemma,
                    Choices = e.GetProperty("choices").EnumerateArray()
                                   .Select(x => x.GetString() ?? "").ToList(),
                    Pos = e.GetProperty("pos").GetString() ?? "",
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NLP quiz] parse error: {ex.Message}");
            return CSharpFallbackQuiz(words, words, count);
        }
    }

    // ── 공통 스크립트 실행 ───────────────────────────────────────────────
    private async Task<string?> RunScriptAsync(string command, int itemCount, object input)
    {
        var inputJson = JsonSerializer.Serialize(input);

        var psi = _isExe
            ? new ProcessStartInfo(_scriptPath, command)
            : new ProcessStartInfo(_pythonExe!, $"\"{_scriptPath}\" {command}");

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        psi.StandardOutputEncoding = new System.Text.UTF8Encoding(false);

        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteLineAsync(inputJson);
        proc.StandardInput.Close();

        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        output = output.TrimStart('\uFEFF');

        if (!string.IsNullOrWhiteSpace(error))
            Debug.WriteLine($"[NLP stderr/{command}] {error.Trim()}");

        return output;
    }

    // ── C# 폴백 ─────────────────────────────────────────────────────────
    public static List<WordEntry> CSharpFallbackAnalyze(List<string> sentences)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","is","are","was","were","be","been","being","have","has",
            "had","do","does","did","will","would","could","should","may","might",
            "can","of","in","to","for","on","at","by","with","from","as","or","and",
            "but","not","this","that","it","i","you","he","she","we","they","what",
            "which","who","how","when","where","if","just","also","very","so","up",
            "its","my","your","get","got","all","one","two","more","than","then"
        };
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var example = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sent in sentences)
        {
            foreach (var raw in sent.Split(
                new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = System.Text.RegularExpressions.Regex
                    .Replace(raw, @"[^a-zA-Z'-]", "")
                    .Trim('-', '\'').ToLowerInvariant();
                if (w.Length < 3 || stop.Contains(w)) continue;
                freq.TryGetValue(w, out var cur);
                freq[w] = cur + 1;
                if (!example.ContainsKey(w)) example[w] = sent;
            }
        }
        return freq.OrderByDescending(kv => kv.Value)
            .Select(kv => new WordEntry
            {
                Lemma = kv.Key,
                Pos = "WORD",
                Frequency = kv.Value,
                GdexScore = Math.Min(1.0, 0.3 + kv.Value * 0.05),
                ExampleSentence = example.GetValueOrDefault(kv.Key, ""),
            }).ToList();
    }

    public static List<QuizItem> CSharpFallbackQuiz(
        List<WordEntry> pool, List<WordEntry> all, int count)
    {
        if (pool.Count < 4) return new();
        var rng = new Random();
        var items = pool.OrderBy(_ => rng.Next()).Take(count).ToList();
        return items.Select(w =>
        {
            var wrong = all.Where(x => x.Lemma != w.Lemma)
                .OrderBy(x => Math.Abs(x.Frequency - w.Frequency))
                .Take(6).OrderBy(_ => rng.Next()).Take(3)
                .Select(x => x.Lemma).ToList();
            wrong.Add(w.Lemma);
            wrong = wrong.OrderBy(_ => rng.Next()).ToList();

            var blank = w.ExampleSentence.Length > 0
                ? System.Text.RegularExpressions.Regex.Replace(
                    w.ExampleSentence,
                    System.Text.RegularExpressions.Regex.Escape(w.Lemma),
                    "_____",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : $"_____ ({w.Lemma})";

            return new QuizItem
            {
                WordId = w.Id,
                Question = "다음 문장에서 빈칸에 알맞은 단어는?",
                Sentence = blank,
                Translation = w.ExampleTranslated ?? "",
                Correct = w.Lemma,
                Choices = wrong,
                Pos = w.Pos,
            };
        }).ToList();
    }
}
