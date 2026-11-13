namespace CodexUsageWidget;

/// <summary>
/// DeepSeek 피크 시간대. 공식 기준은 UTC 01:00–04:00, 06:00–10:00(월–금)이며
/// 한국 시간으로는 10:00–13:00, 15:00–19:00이다. 주말과 중국 공휴일은 피크가 아니다.
/// </summary>
internal static class DeepSeekPeak
{
    private static readonly TimeSpan KoreaOffset = TimeSpan.FromHours(9);

    internal static bool IsPeak(DateTimeOffset now)
    {
        var korea = now.ToOffset(KoreaOffset);
        if (IsRestDay(DateOnly.FromDateTime(korea.DateTime))) return false;
        return (korea.Hour >= 10 && korea.Hour < 13) || (korea.Hour >= 15 && korea.Hour < 19);
    }

    private static bool IsRestDay(DateOnly date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsChinaHoliday(date);

    /// <summary>신정·노동절·국경절은 연도와 무관하게 적용하고, 음력·연휴는 연도별 목록을 따른다.</summary>
    private static bool IsChinaHoliday(DateOnly date)
        => (date.Month == 1 && date.Day == 1)
        || (date.Month == 5 && date.Day == 1)
        || (date.Month == 10 && date.Day <= 7)
        || ChinaHolidays.Contains(date);

    /// <summary>
    /// 중국 본토 공휴일. 2026년은 국무원 발표 기준, 2027–2028년은 추정치다.
    /// 고정 공휴일은 별도로 처리하므로 이 표에는 음력·연휴만 있어도 된다.
    /// </summary>
    private static readonly HashSet<DateOnly> ChinaHolidays = BuildHolidays();

    private static HashSet<DateOnly> BuildHolidays()
    {
        var holidays = new HashSet<DateOnly>();
        void Add(int year, int month, int day, int days)
        {
            var start = new DateOnly(year, month, day);
            for (var index = 0; index < days; index++) holidays.Add(start.AddDays(index));
        }

        // 2026 (공식)
        Add(2026, 1, 1, 3);    // 신정
        Add(2026, 2, 15, 9);   // 춘절
        Add(2026, 4, 4, 3);    // 청명
        Add(2026, 5, 1, 5);    // 노동절
        Add(2026, 6, 19, 3);   // 단오
        Add(2026, 9, 25, 3);   // 중추
        Add(2026, 10, 1, 7);   // 국경절
        // 2027 (추정)
        Add(2027, 1, 1, 1);
        Add(2027, 2, 5, 7);
        Add(2027, 4, 5, 1);
        Add(2027, 5, 1, 2);
        Add(2027, 6, 7, 3);
        Add(2027, 9, 15, 3);
        Add(2027, 10, 1, 7);
        // 2028 (추정)
        Add(2028, 1, 26, 6);   // 춘절
        Add(2028, 4, 4, 1);    // 청명
        Add(2028, 5, 28, 1);   // 단오
        Add(2028, 10, 3, 1);   // 중추
        return holidays;
    }

    /// <summary>시간에 따라 회전하는 무지개색. index는 글자 위치로, 글자마다 색을 어긋나게 한다.</summary>
    internal static Color Rainbow(DateTimeOffset now, int index, float lightness)
    {
        var phase = now.ToUnixTimeMilliseconds() % 3600 / 10.0;
        var hue = (phase + index * 120) % 360;
        return FromHsl(hue, 0.9, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - chroma / 2;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromArgb(
            (int)Math.Round((red + m) * 255),
            (int)Math.Round((green + m) * 255),
            (int)Math.Round((blue + m) * 255));
    }
}
