// ============================================================
// CaptureLog.cs [추가]
// 역할 : 캡처 성공/실패 원인을 파일로 남기는 간단한 로거.
//
// Debug.WriteLine은 Visual Studio에서 F5(디버그)로 실행할 때만 "출력" 창에 보인다.
// 빌드된 exe를 그냥 더블클릭해서 실행한 경우엔 아무 데도 안 보여서 원인 파악이
// 불가능해진다. 그래서 캡처 관련 로그는 여기로 모아 파일에도 같이 남긴다.
//
// 저장 위치 : %APPDATA%\TransLearn\capture_log.txt
// 문제가 생기면 이 파일을 열어 최근 줄만 확인하면 된다.
// ============================================================
using System.IO;

namespace TransLearn.Services;

public static class CaptureLog
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TransLearn", "capture_log.txt");

    private static readonly object _lock = new();

    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(
                    LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로그 기록 자체가 실패해도 캡처/번역 흐름은 막지 않는다.
        }
    }
}
