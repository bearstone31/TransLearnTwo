// ============================================================
// OcrCaptureService.cs
// 역할 : 화면 영역 또는 특정 창을 캡처해 Windows Runtime OCR로 텍스트 추출.
//
// 동작
//   GetVisibleWindows()  — 표시 중인 창 목록 반환 (OcrView ComboBox용)
//   CaptureAsync(hwnd, region) — PrintWindow + WinRT OcrEngine.RecognizeAsync()
//     1. PrintWindow(hwnd, PW_RENDERFULLCONTENT)로 창 비트맵 캡처
//     2. Windows.Media.Ocr.OcrEngine("en-US")으로 텍스트 인식
//     3. 여러 줄을 공백으로 연결해 단일 문자열 반환
// ============================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using System.IO;
namespace TransLearn.Services;

public class OcrCaptureService : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint nFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int  GetWindowText(IntPtr hwnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnum, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    // [추가] 최소화(iconic) 창의 "복원 크기"를 알아내기 위한 P/Invoke
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>
    /// [추가] 캡처용 사각형을 구한다.
    /// 창이 최소화(iconic) 상태면 GetWindowRect는 화면 밖의 아주 작은 사각형
    /// (보통 -32000 부근, 폭 160×28 정도의 아이콘 자리)을 돌려준다. 그 크기로
    /// 비트맵을 만들면 PrintWindow가 제대로 된 내용을 그릴 수 없어 사실상 빈
    /// 이미지가 나온다 — "최소화돼도 정상 인식" 기능이 깨지는 핵심 원인이다.
    /// 최소화 상태에서는 GetWindowPlacement의 복원 크기(rcNormalPosition)를 대신
    /// 사용해야 DWM이 들고 있는 원래 해상도의 콘텐츠를 제대로 캡처할 수 있다.
    /// </summary>
    private static bool TryGetCaptureRect(IntPtr hwnd, out RECT rect)
    {
        if (IsIconic(hwnd))
        {
            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
            if (GetWindowPlacement(hwnd, ref wp))
            {
                rect = wp.rcNormalPosition;
                if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                    return true;
            }
        }
        return GetWindowRect(hwnd, out rect);
    }

    private OcrEngine? _ocrEngine;
    private bool _disposed;

    public OcrCaptureService()
    {
        try
        {
            _ocrEngine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                      ?? OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            _ocrEngine = null; // OCR not available on this system
        }
    }

    /// <summary>Capture a region of the given window (works while minimized/background)</summary>
    public async Task<string> CaptureAndRecognizeAsync(IntPtr hwnd, Rectangle? region = null)
    {
        if (!TryGetCaptureRect(hwnd, out var rect)) return "";

        int w = rect.Right  - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return "";

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
            {
                CaptureLog.Write($"[OcrCapture] PrintWindow 실패 hwnd={hwnd}");
                return "";
            }
        }
        finally { g.ReleaseHdc(hdc); }

        // Crop to requested region
        Bitmap source = bmp;
        if (region.HasValue)
        {
            var clamped = Rectangle.Intersect(region.Value, new Rectangle(0, 0, w, h));
            if (clamped.IsEmpty) return "";
            source = bmp.Clone(clamped, PixelFormat.Format32bppArgb);
        }

        try
        {
            return await RunOcrAsync(source);
        }
        finally
        {
            if (!ReferenceEquals(source, bmp)) source.Dispose();
        }
    }

    /// <summary>Capture the full screen region (no specific window)</summary>
    public async Task<string> CaptureScreenRegionAsync(Rectangle region)
    {
        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.CopyFromScreen(region.Location, Point.Empty, region.Size);
        return await RunOcrAsync(bmp);
    }

    private async Task<string> RunOcrAsync(Bitmap bitmap)
    {
        if (_ocrEngine == null) return "[OCR not available]";

        using var ms = new System.IO.MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var soft    = await decoder.GetSoftwareBitmapAsync();
        var result  = await _ocrEngine.RecognizeAsync(soft);
        return result.Text.Trim();
    }

    /// <summary>
    /// [추가] 번역이 발생한 시점에 대상 창 전체를 PNG로 캡처해 저장한다.
    /// OCR 인식은 하지 않고 순수 스크린샷 저장 용도.
    /// CaptureAndRecognizeAsync와 동일하게 PrintWindow(PW_RENDERFULLCONTENT)를 쓰므로
    /// 창이 최소화되었거나 다른 창에 가려져 있어도 대체로 캡처된다 (앱에 따라 실패 가능).
    /// </summary>
    /// <returns>성공 시 저장된 파일 경로, 실패 시 null</returns>
    public async Task<string?> CaptureWindowToFileAsync(IntPtr hwnd, string filePath)
    {
        if (hwnd == IntPtr.Zero)
        {
            CaptureLog.Write("[Capture] hwnd가 IntPtr.Zero — 대상 창이 지정되지 않음");
            return null;
        }
        if (!TryGetCaptureRect(hwnd, out var rect))
        {
            CaptureLog.Write($"[Capture] 창 사각형을 가져오지 못함 hwnd={hwnd}");
            return null;
        }

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            CaptureLog.Write($"[Capture] 잘못된 창 크기 w={w} h={h} hwnd={hwnd}");
            return null;
        }

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                bool ok;
                try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok)
                {
                    CaptureLog.Write($"[Capture] PrintWindow 실패 hwnd={hwnd} (일부 GPU 가속 앱은 이 플래그를 지원하지 않을 수 있음)");
                    return null;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await Task.Run(() => bmp.Save(filePath, ImageFormat.Png));
            CaptureLog.Write($"[Capture] 저장 완료: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            // 캡처 실패는 번역/저장 흐름을 막지 않는다 (best-effort) — 다만 원인은 출력창에 남긴다.
            CaptureLog.Write($"[Capture] 예외 발생: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// [추가] 특정 창을 선택하지 않고 "화면 영역만 사용" 모드로 OCR할 때를 위한 캡처.
    /// 이 경우엔 대상 창(hwnd)이 없으므로 PrintWindow를 쓸 수 없고,
    /// 대신 실제 화면에서 그 영역을 그대로 캡처한다 (CaptureScreenRegionAsync와 동일한 방식).
    /// </summary>
    public async Task<string?> CaptureScreenRegionToFileAsync(Rectangle region, string filePath)
    {
        try
        {
            using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(region.Location, Point.Empty, region.Size);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await Task.Run(() => bmp.Save(filePath, ImageFormat.Png));
            CaptureLog.Write($"[Capture] 화면 영역 저장 완료: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            CaptureLog.Write($"[Capture] 화면 영역 캡처 예외: {ex.Message}");
            return null;
        }
    }

    /// <summary>Get all visible windows with titles</summary>
    public List<(IntPtr Hwnd, string Title)> GetVisibleWindows()
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            var title = sb.ToString();
            if (!string.IsNullOrWhiteSpace(title))
                list.Add((hwnd, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
