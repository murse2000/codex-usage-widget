using System.Drawing.Drawing2D;

namespace CodexUsageWidget;

internal sealed class DiagnosticsForm : GlassForm
{
    private readonly PerformanceDiagnostics diagnostics;
    private readonly Label sampleSummary = new() { Name = "sampleSummary" };
    private readonly Label workingSetValue = new() { Name = "workingSetValue" };
    private readonly Label privateBytesValue = new() { Name = "privateBytesValue" };
    private readonly Label gcHeapValue = new() { Name = "gcHeapValue" };
    private readonly Label threadCountValue = new() { Name = "threadCountValue" };
    private readonly Label handleCountValue = new() { Name = "handleCountValue" };
    private readonly Label refreshTimeValue = new() { Name = "refreshTimeValue" };
    private readonly Label uiaValue = new() { Name = "uiaValue" };
    private readonly Label processIdValue = new() { Name = "processIdValue" };
    private readonly Label serviceValue = new() { Name = "serviceValue" };
    private readonly DiagnosticsChart chart = new();

    internal DiagnosticsForm(PerformanceDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics ?? new PerformanceDiagnostics();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "성능 진단";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 430);
        ForeColor = WidgetFormStyle.TextColor;

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(15, 8, 15, 8) };
        content.Controls.Add(chart);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            BackColor = Color.Transparent,
            WrapContents = false
        };
        var refreshButton = WidgetFormStyle.CreateButton("새로고침");
        refreshButton.Name = "refreshButton";
        refreshButton.Click += (_, _) => Rebuild();
        var retention = new Label
        {
            Text = "JSONL · 7일 보관 · 6시간마다 정리",
            AutoSize = true,
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = WidgetFormStyle.Muted,
            BackColor = Color.Transparent,
            Margin = new Padding(10, 7, 0, 0)
        };
        footer.Controls.Add(refreshButton);
        footer.Controls.Add(retention);
        content.Controls.Add(footer);

        var chartTitle = new Label
        {
            Text = "최근 24시간 메모리 추세",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = WidgetFormStyle.Muted,
            BackColor = Color.Transparent
        };
        content.Controls.Add(chartTitle);

        var status = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Color.Transparent,
            WrapContents = true
        };
        status.Controls.Add(CreateStatusLabel("마지막 UIA  ", uiaValue));
        status.Controls.Add(uiaValue);
        status.Controls.Add(CreateStatusLabel("app-server PID  ", processIdValue));
        status.Controls.Add(processIdValue);
        status.Controls.Add(CreateStatusLabel("서비스  ", serviceValue));
        status.Controls.Add(serviceValue);
        content.Controls.Add(status);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 126,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4)
        };
        for (var column = 0; column < metrics.ColumnCount; column++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        for (var row = 0; row < metrics.RowCount; row++) metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddMetric(metrics, "WorkingSet", workingSetValue);
        AddMetric(metrics, "Private Bytes", privateBytesValue);
        AddMetric(metrics, "GC Heap", gcHeapValue);
        AddMetric(metrics, "Threads", threadCountValue);
        AddMetric(metrics, "Handles", handleCountValue);
        AddMetric(metrics, "마지막 갱신", refreshTimeValue);
        content.Controls.Add(metrics);

        sampleSummary.Dock = DockStyle.Top;
        sampleSummary.Height = 22;
        sampleSummary.Font = new Font("Segoe UI", 7.5f);
        sampleSummary.ForeColor = WidgetFormStyle.Muted;
        sampleSummary.BackColor = Color.Transparent;
        content.Controls.Add(sampleSummary);

        Controls.Add(content);
        Controls.Add(WidgetFormStyle.CreateHeader("성능 진단", 640, Close));
        ResumeLayout(true);
        Rebuild();
    }

    private void Rebuild()
    {
        var now = DateTimeOffset.Now;
        IReadOnlyList<PerformanceDiagnosticsEntry> entries;
        try
        {
            entries = diagnostics.Load(TimeSpan.FromHours(24), now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            entries = Array.Empty<PerformanceDiagnosticsEntry>();
            sampleSummary.Text = "진단 기록을 읽지 못했습니다.";
        }
        var points = entries.Where(entry => entry.IsRefresh).ToArray();
        chart.SetData(points, now);
        var latest = points.LastOrDefault();
        if (latest is null)
        {
            workingSetValue.Text = privateBytesValue.Text = gcHeapValue.Text = "—";
            threadCountValue.Text = handleCountValue.Text = refreshTimeValue.Text = "—";
            uiaValue.Text = processIdValue.Text = serviceValue.Text = "—";
            if (sampleSummary.Text != "진단 기록을 읽지 못했습니다.")
                sampleSummary.Text = "기록이 없습니다. 사용량을 갱신하면 성능 추세가 쌓입니다.";
            return;
        }

        workingSetValue.Text = FormatBytes(latest.WorkingSet);
        privateBytesValue.Text = FormatBytes(latest.PrivateBytes);
        gcHeapValue.Text = FormatBytes(latest.GcHeapBytes);
        threadCountValue.Text = latest.ThreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        handleCountValue.Text = latest.HandleCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        refreshTimeValue.Text = DateTimeOffset.FromUnixTimeSeconds(latest.TimestampUtc).ToLocalTime().ToString("M/d HH:mm:ss");
        var lastUia = points.LastOrDefault(point => point.UiaMilliseconds is not null);
        uiaValue.Text = lastUia?.UiaMilliseconds is { } uia
            ? $"{uia} ms · {DateTimeOffset.FromUnixTimeSeconds(lastUia.TimestampUtc).ToLocalTime():HH:mm:ss}"
            : "—";
        var processId = points.LastOrDefault(point => point.CodexProcessId is not null);
        processIdValue.Text = processId?.CodexProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
        serviceValue.Text = $"Codex {(latest.CodexOk ? "정상" : "실패")} · Go {(latest.GoOk ? "정상" : "실패")}";
        sampleSummary.Text = $"샘플 {points.Length}개 · 최근 {DateTimeOffset.FromUnixTimeSeconds(latest.TimestampUtc).ToLocalTime():M/d HH:mm:ss}";
    }

    private void AddMetric(TableLayoutPanel layout, string caption, Label value)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 0, 10, 7) };
        var name = new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            Height = 17,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = WidgetFormStyle.Muted,
            BackColor = Color.Transparent
        };
        value.Dock = DockStyle.Top;
        value.Height = 31;
        value.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        value.ForeColor = WidgetFormStyle.TextColor;
        value.BackColor = Color.Transparent;
        card.Controls.Add(value);
        card.Controls.Add(name);
        layout.Controls.Add(card);
    }

    private static Label CreateStatusLabel(string caption, Label value)
    {
        value.AutoSize = true;
        value.Font = new Font("Segoe UI", 8);
        value.ForeColor = WidgetFormStyle.TextColor;
        value.BackColor = Color.Transparent;
        value.Margin = new Padding(0, 3, 16, 0);
        return new Label
        {
            Text = caption,
            AutoSize = true,
            Font = new Font("Segoe UI", 8),
            ForeColor = WidgetFormStyle.Muted,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 3, 4, 0)
        };
    }

    private static string FormatBytes(long bytes)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / 1024d / 1024d);
}

