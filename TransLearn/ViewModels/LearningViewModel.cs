// ============================================================
// LearningViewModel.cs
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TransLearn.Models;
using TransLearn.Services;

namespace TransLearn.ViewModels;

public partial class LearningViewModel : ObservableObject
{
    private readonly NlpBridgeService _nlp = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _nlpAvailable;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _activeTab;

    // 난이도 선택 (1=쉬움, 2=보통, 3=어려움, 4=매우어려움)
    [ObservableProperty] private int _selectedDifficulty = 2;

    // 난이도 숫자 → 0.0~1.0 변환
    private double DifficultyValue => SelectedDifficulty switch
    {
        1 => 0.1,
        2 => 0.4,
        3 => 0.7,
        4 => 1.0,
        _ => 0.4
    };

    public ObservableCollection<WordEntry> Words { get; } = new();
    public ObservableCollection<QuizItem> Quiz { get; } = new();
    public ObservableCollection<string> SourceApps { get; } = new();

    [ObservableProperty] private int _quizScore;
    [ObservableProperty] private int _quizTotal;
    [ObservableProperty] private bool _quizFinished;
    [ObservableProperty] private int _currentQuizIndex;
    [ObservableProperty] private QuizItem? _currentQuiz;
    [ObservableProperty] private string _selectedSourceApp = "전체";

    [ObservableProperty] private string _sortColumn = "Date";
    [ObservableProperty] private bool _sortAscending = false;

    private bool _isRefreshingSourceApps;
    private bool _isLoadingWords;
    public string DateSortArrow =>
        SortColumn == "Date" ? (SortAscending ? "▲" : "▼") : "";

    public string RatingSortArrow =>
        SortColumn == "Rating" ? (SortAscending ? "▲" : "▼") : "";

    public LearningViewModel()
    {
        NlpAvailable = _nlp.IsAvailable;
        StatusText = NlpAvailable
            ? "📊 학습 데이터 분석 준비 완료"
            : "⚠️ Python/spaCy 미설치. 기본 분석 모드로 동작합니다.";

        SourceApps.Add("전체");
        _ = RefreshSourceAppsAsync();
    }

    partial void OnSelectedSourceAppChanged(string value)
    {
        if (_isRefreshingSourceApps) return;
        if (_isLoadingWords) return;

        _ = LoadWordsCoreAsync();
    }

