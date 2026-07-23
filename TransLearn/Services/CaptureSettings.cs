// ============================================================
// CaptureSettings.cs [추가]
// 역할 : 번역 시점 화면 캡처 기능의 전역 설정 (켜짐/꺼짐, 저장 위치).
//        SecureKeyStorage에 평문으로 저장(민감정보 아님, 기존 provider/stt_engine과 동일 패턴).
//
// App.xaml.cs OnStartup에서 Load() 한 번 호출, SettingsViewModel에서 Save() 호출.
// OcrViewModel/SoundViewModel은 캡처 직전에 Enabled만 확인하면 된다.
// ============================================================
using System.IO;

namespace TransLearn.Services;

public static class CaptureSettings
{
    private const string KeyEnabled = "capture_enabled";
    private const string KeyDir     = "capture_dir";

    /// <summary>번역 시점 화면 캡처 기능 사용 여부. 기본값 켜짐.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>캡처 이미지 저장 폴더. 비어있으면 CaptureStorage.DefaultRootDir 사용.</summary>
    public static string StorageDir { get; set; } = "";

    public static void Load()
    {
        var enabledStr = SecureKeyStorage.Load(KeyEnabled);
        Enabled = enabledStr is null || enabledStr == "1"; // 처음 실행이면 기본 켜짐

        StorageDir = SecureKeyStorage.Load(KeyDir) ?? "";
    }

    public static void Save(bool enabled, string? storageDir)
    {
        Enabled = enabled;
        StorageDir = storageDir?.Trim() ?? "";

        SecureKeyStorage.Save(KeyEnabled, Enabled ? "1" : "0");
        SecureKeyStorage.Save(KeyDir, StorageDir);
    }

    /// <summary>
    /// 실제로 사용할 저장 루트 폴더를 반환한다.
    /// 사용자가 지정한 폴더가 비어있거나 더는 접근할 수 없으면(드라이브 분리 등)
    /// 조용히 기본 경로(%APPDATA%\TransLearn\Captures)로 대체한다 — 캡처 자체가 실패하지 않도록.
    /// </summary>
    public static string ResolveEffectiveRootDir()
    {
        if (string.IsNullOrWhiteSpace(StorageDir))
            return CaptureStorage.DefaultRootDir;

        try
        {
            Directory.CreateDirectory(StorageDir); // 존재 확인 + 없으면 생성
            return StorageDir;
        }
        catch
        {
            // 지정한 폴더가 더 이상 유효하지 않음(예: 이동식 드라이브 분리) → 기본 경로로 자동 폴백
            return CaptureStorage.DefaultRootDir;
        }
    }
}
