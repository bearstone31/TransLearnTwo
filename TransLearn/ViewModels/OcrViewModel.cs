// ============================================================
// OcrViewModel.cs
// 역할 : OcrView의 MVVM ViewModel.
//        화면 캡처 루프, Jaccard 유사도 중복 감지, 번역, DB 저장 담당.
//
// 동작 흐름
//   StartCommand → 폴링 루프 시작 (1초 간격)
//     → OcrCaptureService.CaptureAsync() → 텍스트 추출
//     → Jaccard 유사도 체크 (SimilarityThreshold=0.70)
//     → TextQualityFilter.Evaluate()
//     → TranslationService.TranslateAsync()
//     → DatabaseService.InsertTranslationAsync()
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Drawing;
using TransLearn.Models;
using TransLearn.Services;
using TransLearn.Views;

namespace TransLearn.ViewModels;

/// <summary>XAML DisplayMemberPath 바인딩을 위해 튜플 대신 명시적 클래스 사용</summary>
public class WindowInfo
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = "";
    public override string ToString() => Title;
}

public partial class OcrViewModel : ObservableObject
{
    // ── 상태 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _originalText = "";
    [ObservableProperty] private string _translatedText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _statusText = "대기 중...";
    [ObservableProperty] private string _captureRegionInfo = "캡처 영역 미설정";
    [ObservableProperty] private bool _hasRegion;

    // ── 필터 통계 ─────────────────────────────────────────────────────────
    [ObservableProperty] private int _totalCaptures;
    [ObservableProperty] private int _discardedCount;
    [ObservableProperty] private string _lastRejectReason = "";

    public string DiscardRateText =>
        TotalCaptures == 0 ? "" :
        $"폐기율 {DiscardedCount * 100 / TotalCaptures}%  ({DiscardedCount}/{TotalCaptures})";

    // ── 창 선택 ──────────────────────────────────────────────────────────
    [ObservableProperty] private WindowInfo? _selectedWindow;
    public ObservableCollection<WindowInfo> Windows { get; } = new();

    // ── 유사도 설정 ──────────────────────────────────────────────────────
    /// <summary>Jaccard 유사도가 이 값 이상이면 중복으로 폐기 (0.0~1.0, 기본 0.70)</summary>
    [ObservableProperty] private double _similarityThreshold = 0.70;

    private readonly TextQualityFilter _filter = new();
    private CancellationTokenSource? _cts;
    private Rectangle? _captureRegion;
    private OcrRegionWindow? _regionWindow;

    // 유사도 비교용: 마지막으로 통과한 문장의 토큰 집합 (슬라이딩 윈도우 3)
    private readonly Queue<HashSet<string>> _recentTokenSets = new();
    private const int SimilarityWindowSize = 3;

    // ── 창 목록 새로고침 ──────────────────────────────────────────────────
    [RelayCommand]
    public void RefreshWindows()
    {
        Windows.Clear();
        Windows.Add(new WindowInfo { Hwnd = IntPtr.Zero, Title = "── 창 선택 안 함 (화면 영역만 사용) ──" });
        foreach (var (hwnd, title) in App.OcrCapture.GetVisibleWindows())
            Windows.Add(new WindowInfo { Hwnd = hwnd, Title = title });
        if (SelectedWindow == null && Windows.Count > 0)
            SelectedWindow = Windows[0];
    }

    [RelayCommand]
    private void SetCaptureRegion()
    {
        _regionWindow?.Close();
        _regionWindow = new OcrRegionWindow();
        _regionWindow.RegionSelected += rect =>
        {
            _captureRegion = rect;
            HasRegion = true;
            CaptureRegionInfo = $"영역: {rect.X},{rect.Y}  {rect.Width}×{rect.Height}";
        };
        _regionWindow.Show();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        IsPaused = false;
        StatusText = "캡처 중...";
        TotalCaptures = 0;
        DiscardedCount = 0;
        _recentTokenSets.Clear();
        _cts = new CancellationTokenSource();
        try { await RunCaptureLoopAsync(_cts.Token); }
        catch (OperationCanceledException) { }
        finally { IsRunning = false; StatusText = "중지됨"; }
    }

