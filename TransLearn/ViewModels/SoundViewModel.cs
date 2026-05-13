// ============================================================
// SoundViewModel.cs
// 역할 : SoundView의 MVVM ViewModel.
//        오디오 캡처 → STT → 텍스트 품질 필터 → 번역 → DB 저장
//        파이프라인 전체를 조율하고 UI 바인딩 프로퍼티를 제공한다.
//
// 주요 기능
//   1. STT 시작/중지 (선택된 엔진에 따라 Windows or Azure)
//   2. VU 미터: AudioCaptureService.AudioLevelChanged → AudioLevel → UI 바
//   3. 텍스트 품질 필터(TextQualityFilter) + Jaccard 유사도 중복 제거
//   4. 번역(TranslationService) 후 DB 저장 및 RecentItems 표시
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TransLearn.Models;
using TransLearn.Services;
using System.ComponentModel;

namespace TransLearn.ViewModels;

/// <summary>
/// 실행 중인 앱 정보. ComboBox 바인딩용 명시적 클래스.
/// ProcessId = -1 이면 전체 시스템 오디오.
/// </summary>
public class RunningAppInfo
{
    public int    ProcessId { get; set; } = -1;
    public string Name      { get; set; } = "";
    public override string ToString() => Name;
}

public partial class SoundViewModel : ObservableObject
{
    // ── 번역 결과 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _originalText   = "";
    [ObservableProperty] private string _translatedText = "";
    [ObservableProperty] private string _interimText    = "";  // 인식 중간 결과

    // ── 상태 ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusText     = "대기 중...";
    [ObservableProperty] private bool   _sttConfigured;

    /// <summary>현재 STT 엔진 + 설정 상태를 설명하는 UI 텍스트</summary>
    [ObservableProperty] private string _sttInfoText    = "";

    // ── VU 미터 ──────────────────────────────────────────────────────────────
    /// <summary>현재 오디오 레벨 0.0~1.0 (RMS 기반)</summary>
    [ObservableProperty] private double _audioLevel;
    /// <summary>피크 홀드 레벨 0.0~1.0 (서서히 감소)</summary>
    [ObservableProperty] private double _peakLevel;

    /// <summary>VU 바 너비(px). AudioLevel * 220. XAML에서 Width에 바인딩.</summary>
    public double AudioLevelWidth => AudioLevel * 220.0;
    /// <summary>피크 바늘 위치(px). XAML Canvas.Left에 바인딩.</summary>
    public double PeakPosition    => Math.Max(0, PeakLevel * 220.0 - 2);
    /// <summary>dB 표시 문자열. -∞ ~ 0 dB.</summary>
    public string AudioLevelDb    => AudioLevel < 0.001
        ? "-∞ dB" : $"{20 * Math.Log10(AudioLevel):F0} dB";

    // ── 앱 선택 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private RunningAppInfo? _selectedApp;
    public ObservableCollection<RunningAppInfo>  RunningApps { get; } = new();

    // ── 필터 통계 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private int    _totalSentences;
    [ObservableProperty] private int    _discardedCount;
    [ObservableProperty] private string _lastRejectReason = "";

    public string DiscardRateText =>
        TotalSentences == 0 ? "" :
        $"폐기율 {DiscardedCount * 100 / TotalSentences}%  ({DiscardedCount}/{TotalSentences})";

    // ── 최근 번역 기록 ────────────────────────────────────────────────────────
    public ObservableCollection<TranslationRecord> RecentItems { get; } = new();

    // ── 내부 상태 ─────────────────────────────────────────────────────────────
    private readonly TextQualityFilter _filter = new();

    // Jaccard 유사도 중복 감지 — OCR과 동일 방식
    // STT는 같은 내용을 약간 다르게 인식할 수 있으므로 임계값을 0.60으로 낮춤
    private readonly Queue<HashSet<string>> _recentTokenSets = new();
    private const int    JaccardWindowSize     = 3;    // 비교 대상 최근 N 문장
    private const double JaccardThreshold      = 0.60; // 60% 이상 유사 → 중복 폐기

