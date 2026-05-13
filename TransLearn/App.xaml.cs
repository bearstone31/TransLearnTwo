// ============================================================
// App.xaml.cs
// 역할 : WPF 앱 진입점. 전역 서비스 인스턴스를 싱글턴으로 관리.
//
// 서비스 초기화 순서 (OnStartup)
//   1. DatabaseService  — SQLite DB 초기화 및 마이그레이션
//   2. TranslationService — Google/DeepL 번역 엔진 설정
//   3. OcrCaptureService  — Windows Runtime OCR 준비
//   4. AudioCaptureService — WASAPI Loopback 캡처 준비
//   5. SttService         — STT 엔진 선택 로드 (실제 시작은 StartCommand에서)
//
// 전역 접근 패턴
//   App.Database.InsertTranslationAsync(...)
//   App.Stt.StartAsync()
//   App.AudioCapture.AudioLevelChanged += ...
// ============================================================
using System.Windows;
using TransLearn.Services;

namespace TransLearn;

public partial class App : Application
{
    // 전역 서비스 싱글턴 — ViewModel 어디서든 App.Xxx 로 접근
    public static DatabaseService     Database     { get; private set; } = null!;
    public static TranslationService  Translation  { get; private set; } = null!;
    public static OcrCaptureService   OcrCapture   { get; private set; } = null!;
    public static AudioCaptureService AudioCapture { get; private set; } = null!;
    public static SttService          Stt          { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // DB 파일: %APPDATA%\TransLearn\translearn.db
        var dbPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TransLearn", "translearn.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);

        Database     = new DatabaseService(dbPath);
        await Database.InitializeAsync();  // 스키마 초기화 + 마이그레이션

        Translation  = new TranslationService();
        OcrCapture   = new OcrCaptureService();
        AudioCapture = new AudioCaptureService(); // WASAPI 준비 (아직 캡처 시작 안 함)
        Stt          = new SttService();          // 엔진 선택만 로드 (아직 인식 시작 안 함)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AudioCapture?.Dispose();
        Stt?.DisposeAsync().AsTask().Wait(2000);
        base.OnExit(e);
    }
}