    private async Task RunCaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsPaused) { await Task.Delay(500, ct); continue; }

            try
            {
                // ── 1. 화면 캡처 ─────────────────────────────────────────
                var hwnd = SelectedWindow?.Hwnd ?? IntPtr.Zero;
                bool useWin = hwnd != IntPtr.Zero;

                string raw;
                if (useWin)
                    raw = await App.OcrCapture.CaptureAndRecognizeAsync(hwnd, _captureRegion);
                else if (_captureRegion.HasValue)
                    raw = await App.OcrCapture.CaptureScreenRegionAsync(_captureRegion.Value);
                else
                {
                    StatusText = "캡처 영역 또는 대상 창을 선택해 주세요.";
                    await Task.Delay(1000, ct);
                    continue;
                }

                TotalCaptures++;

                // ── 2. 기본 품질 필터 ─────────────────────────────────────
                var result = _filter.Evaluate(raw, CaptureSource.OCR);
                if (!result.Passed)
                {
                    DiscardedCount++;
                    LastRejectReason = $"폐기: {result.ReasonDetail}";
                    OnPropertyChanged(nameof(DiscardRateText));
                    await Task.Delay(1500, ct);
                    continue;
                }

                // ── 3. 유사도 기반 중복 폐기 ─────────────────────────────
                //    Jaccard 유사도로 최근 SimilarityWindowSize개 문장과 비교
                var tokens = Tokenize(result.CleanedText);
                var similarity = MaxJaccard(tokens);
                if (similarity >= SimilarityThreshold)
                {
                    DiscardedCount++;
                    LastRejectReason = $"유사 중복 폐기 (유사도 {similarity:P0} ≥ {SimilarityThreshold:P0})";
                    OnPropertyChanged(nameof(DiscardRateText));
                    System.Diagnostics.Debug.WriteLine(
                        $"[OCR SIMILAR] similarity={similarity:F2}: \"{result.CleanedText[..Math.Min(40, result.CleanedText.Length)]}...\"");
                    await Task.Delay(1500, ct);
                    continue;
                }

                // 통과 → 슬라이딩 윈도우에 추가
                _recentTokenSets.Enqueue(tokens);
                if (_recentTokenSets.Count > SimilarityWindowSize)
                    _recentTokenSets.Dequeue();

                // ── 4. 번역 → DB 저장 ─────────────────────────────────────
                OriginalText = result.CleanedText;
                LastRejectReason = "";
                StatusText = "번역 중...";
                OnPropertyChanged(nameof(DiscardRateText));

                var translated = await App.Translation.TranslateAsync(result.CleanedText);
                TranslatedText = translated;
                StatusText = "캡처 중...";

                _ = Task.Run(() => App.Database.InsertTranslationAsync(
                    result.CleanedText, translated, CaptureType.OCR,
                    SelectedWindow?.Title ?? "화면 캡처"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText = $"오류: {ex.Message}";
            }

            await Task.Delay(1500, ct);
        }
    }

    // ── 유사도 헬퍼 ──────────────────────────────────────────────────────

    /// <summary>소문자 단어 토큰 집합으로 변환 (3자 이상 알파벳)</summary>
    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = System.Text.RegularExpressions.Regex
                .Replace(word, @"[^a-zA-Z]", "").ToLowerInvariant();
            if (clean.Length >= 3) set.Add(clean);
        }
        return set;
    }

    /// <summary>최근 통과 문장들과의 최대 Jaccard 유사도 반환</summary>
    private double MaxJaccard(HashSet<string> tokens)
    {
        if (tokens.Count == 0 || _recentTokenSets.Count == 0) return 0.0;
        double max = 0.0;
        foreach (var prev in _recentTokenSets)
        {
            if (prev.Count == 0) continue;
            int intersect = tokens.Count(t => prev.Contains(t));
            int union = tokens.Count + prev.Count - intersect;
            double j = union == 0 ? 0 : (double)intersect / union;
            if (j > max) max = j;
        }
        return max;
    }

    // ── 커맨드 ───────────────────────────────────────────────────────────
    [RelayCommand] private void Pause() => IsPaused = !IsPaused;
    [RelayCommand] private void Stop() { _cts?.Cancel(); IsRunning = false; IsPaused = false; }

    [RelayCommand]
    private void ResetStats()
    {
        TotalCaptures = 0;
        DiscardedCount = 0;
        OnPropertyChanged(nameof(DiscardRateText));
    }
}