internal sealed class DiagnosticsChart : Control
{
    private IReadOnlyList<PerformanceDiagnosticsEntry> points = Array.Empty<PerformanceDiagnosticsEntry>();
    private DateTimeOffset now = DateTimeOffset.Now;

    internal DiagnosticsChart()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    internal void SetData(IReadOnlyList<PerformanceDiagnosticsEntry> value, DateTimeOffset now)
    {
        var start = now.AddHours(-24);
        points = value.Where(point => point.IsRefresh
            && DateTimeOffset.FromUnixTimeSeconds(point.TimestampUtc) >= start
            && DateTimeOffset.FromUnixTimeSeconds(point.TimestampUtc) <= now).ToArray();
        this.now = now;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 7.5f);
        using var muted = new SolidBrush(WidgetFormStyle.Muted);
        using var grid = new Pen(WidgetFormStyle.RuleColor);
        using var workingSetPen = new Pen(WidgetFormStyle.BrandColor, 1.8f);
        using var privatePen = new Pen(WidgetFormStyle.Accent, 1.8f);
        graphics.DrawString("WorkingSet", font, muted, 15, 5);
        graphics.DrawString("Private Bytes", font, muted, 88, 5);
        using (var swatch = new Pen(WidgetFormStyle.BrandColor, 2)) graphics.DrawLine(swatch, 70, 12, 82, 12);
        using (var swatch = new Pen(WidgetFormStyle.Accent, 2)) graphics.DrawLine(swatch, 153, 12, 165, 12);

        var plot = new Rectangle(48, 28, Math.Max(1, Width - 64), Math.Max(1, Height - 58));
        var maximumBytes = points.Count == 0 ? 100L * 1024 * 1024 : Math.Max(1, points.Max(point => Math.Max(point.WorkingSet, point.PrivateBytes)));
        for (var index = 0; index <= 4; index++)
        {
            var y = plot.Bottom - plot.Height * index / 4f;
            graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            var megabytes = maximumBytes / 1024d / 1024d * index / 4d;
            graphics.DrawString($"{megabytes:0.#}", font, muted, 8, y - 7);
        }
        var start = now.AddHours(-24);
        for (var index = 0; index <= 4; index++)
        {
            var x = plot.Left + plot.Width * index / 4f;
            var text = (start + TimeSpan.FromHours(6 * index)).ToLocalTime().ToString("HH:mm");
            var size = TextRenderer.MeasureText(text, font);
            graphics.DrawString(text, font, muted, Math.Clamp(x - size.Width / 2, plot.Left, plot.Right - size.Width), plot.Bottom + 8);
        }
        graphics.DrawString("MB", font, muted, 10, plot.Top - 13);

        if (points.Count == 0)
        {
            graphics.DrawString("기록 없음", font, muted, plot.Left + plot.Width / 2 - 24, plot.Top + plot.Height / 2 - 7);
            return;
        }

        DrawSeries(graphics, points.Select(point => PointFor(point, point.WorkingSet, maximumBytes, plot, start)), workingSetPen);
        DrawSeries(graphics, points.Select(point => PointFor(point, point.PrivateBytes, maximumBytes, plot, start)), privatePen);
    }

    private static PointF PointFor(PerformanceDiagnosticsEntry point, long bytes, long maximumBytes, Rectangle plot, DateTimeOffset start)
    {
        var elapsed = DateTimeOffset.FromUnixTimeSeconds(point.TimestampUtc) - start;
        return new PointF(
            plot.Left + (float)(elapsed.TotalMilliseconds / TimeSpan.FromHours(24).TotalMilliseconds) * plot.Width,
            plot.Bottom - (float)(bytes / (double)maximumBytes) * plot.Height);
    }

    private static void DrawSeries(Graphics graphics, IEnumerable<PointF> source, Pen pen)
    {
        var points = source.ToArray();
        if (points.Length == 1)
        {
            using var brush = new SolidBrush(pen.Color);
            graphics.FillEllipse(brush, points[0].X - 2.5f, points[0].Y - 2.5f, 5, 5);
        }
        else if (points.Length > 1)
        {
            graphics.DrawLines(pen, points);
        }
    }
}
