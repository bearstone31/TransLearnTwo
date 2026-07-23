// ============================================================
// CaptureStorage.cs
// 역할 : 번역 시점 화면 캡처 이미지의 저장 경로를 관리하는 헬퍼.
//
// 저장 위치 : {루트}\yyyy-MM-dd\HHmmss_fff_xxxxxxxx.png
//   - 루트는 기본적으로 %APPDATA%\TransLearn\Captures, 설정에서 바꿀 수 있음 (CaptureSettings)
//   - 날짜별 폴더로 분리해 탐색기에서도 쉽게 찾을 수 있게 함
//   - 파일명은 시:분:초.밀리초 + 짧은 GUID로 충돌 방지
//
// [추가] 사용자가 지정한 저장 폴더가 더 이상 유효하지 않으면(이동식 드라이브 분리 등)
//        CaptureSettings.ResolveEffectiveRootDir()가 자동으로 기본 경로로 대체하므로
//        여기서는 폴더 생성 실패를 별도로 신경 쓸 필요 없이 그대로 두면 된다.
//        (호출부인 OcrCaptureService.CaptureWindowToFileAsync가 예외를 전부 흡수한다)
// ============================================================
using System.IO;

namespace TransLearn.Services;

public static class CaptureStorage
{
    public static readonly string DefaultRootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TransLearn", "Captures");

    /// <summary>새 캡처 이미지를 저장할 파일 경로를 생성한다 (폴더는 미리 생성됨).</summary>
    public static string NewFilePath()
    {
        var root = CaptureSettings.ResolveEffectiveRootDir();
        var dayDir = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayDir);

        var name = $"{DateTime.Now:HHmmss_fff}_{Guid.NewGuid():N}"[..21] + ".png";
        return Path.Combine(dayDir, name);
    }

    /// <summary>저장된 캡처 파일을 삭제한다 (기록 삭제 시 함께 정리, 실패해도 무시).</summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 이미 지워졌거나 접근 불가 — 무시 */ }
    }
}
