using System.Drawing.Drawing2D;

namespace CodexUsageWidget;

internal sealed class UsageHistoryForm : GlassForm
{
    private readonly UsageHistoryStore store;
    private readonly ComboBox rangeBox = WidgetFormStyle.CreateComboBox();
    private readonly ComboBox serviceBox = WidgetFormStyle.CreateComboBox();
    private readonly ComboBox windowBox = WidgetFormStyle.CreateComboBox();
    private readonly Label summary = new();
    private readonly HistoryChart chart = new();

    internal UsageHistoryForm(UsageHistoryStore store)
    {
        this.store = store;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "사용량 기록";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 380);
        ForeColor = WidgetFormStyle.TextColor;
        var header = WidgetFormStyle.CreateHeader("사용량 기록", 580, Close);

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(15, 8, 15, 4),
            BackColor = Color.Transparent,
            WrapContents = false
        };
        rangeBox.Items.AddRange(new object[] { "24시간", "7일", "30일" });
        serviceBox.Items.AddRange(new object[] { "전체", "Codex", "OpenCode Go" });
        windowBox.Items.AddRange(new object[] { "전체", "5시간", "주간", "월간" });
        rangeBox.SelectedIndex = 2;
        serviceBox.SelectedIndex = 0;
        windowBox.SelectedIndex = 0;
        AddFilter(filters, "기간", rangeBox);
        AddFilter(filters, "서비스", serviceBox);
        AddFilter(filters, "구간", windowBox);

        summary.Dock = DockStyle.Top;
        summary.Height = 24;
        summary.Padding = new Padding(15, 2, 15, 0);
        summary.ForeColor = WidgetFormStyle.Muted;
        summary.Font = new Font("Segoe UI", 7.5f);
        summary.BackColor = Color.Transparent;

        chart.Dock = DockStyle.Fill;
        chart.BackColor = Color.Transparent;
        Controls.Add(chart);
        Controls.Add(summary);
        Controls.Add(filters);
        Controls.Add(header);
        ResumeLayout(true);
        Rebuild();
    }

    private void AddFilter(FlowLayoutPanel layout, string label, ComboBox box)
    {
        var group = new Panel { Size = new Size(112, 45), Margin = new Padding(0, 0, 6, 0), BackColor = Color.Transparent };
        var caption = WidgetFormStyle.CreateFilterLabel(label);
        caption.Location = new Point(0, 0);
        box.Location = new Point(0, 19);
        box.SelectedIndexChanged += (_, _) => Rebuild();
        group.Controls.Add(caption);
        group.Controls.Add(box);
        layout.Controls.Add(group);
    }

    private void Rebuild()
    {
        var range = rangeBox.SelectedIndex switch { 0 => TimeSpan.FromHours(24), 1 => TimeSpan.FromDays(7), _ => TimeSpan.FromDays(30) };
        var now = DateTimeOffset.Now;
        var points = store.Load(range, now).Where(point => Matches(point)).ToArray();
        var segments = UsageHistorySeries.Build(points, range, now, 240);
        chart.SetData(segments, now, range);
        summary.Text = points.Length == 0
            ? "아직 기록이 없습니다. 사용량을 갱신하면 추세가 쌓입니다."
            : $"기록 {points.Length}개 · 최근 {DateTimeOffset.FromUnixTimeSeconds(points[^1].TimestampUtc).ToLocalTime():M/d HH:mm}";
    }

    private bool Matches(UsageHistoryPoint point)
    {
        var service = serviceBox.SelectedIndex;
        if (service == 1 && point.Service != "Codex") return false;
        if (service == 2 && point.Service != "Go") return false;
        return windowBox.SelectedIndex switch
        {
            1 => point.Key is "300" or "rolling",
            2 => point.Key is "10080" or "weekly",
            3 => point.Key == "monthly",
            _ => true
        };
    }
}

internal sealed class HistoryChart : Control
{
    private IReadOnlyList<UsageHistorySegment> segments = Array.Empty<UsageHistorySegment>();
    private DateTimeOffset now;
    private TimeSpan range = TimeSpan.FromDays(30);

    internal HistoryChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
    }

    internal void SetData(IReadOnlyList<UsageHistorySegment> value, DateTimeOffset now, TimeSpan range)
    {
        segments = value;
        this.now = now;
        this.range = range;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 7.5f);
        using var labelBrush = new SolidBrush(WidgetFormStyle.Muted);
        using var grid = new Pen(WidgetFormStyle.RuleColor);
        var legend = segments
            .GroupBy(segment => new { segment.Service, segment.Key })
            .Select(group => group.First())
            .Take(5)
            .ToArray();
        var legendX = 15;
        foreach (var segment in legend)
        {
            using var pen = new Pen(ColorFor(segment), 2);
            g.DrawLine(pen, legendX, 13, legendX + 12, 13);
            g.DrawString(SegmentName(segment), font, labelBrush, legendX + 16, 7);
            legendX += 16 + TextRenderer.MeasureText(SegmentName(segment), font).Width + 14;
        }

        var plot = new Rectangle(48, 30, Math.Max(1, Width - 66), Math.Max(1, Height - 70));
        foreach (var percent in new[] { 0, 25, 50, 75, 100 })
        {
            var y = plot.Bottom - plot.Height * percent / 100f;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{percent}%", font, labelBrush, 8, y - 7);
        }
        var start = now - range;
        for (var index = 0; index <= 4; index++)
        {
            var x = plot.Left + plot.Width * index / 4f;
            var time = start + range * index / 4.0;
            var text = range.TotalDays >= 7 ? time.ToLocalTime().ToString("M/d") : time.ToLocalTime().ToString("M/d HH:mm");
            var size = TextRenderer.MeasureText(text, font);
            g.DrawString(text, font, labelBrush, Math.Clamp(x - size.Width / 2, plot.Left, plot.Right - size.Width), plot.Bottom + 9);
        }

        foreach (var segment in segments)
        {
            using var pen = new Pen(ColorFor(segment), 1.6f);
            var points = segment.Points.Select(point =>
            {
                var elapsed = DateTimeOffset.FromUnixTimeSeconds(point.TimestampUtc) - start;
                var x = plot.Left + (float)(elapsed.TotalMilliseconds / range.TotalMilliseconds) * plot.Width;
                var y = plot.Bottom - point.Remaining / 100f * plot.Height;
                return new PointF(x, y);
            }).ToArray();
            if (points.Length == 1)
            {
                using var brush = new SolidBrush(pen.Color);
                g.FillEllipse(brush, points[0].X - 2.5f, points[0].Y - 2.5f, 5, 5);
            }
            else if (points.Length > 1)
            {
                g.DrawLines(pen, points);
            }
        }
    }

    private static string SegmentName(UsageHistorySegment segment) => segment.Service == "Codex"
        ? segment.Key == "300" ? "Codex 5시간" : "Codex 주간"
        : segment.Key switch { "rolling" => "Go 5시간", "weekly" => "Go 주간", "monthly" => "Go 월간", _ => segment.Label };

    private static Color ColorFor(UsageHistorySegment segment) => segment.Service switch
    {
        "Codex" => segment.Key == "300" ? WidgetFormStyle.BrandColor : Color.FromArgb(112, 146, 216),
        "Go" => segment.Key == "rolling" ? WidgetFormStyle.Accent : Color.FromArgb(65, 166, 137),
        _ => WidgetFormStyle.Muted
    };
}
