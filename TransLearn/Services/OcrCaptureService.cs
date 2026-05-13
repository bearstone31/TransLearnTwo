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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

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
        if (!GetWindowRect(hwnd, out var rect)) return "";

        int w = rect.Right  - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return "";

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                return "";
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
