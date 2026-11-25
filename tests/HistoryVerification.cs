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

            var repeatedCycles = new[]
            {
                Sample(2, 70, 100),
                Sample(1, 85, 300, "주간", "Codex", "weekly"),
                Sample(0, 90, 100),
                Sample(1, 80, 200),
                Sample(1, 60, 900, "5시간", "OpenCode Go", "rolling")
            };
            var repeatedSegments = UsageHistorySeries.Build(repeatedCycles, TimeSpan.FromHours(24), now, 200)
                .Where(segment => segment.Service == "Codex" && segment.Key == "300")
                .ToArray();
            Check(repeatedSegments.Select(segment => segment.ResetsAt).SequenceEqual(new long[] { 100, 200, 100 })
                && repeatedSegments.All(segment => segment.Points.Count == 1),
                "A-B-A 리셋 주기를 비연속 segment로 분리");
            Check(repeatedSegments.Select(segment => segment.Points[0].Remaining).SequenceEqual(new[] { 90, 80, 70 }),
                "다른 서비스·구간과 섞여도 구간별 시간순 정렬");

            var relabeled = new[]
            {
                Sample(0, 90, 100, "5시간"),
                Sample(1, 80, 100, "5시간 상세")
            };
            var relabeledSegments = UsageHistorySeries.Build(relabeled, TimeSpan.FromHours(24), now, 200);
            Check(relabeledSegments.Count == 1 && relabeledSegments[0].Points.Count == 2,
                "Label 변경만으로 리셋 주기 segment 분리하지 않음");

            var samples = new[]
            {
                Sample(0, 90), Sample(1, 10), Sample(2, 80), Sample(3, 70), Sample(4, 60)
            };
            var reduced = UsageHistorySeries.Downsample(samples, 3);
            Check(reduced.Select(point => point.Remaining).SequenceEqual(new[] { 90, 10, 60 }),
                "다운샘플링이 첫값·최소값·마지막값 보존");

            var longSamples = Enumerable.Range(0, 1001)
                .Select(index => Sample(index, index == 950 ? 100 : index / 20))
                .ToArray();
            var reducedLong = UsageHistorySeries.Downsample(longSamples, 21);
            Check(reducedLong.Select(point => (point.TimestampUtc - longSamples[0].TimestampUtc) / 60)
                .SequenceEqual(new long[] { 0, 50, 100, 150, 200, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800, 850, 900, 950, 1000 }),
                "긴 시계열을 전체 구간에서 균등하게 다운샘플링");
            Check(reducedLong.Select(point => point.Remaining)
                .SequenceEqual(new[] { 0, 2, 5, 7, 10, 12, 15, 17, 20, 22, 25, 27, 30, 32, 35, 37, 40, 42, 45, 100, 50 }),
                "후반부 추세와 spike 다운샘플링에서 보존");

            using (var form = new UsageHistoryForm(store))
            {
                form.Show();
                Application.DoEvents();
                Check(form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Panel>()
                    .SelectMany(panel => panel.Controls.Cast<Control>()).OfType<ComboBox>().Count() == 3,
                    "히스토리 범위·서비스·구간 선택");
                Check(form.Controls.OfType<HistoryChart>().Any(), "히스토리 그래프 표시");
                Check(form.Text == "사용량 기록", "히스토리 창 제목");
                var header = form.Controls.OfType<Panel>().Single(panel => panel.Controls.OfType<Label>()
                    .Any(label => label.Text == "사용량 기록"));
                var closeButton = header.Controls.OfType<Button>().Single();
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
            var emptyStore = new UsageHistoryStore(Path.Combine(directory, "empty"));
            using (var empty = new UsageHistoryForm(emptyStore))
            {
                empty.Show();
                Application.DoEvents();
                Check(empty.Controls.OfType<HistoryChart>().Any(), "기록이 없어도 빈 그래프 표시");
                var emptySummary = empty.Controls.OfType<Label>().Single(label => label.Text.Contains("기록이 없습니다", StringComparison.Ordinal));
                emptyStore.Record(new[]
                {
                    new UsageHistoryPoint(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Codex", "10080", "주간 한도", 96, 1)
                });
                empty.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                    .Single(button => button.Text == "새로고침").PerformClick();
                Application.DoEvents();
                Check(!emptySummary.Text.Contains("기록이 없습니다", StringComparison.Ordinal), "수동 새로고침으로 최신 사용량 그래프 반영");
                var timerField = typeof(UsageHistoryForm).GetField("refreshTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Check(timerField?.GetValue(empty) is System.Windows.Forms.Timer { Enabled: true }, "그래프 창 자동 갱신 타이머 사용");
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

    private static UsageHistoryPoint Sample(int minute, int remaining, long resetsAt = 100,
        string label = "5시간", string service = "Codex", string key = "300")
        => new(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero).AddMinutes(minute).ToUnixTimeSeconds(),
            service, key, label, remaining, resetsAt);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }
}
