// ============================================================
// OcrViewModel.cs
// 역할 : OcrView의 MVVM ViewModel.
//        화면 캡처 루프, Jaccard 유사도 중복 감지, 번역, DB 저장 담당.
//        [추가] 번역 자막 오버레이 창 토글
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Drawing;
using TransLearn.Models;
using TransLearn.Services;
using TransLearn.Views;
using System.Diagnostics;

namespace TransLearn.ViewModels;

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

    // ── [추가] 오버레이 상태 ──────────────────────────────────────────────
    [ObservableProperty] private bool _isOverlayVisible;
    private OverlayWindow? _overlayWindow;

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
    [ObservableProperty] private double _similarityThreshold = 0.70;

    private readonly TextQualityFilter _filter = new();
    private CancellationTokenSource? _cts;
    private Rectangle? _captureRegion;
    private OcrRegionWindow? _regionWindow;

    private readonly Queue<HashSet<string>> _recentTokenSets = new();
    private const int SimilarityWindowSize = 3;
    private const int CaptureIntervalMs = 400;   // 번역 API를 기다리지 않고 다음 OCR로 넘어가기 위한 짧은 주기
    private const int RejectIntervalMs = 250;    // 폐기/중복 시 대기시간

    // 번역은 취소하지 않고 큐에서 순서대로 처리한다.
    // 목적: OCR 루프는 빠르게 유지하되, 번역기록 누락을 막는다.
    private readonly Queue<OcrTranslationJob> _translationQueue = new();
    private readonly SemaphoreSlim _translationSignal = new(0);
    private readonly object _translationQueueLock = new();
    private Task? _translationWorkerTask;
    private int _translationSeq = 0;
    private int _latestOverlaySeq = 0;

    private sealed record OcrTranslationJob(
        int Seq,
        string OriginalText,
        string AppName
    );

    private static void OcrLog(string message)
    {
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

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

    // ── [추가] 오버레이 토글 ─────────────────────────────────────────────
    [RelayCommand]
    private void ToggleOverlay()
    {
        if (_overlayWindow == null)
        {
            _overlayWindow = new OverlayWindow();
            _overlayWindow.Closed += (_, _) =>
            {
                _overlayWindow = null;
                IsOverlayVisible = false;
            };
        }

        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
            IsOverlayVisible = false;
        }
        else
        {
            _overlayWindow.Show();
            IsOverlayVisible = true;

            // 현재 번역문이 있으면 바로 표시
            if (!string.IsNullOrWhiteSpace(TranslatedText))
                _overlayWindow.UpdateTranslation(TranslatedText);
        }
    }

    // ── TranslatedText 변경 시 오버레이 자동 업데이트 ────────────────────
    partial void OnTranslatedTextChanged(string value)
    {
        if (_overlayWindow?.IsVisible == true)
            _overlayWindow.UpdateTranslation(value);
    }

    // ── 시작 ──────────────────────────────────────────────────────────────
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


        // 번역 API 미리 호출해서 첫 번역 지연 줄이기
        _ = App.Translation.TranslateAsync("hello", "KO");

        lock (_translationQueueLock)
        {
            _translationQueue.Clear();
        }

        _cts = new CancellationTokenSource();
        _translationWorkerTask = Task.Run(() => ProcessTranslationQueueAsync(_cts.Token));

        try { await RunCaptureLoopAsync(_cts.Token); }
        catch (OperationCanceledException) { }
        finally { IsRunning = false; StatusText = "중지됨"; }
    }

    private async Task RunCaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsPaused)
            {
                await Task.Delay(200, ct);
                continue;
            }

            try
            {
                var sw = Stopwatch.StartNew();

                var hwnd = SelectedWindow?.Hwnd ?? IntPtr.Zero;
                bool useWin = hwnd != IntPtr.Zero;

                string raw;

                if (useWin)
                {
                    raw = await App.OcrCapture.CaptureAndRecognizeAsync(hwnd, _captureRegion);
                }
                else if (_captureRegion.HasValue)
                {
                    raw = await App.OcrCapture.CaptureScreenRegionAsync(_captureRegion.Value);
                }
                else
                {
                    StatusText = "캡처 영역 또는 대상 창을 선택해 주세요.";
                    await Task.Delay(1000, ct);
                    continue;
                }

                OcrLog($"[OCR TIME] 캡처+OCR: {sw.ElapsedMilliseconds}ms");

                TotalCaptures++;

                var result = _filter.Evaluate(raw, CaptureSource.OCR);

                OcrLog($"[OCR TIME] 품질검사: {sw.ElapsedMilliseconds}ms");

                if (!result.Passed)
                {
                    DiscardedCount++;
                    LastRejectReason = $"폐기: {result.ReasonDetail}";
                    OnPropertyChanged(nameof(DiscardRateText));

                    OcrLog($"[OCR TIME] 전체(폐기): {sw.ElapsedMilliseconds}ms / reason={result.ReasonDetail}");

                    await Task.Delay(RejectIntervalMs, ct);
                    continue;
                }

                var tokens = Tokenize(result.CleanedText);
                var similarity = MaxJaccard(tokens);

                OcrLog($"[OCR TIME] 유사도검사: {sw.ElapsedMilliseconds}ms");

                if (similarity >= SimilarityThreshold)
                {
                    DiscardedCount++;
                    LastRejectReason = $"유사 중복 폐기 (유사도 {similarity:P0} ≥ {SimilarityThreshold:P0})";
                    OnPropertyChanged(nameof(DiscardRateText));

                    OcrLog($"[OCR SIMILAR] similarity={similarity:F2}: \"{result.CleanedText[..Math.Min(40, result.CleanedText.Length)]}...\"");
                    OcrLog($"[OCR TIME] 전체(유사중복): {sw.ElapsedMilliseconds}ms");

                    await Task.Delay(RejectIntervalMs, ct);
                    continue;
                }

                _recentTokenSets.Enqueue(tokens);
                if (_recentTokenSets.Count > SimilarityWindowSize)
                    _recentTokenSets.Dequeue();

                OriginalText = result.CleanedText;
                LastRejectReason = "";
                StatusText = "번역 요청 중...";
                OnPropertyChanged(nameof(DiscardRateText));

                EnqueueTranslation(
                    result.CleanedText,
                    SelectedWindow?.Title ?? "화면 캡처");

                OcrLog($"[OCR TIME] 전체(OCR루프/번역요청까지): {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText = $"오류: {ex.Message}";
            }

            await Task.Delay(CaptureIntervalMs, ct);
        }
    }

    private void EnqueueTranslation(string cleanedText, string appName)
    {
        var seq = Interlocked.Increment(ref _translationSeq);

        // 오버레이는 최신 자막만 표시하기 위해 최신 번호만 갱신한다.
        // 번역 자체는 취소하지 않으므로 번역기록 누락 위험이 줄어든다.
        Interlocked.Exchange(ref _latestOverlaySeq, seq);

        var job = new OcrTranslationJob(
            Seq: seq,
            OriginalText: cleanedText,
            AppName: appName
        );

        lock (_translationQueueLock)
        {
            _translationQueue.Enqueue(job);
        }

        _translationSignal.Release();

        OcrLog($"[OCR QUEUE] 번역 큐 추가 seq={seq}: \"{cleanedText[..Math.Min(40, cleanedText.Length)]}...\"");
    }

    private async Task ProcessTranslationQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            OcrTranslationJob job;

            try
            {
                await _translationSignal.WaitAsync(ct);

                lock (_translationQueueLock)
                {
                    if (!_translationQueue.TryDequeue(out job!))
                        continue;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var sw = Stopwatch.StartNew();

            try
            {
                var translated = await App.Translation.TranslateAsync(job.OriginalText, "KO", ct);

                OcrLog($"[OCR TIME] 번역 seq={job.Seq}: {sw.ElapsedMilliseconds}ms");

                var latestSeq = Volatile.Read(ref _latestOverlaySeq);

                if (job.Seq == latestSeq)
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        TranslatedText = translated;
                        StatusText = "캡처 중...";
                    });

                    OcrLog($"[OCR TIME] 오버레이 표시 seq={job.Seq}: {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    OcrLog($"[OCR RECORD] 기록 저장 예정 seq={job.Seq}, 최신 seq={latestSeq}");
                }

                // 속도 최적화를 위해 OCR 루프에서는 DB 저장을 기다리지 않는다.
                // 대신 번역 작업자 안에서는 await 해서 기록 저장 자체는 보장한다.
                await App.Database.InsertTranslationAsync(
                    job.OriginalText,
                    translated,
                    CaptureType.OCR,
                    job.AppName);

                OcrLog($"[OCR DB] 번역기록 저장 완료 seq={job.Seq}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OcrLog($"[OCR ERROR] 번역/저장 실패 seq={job.Seq}: {ex.Message}");
            }
        }
    }



    // ── 유사도 헬퍼 ──────────────────────────────────────────────────────
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
    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        IsRunning = false;
        IsPaused = false;
    }

    [RelayCommand]
    private void ResetStats()
    {
        TotalCaptures = 0;
        DiscardedCount = 0;
        OnPropertyChanged(nameof(DiscardRateText));
    }
}