    public async Task RefreshSourceAppsAsync()
    {
        try
        {
            _isRefreshingSourceApps = true;

            var current = SelectedSourceApp;

            var apps = await App.Database.GetWordSourceAppsAsync();

            SourceApps.Clear();
            SourceApps.Add("전체");

            foreach (var app in apps)
            {
                if (!SourceApps.Contains(app))
                    SourceApps.Add(app);
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                SelectedSourceApp = "전체";
            }
            else if (SourceApps.Contains(current))
            {
                SelectedSourceApp = current;
            }
            else
            {
                // 현재 선택한 앱이 번역기록에서 전부 삭제된 경우
                // 앱 목록에서도 사라졌으므로 전체로 돌림
                SelectedSourceApp = "전체";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"앱 목록 로드 오류: {ex.Message}";
        }
        finally
        {
            _isRefreshingSourceApps = false;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 1. 미분석 문장 분석
    // ────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        IsLoading = true;
        StatusText = "미분석 번역 기록을 불러오는 중...";
        try
        {
            var records = await Task.Run(() =>
                App.Database.GetUnanalyzedTranslationsAsync(limit: 1000).GetAwaiter().GetResult());

            if (records.Count == 0)
            {
                StatusText = "새로 분석할 번역 기록이 없습니다.";
                return;
            }

            StatusText = $"NLP 분석 중... (새 문장 {records.Count}개)";

            List<WordEntry> words;
            if (_nlp.IsAvailable)
                words = await _nlp.AnalyzeSentencesAsync(records.Select(r => r.OriginalText));
            else
                words = SimpleFrequencyCount(records.Select(r => r.OriginalText));

            foreach (var w in words)
            {
                var example = records.FirstOrDefault(r =>
                    r.OriginalText.Contains(w.ExampleSentence, StringComparison.OrdinalIgnoreCase));
                w.ExampleTranslated = example?.Translated;
                await App.Database.UpsertWordAsync(w, example?.Id);
            }

            await App.Database.MarkAnalyzedAsync(records.Select(r => r.Id));
            await LoadWordsFromDbAsync();
            StatusText = $"✅ {words.Count}개 단어 분석 완료  (문장 {records.Count}개 처리됨)";
        }
        catch (Exception ex) { StatusText = $"오류: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. DB에서 단어장 로드
    // ────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task LoadWordsFromDbAsync()
    {
        _isLoadingWords = true;
        IsLoading = true;

        try
        {
            // 단어장 새로고침할 때는 앱 목록도 새로고침
            await RefreshSourceAppsAsync();

            await LoadWordsCoreAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"단어 로드 오류: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _isLoadingWords = false;
        }
    }

    private async Task LoadWordsCoreAsync()
    {
        try
        {
            var selectedApp = SelectedSourceApp;

            var appFilter =
                string.IsNullOrWhiteSpace(selectedApp) || selectedApp == "전체"
                    ? null
                    : selectedApp;

            var data = await App.Database.GetWordEntriesAsync(appName: appFilter);

            Words.Clear();

            foreach (var w in data)
                Words.Add(w);

            ApplyWordSort();

            StatusText = appFilter == null
                ? $"📚 전체 단어 {Words.Count}개 로드됨"
                : $"📚 [{selectedApp}] 단어 {Words.Count}개 로드됨";
        }
        catch (Exception ex)
        {
            StatusText = $"단어 로드 오류: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SortWords(string column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = false;
        }

        ApplyWordSort();

        OnPropertyChanged(nameof(DateSortArrow));
        OnPropertyChanged(nameof(RatingSortArrow));
    }

    private void ApplyWordSort()
    {
        IEnumerable<WordEntry> sorted = SortColumn switch
        {
            "Date" => SortAscending
                ? Words.OrderBy(w => w.CreatedAt)
                : Words.OrderByDescending(w => w.CreatedAt),

            "Rating" => SortAscending
                ? Words.OrderBy(w => w.UserScore)
                : Words.OrderByDescending(w => w.UserScore),

            _ => Words
        };

        var sortedList = sorted.ToList();

        Words.Clear();
        foreach (var word in sortedList)
            Words.Add(word);
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. 단어장 평가
    // ────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task RateWordAsync(WordEntry word) => await RateWord(word, thumbsUp: true);

    [RelayCommand]
    private async Task DislikeWordAsync(WordEntry word) => await RateWord(word, thumbsUp: false);

    [RelayCommand]
    private async Task DeleteWordAsync(WordEntry word)
    {
        if (word == null) return;
        await App.Database.DeleteWordAsync(word.Id);
        Words.Remove(word);
    }

    private async Task RateWord(WordEntry word, bool thumbsUp)
    {
        await App.Database.RateWordAsync(word.Id, thumbsUp);
        if (thumbsUp) word.ThumbsUp++;
        else word.ThumbsDown++;
        var index = Words.IndexOf(word);
        if (index >= 0)
        {
            Words.RemoveAt(index);
            Words.Insert(index, word);
        }

    }

    // ────────────────────────────────────────────────────────────────────
    // 4. 퀴즈 생성
    // ────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task GenerateQuizAsync()
    {
        if (Words.Count < 4)
        {
            StatusText = "퀴즈를 생성하려면 최소 4개 이상의 단어가 필요합니다.";
            return;
        }
        IsLoading = true;
        try
        {
            var candidates = Words.OrderByDescending(w => w.LearnPriority).Take(50).ToList();
            var pool = candidates.Count >= 10 ? candidates : Words.ToList();

            List<QuizItem> items;
            if (_nlp.IsAvailable)
                items = await _nlp.GenerateQuizAsync(pool, 10, DifficultyValue);
            else
                items = GenerateFallbackQuiz(pool, Words.ToList(), 10);

            Quiz.Clear();
            foreach (var q in items) Quiz.Add(q);

            QuizTotal = Quiz.Count;
            QuizScore = 0;
            QuizFinished = false;
            CurrentQuizIndex = 0;
            CurrentQuiz = Quiz.FirstOrDefault();
            ActiveTab = 1;

            var diffLabel = SelectedDifficulty switch
            {
                1 => "쉬움",
                2 => "보통",
                3 => "어려움",
                4 => "매우 어려움",
                _ => "보통"
            };
            StatusText = $"📝 {Quiz.Count}개 퀴즈 생성됨  (난이도: {diffLabel})";
        }
        finally { IsLoading = false; }
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. 퀴즈 답변
    // ────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task AnswerQuizAsync(string answer)
    {
        if (CurrentQuiz == null) return;
        bool correct = answer == CurrentQuiz.Correct;
        CurrentQuiz.UserAnswer = correct;

        var word = Words.FirstOrDefault(w => w.Id == CurrentQuiz.WordId);
        if (word != null) await RateWord(word, thumbsUp: correct);
        if (correct) QuizScore++;

        CurrentQuizIndex++;
        if (CurrentQuizIndex < Quiz.Count)
            CurrentQuiz = Quiz[CurrentQuizIndex];
        else
        {
            QuizFinished = true;
            CurrentQuiz = null;
            StatusText = $"🏆 퀴즈 완료! {QuizScore}/{QuizTotal} 정답";
        }
    }

    [RelayCommand]
    private async Task RateQuizItemAsync(QuizItem item)
    {
        var word = Words.FirstOrDefault(w => w.Id == item.WordId);
        if (word != null) await RateWord(word, thumbsUp: true);
    }

    [RelayCommand]
    private async Task DislikeQuizItemAsync(QuizItem item)
    {
        var word = Words.FirstOrDefault(w => w.Id == item.WordId);
        if (word != null) await RateWord(word, thumbsUp: false);
    }

    // ────────────────────────────────────────────────────────────────────
    // 폴백
    // ────────────────────────────────────────────────────────────────────
    private static List<WordEntry> SimpleFrequencyCount(IEnumerable<string> sentences)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","is","are","was","were","be","been","being",
            "have","has","had","do","does","did","will","would","could","should",
            "may","might","shall","can","of","in","to","for","on","at","by",
            "with","from","as","or","and","but","not","this","that","it","i","you",
            "he","she","we","they","what","which","who","how","when","where","if"
        };
        var freq = new Dictionary<string, (int count, string example)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sentences)
        {
            foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = System.Text.RegularExpressions.Regex.Replace(w, @"[^a-zA-Z]", "").ToLowerInvariant();
                if (clean.Length < 4 || stopWords.Contains(clean)) continue;
                if (!freq.ContainsKey(clean)) freq[clean] = (1, s);
                else freq[clean] = (freq[clean].count + 1, freq[clean].example);
            }
        }
        return freq.Where(kv => kv.Value.count > 1)
            .OrderByDescending(kv => kv.Value.count)
            .Select(kv => new WordEntry
            {
                Lemma = kv.Key,
                Pos = "WORD",
                Frequency = kv.Value.count,
                GdexScore = 0.5,
                ExampleSentence = kv.Value.example,
            }).ToList();
    }

    private static List<QuizItem> GenerateFallbackQuiz(
        List<WordEntry> prioritized, List<WordEntry> allWords, int count)
    {
        var rng = new Random();
        var pool = prioritized.Count >= count ? prioritized : allWords;
        return pool.Take(count).Select(w =>
        {
            var wrong = allWords.Where(x => x.Lemma != w.Lemma)
                .OrderBy(x => Math.Abs(x.Frequency - w.Frequency))
                .Take(6).OrderBy(_ => rng.Next()).Take(3)
                .Select(x => x.Lemma).ToList();
            wrong.Add(w.Lemma);
            wrong = wrong.OrderBy(_ => rng.Next()).ToList();
            return new QuizItem
            {
                WordId = w.Id,
                Question = "다음 문장에서 빈칸에 알맞은 단어는?",
                Sentence = w.ExampleSentence.Replace(w.Lemma, "_____", StringComparison.OrdinalIgnoreCase),
                Correct = w.Lemma,
                Choices = wrong,
                Pos = w.Pos
            };
        }).ToList();
    }
}
