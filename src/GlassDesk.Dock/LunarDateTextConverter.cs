using System.Globalization;
using System.Windows.Data;

namespace GlassDesk.Dock;

public sealed class LunarDateTextConverter : IValueConverter
{
    private static readonly ChineseLunisolarCalendar Calendar = new();
    private static readonly string[] Months = ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private const string Digits = "一二三四五六七八九十";

    public static string Format(DateTime date, bool fullDate = false)
    {
        if (date < Calendar.MinSupportedDateTime || date > Calendar.MaxSupportedDateTime) return string.Empty;
        var year = Calendar.GetYear(date);
        var month = Calendar.GetMonth(date);
        var leapMonth = Calendar.GetLeapMonth(year);
        var isLeap = leapMonth > 0 && month == leapMonth;
        if (leapMonth > 0 && month >= leapMonth) month--;
        var monthText = (isLeap ? "闰" : "") + Months[month - 1];
        var day = Calendar.GetDayOfMonth(date);
        var dayText = day switch
        {
            <= 10 => "初" + Digits[day - 1],
            < 20 => "十" + Digits[day - 11],
            20 => "二十",
            < 30 => "廿" + Digits[day - 21],
            _ => "三十",
        };
        return fullDate ? $"农历{year}年{monthText}{dayText}" : day == 1 ? monthText : dayText;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime date ? Format(date) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