    // 피크 레벨 감소 타이머 (100ms 간격, 0.025씩 감소 → 약 4초 후 소멸)
    private readonly System.Windows.Threading.DispatcherTimer _peakTimer;

    // ── 생성자 ────────────────────────────────────────────────────────────────
    public SoundViewModel()
    {
        if (DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject()))
            return;

        RefreshSttInfo();
        RefreshApps();

        // STT 이벤트 구독
        App.Stt.SentenceRecognized += OnSentenceRecognized;
        App.Stt.Recognizing        += t =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => InterimText = t);
        App.Stt.Error              += msg =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => StatusText = msg);

        // STT 엔진 변경 시 UI 텍스트 갱신
        SttService.EngineChanged += () =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshSttInfo);

        // 오디오 데이터 → STT 피드
        App.AudioCapture.DataAvailable += (chunk, fmt) =>
        {
            if (IsRunning) App.Stt.FeedAudio(chunk, fmt);
        };

        // VU 미터: 오디오 레벨 이벤트 → AudioLevel 프로퍼티 갱신
        App.AudioCapture.AudioLevelChanged += level =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => AudioLevel = level,
                System.Windows.Threading.DispatcherPriority.Background);

        // 피크 홀드 감소 타이머
        _peakTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(100) };
        _peakTimer.Tick += (_, _) =>
        {
            if (PeakLevel > 0) PeakLevel = Math.Max(0, PeakLevel - 0.025);
        };
    }

    // ── STT 정보 텍스트 갱신 ─────────────────────────────────────────────────
    /// <summary>현재 선택된 엔진과 설정 상태를 SttInfoText/SttConfigured에 반영</summary>
    private void RefreshSttInfo()
    {
        SttConfigured = App.Stt.IsConfigured;
        SttInfoText = SttService.SelectedEngine == SttEngineType.Windows
            ? "🖥 Windows 내장 STT 사용 중 (무료·오프라인)"
            : App.Stt.IsConfigured
                ? "☁️ Azure STT 사용 중"
                : "⚠️ Azure STT 키가 설정되지 않았습니다. 환경설정에서 입력해 주세요.";
    }

    // ── 앱 목록 새로고침 ──────────────────────────────────────────────────────
    [RelayCommand]
    public void RefreshApps()
    {
        RunningApps.Clear();
        RunningApps.Add(new RunningAppInfo { ProcessId = -1, Name = "🔊 전체 시스템 오디오 (모든 앱)" });

        foreach (var p in Process.GetProcesses()
            .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .OrderBy(p => p.ProcessName))
        {
            RunningApps.Add(new RunningAppInfo
            {
                ProcessId = p.Id,
                Name = $"  {p.ProcessName}  —  {p.MainWindowTitle}"
            });
        }

        if (SelectedApp == null || !RunningApps.Any(a => a.ProcessId == SelectedApp.ProcessId))
            SelectedApp = RunningApps.FirstOrDefault();
    }

    // ── 시작 / 중지 ───────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning      = true;
        TotalSentences = 0;
        DiscardedCount = 0;
        _recentTokenSets.Clear();
        StatusText = "STT 시작 중...";
        _peakTimer.Start();
        await App.Stt.StartAsync();
        App.AudioCapture.Start();
        StatusText = SttService.SelectedEngine == SttEngineType.Windows
            ? "Windows STT 인식 중..." : "Azure STT 인식 중...";
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        App.AudioCapture.Stop();
        await App.Stt.StopAsync();
        _peakTimer.Stop();
        AudioLevel = 0;
        PeakLevel  = 0;
        IsRunning  = false;
        StatusText = "중지됨";
    }

    // ── AudioLevel 변경 시 파생 프로퍼티 알림 ────────────────────────────────
    partial void OnAudioLevelChanged(double value)
    {
        // 피크 홀드 업데이트
        if (value > PeakLevel) PeakLevel = value;
        OnPropertyChanged(nameof(AudioLevelWidth));
        OnPropertyChanged(nameof(AudioLevelDb));
    }

    partial void OnPeakLevelChanged(double value)
        => OnPropertyChanged(nameof(PeakPosition));

    // ── 문장 수신 처리 (STT → 필터 → 번역 → DB) ─────────────────────────────
    /// <summary>
    /// STT 엔진이 완성 문장을 인식했을 때 호출.
    /// 처리 순서:
    ///   1. TextQualityFilter — 무의미 텍스트 폐기
    ///   2. Jaccard 유사도   — 반복/유사 문장 폐기
    ///   3. 번역 API 호출
    ///   4. DB 저장 + UI 갱신
    /// </summary>
    private async void OnSentenceRecognized(string sentence)
    {
        TotalSentences++;

        // ── STEP 1: 품질 필터 ─────────────────────────────────────────────────
        var result = _filter.Evaluate(sentence, CaptureSource.Sound);
        if (!result.Passed)
        {
            DiscardedCount++;
            LastRejectReason = $"폐기: {result.ReasonDetail}";
            OnPropertyChanged(nameof(DiscardRateText));
            Debug.WriteLine($"[STT DISCARD quality] {result.Reason}: {result.ReasonDetail} | \"{sentence}\"");
            return;
        }

        var clean = result.CleanedText;

        // ── STEP 2: Jaccard 유사도 중복 감지 ─────────────────────────────────
        var tokens = Tokenize(clean);
        if (_recentTokenSets.Any(prev => JaccardSimilarity(tokens, prev) >= JaccardThreshold))
        {
            DiscardedCount++;
            LastRejectReason = "유사 문장 중복 (Jaccard)";
            OnPropertyChanged(nameof(DiscardRateText));
            Debug.WriteLine($"[STT DISCARD jaccard] \"{clean}\"");
            return;
        }

        // 슬라이딩 윈도우에 현재 문장 토큰 추가
        _recentTokenSets.Enqueue(tokens);
        if (_recentTokenSets.Count > JaccardWindowSize) _recentTokenSets.Dequeue();

        LastRejectReason = "";
        OnPropertyChanged(nameof(DiscardRateText));

        // ── STEP 3~4: UI 스레드에서 번역 + DB 저장 ───────────────────────────
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            OriginalText   = clean;
            InterimText    = "";
            StatusText     = "번역 중...";

            var translated = await App.Translation.TranslateAsync(clean);
            TranslatedText = translated;
            StatusText     = SttService.SelectedEngine == SttEngineType.Windows
                ? "Windows STT 인식 중..." : "Azure STT 인식 중...";

            var appName = SelectedApp?.Name ?? "시스템 오디오";
            RecentItems.Insert(0, new TranslationRecord
            {
                OriginalText = clean,
                Translated   = translated,
                CaptureType  = CaptureType.Sound,
                AppName      = appName,
                CapturedAt   = DateTime.Now
            });
            while (RecentItems.Count > 100) RecentItems.RemoveAt(RecentItems.Count - 1);

            _ = Task.Run(() => App.Database.InsertTranslationAsync(
                clean, translated, CaptureType.Sound, appName));
        });
    }

    // ── Jaccard 유사도 헬퍼 ──────────────────────────────────────────────────
    /// <summary>
    /// Jaccard 유사도 = |교집합| / |합집합|.
    /// 1.0에 가까울수록 두 문장이 동일한 내용.
    /// </summary>
    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        int intersection = a.Intersect(b).Count();
        int union        = a.Union(b).Count();
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    /// <summary>문장을 소문자 토큰 집합으로 변환 (3자 이상 알파벳만)</summary>
    private static HashSet<string> Tokenize(string text)
        => text.ToLowerInvariant()
               .Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(w => w.Length >= 3 && w.All(char.IsLetter))
               .ToHashSet();
}
