// ============================================================
// SettingsViewModel.cs
// 역할 : SettingsView의 MVVM ViewModel.
//        번역 API 키, STT 엔진 선택, Azure STT 키를 관리.
//        API 키는 SecureKeyStorage(DPAPI 암호화)에 영속 저장.
//
// 새로 추가된 기능
//   STT 엔진 선택: Windows 내장 STT(무료) ↔ Azure STT 토글
//   선택 저장 시 App.Stt.NotifyEngineChanged()로 SoundViewModel UI 갱신
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransLearn.Services;
using System.ComponentModel;

namespace TransLearn.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // ── 번역 설정 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _deepLKey      = "";
    [ObservableProperty] private bool   _deepLKeySet;
    [ObservableProperty] private string _deepLStatus   = "";
    [ObservableProperty] private string _selectedProvider = "Google (무료)";
    public List<string> Providers { get; } = new() { "Google (무료)", "DeepL API" };
    [ObservableProperty] private int _contextSize = 3;

    // ── DeepL API 키 ──────────────────────────────────────────────────────────
    // (SettingsView.xaml.cs에서 PasswordBox.PasswordChanged로 연결)

    // ── STT 엔진 선택 ─────────────────────────────────────────────────────────
    /// <summary>현재 선택된 STT 엔진 표시 문자열 (ComboBox 바인딩)</summary>
    [ObservableProperty] private string _selectedSttEngine = "Windows 내장 STT (무료·오프라인)";

    public List<string> SttEngines { get; } = new()
    {
        "Windows 내장 STT (무료·오프라인)",
        "Azure STT (클라우드·고정밀)"
    };

    [ObservableProperty] private string _sttEngineStatus = "";

    // Azure 키 섹션 표시 여부 (Azure 선택 시만 표시)
    public bool ShowAzureSection => SelectedSttEngine == "Azure STT (클라우드·고정밀)";

    // ── Azure STT 키 ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _azureKey     = "";
    [ObservableProperty] private string _azureRegion  = "eastus";
    [ObservableProperty] private bool   _azureKeySet;
    [ObservableProperty] private string _azureStatus  = "";

    // ── [추가] 캡처(스크린샷) 설정 ───────────────────────────────────────────────
    [ObservableProperty] private bool   _captureEnabled = true;
    [ObservableProperty] private string _captureStorageDir = "";
    [ObservableProperty] private string _captureStatus = "";

    /// <summary>사용자가 별도 폴더를 지정하지 않았을 때 실제로 쓰이는 기본 경로 (UI 안내용)</summary>
    public string CaptureDefaultDirHint => $"비워두면 기본 위치 사용: {CaptureStorage.DefaultRootDir}";

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

        // 저장된 STT 엔진 복원
        SelectedSttEngine = SttService.SelectedEngine == SttEngineType.Azure
            ? "Azure STT (클라우드·고정밀)"
            : "Windows 내장 STT (무료·오프라인)";

        // [추가] 캡처 설정 복원 (App.xaml.cs에서 이미 CaptureSettings.Load() 완료된 상태)
        CaptureEnabled    = CaptureSettings.Enabled;
        CaptureStorageDir = CaptureSettings.StorageDir;

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
        SecureKeyStorage.Save("azure_key",    AzureKey);
        SecureKeyStorage.Save("azure_region", AzureRegion);
        AzureKey    = "";
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

    // ── 번역 설정 ─────────────────────────────────────────────────────────────
    [RelayCommand]
    private void SaveDeepLKey()
    {
        if (string.IsNullOrWhiteSpace(DeepLKey)) { DeepLStatus = "❌ API 키를 입력해 주세요."; return; }
        SecureKeyStorage.Save("deepl_key", DeepLKey);
        DeepLKey    = "";
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
    private void ApplyTranslationProvider()
    {
        var provider = SelectedProvider == "DeepL API"
            ? TranslationProvider.DeepL : TranslationProvider.Google;
        App.Translation.Configure(provider, SecureKeyStorage.Load("deepl_key"), ContextSize);
    }

    partial void OnSelectedProviderChanged(string value) => ApplyTranslationProvider();
    partial void OnContextSizeChanged(int value)         => ApplyTranslationProvider();

    partial void OnSelectedSttEngineChanged(string value)
    {
        OnPropertyChanged(nameof(ShowAzureSection));
    }
}
