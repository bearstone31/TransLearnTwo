// ============================================================
// SettingsViewModel.cs
// 역할 : SettingsView의 MVVM ViewModel.
//        번역 API 키, STT 엔진 선택, Azure STT 키를 관리.
//        API 키는 SecureKeyStorage(DPAPI 암호화)에 영속 저장.
//
// 새로 추가된 기능
//   STT 엔진 선택: 현재는 Azure STT 하나만 노출 (Windows 구현은 코드에 그대로 보존)
//   [추가] 기억노트(교정 사전): txt 파일 불러오기 / 편집 / on-off
//   [추가] 문장 끊기 설정: 침묵 판정 시간 / 최대 세그먼트 시간 / 자막 문장 수
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TransLearn.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace TransLearn.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // ── 번역 설정 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _deepLKey = "";
    [ObservableProperty] private bool _deepLKeySet;
    [ObservableProperty] private string _deepLStatus = "";
    [ObservableProperty] private string _selectedProvider = "Google (무료)";
    public List<string> Providers { get; } = new() { "Google (무료)", "DeepL API" };
    [ObservableProperty] private int _contextSize = 3;

    // ── DeepL API 키 ──────────────────────────────────────────────────────────
    // (SettingsView.xaml.cs에서 PasswordBox.PasswordChanged로 연결)

    // ── STT 엔진 선택 ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _selectedSttEngine = "Azure STT (클라우드·고정밀)";

    // [수정] Windows 내장 STT는 인식 품질 문제로 목록에서 잠시 제외한다.
    //        SttService 쪽 Windows 구현 코드는 그대로 살아 있으므로,
    //        아래 주석만 풀면 언제든 선택지를 되살릴 수 있다.
    public List<string> SttEngines { get; } = new()
    {
        "Azure STT (클라우드·고정밀)"
        // "Windows 내장 STT (무료·오프라인)",
    };

    [ObservableProperty] private string _sttEngineStatus = "";

    // Azure 키 섹션 표시 여부 (Azure 선택 시만 표시)
    public bool ShowAzureSection => SelectedSttEngine == "Azure STT (클라우드·고정밀)";

    // ── Azure STT 키 ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _azureKey = "";
    [ObservableProperty] private string _azureRegion = "eastus";
    [ObservableProperty] private bool _azureKeySet;
    [ObservableProperty] private string _azureStatus = "";

    // ── [추가] 문장 끊기(세그먼트) 설정 ────────────────────────────────────────
    [ObservableProperty] private int _sttSilenceTimeoutMs = SttSettings.DefaultSilenceTimeoutMs;
    [ObservableProperty] private int _sttMaxSegmentMs = SttSettings.DefaultMaxSegmentMs;
    [ObservableProperty] private int _subtitleMaxSentences = SttSettings.DefaultSubtitleMaxSentences;
    [ObservableProperty] private string _sttSegmentStatus = "";

    public string SttSilenceLabel => $"{SttSilenceTimeoutMs} ms";

    public string SttMaxSegmentLabel =>
        SttMaxSegmentMs <= 0 ? "사용 안 함" : $"{SttMaxSegmentMs / 1000.0:0.#} 초";

    public string SubtitleMaxLabel => $"{SubtitleMaxSentences} 문장";

    // ── [추가] 캡처(스크린샷) 설정 ───────────────────────────────────────────────
    [ObservableProperty] private bool _captureEnabled = true;
    [ObservableProperty] private string _captureStorageDir = "";
    [ObservableProperty] private string _captureStatus = "";

    /// <summary>사용자가 별도 폴더를 지정하지 않았을 때 실제로 쓰이는 기본 경로 (UI 안내용)</summary>
    public string CaptureDefaultDirHint => $"비워두면 기본 위치 사용: {CaptureStorage.DefaultRootDir}";

    // ── [추가] 기억노트 (교정 사전) ─────────────────────────────────────────────
    [ObservableProperty] private bool _memoryNoteEnabled = true;
    [ObservableProperty] private bool _memoryNoteWholeWord = true;
    [ObservableProperty] private string _memoryNoteStatus = "";
    [ObservableProperty] private string _memoryNotePath = "";

    /// <summary>현재 등록된 교정 단어 목록 (읽기 전용 표시용)</summary>
    public ObservableCollection<MemoryNoteEntry> MemoryNotes { get; } = new();

    // ── 생성자 ────────────────────────────────────────────────────────────────
    public SettingsViewModel()
    {
        if (DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject()))
            return;

        DeepLKeySet = SecureKeyStorage.Exists("deepl_key");
        AzureKeySet = SecureKeyStorage.Exists("azure_key");

        if (SecureKeyStorage.Exists("provider"))
            SelectedProvider = SecureKeyStorage.Load("provider") ?? "Google (무료)";
        if (SecureKeyStorage.Exists("azure_region"))
            AzureRegion = SecureKeyStorage.Load("azure_region") ?? "eastus";

        // [수정] 선택지가 Azure 하나뿐이므로 항상 Azure로 맞춘다.
        //        예전에 Windows로 저장해 둔 상태여도 자동으로 Azure로 넘어온다.
        if (SttService.SelectedEngine != SttEngineType.Azure)
        {
            SttService.SelectedEngine = SttEngineType.Azure;
            SttService.NotifyEngineChanged();
        }
        SelectedSttEngine = "Azure STT (클라우드·고정밀)";

        // [추가] 문장 끊기 설정 복원
        InitSttSegment();

        // [추가] 캡처 설정 복원 (App.xaml.cs에서 이미 CaptureSettings.Load() 완료된 상태)
        CaptureEnabled = CaptureSettings.Enabled;
        CaptureStorageDir = CaptureSettings.StorageDir;

        // [추가] 기억노트 설정 복원
        InitMemoryNote();

        RefreshSttStatus();
        ApplyTranslationProvider();
    }

    // ── STT 엔진 설정 저장 ────────────────────────────────────────────────────
    [RelayCommand]
    private void SaveSttEngine()
    {
        SttService.SelectedEngine = SelectedSttEngine == "Azure STT (클라우드·고정밀)"
            ? SttEngineType.Azure : SttEngineType.Windows;

        // SoundViewModel에 변경 알림 (SttInfoText / SttConfigured 갱신)
        SttService.NotifyEngineChanged();

        RefreshSttStatus();
    }

    private void RefreshSttStatus()
    {
        if (SttService.SelectedEngine == SttEngineType.Windows)
        {
            SttEngineStatus =
                "✅ Windows 내장 STT 설정됨\n" +
                "• API 키 불필요, 완전 무료\n" +
                "• 오프라인 동작 (인터넷 불필요)\n" +
                "• 영어(미국) 언어팩 필요: Windows 설정 → 시간 및 언어 → 음성";
        }
        else
        {
            SttEngineStatus = AzureKeySet
                ? "✅ Azure STT 설정됨 — 높은 정확도, 월 5시간 무료"
                : "⚠️ Azure STT 선택됨 — 아래에서 API 키를 저장해 주세요.";
        }
        OnPropertyChanged(nameof(ShowAzureSection));
    }

    // ── Azure STT 키 관리 ────────────────────────────────────────────────────
    [RelayCommand]
    private void SaveAzureKey()
    {
        if (string.IsNullOrWhiteSpace(AzureKey)) { AzureStatus = "❌ Azure 키를 입력해 주세요."; return; }
        SecureKeyStorage.Save("azure_key", AzureKey);
        SecureKeyStorage.Save("azure_region", AzureRegion);
        AzureKey = "";
        AzureKeySet = true;
        AzureStatus = $"✅ Azure STT 키 저장됨 (리전: {AzureRegion})";
        RefreshSttStatus();
        SttService.NotifyEngineChanged();
    }

    [RelayCommand]
    private void DeleteAzureKey()
    {
        SecureKeyStorage.Delete("azure_key");
        AzureKeySet = false;
        AzureStatus = "🗑 Azure STT 키가 삭제되었습니다.";
        RefreshSttStatus();
        SttService.NotifyEngineChanged();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // [추가] 문장 끊기(세그먼트) 설정
    // ══════════════════════════════════════════════════════════════════════════

    private void InitSttSegment()
    {
        SttSettings.Load();

        // 백킹 필드에 직접 대입해 생성자 단계에서 OnXxxChanged 가 도는 것을 피한다
        _sttSilenceTimeoutMs = SttSettings.SilenceTimeoutMs;
        _sttMaxSegmentMs = SttSettings.MaxSegmentMs;
        _subtitleMaxSentences = SttSettings.SubtitleMaxSentences;

        SttSegmentStatus = "";
    }

    [RelayCommand]
    private void SaveSttSegment()
    {
        SttSettings.SilenceTimeoutMs = SttSilenceTimeoutMs;
        SttSettings.MaxSegmentMs = SttMaxSegmentMs;
        SttSettings.SubtitleMaxSentences = SubtitleMaxSentences;
        SttSettings.Save();

        // 저장 과정에서 범위 보정이 일어났을 수 있으므로 UI를 되맞춘다
        SttSilenceTimeoutMs = SttSettings.SilenceTimeoutMs;
        SttMaxSegmentMs = SttSettings.MaxSegmentMs;
        SubtitleMaxSentences = SttSettings.SubtitleMaxSentences;

        SttSegmentStatus =
            "✅ 저장되었습니다. 자막 문장 수는 즉시 적용되고, " +
            "침묵·최대 시간은 sound 번역을 중지했다 다시 시작해야 반영됩니다.";
    }

    [RelayCommand]
    private void ResetSttSegment()
    {
        SttSettings.ResetToDefault();

        SttSilenceTimeoutMs = SttSettings.SilenceTimeoutMs;
        SttMaxSegmentMs = SttSettings.MaxSegmentMs;
        SubtitleMaxSentences = SttSettings.SubtitleMaxSentences;

        SttSegmentStatus = "기본값으로 되돌렸습니다.";
    }

    partial void OnSttSilenceTimeoutMsChanged(int value)
        => OnPropertyChanged(nameof(SttSilenceLabel));

    partial void OnSttMaxSegmentMsChanged(int value)
        => OnPropertyChanged(nameof(SttMaxSegmentLabel));

    partial void OnSubtitleMaxSentencesChanged(int value)
    {
        OnPropertyChanged(nameof(SubtitleMaxLabel));
        SttSettings.SubtitleMaxSentences = value;   // 자막 길이는 즉시 반영
    }

    // ── 번역 설정 ─────────────────────────────────────────────────────────────
    [RelayCommand]
    private void SaveDeepLKey()
    {
        if (string.IsNullOrWhiteSpace(DeepLKey)) { DeepLStatus = "❌ API 키를 입력해 주세요."; return; }
        SecureKeyStorage.Save("deepl_key", DeepLKey);
        DeepLKey = "";
        DeepLKeySet = true;
        DeepLStatus = "✅ DeepL API 키가 안전하게 저장되었습니다.";
        ApplyTranslationProvider();
    }

    [RelayCommand]
    private void DeleteDeepLKey()
    {
        SecureKeyStorage.Delete("deepl_key");
        DeepLKeySet = false;
        DeepLStatus = "🗑 DeepL API 키가 삭제되었습니다.";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SecureKeyStorage.Save("provider", SelectedProvider);
        ApplyTranslationProvider();
        DeepLStatus = "✅ 설정이 저장되었습니다.";
    }

    [ObservableProperty] private string _resetStatus = "";

    [RelayCommand]
    private async Task ResetDatabaseAsync()
    {
        await App.Database.ResetAllAsync();
        ResetStatus = "🗑 모든 번역 기록과 단어장이 초기화되었습니다.";
    }

    // ── [추가] 캡처(스크린샷) 설정 저장 ──────────────────────────────────────────
    [RelayCommand]
    private void SaveCaptureSettings()
    {
        CaptureSettings.Save(CaptureEnabled, CaptureStorageDir);
        CaptureStatus = CaptureEnabled
            ? "✅ 캡처 설정이 저장되었습니다. (캡처 켜짐)"
            : "✅ 캡처 설정이 저장되었습니다. (캡처 꺼짐 — 번역 기록은 계속 저장되고, 화면 캡처만 생략됩니다)";
    }

    [RelayCommand]
    private void ResetCaptureDir()
    {
        CaptureStorageDir = "";
        CaptureStatus = "폴더 지정을 해제했습니다. 저장을 눌러야 실제로 반영됩니다.";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // [추가] 기억노트 (교정 사전)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>저장된 on/off 설정을 복원하고 목록을 읽어온다. (생성자에서 1회)</summary>
    private void InitMemoryNote()
    {
        // 백킹 필드에 직접 대입해 생성자 단계에서 OnXxxChanged 가 도는 것을 피한다
        if (SecureKeyStorage.Exists("memory_note_enabled"))
            _memoryNoteEnabled = SecureKeyStorage.Load("memory_note_enabled") != "0";
        if (SecureKeyStorage.Exists("memory_note_wholeword"))
            _memoryNoteWholeWord = SecureKeyStorage.Load("memory_note_wholeword") != "0";

        MemoryNoteService.Enabled = _memoryNoteEnabled;
        MemoryNoteService.WholeWordOnly = _memoryNoteWholeWord;

        MemoryNotePath = MemoryNoteService.FilePath;
        RefreshMemoryNote();
    }

    /// <summary>파일을 다시 읽어 목록과 상태 문구를 갱신한다.</summary>
    private void RefreshMemoryNote()
    {
        MemoryNoteService.Load();

        MemoryNotes.Clear();
        foreach (var e in MemoryNoteService.GetAll())
            MemoryNotes.Add(e);

        if (MemoryNotes.Count > 0)
            MemoryNoteStatus = $"✅ {MemoryNotes.Count}개 등록됨";
        else if (File.Exists(MemoryNoteService.FilePath))
            MemoryNoteStatus = "⚠️ 파일은 있으나 읽어들인 단어가 없습니다. 단어와 번역 사이를 Tab으로 구분했는지 확인해 주세요.";
        else
            MemoryNoteStatus = "등록된 단어가 없습니다. txt 파일을 불러오거나 '편집'으로 직접 작성해 주세요.";
    }

    /// <summary>사용자가 만든 txt 파일을 골라 기억노트로 가져온다.</summary>
    [RelayCommand]
    private void ImportMemoryNote()
    {
        var dlg = new OpenFileDialog
        {
            Title = "기억노트로 사용할 txt 파일 선택",
            Filter = "텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var count = MemoryNoteService.ImportFrom(dlg.FileName);
            RefreshMemoryNote();

            MemoryNoteStatus = count > 0
                ? $"✅ {Path.GetFileName(dlg.FileName)} 에서 {count}개를 불러왔습니다."
                : "⚠️ 읽어들인 단어가 0개입니다. 각 줄이 '대상단어 [Tab] 고정번역' 형식인지 확인해 주세요.";
        }
        catch (Exception ex)
        {
            MemoryNoteStatus = $"❌ 불러오기 실패: {ex.Message}";
        }
    }

    /// <summary>기억노트 파일을 메모장으로 연다. 파일이 없으면 서식을 갖춰 새로 만든다.</summary>
    [RelayCommand]
    private void OpenMemoryNoteFile()
    {
        try
        {
            if (!File.Exists(MemoryNoteService.FilePath))
                MemoryNoteService.Save(new List<MemoryNoteEntry>());

            Process.Start(new ProcessStartInfo("notepad.exe", MemoryNoteService.FilePath)
            {
                UseShellExecute = true
            });

            MemoryNoteStatus = "메모장에서 편집·저장한 뒤 '다시 불러오기'를 눌러 주세요.";
        }
        catch (Exception ex)
        {
            MemoryNoteStatus = $"❌ 파일을 열지 못했습니다: {ex.Message}";
        }
    }

    /// <summary>파일을 다시 읽어들인다. (메모장으로 편집한 뒤 사용)</summary>
    [RelayCommand]
    private void ReloadMemoryNote() => RefreshMemoryNote();

    partial void OnMemoryNoteEnabledChanged(bool value)
    {
        MemoryNoteService.Enabled = value;
        SecureKeyStorage.Save("memory_note_enabled", value ? "1" : "0");

        if (value) RefreshMemoryNote();
        else MemoryNoteStatus = "⏸ 기억노트를 사용하지 않습니다. 번역은 원래 방식으로 동작합니다.";
    }

    partial void OnMemoryNoteWholeWordChanged(bool value)
    {
        MemoryNoteService.WholeWordOnly = value;
        SecureKeyStorage.Save("memory_note_wholeword", value ? "1" : "0");

        MemoryNoteStatus = value
            ? "완벽히 일치하는 단어만 교정합니다. (max 는 교정, maximum 은 통과)"
            : "단어 일부만 일치해도 교정합니다. 한글·한자 단어를 등록했다면 이 설정이 필요합니다.";
    }

    // ══════════════════════════════════════════════════════════════════════════

    private void ApplyTranslationProvider()
    {
        var provider = SelectedProvider == "DeepL API"
            ? TranslationProvider.DeepL : TranslationProvider.Google;
        App.Translation.Configure(provider, SecureKeyStorage.Load("deepl_key"), ContextSize);
    }

    partial void OnSelectedProviderChanged(string value) => ApplyTranslationProvider();
    partial void OnContextSizeChanged(int value) => ApplyTranslationProvider();

    partial void OnSelectedSttEngineChanged(string value)
    {
        OnPropertyChanged(nameof(ShowAzureSection));
    }
}
