using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexUsageWidget;

internal static class Program
{
    /// <summary>이미 실행 중인 위젯에 창 표시를 요청하는 메시지.</summary>
    internal const int ShowWindowMessage = 0x8001;

    [STAThread]
    private static void Main()
    {
        // 같은 사용자 세션에서 위젯은 하나만 실행한다. 두 번째 실행은 기존 창을 띄우고 끝난다.
        using var mutex = new Mutex(false, @"Local\CodexUsageWidget");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            ActivateExisting();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new UsageForm());
    }

    private static void ActivateExisting()
    {
        var window = Native.FindWindow(null, "Codex Usage");
        if (window != IntPtr.Zero) Native.PostMessage(window, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string? className, string? windowName);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}

internal sealed class UsageForm : GlassForm
{
    private static readonly Color Muted = Color.FromArgb(89, 108, 134);
    private static readonly Color TextColor = Color.FromArgb(31, 45, 67);
    private static readonly Color Accent = Color.FromArgb(18, 137, 105);
    private static readonly Color BrandColor = Color.FromArgb(53, 102, 195);
    private const int SectionTop = 31;
    private const int FirstRowGap = 19;
    private const int RowPitch = 55;
    private const int GroupGap = 12;
    private readonly IconButton refreshButton = new(IconButton.Icon.Refresh);
    private readonly UsageRow codexPrimary = new();
    private readonly UsageRow codexSecondary = new();
    private readonly UsageRow goRolling = new();
    private readonly UsageRow goWeekly = new();
    private readonly UsageRow goMonthly = new();
    private readonly Label codexSection = new() { Text = "CODEX" };
    private readonly Label goSection = new() { Text = "OPENCODE GO" };
    private readonly Panel codexRule = new();
    private readonly Panel goRule = new();
    private readonly Label status = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 180_000 };
    private readonly System.Windows.Forms.Timer tiboTimer = new() { Interval = 120_000 };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 21_600_000 };
    private readonly ToolStripMenuItem updateStatus = new($"현재 v{AppUpdate.CurrentVersion.ToString(3)}") { Enabled = false };
    private readonly ToolStripMenuItem updateCheck = new("업데이트 확인");
    private readonly HttpClient tiboClient = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 262144 };
    private readonly HttpClient goClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = OpenCodeGoUsage.MaximumResponseSize };
    private readonly ToolStripMenuItem tiboStatus = new("아직 확인하지 않음") { Enabled = false };
    private readonly ToolStripMenuItem tiboOriginal = new("최근 예고·관찰 X 원문") { Enabled = false };
    private readonly NotifyIcon tray = new() { Text = "Codex 사용량" };
    private readonly TaskbarBadge taskbarBadge;
    private readonly WidgetSettings settings = LoadSettings();
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "settings.json");
    private string? goApiKey;
    private IReadOnlyList<GoUsageWindow>? goWindows;
    private UsageSnapshot? codexUsage;
    private bool codexStale;
    private bool goStale;
    private bool goAuthFailed;
    private bool refreshing;
    private bool checkingTibo;
    private DateTimeOffset nextTiboCheck;
    private TiboNotice? tiboNotice;
    private bool checkingUpdate;

    public UsageForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Codex Usage";
        Icon = LoadIcon();
        tray.Icon = Icon;
        tray.Visible = true;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = settings.AlwaysOnTop;
        ShowInTaskbar = false;
        ClientSize = new Size(252, 158);
        ForeColor = TextColor;
        goApiKey = SecretStore.Unprotect(settings.OpenCodeGoKey);
        BuildUi();
        ResumeLayout(true);
        BuildTrayMenu();
        taskbarBadge = new TaskbarBadge(Icon, tray.ContextMenuStrip!, () => { if (!IsDisposed) BeginInvoke((Action)ShowWidget); });
        Opacity = Math.Clamp(settings.Opacity, 0.45, 1);
        RestorePosition();
        ResizeEnd += (_, _) => SavePosition();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        DpiChanged += (_, _) => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)FitUsageLayout); };
        timer.Tick += async (_, _) => await RefreshUsageAsync();
        tiboTimer.Tick += async (_, _) => await RefreshTiboAsync();
        Shown += async (_, _) => await RefreshUsageAsync();
        Shown += (_, _) => taskbarBadge.Start();
        Shown += async (_, _) => await RefreshTiboAsync();
        Shown += async (_, _) => await CheckUpdateAsync(false);
        updateTimer.Tick += async (_, _) => await CheckUpdateAsync(false);
        FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            tiboTimer.Stop();
            tiboTimer.Dispose();
            tiboClient.Dispose();
            goClient.Dispose();
            updateTimer.Stop();
            updateTimer.Dispose();
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
            taskbarBadge.Dispose();
            Icon?.Dispose();
        };
        timer.Start();
        tiboTimer.Start();
        updateTimer.Start();
    }

    private void BuildUi()
    {
        var header = new Panel { Location = new Point(1, 1), Size = new Size(250, 25), BackColor = Color.Transparent };
        header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(header);
        header.MouseDown += DragWindow;

        var brand = new Label { Text = "사용량", Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = TextColor, Location = new Point(14, 2), Size = new Size(80, 19), BackColor = Color.Transparent };
        header.Controls.Add(brand);
        brand.MouseDown += DragWindow;

        StyleButton(refreshButton, new Point(202, 2), new Size(20, 20));
        refreshButton.Click += async (_, _) => await RefreshUsageAsync();
        header.Controls.Add(refreshButton);

        var opacityButton = new Button { Text = "◐", AccessibleName = "위젯 설정" };
        StyleButton(opacityButton, new Point(180, 2), new Size(20, 20), 9);
        opacityButton.Click += (_, _) => ShowOpacityPicker();
        header.Controls.Add(opacityButton);

        var close = new IconButton(IconButton.Icon.Close);
        StyleButton(close, new Point(225, 2), new Size(20, 20));
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(250, 218, 224);
        close.AccessibleName = "트레이로 숨기기";
        close.Click += (_, _) => Hide();
        header.Controls.Add(close);

        AddSection(codexSection, codexRule, BrandColor);
        AddRow(codexPrimary);
        AddRow(codexSecondary);
        AddSection(goSection, goRule, Accent);
        AddRow(goRolling);
        AddRow(goWeekly);
        AddRow(goMonthly);

        status.Font = new Font("Segoe UI", 7.5f);
        status.AutoSize = true;
        status.ForeColor = Muted;
        Controls.Add(status);
        foreach (var label in Controls.OfType<Label>()) label.BackColor = Color.Transparent;
    }

    private void AddSection(Label label, Panel rule, Color color)
    {
        label.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        label.ForeColor = color;
        label.AutoSize = true;
        rule.Height = LogicalToDeviceUnits(1);
        rule.BackColor = Color.FromArgb(206, 220, 238);
        Controls.Add(label);
        Controls.Add(rule);
        label.Visible = false;
        rule.Visible = false;
    }

    private void AddRow(UsageRow row)
    {
        row.Name.Font = new Font("Segoe UI", 8.5f);
        row.Name.ForeColor = Muted;
        row.Percent.TextAlign = ContentAlignment.MiddleRight;
        row.Percent.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        row.Percent.ForeColor = TextColor;
        row.ResetCaption.Text = "초기화";
        row.ResetCaption.Font = new Font("Segoe UI", 8);
        row.ResetCaption.ForeColor = Muted;
        row.ResetCaption.AutoSize = true;
        row.Reset.Font = new Font("Segoe UI", 7.5f);
        row.Reset.ForeColor = TextColor;
        row.Reset.AutoSize = true;
        Controls.Add(row.Name);
        Controls.Add(row.Percent);
        Controls.Add(row.Bar);
        Controls.Add(row.ResetCaption);
        Controls.Add(row.Reset);
        row.SetShown(false);
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(UsageForm).Assembly.GetManifestResourceStream("CodexUsageWidget.Widget.ico")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void StyleButton(Button button, Point location, Size size, float fontSize = 9)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 229, 248);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(197, 216, 243);
        button.Font = new Font("Segoe UI Symbol", fontSize);
        button.ForeColor = Muted;
        button.BackColor = Color.Transparent;
        button.Location = location;
        button.Size = size;
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
    }

    private sealed class IconButton : Button
    {
        public enum Icon { Refresh, Close }

        private readonly Icon icon;

        public IconButton(Icon icon)
        {
            this.icon = icon;
            Text = string.Empty;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var color = Enabled ? ForeColor : Muted;
            using var pen = new Pen(color, 1.7f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var centerX = ClientSize.Width / 2f;
            var centerY = ClientSize.Height / 2f;

            if (icon == Icon.Close)
            {
                e.Graphics.DrawLine(pen, centerX - 3.5f, centerY - 3.5f, centerX + 3.5f, centerY + 3.5f);
                e.Graphics.DrawLine(pen, centerX + 3.5f, centerY - 3.5f, centerX - 3.5f, centerY + 3.5f);
            }
            else
            {
                e.Graphics.DrawArc(pen, centerX - 5f, centerY - 5f, 10f, 10f, 45, 285);
                e.Graphics.DrawLine(pen, centerX + 3f, centerY - 5f, centerX + 5f, centerY - 5f);
                e.Graphics.DrawLine(pen, centerX + 5f, centerY - 5f, centerX + 5f, centerY - 3f);
            }
        }
    }

    private async Task RefreshUsageAsync()
    {
        if (refreshing || IsDisposed) return;
        refreshing = true;
        refreshButton.Enabled = false;
        status.Text = "확인 중…";
        try
        {
            var codexTask = settings.ShowCodex
                ? Task.Run(FetchCodex)
                : Task.FromResult(FetchResult<UsageSnapshot>.Empty);
            var goTask = settings.ShowOpenCodeGo && goApiKey is { } apiKey
                ? FetchGoAsync(apiKey)
                : Task.FromResult(FetchResult<IReadOnlyList<GoUsageWindow>>.Empty);
            await Task.WhenAll(codexTask, goTask);
            if (IsDisposed) return;

            var alerts = new List<string>();
            if (settings.ShowCodex) ApplyCodex(codexTask.Result, alerts);
            if (settings.ShowOpenCodeGo) ApplyGo(goTask.Result, alerts);

            status.Text = StatusText();
            FitUsageLayout();
            KeepOnScreen(Screen.FromControl(this));
            var settingsSaved = SavePosition();
            if (alerts.Count > 0) tray.ShowBalloonTip(10000, "사용량 알림", string.Join("\n", alerts), ToolTipIcon.Warning);
            UpdateTrayText();
            taskbarBadge.Update(TaskbarRows());
            if (!settingsSaved)
            {
                status.Text = StatusText() + " · 설정 저장 실패";
                FitUsageLayout();
            }
        }
        catch
        {
            if (!IsDisposed)
            {
                codexStale = goStale = true;
                RenderCodex();
                RenderGo();
                status.Text = "갱신 실패 · " + DateTime.Now.ToString("HH:mm");
                FitUsageLayout();
                UpdateTrayText();
                taskbarBadge.MarkStale();
            }
        }
        finally
        {
            refreshing = false;
            if (!IsDisposed) refreshButton.Enabled = true;
        }
    }

    private sealed class FetchResult<T>
    {
        internal FetchResult(T? value, Exception? error) { Value = value; Error = error; }
        internal T? Value { get; }
        internal Exception? Error { get; }
        internal static FetchResult<T> Empty { get; } = new(default, null);
    }

    private static FetchResult<UsageSnapshot> FetchCodex()
    {
        try { return new(ReadUsage(), null); }
        catch (Exception error) { return new(default, error); }
    }

    private async Task<FetchResult<IReadOnlyList<GoUsageWindow>>> FetchGoAsync(string apiKey)
    {
        try { return new(await OpenCodeGoUsage.ReadAsync(goClient, apiKey).ConfigureAwait(false), null); }
        catch (Exception error) { return new(default, error); }
    }

    private void ApplyCodex(FetchResult<UsageSnapshot> result, List<string> alerts)
    {
        if (result.Value is { } usage)
        {
            codexUsage = usage;
            codexStale = false;
            foreach (var window in new[] { usage.Primary, usage.Secondary }.OfType<UsageWindow>())
            {
                var remaining = 100 - Math.Clamp(window.UsedPercent, 0, 100);
                var threshold = RecordAlert(settings, $"codex:{window.DurationMinutes}", remaining, window.ResetsAt);
                if (threshold > 0) alerts.Add($"{WindowName(window.DurationMinutes)} 잔여량이 {threshold}% 미만입니다. (현재 {remaining}%)");
            }
        }
        else if (result.Error is not null)
        {
            codexStale = true;
        }
        RenderCodex();
    }

    private void ApplyGo(FetchResult<IReadOnlyList<GoUsageWindow>> result, List<string> alerts)
    {
        if (result.Value is { } windows)
        {
            goWindows = windows;
            goStale = false;
            goAuthFailed = false;
            foreach (var window in windows)
            {
                var remaining = 100 - Math.Clamp(window.UsedPercent, 0, 100);
                var threshold = RecordAlert(settings, $"go:{window.Key}", remaining, window.ResetsAt);
                if (threshold > 0) alerts.Add($"OpenCode Go {window.Label} 잔여량이 {threshold}% 미만입니다. (현재 {remaining}%)");
            }
        }
        else if (result.Error is OpenCodeGoAuthException)
        {
            // 인증되지 않은 키는 목록에 남기지 않는다.
            goWindows = null;
            goAuthFailed = true;
            goStale = false;
        }
        else if (result.Error is not null)
        {
            goStale = true;
        }
        RenderGo();
    }

    private void RenderCodex()
    {
        if (codexUsage is not { } usage) return;
        SetRow(codexPrimary, WindowName(usage.Primary.DurationMinutes) + " 남음", usage.Primary, codexStale);
        if (usage.Secondary is { } secondary)
            SetRow(codexSecondary, WindowName(secondary.DurationMinutes) + " 남음", secondary, codexStale);
    }

    private void RenderGo()
    {
        if (goWindows is not { } windows) return;
        foreach (var window in windows)
        {
            var row = window.Key switch
            {
                "rolling" => goRolling,
                "weekly" => goWeekly,
                "monthly" => goMonthly,
                _ => null
            };
            if (row is not null) SetRow(row, window.Label + " 남음", window.UsedPercent, window.ResetsAt, window.WindowMinutes, goStale);
        }
    }

    private static void SetRow(UsageRow row, string name, UsageWindow window, bool stale)
        => SetRow(row, name, window.UsedPercent, window.ResetsAt, window.DurationMinutes, stale);

    private static void SetRow(UsageRow row, string name, int usedPercent, long resetsAt, int windowMinutes, bool stale)
    {
        var remaining = 100 - Math.Clamp(usedPercent, 0, 100);
        row.Name.Text = name;
        row.Percent.Text = $"{remaining}%{(stale ? " !" : "")}";
        row.Percent.ForeColor = RemainingColor(remaining);
        row.Bar.Remaining = remaining;
        row.Bar.Stale = stale;
        row.Bar.Invalidate();
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetsAt);
        var timeLeft = resetAt - DateTimeOffset.Now;
        if (timeLeft < TimeSpan.Zero) timeLeft = TimeSpan.Zero;
        row.Reset.Text = $"{resetAt.LocalDateTime:M/d HH:mm} ({RemainingText(windowMinutes, timeLeft)})";
    }

    private static string RemainingText(int windowMinutes, TimeSpan timeLeft) => windowMinutes == 300
        ? $"{(int)Math.Ceiling(timeLeft.TotalMinutes)}분 남음"
        : $"{(int)timeLeft.TotalDays}일 {timeLeft.Hours:00}시간 남음";

    private string StatusText()
    {
        if (!settings.ShowCodex && !settings.ShowOpenCodeGo) return "표시할 서비스가 없습니다 · 설정에서 선택";
        var text = "3분마다 갱신 · " + DateTime.Now.ToString("HH:mm");
        if (settings.ShowCodex && codexStale) text += " · Codex 실패";
        if (settings.ShowOpenCodeGo && goAuthFailed) text += " · Go 키 인증 실패";
        else if (settings.ShowOpenCodeGo && goStale) text += " · Go 조회 실패";
        return text;
    }

    private void UpdateTrayText()
    {
        var parts = new List<string>();
        if (settings.ShowCodex && codexUsage is { } usage)
            parts.Add($"{100 - Math.Clamp(usage.Primary.UsedPercent, 0, 100)}%");
        if (settings.ShowOpenCodeGo && goWindows is { Count: > 0 } windows)
        {
            var lowest = windows.OrderBy(window => 100 - Math.Clamp(window.UsedPercent, 0, 100)).First();
            parts.Add($"Go {100 - Math.Clamp(lowest.UsedPercent, 0, 100)}%");
        }
        tray.Text = parts.Count == 0 ? "Codex 사용량" : "Codex 사용량 · " + string.Join(" · ", parts);
    }

    /// <summary>
    /// 작업표시줄에는 서비스마다 한 줄씩, 5시간·주간·월간 순서의 열을 맞춰 표시한다.
    /// </summary>
    private IReadOnlyList<TaskbarBadge.Row> TaskbarRows()
    {
        var rows = new List<TaskbarBadge.Row>();
        if (settings.ShowCodex && codexUsage is { } usage)
        {
            rows.Add(new TaskbarBadge.Row("Codex", new[]
            {
                BadgeCell(usage.Primary),
                usage.Secondary is { } secondary ? BadgeCell(secondary) : null,
                null
            }));
        }
        if (settings.ShowOpenCodeGo && goWindows is { Count: > 0 } windows)
        {
            rows.Add(new TaskbarBadge.Row("Go", new[]
            {
                GoBadgeCell(windows, "rolling"),
                GoBadgeCell(windows, "weekly"),
                GoBadgeCell(windows, "monthly")
            }));
        }
        // 서비스가 하나만 남아도 한 줄에 큰 글자로 늘어나지 않도록 첫 구간과 나머지를 두 줄로 나눈다.
        if (rows.Count != 1) return rows;
        var only = rows[0];
        var cells = only.Cells.Where(cell => cell is not null).Select(cell => cell!).ToList();
        if (cells.Count < 2) return rows;
        return new[]
        {
            new TaskbarBadge.Row(only.Service, new TaskbarBadge.Cell?[] { cells[0], null, null }),
            new TaskbarBadge.Row(string.Empty, cells.Skip(1).Cast<TaskbarBadge.Cell?>().ToArray())
        };
    }

    private static TaskbarBadge.Cell BadgeCell(UsageWindow window)
        => new(WindowName(window.DurationMinutes).Replace(" 한도", ""), 100 - Math.Clamp(window.UsedPercent, 0, 100),
            BadgeResetText(window.DurationMinutes, ResetLeft(window.ResetsAt)));

    private static TaskbarBadge.Cell? GoBadgeCell(IReadOnlyList<GoUsageWindow> windows, string key)
        => windows.Where(window => window.Key == key)
            .Select(window => (TaskbarBadge.Cell?)new TaskbarBadge.Cell(window.Label.Replace(" 한도", ""),
                100 - Math.Clamp(window.UsedPercent, 0, 100),
                BadgeResetText(window.WindowMinutes, ResetLeft(window.ResetsAt))))
            .FirstOrDefault();

    /// <summary>작업표시줄에서는 초기화까지 남은 시간을 괄호로 짧게 적는다.</summary>
    private static string BadgeResetText(int windowMinutes, TimeSpan timeLeft) => windowMinutes switch
    {
        300 => $"({(int)Math.Ceiling(timeLeft.TotalMinutes)}분)",
        10080 => $"({(int)timeLeft.TotalHours}시간)",
        _ => timeLeft >= TimeSpan.FromDays(1)
            ? $"({(int)timeLeft.TotalDays}일 {timeLeft.Hours}시간)"
            : $"({(int)Math.Ceiling(timeLeft.TotalHours)}시간)"
    };

    private static TimeSpan ResetLeft(long resetsAt)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds(resetsAt) - DateTimeOffset.Now;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private async Task RefreshTiboAsync()
    {
        if (checkingTibo || IsDisposed || !settings.TiboAlertsEnabled || DateTimeOffset.UtcNow < nextTiboCheck) return;
        checkingTibo = true;
        nextTiboCheck = DateTimeOffset.UtcNow.AddMinutes(2);
        try
        {
            var result = await TiboResetFeed.ReadAsync(tiboClient);
            if (IsDisposed || !settings.TiboAlertsEnabled) return;
            nextTiboCheck = result.RetryAt;
            if (result.Snapshot is { } snapshot) ApplyTiboSnapshot(snapshot);
            else tiboStatus.Text = $"요청 제한 · {nextTiboCheck.LocalDateTime:HH:mm} 이후 재확인";
        }
        catch (Exception error)
        {
            if (!IsDisposed && settings.TiboAlertsEnabled)
                tiboStatus.Text = error is InvalidDataException ? error.Message : "공지 조회 실패 · 2분 후 재확인";
        }
        finally
        {
            checkingTibo = false;
            if (!IsDisposed)
            {
                tiboTimer.Stop();
                tiboTimer.Interval = (int)Math.Clamp(Math.Ceiling((nextTiboCheck - DateTimeOffset.UtcNow).TotalMilliseconds), 120_000, int.MaxValue);
                tiboTimer.Start();
            }
        }
    }

    private void ApplyTiboSnapshot(TiboSnapshot snapshot)
    {
        tiboNotice = snapshot.Notice;
        tiboOriginal.Enabled = tiboNotice is not null;
        tiboStatus.Text = (tiboNotice is null ? "등록된 예고·관찰 없음" : snapshot.ReferenceTimePassed ? "예고·관찰 기한 지남 · 원문 확인"
            : tiboNotice.IsWatch ? "리셋 가능성 관찰 · 확정 아님" : "리셋 예고 등록됨")
            + $" · 자료 {snapshot.GeneratedAt.LocalDateTime:M/d HH:mm}";
        if (!settings.TiboAlertsEnabled || tiboNotice is null || snapshot.ReferenceTimePassed || settings.TiboAlertKeys.Contains(tiboNotice.AlertKey)) return;

        settings.TiboAlertKeys.Add(tiboNotice.AlertKey);
        if (!SavePosition())
        {
            settings.TiboAlertKeys.Remove(tiboNotice.AlertKey);
            tiboStatus.Text = "설정 저장 실패 · 예고 알림 보류";
            return;
        }
        var alarm = new ResetAlarmForm(tiboNotice);
        alarm.FormClosed += (_, _) => alarm.Dispose();
        alarm.Show(this);
    }

    private async Task CheckUpdateAsync(bool manual)
    {
        if (checkingUpdate || IsDisposed) return;
        checkingUpdate = true;
        updateCheck.Enabled = false;
        string? directory = null;
        var installerStarted = false;
        var approved = false;
        try
        {
            updateStatus.Text = "새 버전 확인 중…";
            var release = await AppUpdate.FindAsync();
            if (IsDisposed) return;
            if (release is null)
            {
                updateStatus.Text = $"v{AppUpdate.CurrentVersion.ToString(3)} · 최신 버전";
                if (manual) MessageBox.Show(this, "현재 최신 버전을 사용하고 있습니다.", "Codex 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            updateStatus.Text = $"새 버전 {release.Tag} 사용 가능";
            if (!manual && settings.DeclinedUpdateVersion == release.Tag) return;
            ShowWidget();
            if (MessageBox.Show(this, $"새 버전 {release.Tag}이 있습니다. (현재 v{AppUpdate.CurrentVersion.ToString(3)})\n\n다운로드하고 설치할까요? 설치할 때 위젯이 종료된 후 다시 실행됩니다.",
                "Codex 업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                settings.DeclinedUpdateVersion = release.Tag;
                SavePosition();
                return;
            }
            updateStatus.Text = "업데이트 다운로드·검증 중…";
            approved = true;
            directory = await AppUpdate.DownloadAsync(release);
            if (IsDisposed) return;
            updateStatus.Text = "업데이트 설치 준비 중…";
            SavePosition();
            await AppUpdate.StartInstallerAsync(directory, Environment.ProcessPath!);
            installerStarted = true;
            Close();
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                updateStatus.Text = "확인/설치 실패 · 수동 확인으로 재시도";
                if (manual || approved)
                    MessageBox.Show(this, error.Message + "\n\n기존 실행 파일과 설정은 유지됩니다.", "Codex 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!installerStarted && directory is not null)
            {
                try { Directory.Delete(directory, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            checkingUpdate = false;
            if (!IsDisposed) updateCheck.Enabled = true;
        }
    }

    private void FitUsageLayout()
    {
        ApplyVisibility();
        var width = LogicalToDeviceUnits(252);
        foreach (var row in AllRows().Where(row => row.Shown))
            width = Math.Max(width, LogicalToDeviceUnits(58) + row.Reset.PreferredWidth + LogicalToDeviceUnits(15));

        var y = LogicalToDeviceUnits(SectionTop);
        if (ShowCodexSection)
        {
            PlaceSection(codexSection, codexRule, y, width);
            y += LogicalToDeviceUnits(FirstRowGap);
            foreach (var row in CodexRows())
            {
                PlaceRow(row, y, width);
                y += RowStep(row, y);
            }
            if (ShowGoSection) y += LogicalToDeviceUnits(GroupGap);
        }
        if (ShowGoSection)
        {
            PlaceSection(goSection, goRule, y, width);
            y += LogicalToDeviceUnits(FirstRowGap);
            foreach (var row in GoRows())
            {
                PlaceRow(row, y, width);
                y += RowStep(row, y);
            }
        }

        var bottom = AllRows().Where(row => row.Shown).Select(row => row.Reset.Bottom).DefaultIfEmpty(y).Max();
        status.Location = new Point(LogicalToDeviceUnits(15), Math.Max(Math.Max(y, bottom) + LogicalToDeviceUnits(8), LogicalToDeviceUnits(SectionTop + 6)));
        ClientSize = new Size(Math.Max(width, status.Right + LogicalToDeviceUnits(15)), status.Bottom + LogicalToDeviceUnits(9));
    }

    private int RowStep(UsageRow row, int top)
        => Math.Max(LogicalToDeviceUnits(RowPitch), row.Reset.Bottom - top + LogicalToDeviceUnits(6));

    private bool ShowCodexSection => settings.ShowCodex && codexUsage is not null;
    private bool ShowGoSection => settings.ShowOpenCodeGo && goWindows is not null;

    private void ApplyVisibility()
    {
        var codex = ShowCodexSection;
        var go = ShowGoSection;
        codexSection.Visible = codexRule.Visible = codex;
        goSection.Visible = goRule.Visible = go;
        codexPrimary.SetShown(codex);
        codexSecondary.SetShown(codex && codexUsage?.Secondary is not null);
        goRolling.SetShown(go && HasGo("rolling"));
        goWeekly.SetShown(go && HasGo("weekly"));
        goMonthly.SetShown(go && HasGo("monthly"));
    }

    private bool HasGo(string key) => goWindows?.Any(window => window.Key == key) == true;

    private IEnumerable<UsageRow> AllRows()
    {
        yield return codexPrimary;
        yield return codexSecondary;
        yield return goRolling;
        yield return goWeekly;
        yield return goMonthly;
    }

    private IEnumerable<UsageRow> CodexRows() => new[] { codexPrimary, codexSecondary }.Where(row => row.Shown);

    private IEnumerable<UsageRow> GoRows() => new[] { goRolling, goWeekly, goMonthly }.Where(row => row.Shown);

    private void PlaceSection(Label label, Panel rule, int top, int width)
    {
        label.Location = new Point(LogicalToDeviceUnits(15), top);
        var left = LogicalToDeviceUnits(15) + label.PreferredWidth + LogicalToDeviceUnits(8);
        rule.SetBounds(left, top + LogicalToDeviceUnits(7), Math.Max(0, width - left - LogicalToDeviceUnits(15)), LogicalToDeviceUnits(1));
    }

    private void PlaceRow(UsageRow row, int top, int width)
    {
        row.Name.SetBounds(LogicalToDeviceUnits(15), top, LogicalToDeviceUnits(110), LogicalToDeviceUnits(19));
        row.Percent.SetBounds(LogicalToDeviceUnits(126), top - LogicalToDeviceUnits(7), Math.Max(LogicalToDeviceUnits(40), width - LogicalToDeviceUnits(141)), LogicalToDeviceUnits(30));
        row.Bar.SetBounds(LogicalToDeviceUnits(15), top + LogicalToDeviceUnits(23), Math.Max(LogicalToDeviceUnits(40), width - LogicalToDeviceUnits(30)), LogicalToDeviceUnits(5));
        row.ResetCaption.Location = new Point(LogicalToDeviceUnits(15), top + LogicalToDeviceUnits(33));
        row.Reset.Location = new Point(LogicalToDeviceUnits(58), top + LogicalToDeviceUnits(32));
    }

    private static string WindowName(int minutes) => minutes switch
    {
        10080 => "주간 한도",
        300 => "5시간 한도",
        > 0 => $"{minutes}분 한도",
        _ => "사용 한도"
    };

    private static UsageSnapshot ReadUsage()
    {
        var codex = FindCodexExecutable();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = codex,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--stdio");
        if (!process.Start()) throw new InvalidOperationException();
        try
        {
            process.StandardInput.AutoFlush = true;
            Send(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex-usage-widget", title = "Codex Usage Widget", version = "1.0" }, capabilities = new { experimentalApi = false } } });
            using var initialized = ReadResponse(process, 1);
            Send(process, new { method = "initialized" });
            Send(process, new { id = 2, method = "account/rateLimits/read", @params = new { excludeResetCreditDetails = true } });
            using var response = ReadResponse(process, 2);
            var result = response.RootElement.GetProperty("result");
            var limits = result.GetProperty("rateLimits");
            if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object && byId.TryGetProperty("codex", out var codexLimit)) limits = codexLimit;
            var primary = ReadWindow(limits.GetProperty("primary"));
            UsageWindow? secondary = limits.TryGetProperty("secondary", out var value) && value.ValueKind == JsonValueKind.Object ? ReadWindow(value) : null;
            process.StandardInput.Close();
            process.WaitForExit(2000);
            return new UsageSnapshot(primary, secondary);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    private static UsageWindow ReadWindow(JsonElement value) => new(
        value.GetProperty("usedPercent").GetInt32(),
        value.GetProperty("windowDurationMins").GetInt32(),
        value.GetProperty("resetsAt").GetInt64());

    private static void Send(Process process, object message) => process.StandardInput.WriteLine(JsonSerializer.Serialize(message));

    private static JsonDocument ReadResponse(Process process, int id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            if (line is null) break;
            var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                if (message.RootElement.TryGetProperty("error", out var error))
                {
                    var errorText = error.TryGetProperty("message", out var text) ? text.GetString() : "Codex app-server 오류";
                    message.Dispose();
                    throw new InvalidOperationException(errorText);
                }
                return message;
            }
            message.Dispose();
        }
        throw new TimeoutException("Codex app-server 응답 시간이 초과됐습니다.");
    }

    private static string FindCodexExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var candidateInApp = Directory.Exists(root)
            ? Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return candidateInApp ?? throw new FileNotFoundException("Codex CLI를 찾을 수 없습니다.");
    }

    private static Color RemainingColor(int remaining) => remaining < 10
        ? Color.FromArgb(199, 58, 69)
        : remaining < 30 ? Color.FromArgb(173, 109, 20) : Accent;

    private static int RecordAlert(WidgetSettings settings, string key, int remaining, long resetsAt)
    {
        if (!settings.Alerts.TryGetValue(key, out var state) || state.ResetsAt != resetsAt)
            settings.Alerts[key] = state = new AlertState { ResetsAt = resetsAt };
        var level = remaining < 10 ? 2 : remaining < 30 ? 1 : 0;
        if (level <= state.Level) return 0;
        state.Level = level;
        return level == 2 ? 10 : 30;
    }

    private void BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        var toggle = new ToolStripMenuItem("위젯 숨기기", null, (_, _) => ToggleVisibility());
        menu.Items.Add(toggle);
        menu.Items.Add("새로고침", null, async (_, _) => await RefreshUsageAsync());
        menu.Items.Add("위젯 설정", null, (_, _) => { ShowWidget(); ShowOpacityPicker(); });
        var services = new ToolStripMenuItem("표시 서비스");
        var codexItem = new ToolStripMenuItem("Codex") { CheckOnClick = true, Checked = settings.ShowCodex };
        codexItem.CheckedChanged += (_, _) => SetService(codex: codexItem.Checked);
        var goItem = new ToolStripMenuItem("OpenCode Go") { CheckOnClick = true, Checked = settings.ShowOpenCodeGo };
        goItem.CheckedChanged += (_, _) => SetService(go: goItem.Checked);
        services.DropDownItems.Add(codexItem);
        services.DropDownItems.Add(goItem);
        services.DropDownItems.Add(new ToolStripSeparator());
        services.DropDownItems.Add("OpenCode Go API 키…", null, (_, _) => { ShowWidget(); ShowOpacityPicker(); });
        menu.Items.Add(services);
        var monitors = new ToolStripMenuItem("표시 모니터");
        menu.Items.Add(monitors);
        var tibo = new ToolStripMenuItem("Tibo 리셋 예고");
        var tiboEnabled = new ToolStripMenuItem("예고·관찰 알림 받기") { CheckOnClick = true, Checked = settings.TiboAlertsEnabled };
        tiboEnabled.CheckedChanged += async (_, _) =>
        {
            settings.TiboAlertsEnabled = tiboEnabled.Checked;
            SavePosition();
            if (settings.TiboAlertsEnabled)
            {
                tiboStatus.Text = "예고 확인 대기";
                await RefreshTiboAsync();
            }
            else tiboStatus.Text = "예고 알림 꺼짐";
        };
        if (!settings.TiboAlertsEnabled) tiboStatus.Text = "예고 알림 꺼짐";
        tiboOriginal.Click += (_, _) =>
        {
            if (tiboNotice is not null) Process.Start(new ProcessStartInfo(tiboNotice.Url.AbsoluteUri) { UseShellExecute = true });
        };
        tibo.DropDownItems.Add(tiboEnabled);
        tibo.DropDownItems.Add(tiboStatus);
        tibo.DropDownItems.Add(tiboOriginal);
        tibo.DropDownItems.Add("데이터: Codex Resets (비공식)", null,
            (_, _) => Process.Start(new ProcessStartInfo(TiboResetFeed.SourceUrl) { UseShellExecute = true }));
        menu.Items.Add(tibo);
        var updates = new ToolStripMenuItem("앱 업데이트");
        updates.DropDownItems.Add(updateStatus);
        updateCheck.Click += async (_, _) => await CheckUpdateAsync(true);
        updates.DropDownItems.Add(updateCheck);
        menu.Items.Add(updates);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Close());
        menu.Opening += (_, _) =>
        {
            toggle.Text = Visible ? "위젯 숨기기" : "위젯 보이기";
            if (codexItem.Checked != settings.ShowCodex) codexItem.Checked = settings.ShowCodex;
            if (goItem.Checked != settings.ShowOpenCodeGo) goItem.Checked = settings.ShowOpenCodeGo;
            while (monitors.DropDownItems.Count > 0) monitors.DropDownItems[0].Dispose();
            foreach (var screen in Screen.AllScreens)
            {
                var item = new ToolStripMenuItem($"{screen.DeviceName} ({screen.Bounds.Width} × {screen.Bounds.Height})")
                { Checked = screen.DeviceName == Screen.FromControl(this).DeviceName };
                item.Click += (_, _) =>
                {
                    var area = screen.WorkingArea;
                    Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
                    KeepOnScreen(screen);
                    SavePosition();
                    ShowWidget();
                };
                monitors.DropDownItems.Add(item);
            }
        };
        tray.ContextMenuStrip = menu;
        ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ToggleVisibility();
        tray.BalloonTipClicked += (_, _) => ShowWidget();
    }

    private void ToggleVisibility()
    {
        if (Visible) Hide(); else ShowWidget();
    }

    private void ShowWidget()
    {
        KeepOnScreen(Screen.FromControl(this));
        Show();
        Activate();
    }

    private void RestorePosition()
    {
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor) ?? Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        Location = settings.X is { } x && settings.Y is { } y
            ? new Point(area.Left + x, area.Top + y)
            : new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        KeepOnScreen(screen);
        taskbarBadge.SetMonitor(screen.DeviceName);
    }

    private void KeepOnScreen(Screen screen)
    {
        var area = screen.WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => { RestorePosition(); FitUsageLayout(); }));
    }

    private bool SavePosition()
    {
        var screen = Screen.FromControl(this);
        settings.Monitor = screen.DeviceName;
        taskbarBadge.SetMonitor(screen.DeviceName);
        settings.X = Left - screen.WorkingArea.Left;
        settings.Y = Top - screen.WorkingArea.Top;
        settings.Opacity = Opacity;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = "설정 저장 실패";
            return false;
        }
    }

    private static WidgetSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath)) ?? new();
            // 알림 키에 서비스 접두사를 붙이기 전에 저장된 기록을 옮긴다.
            foreach (var legacy in new[] { "300", "10080" })
                if (settings.Alerts.Remove(legacy, out var state)) settings.Alerts["codex:" + legacy] = state;
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private sealed class WidgetSettings
    {
        public string? Monitor { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public double Opacity { get; set; } = 1;
        public bool AlwaysOnTop { get; set; } = true;
        public bool ShowCodex { get; set; } = true;
        public bool ShowOpenCodeGo { get; set; } = true;
        public string? OpenCodeGoKey { get; set; }
        public Dictionary<string, AlertState> Alerts { get; set; } = new();
        public bool TiboAlertsEnabled { get; set; } = true;
        public HashSet<string> TiboAlertKeys { get; set; } = new();
        public string? DeclinedUpdateVersion { get; set; }
    }

    private sealed class AlertState
    {
        public long ResetsAt { get; set; }
        public int Level { get; set; }
    }

    private void SetService(bool? codex = null, bool? go = null)
    {
        if (codex is { } codexValue) settings.ShowCodex = codexValue;
        if (go is { } goValue) settings.ShowOpenCodeGo = goValue;
        SavePosition();
        status.Text = StatusText();
        FitUsageLayout();
        UpdateTrayText();
        taskbarBadge.Update(TaskbarRows());
        if ((codex == true && codexUsage is null) || (go == true && goWindows is null)) _ = RefreshUsageAsync();
    }

    private string GoKeyStatusText()
    {
        if (goApiKey is null) return "키 없음 · OpenCode Go 숨김";
        if (goAuthFailed) return "인증되지 않은 키 · OpenCode Go 숨김";
        if (goWindows is null) return "키 저장됨 · 확인 대기";
        return "인증 확인 · " + DateTime.Now.ToString("HH:mm");
    }

    private async Task<string?> SaveGoKeyAsync(string value)
    {
        if (value.Length == 0) return "키를 입력하세요.";
        IReadOnlyList<GoUsageWindow> windows;
        try
        {
            windows = await OpenCodeGoUsage.ReadAsync(goClient, value);
        }
        catch (OpenCodeGoAuthException)
        {
            return "인증되지 않은 키입니다.";
        }
        catch (Exception error)
        {
            return "확인 실패 · " + error.Message;
        }

        var protectedKey = SecretStore.Protect(value);
        if (protectedKey is null) return "키를 안전하게 저장하지 못했습니다.";
        settings.OpenCodeGoKey = protectedKey;
        goApiKey = value;
        goAuthFailed = false;
        goWindows = windows;
        goStale = false;
        if (!SavePosition()) return "설정 저장 실패";
        RenderGo();
        status.Text = StatusText();
        FitUsageLayout();
        UpdateTrayText();
        taskbarBadge.Update(TaskbarRows());
        return null;
    }

    private void ClearGoKey()
    {
        settings.OpenCodeGoKey = null;
        goApiKey = null;
        goWindows = null;
        goAuthFailed = false;
        goStale = false;
        SavePosition();
        status.Text = StatusText();
        FitUsageLayout();
        UpdateTrayText();
        taskbarBadge.Update(TaskbarRows());
    }

    private void ShowOpacityPicker()
    {
        var picker = new OpacityPicker(Opacity, value => { Opacity = value; SavePosition(); }, IsStartupEnabled(), SetStartupEnabled,
            TopMost, value => { TopMost = settings.AlwaysOnTop = value; SavePosition(); },
            settings.ShowCodex, value => SetService(codex: value),
            settings.ShowOpenCodeGo, value => SetService(go: value),
            SaveGoKeyAsync, ClearGoKey, GoKeyStatusText());
        var area = Screen.FromControl(this).WorkingArea;
        var x = Math.Min(Right - picker.Width, area.Right - picker.Width - 8);
        var y = Bottom + 6;
        if (y + picker.Height > area.Bottom) y = Top - picker.Height - 6;
        picker.Location = new Point(Math.Max(area.Left + 8, x), Math.Max(area.Top + 8, y));
        picker.Show(this);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var registeredPath = key?.GetValue("CodexUsageWidget") as string;
        var currentPath = Environment.ProcessPath;
        return currentPath is not null && string.Equals(registeredPath, $"\"{currentPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartupEnabled(bool enabled)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("시작프로그램 레지스트리를 열 수 없습니다.");

        if (enabled)
        {
            var currentPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("현재 실행 파일의 경로를 확인할 수 없습니다.");
            key.SetValue("CodexUsageWidget", $"\"{currentPath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("CodexUsageWidget", throwOnMissingValue: false);
        }
    }

    private static void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(ActiveForm!.Handle, 0xA1, 0x2, 0);
    }

    private sealed record UsageWindow(int UsedPercent, int DurationMinutes, long ResetsAt);
    private sealed record UsageSnapshot(UsageWindow Primary, UsageWindow? Secondary);

    private sealed class UsageRow
    {
        private bool shown;

        internal Label Name { get; } = new();
        internal Label Percent { get; } = new();
        internal RemainingBar Bar { get; } = new();
        internal Label ResetCaption { get; } = new();
        internal Label Reset { get; } = new();

        /// <summary>
        /// 목록에 포함되는지 여부. Control.Visible은 부모 창이 숨겨지면 false가 되므로
        /// 레이아웃 계산에는 이 값을 따로 보관한다.
        /// </summary>
        internal bool Shown => shown;

        internal void SetShown(bool value)
        {
            shown = value;
            Name.Visible = Percent.Visible = Bar.Visible = ResetCaption.Visible = Reset.Visible = value;
        }
    }

    private sealed class RemainingBar : Control
    {
        public int Remaining { get; set; } = 100;
        public bool Stale { get; set; }

        public RemainingBar()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var track = GlassForm.RoundedRectangle(ClientRectangle, ClientSize.Height / 2f);
            using var trackBrush = new SolidBrush(Color.FromArgb(34, 98, 132, 177));
            e.Graphics.FillPath(trackBrush, track);
            var width = (int)(ClientSize.Width * Math.Clamp(Remaining, 0, 100) / 100d);
            if (width > 0)
            {
                var color = RemainingColor(Remaining);
                using var brush = new SolidBrush(Stale ? Color.FromArgb(140, color) : color);
                using var fill = GlassForm.RoundedRectangle(new RectangleF(0, 0, width, ClientSize.Height), ClientSize.Height / 2f);
                e.Graphics.FillPath(brush, fill);
            }
        }
    }

    private sealed class OpacityPicker : GlassForm
    {
        private readonly TrackBar slider = new();
        private readonly Label valueLabel = new();
        private readonly CheckBox startupCheck = new();
        private readonly CheckBox topMostCheck = new();
        private readonly CheckBox codexCheck = new();
        private readonly CheckBox goCheck = new();
        private readonly TextBox keyBox = new();
        private readonly Label keyStatusLabel = new();

        public OpacityPicker(double current, Action<double> onChange, bool startupEnabled, Action<bool> onStartupChange,
            bool alwaysOnTop, Action<bool> onTopMostChange, bool codexEnabled, Action<bool> onCodexChange,
            bool goEnabled, Action<bool> onGoChange, Func<string, Task<string?>> onSaveKey, Action onClearKey, string keyStatusText)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = alwaysOnTop;
            ForeColor = TextColor;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(15),
                BackColor = Color.Transparent
            };
            Controls.Add(layout);
            Text = "위젯 설정";
            var header = new Panel { Size = new Size(210, 28), Margin = new Padding(0, 0, 0, 8), BackColor = Color.Transparent };
            var title = new Label { Text = "위젯 설정", AutoSize = true, Location = new Point(0, 3), Font = new Font("Segoe UI", 9, FontStyle.Bold), BackColor = Color.Transparent };
            var close = new IconButton(IconButton.Icon.Close) { Size = new Size(24, 24), Location = new Point(186, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, ForeColor = Muted, AccessibleName = "설정 닫기" };
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 229, 248);
            close.Click += (_, _) => Close();
            header.Controls.Add(title);
            header.Controls.Add(close);
            header.MouseDown += DragWindow;
            title.MouseDown += DragWindow;
            layout.Controls.Add(header);
            valueLabel.Text = $"투명도  {Math.Round(current * 100)}%";
            valueLabel.Font = new Font("Segoe UI", 8.5f);
            valueLabel.ForeColor = TextColor;
            valueLabel.AutoSize = true;
            valueLabel.BackColor = Color.Transparent;
            valueLabel.Margin = new Padding(0, 0, 0, 4);
            layout.Controls.Add(valueLabel);

            slider.Minimum = 45;
            slider.Maximum = 100;
            slider.TickStyle = TickStyle.None;
            slider.BackColor = Color.FromArgb(239, 245, 254);
            slider.SmallChange = 5;
            slider.LargeChange = 10;
            slider.Value = Math.Clamp((int)Math.Round(current * 100), slider.Minimum, slider.Maximum);
            slider.AutoSize = false;
            slider.Size = new Size(210, 34);
            slider.Margin = new Padding(0, 0, 0, 8);
            slider.ValueChanged += (_, _) =>
            {
                valueLabel.Text = $"투명도  {slider.Value}%";
                onChange(slider.Value / 100d);
            };
            layout.Controls.Add(slider);

            topMostCheck.Text = "항상 위";
            topMostCheck.AccessibleName = "항상 위";
            topMostCheck.Font = new Font("Segoe UI", 9);
            topMostCheck.AutoSize = true;
            topMostCheck.BackColor = Color.Transparent;
            topMostCheck.Margin = new Padding(0, 0, 0, 10);
            topMostCheck.Checked = alwaysOnTop;
            topMostCheck.CheckedChanged += (_, _) =>
            {
                TopMost = topMostCheck.Checked;
                onTopMostChange(topMostCheck.Checked);
            };
            layout.Controls.Add(topMostCheck);

            startupCheck.Text = "윈도우 시작 시 자동 실행";
            startupCheck.Font = new Font("Segoe UI", 9);
            startupCheck.ForeColor = TextColor;
            startupCheck.BackColor = Color.Transparent;
            startupCheck.AutoSize = true;
            startupCheck.Margin = Padding.Empty;
            startupCheck.AccessibleName = "윈도우 시작 시 자동 실행";
            startupCheck.Checked = startupEnabled;
            var updatingStartupCheck = false;
            startupCheck.CheckedChanged += (_, _) =>
            {
                if (updatingStartupCheck) return;
                try
                {
                    onStartupChange(startupCheck.Checked);
                }
                catch (Exception error)
                {
                    updatingStartupCheck = true;
                    try { startupCheck.Checked = IsStartupEnabled(); }
                    catch { startupCheck.Checked = !startupCheck.Checked; }
                    updatingStartupCheck = false;
                    MessageBox.Show(this, $"시작프로그램 설정을 저장하지 못했습니다.\n{error.Message}", "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            layout.Controls.Add(startupCheck);

            var servicesLabel = new Label { Text = "표시 서비스", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Muted,
                AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 14, 0, 5) };
            layout.Controls.Add(servicesLabel);

            codexCheck.Text = "Codex";
            StyleCheck(codexCheck, codexEnabled);
            codexCheck.CheckedChanged += (_, _) => onCodexChange(codexCheck.Checked);
            layout.Controls.Add(codexCheck);

            goCheck.Text = "OpenCode Go";
            StyleCheck(goCheck, goEnabled);
            goCheck.Margin = new Padding(0, 0, 0, 12);
            goCheck.CheckedChanged += (_, _) => onGoChange(goCheck.Checked);
            layout.Controls.Add(goCheck);

            var keyLabel = new Label { Text = "OpenCode Go API 키", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Muted,
                AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) };
            layout.Controls.Add(keyLabel);

            keyBox.Width = 210;
            keyBox.Font = new Font("Segoe UI", 9);
            keyBox.UseSystemPasswordChar = true;
            keyBox.PlaceholderText = "oc_sk_…";
            keyBox.AccessibleName = "OpenCode Go API 키";
            keyBox.Margin = new Padding(0, 0, 0, 5);
            layout.Controls.Add(keyBox);

            var keyButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 5) };
            var saveKey = new Button { Text = "키 저장", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 6, 0) };
            var clearKey = new Button { Text = "키 지우기", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = Padding.Empty };
            keyButtons.Controls.Add(saveKey);
            keyButtons.Controls.Add(clearKey);
            layout.Controls.Add(keyButtons);

            keyStatusLabel.Text = keyStatusText;
            keyStatusLabel.Font = new Font("Segoe UI", 7.5f);
            keyStatusLabel.ForeColor = Muted;
            keyStatusLabel.AutoSize = true;
            keyStatusLabel.BackColor = Color.Transparent;
            keyStatusLabel.Margin = Padding.Empty;
            keyStatusLabel.AccessibleName = "OpenCode Go 키 상태";
            layout.Controls.Add(keyStatusLabel);

            saveKey.Click += async (_, _) =>
            {
                saveKey.Enabled = false;
                keyStatusLabel.Text = "확인 중…";
                keyStatusLabel.ForeColor = Muted;
                try
                {
                    var error = await onSaveKey(keyBox.Text.Trim());
                    keyBox.Clear();
                    keyStatusLabel.Text = error ?? "인증 확인 · 키 저장됨";
                    keyStatusLabel.ForeColor = error is null ? Accent : Color.FromArgb(173, 109, 20);
                }
                finally
                {
                    saveKey.Enabled = true;
                }
            };
            clearKey.Click += (_, _) =>
            {
                onClearKey();
                keyBox.Clear();
                keyStatusLabel.Text = "키 없음 · OpenCode Go 숨김";
                keyStatusLabel.ForeColor = Muted;
            };
            ResumeLayout(true);
        }

        private static void StyleCheck(CheckBox check, bool value)
        {
            check.Font = new Font("Segoe UI", 9);
            check.ForeColor = TextColor;
            check.BackColor = Color.Transparent;
            check.AutoSize = true;
            check.Margin = new Padding(0, 0, 0, 4);
            check.Checked = value;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // 두 번째 실행에서 보낸 요청이면 숨겨 둔 위젯을 다시 보여준다.
        if (m.Msg == Program.ShowWindowMessage)
        {
            ShowWidget();
            return;
        }
        base.WndProc(ref m);
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);
    }
}
