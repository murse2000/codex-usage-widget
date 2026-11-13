using System.Drawing;
using System.Reflection;

internal static class PeakVerification
{
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Peak = Assembly.Load("CodexUsageWidget").GetType("CodexUsageWidget.DeepSeekPeak")!;
    private static int checks;

    public static void Run()
    {
        var isPeak = Peak.GetMethod("IsPeak", Static)!;
        bool At(string value) => (bool)isPeak.Invoke(null, new object[] { DateTimeOffset.Parse(value) })!;

        Check(!At("2026-11-13T09:59:00+09:00"), "피크 시작 전");
        Check(At("2026-11-13T10:00:00+09:00"), "오전 피크 시작");
        Check(At("2026-11-13T12:59:59+09:00"), "오전 피크 끝");
        Check(!At("2026-11-13T13:00:00+09:00"), "오전 피크 종료");
        Check(!At("2026-11-13T14:59:00+09:00"), "점심 제외");
        Check(At("2026-11-13T15:00:00+09:00"), "오후 피크 시작");
        Check(At("2026-11-13T18:59:00+09:00"), "오후 피크 끝");
        Check(!At("2026-11-13T19:00:00+09:00"), "오후 피크 종료");
        Check(!At("2026-11-14T10:30:00+09:00"), "토요일 제외");
        Check(!At("2026-11-15T15:30:00+09:00"), "일요일 제외");
        Check(!At("2026-10-02T10:30:00+09:00"), "국경절 공휴일 제외");
        Check(!At("2026-02-18T15:30:00+09:00"), "춘절 공휴일 제외");
        Check(At("2026-11-13T01:30:00Z"), "UTC 입력을 한국 시간으로 변환");
        Check(At("2027-03-01T10:30:00+09:00"), "공휴일이 아닌 평일 피크");

        var rainbow = Peak.GetMethod("Rainbow", Static)!;
        var first = (Color)rainbow.Invoke(null, new object[] { DateTimeOffset.Parse("2026-11-13T10:00:00+09:00"), 0, 0.45f })!;
        var second = (Color)rainbow.Invoke(null, new object[] { DateTimeOffset.Parse("2026-11-13T10:00:00.4+09:00"), 0, 0.45f })!;
        Check(first.ToArgb() != second.ToArgb(), "시간에 따라 무지개색 변화");
        var next = (Color)rainbow.Invoke(null, new object[] { DateTimeOffset.Parse("2026-11-13T10:00:00+09:00"), 1, 0.45f })!;
        Check(first.ToArgb() != next.ToArgb(), "글자마다 다른 무지개색");

        Console.WriteLine($"피크 시간 검증 {checks}개 통과");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }
}
