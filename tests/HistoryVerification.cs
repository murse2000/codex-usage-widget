using CodexUsageWidget;

internal static class HistoryVerification
{
    private static int checks;

    public static void Run()
    {
        var directory = Path.GetFullPath(Path.Combine("obj", "history-verification"));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        try
        {
            var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            var store = new UsageHistoryStore(directory);
            var old = new UsageHistoryPoint(now.AddDays(-31).ToUnixTimeSeconds(), "Codex", "300", "5시간", 50, 1);
            var first = new UsageHistoryPoint(now.AddHours(-2).ToUnixTimeSeconds(), "Codex", "300", "5시간", 80, 100);
            var second = new UsageHistoryPoint(now.AddHours(-1).ToUnixTimeSeconds(), "Codex", "300", "5시간", 60, 100);
            var third = new UsageHistoryPoint(now.AddMinutes(-30).ToUnixTimeSeconds(), "Codex", "300", "5시간", 90, 200);
            var fourth = new UsageHistoryPoint(now.AddMinutes(-10).ToUnixTimeSeconds(), "Codex", "300", "5시간", 50, 200);
            var oldFile = Path.Combine(directory, "history-20000101.jsonl");
            File.WriteAllText(oldFile, "{\"TimestampUtc\":1}\n");
            store.Record(new[] { old, first, second, third, fourth });
            Check(!File.Exists(oldFile), "30일보다 오래된 히스토리 파일 정리");

            File.AppendAllText(Path.Combine(directory, "history-20261125.jsonl"), "{broken json\n");
            var loaded = store.Load(TimeSpan.FromDays(30), now);
            Check(loaded.Count == 4, "보관 기간 밖 기록과 손상 줄 제외");
            Check(loaded.All(point => point.TimestampUtc >= now.AddDays(-30).ToUnixTimeSeconds()), "30일 이전 기록 제외");

            var segments = UsageHistorySeries.Build(loaded, TimeSpan.FromHours(24), now, 200);
            Check(segments.Count == 2, "리셋 시각 변경 시 별도 선으로 분리");
            Check(segments[0].ResetsAt == 100 && segments[0].Points.Count == 2, "첫 리셋 주기 기록");
            Check(segments[1].ResetsAt == 200 && segments[1].Points.Count == 2, "새 리셋 주기 기록");

            var samples = new[]
            {
                Sample(0, 90), Sample(1, 10), Sample(2, 80), Sample(3, 70), Sample(4, 60)
            };
            var reduced = UsageHistorySeries.Downsample(samples, 3);
            Check(reduced.Select(point => point.Remaining).SequenceEqual(new[] { 90, 10, 60 }),
                "다운샘플링이 첫값·최소값·마지막값 보존");

            using (var form = new UsageHistoryForm(store))
            {
                form.Show();
                Application.DoEvents();
                Check(form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Panel>()
                    .SelectMany(panel => panel.Controls.Cast<Control>()).OfType<ComboBox>().Count() == 3,
                    "히스토리 범위·서비스·구간 선택");
                Check(form.Controls.OfType<HistoryChart>().Any(), "히스토리 그래프 표시");
                Check(form.Text == "사용량 기록", "히스토리 창 제목");
                var closeButton = form.Controls.OfType<Panel>().Single(panel => panel.Controls.OfType<Button>().Any())
                    .Controls.OfType<Button>().Single();
                Check(closeButton.Visible && closeButton.Right <= closeButton.Parent!.Width && closeButton.Left >= 0,
                    "히스토리 창 닫기 버튼 표시");
                Directory.CreateDirectory("obj/layout-verification");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save("obj/layout-verification/history.png");
                }
                form.Close();
            }
            using (var empty = new UsageHistoryForm(new UsageHistoryStore(Path.Combine(directory, "empty"))))
            {
                empty.Show();
                Application.DoEvents();
                Check(empty.Controls.OfType<HistoryChart>().Any(), "기록이 없어도 빈 그래프 표시");
                empty.Close();
            }

            store.DeleteAll();
            Check(!Directory.EnumerateFiles(directory, "history-*.jsonl").Any(), "히스토리 전체 삭제");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine($"히스토리 검증 {checks}개 통과");
    }

    private static UsageHistoryPoint Sample(int minute, int remaining)
        => new(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero).AddMinutes(minute).ToUnixTimeSeconds(),
            "Codex", "300", "5시간", remaining, 100);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }
}
