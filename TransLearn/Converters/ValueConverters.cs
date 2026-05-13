// ============================================================
// ValueConverters.cs
// 역할 : XAML 데이터 바인딩용 IValueConverter 구현체 모음.
//
// 포함 컨버터
//   BoolToVisibilityConverter — bool → Visible/Collapsed (Invert 옵션)
//   BoolToStringConverter     — bool → 사용자 정의 문자열 (TrueValue/FalseValue)
//   RunningBrushConverter     — bool → 초록/회색 SolidColorBrush (상태 표시등)
//   NotEmptyConverter         — string → bool (빈 문자열 아닌지 여부)
// ============================================================
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TransLearn.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool v = value is bool b && b;
        if (Invert) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is Visibility.Visible;
}

[ValueConversion(typeof(bool), typeof(string))]
public class BoolToStringConverter : IValueConverter
{
    public string TrueValue  { get; set; } = "Yes";
    public string FalseValue { get; set; } = "No";
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b && b ? TrueValue : FalseValue;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v?.ToString() == TrueValue;
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class RunningBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => new SolidColorBrush(value is bool b && b
            ? Color.FromRgb(0, 200, 83)
            : Color.FromRgb(100, 116, 139));
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

[ValueConversion(typeof(string), typeof(bool))]
public class NotEmptyConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => !string.IsNullOrWhiteSpace(v as string);
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}
