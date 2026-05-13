// ============================================================
// ExtraConverters.cs
// 역할 : 추가 XAML 컨버터 모음.
//
// 포함 컨버터
//   NotEmptyVisibilityConverter — string → Visible/Collapsed (빈 문자열 체크)
//   ProgressWidthConverter     — (index, total) → 픽셀 너비 (퀴즈 진행 바)
// ============================================================
using System.Globalization;
using System.Windows;
using System.Windows.Data;

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
