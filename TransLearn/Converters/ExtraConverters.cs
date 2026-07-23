// ============================================================
// ExtraConverters.cs
// 역할 : 추가 XAML 컨버터 모음.
//
// 포함 컨버터
//   NotEmptyVisibilityConverter    — string → Visible/Collapsed (빈 문자열 체크)
//   ProgressWidthConverter        — (index, total) → 픽셀 너비 (퀴즈 진행 바)
//   ImagePathToBitmapConverter    — [추가] 캡처 이미지 파일 경로 → 썸네일 BitmapImage
//   ImageAvailableVisibilityConverter — [추가] 경로가 있고 파일이 실제로 존재할 때만 Visible
//                                        (캡처 파일이 삭제/이동됐을 때 깨진 썸네일 자리가 남지 않도록)
// ============================================================
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TransLearn.Converters;

[ValueConversion(typeof(string), typeof(Visibility))]
public class NotEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => !string.IsNullOrWhiteSpace(v as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>퀴즈 진행 바 너비 계산: index/total * 160px</summary>
public class ProgressWidthConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        if (values[0] is not int index || values[1] is not int total || total == 0) return 0.0;
        return Math.Min(160.0, 160.0 * index / total);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>[추가] 캡처 이미지 파일 경로 → 썸네일용 BitmapImage. 파일이 없거나 손상됐으면 null.</summary>
[ValueConversion(typeof(string), typeof(ImageSource))]
public class ImagePathToBitmapConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var path = v as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null!;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 160; // 썸네일 크기로만 디코딩 (메모리 절약)
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null!;
        }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// [추가] 캡처 이미지 경로가 비어있지 않고, 파일이 실제로 디스크에 존재할 때만 Visible.
/// 파일이 나중에 삭제되거나 다른 곳으로 이동돼도(=경로만 남고 실체가 없어져도) 깨진 썸네일이
/// 화면에 남지 않도록 하기 위한 컨버터. Invert=True면 반대로 "파일이 없을 때만" Visible.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public class ImageAvailableVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var path = v as string;
        bool available;
        try { available = !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { available = false; } // 접근 불가능한 경로(제거된 드라이브 등)도 안전하게 처리
        if (Invert) available = !available;
        return available ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}
