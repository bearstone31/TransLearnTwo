// ============================================================
// SoundViewModel.cs
// [개선] 유튜브 자막처럼 빠른 느낌
//   - 인터림 타이머 300ms
//   - 강제 번역 타이머 3초
//   - 번역 중 "..." 애니메이션
//   - 확정 문장 오면 깔끔하게 교체
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TransLearn.Models;
using TransLearn.Services;
using System.ComponentModel;
using TransLearn.Views;

namespace TransLearn.ViewModels;

public class RunningAppInfo
{
    public int ProcessId { get; set; } = -1;
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public partial class SoundViewModel : ObservableObject
{
    [ObservableProperty] private string _originalText = "";
    [ObservableProperty] private string _translatedText = "";
    [ObservableProperty] private string _interimText = "";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "대기 중...";
    [ObservableProperty] private bool _sttConfigured;
    [ObservableProperty] private string _sttInfoText = "";

    [ObservableProperty] private bool _isOverlayVisible;
    private OverlayWindow? _overlayWindow;

    [ObservableProperty] private double _audioLevel;
    [ObservableProperty] private double _peakLevel;

    public double AudioLevelWidth => AudioLevel * 220.0;
    public double PeakPosition => Math.Max(0, PeakLevel * 220.0 - 2);
    public string AudioLevelDb => AudioLevel < 0.001
        ? "-∞ dB" : $"{20 * Math.Log10(AudioLevel):F0} dB";

    [ObservableProperty] private RunningAppInfo? _selectedApp;
    public ObservableCollection<RunningAppInfo> RunningApps { get; } = new();

    [ObservableProperty] private int _totalSentences;
    [ObservableProperty] private int _discardedCount;
    [ObservableProperty] private string _lastRejectReason = "";

    public string DiscardRateText =>
        TotalSentences == 0 ? "" :
        $"폐기율 {DiscardedCount * 100 / TotalSentences}%  ({DiscardedCount}/{TotalSentences})";

    public ObservableCollection<TranslationRecord> RecentItems { get; } = new();

    private readonly TextQualityFilter _filter = new();
    private readonly Queue<HashSet<string>> _recentTokenSets = new();
    private const int JaccardWindowSize = 3;
    private const double JaccardThreshold = 0.60;

    private readonly System.Windows.Threading.DispatcherTimer _peakTimer = null!;
    private readonly System.Windows.Threading.DispatcherTimer _interimTimer = null!;
    private readonly System.Windows.Threading.DispatcherTimer _forceTranslateTimer = null!;
    private readonly System.Windows.Threading.DispatcherTimer _dotAnimTimer = null!;

    private string _lastTranslatedInterim = "";
    private bool _isTranslating = false;
    private int _dotCount = 0;
    private string _pendingTranslation = ""; // 번역 완료 후 표시 대기

    public SoundViewModel()
    {
        if (DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject()))
            return;

        RefreshSttInfo();
        RefreshApps();

        App.Stt.SentenceRecognized += OnSentenceRecognized;

        App.Stt.Recognizing += t =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                InterimText = t;
                if (!string.IsNullOrWhiteSpace(t))
                {
                    _interimTimer.Stop();
                    _interimTimer.Start();
                }
            });

        App.Stt.Error += msg =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => StatusText = msg);

        SttService.EngineChanged += () =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshSttInfo);

        App.AudioCapture.DataAvailable += (chunk, fmt) =>
        {
            if (IsRunning) App.Stt.FeedAudio(chunk, fmt);
        };

        App.AudioCapture.AudioLevelChanged += level =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => AudioLevel = level,
                System.Windows.Threading.DispatcherPriority.Background);

        // 피크 홀드 타이머
        _peakTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(100) };
        _peakTimer.Tick += (_, _) =>
        {
            if (PeakLevel > 0) PeakLevel = Math.Max(0, PeakLevel - 0.025);
        };

        // 침묵 감지 → 번역 (300ms)
        _interimTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(300) };
        _interimTimer.Tick += async (_, _) =>
        {
            _interimTimer.Stop();
            await TranslateInterimAsync();
        };

        // 강제 번역 (3초마다 — 말이 길어도 중간중간 번역)
        _forceTranslateTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(3000) };
        _forceTranslateTimer.Tick += async (_, _) =>
        {
            await TranslateInterimAsync(force: true);
        };

        // 번역 중 "..." 애니메이션 (400ms 간격)
        _dotAnimTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(400) };
        _dotAnimTimer.Tick += (_, _) =>
        {
            _dotCount = (_dotCount + 1) % 4;
            // 오버레이는 이전 번역 유지 (번역 중... 표시 안 함)
        };
    }

    // ── 중간 결과 번역 ───────────────────────────────────────────────────────
    private async Task TranslateInterimAsync(bool force = false)
    {
        var text = InterimText?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!force && text == _lastTranslatedInterim) return;
        if (_isTranslating) return;

        _isTranslating = true;
        _lastTranslatedInterim = text;
        _dotCount = 0;
        _dotAnimTimer.Start();

        try
        {
            var translated = await App.Translation.TranslateAsync(text);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                TranslatedText = translated;
            }
        }
        catch { }
        finally
        {
            _isTranslating = false;
            _dotAnimTimer.Stop();
        }
    }

    // ── STT 정보 갱신 ────────────────────────────────────────────────────────
    private void RefreshSttInfo()
    {
        SttConfigured = App.Stt.IsConfigured;
        SttInfoText = SttService.SelectedEngine == SttEngineType.Windows
            ? "🖥 Windows 내장 STT 사용 중 (무료·오프라인)"
            : App.Stt.IsConfigured
                ? "☁️ Azure STT 사용 중"
                : "⚠️ Azure STT 키가 설정되지 않았습니다. 환경설정에서 입력해 주세요.";
    }

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

    // ── 오버레이 토글 ────────────────────────────────────────────────────────
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
            if (!string.IsNullOrWhiteSpace(TranslatedText))
                _overlayWindow.UpdateTranslation(TranslatedText);
        }
    }

    partial void OnTranslatedTextChanged(string value)
    {
        if (_overlayWindow?.IsVisible == true)
            _overlayWindow.UpdateTranslation(value);
    }

    // ── 시작 / 중지 ──────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        TotalSentences = 0;
        DiscardedCount = 0;
        _recentTokenSets.Clear();
        _lastTranslatedInterim = "";
        StatusText = "STT 시작 중...";
        _peakTimer.Start();
        _forceTranslateTimer.Start();
        await App.Stt.StartAsync();
        App.AudioCapture.Start();
        StatusText = SttService.SelectedEngine == SttEngineType.Windows
            ? "Windows STT 인식 중..." : "Azure STT 인식 중...";
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _interimTimer.Stop();
        _forceTranslateTimer.Stop();
        _dotAnimTimer.Stop();
        App.AudioCapture.Stop();
        await App.Stt.StopAsync();
        _peakTimer.Stop();
        AudioLevel = 0;
        PeakLevel = 0;
        IsRunning = false;
        StatusText = "중지됨";
    }

    partial void OnAudioLevelChanged(double value)
    {
        if (value > PeakLevel) PeakLevel = value;
        OnPropertyChanged(nameof(AudioLevelWidth));
        OnPropertyChanged(nameof(AudioLevelDb));
    }

    partial void OnPeakLevelChanged(double value)
        => OnPropertyChanged(nameof(PeakPosition));

    // ── 확정 문장 처리 ───────────────────────────────────────────────────────
    private async void OnSentenceRecognized(string sentence)
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _interimTimer.Stop();
            _lastTranslatedInterim = "";
        });

        var sentences = SplitSentences(sentence);

        foreach (var sent in sentences)
        {
            TotalSentences++;

            var result = _filter.Evaluate(sent, CaptureSource.Sound);
            if (!result.Passed)
            {
                DiscardedCount++;
                LastRejectReason = $"폐기: {result.ReasonDetail}";
                OnPropertyChanged(nameof(DiscardRateText));
                continue;
            }

            var clean = result.CleanedText;

            var tokens = Tokenize(clean);
            if (_recentTokenSets.Any(prev => JaccardSimilarity(tokens, prev) >= JaccardThreshold))
            {
                DiscardedCount++;
                LastRejectReason = "유사 문장 중복";
                OnPropertyChanged(nameof(DiscardRateText));
                continue;
            }

            _recentTokenSets.Enqueue(tokens);
            if (_recentTokenSets.Count > JaccardWindowSize) _recentTokenSets.Dequeue();

            LastRejectReason = "";
            OnPropertyChanged(nameof(DiscardRateText));

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                OriginalText = clean;
                InterimText = "";
                StatusText = "번역 중...";

                var translated = await App.Translation.TranslateAsync(clean);
                TranslatedText = translated;

                StatusText = SttService.SelectedEngine == SttEngineType.Windows
                    ? "Windows STT 인식 중..." : "Azure STT 인식 중...";

                var appName = SelectedApp?.Name ?? "시스템 오디오";
                RecentItems.Insert(0, new TranslationRecord
                {
                    OriginalText = clean,
                    Translated = translated,
                    CaptureType = CaptureType.Sound,
                    AppName = appName,
                    CapturedAt = DateTime.Now
                });
                while (RecentItems.Count > 100) RecentItems.RemoveAt(RecentItems.Count - 1);

                _ = Task.Run(() => App.Database.InsertTranslationAsync(
                    clean, translated, CaptureType.Sound, appName));
            });
        }
    }

    // ── 문장 분리 헬퍼 ──────────────────────────────────────────────────────
    private static List<string> SplitSentences(string text)
    {
        var raw = System.Text.RegularExpressions.Regex
            .Split(text.Trim(), @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var result = new List<string>();
        var buffer = "";

        foreach (var s in raw)
        {
            buffer = string.IsNullOrWhiteSpace(buffer) ? s : buffer + " " + s;
            if (buffer.Split(' ').Length >= 4)
            {
                result.Add(buffer);
                buffer = "";
            }
        }
        if (!string.IsNullOrWhiteSpace(buffer))
            result.Add(buffer);

        return result.Count > 0 ? result : new List<string> { text.Trim() };
    }

    // ── Jaccard 헬퍼 ────────────────────────────────────────────────────────
    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        int intersection = a.Intersect(b).Count();
        int union = a.Union(b).Count();
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
        => text.ToLowerInvariant()
               .Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(w => w.Length >= 3 && w.All(char.IsLetter))
               .ToHashSet();
}
