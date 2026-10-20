using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexUsageWidget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new UsageForm());
    }
}

internal sealed class UsageForm : GlassForm
{
    private static readonly Color Muted = Color.FromArgb(89, 108, 134);
    private static readonly Color TextColor = Color.FromArgb(31, 45, 67);
    private static readonly Color Accent = Color.FromArgb(18, 137, 105);
    private readonly IconButton refreshButton = new(IconButton.Icon.Refresh);
    private readonly Label primaryName = new();
    private readonly Label primaryPercent = new();
    private readonly RemainingBar primaryBar = new();
    private readonly Label primaryReset = new();
    private readonly Label secondaryName = new();
    private readonly Label secondaryPercent = new();
    private readonly RemainingBar secondaryBar = new();
    private readonly Label secondaryResetCaption = new();
    private readonly Label secondaryReset = new();
    private readonly Label status = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 180_000 };
    private readonly System.Windows.Forms.Timer tiboTimer = new() { Interval = 120_000 };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 21_600_000 };
    private readonly ToolStripMenuItem updateStatus = new($"현재 v{AppUpdate.CurrentVersion.ToString(3)}") { Enabled = false };
    private readonly ToolStripMenuItem updateCheck = new("업데이트 확인");
    private readonly HttpClient tiboClient = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 262144 };
    private readonly ToolStripMenuItem tiboStatus = new("아직 확인하지 않음") { Enabled = false };
    private readonly ToolStripMenuItem tiboOriginal = new("최근 예고·관찰 X 원문") { Enabled = false };
    private readonly NotifyIcon tray = new() { Text = "Codex 사용량" };
    private readonly TaskbarBadge taskbarBadge;
    private readonly WidgetSettings settings = LoadSettings();
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "settings.json");
    private bool refreshing;
    private bool hasSecondary;
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
        BuildUi();
        ResumeLayout(true);
        BuildTrayMenu();
        taskbarBadge = new TaskbarBadge(Icon, tray.ContextMenuStrip!, () => { if (!IsDisposed) BeginInvoke((Action)ShowWidget); });
        Opacity = Math.Clamp(settings.Opacity, 0.45, 1);
        RestorePosition();
        ResizeEnd += (_, _) => SavePosition();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
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

        var brand = new Label { Text = "CODEX", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(53, 102, 195), Location = new Point(14, 3), Size = new Size(54, 18), BackColor = Color.Transparent };
        var heading = new Label { Text = "사용량", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Muted, Location = new Point(68, 3), Size = new Size(58, 18), BackColor = Color.Transparent };
        header.Controls.Add(brand);
        header.Controls.Add(heading);
        brand.MouseDown += DragWindow;
        heading.MouseDown += DragWindow;

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

        primaryName.SetBounds(15, 44, 120, 19);
        primaryName.Font = new Font("Segoe UI", 8.5f);
        primaryName.ForeColor = Muted;
        Controls.Add(primaryName);

        primaryPercent.SetBounds(130, 38, 106, 35);
        primaryPercent.TextAlign = ContentAlignment.MiddleRight;
        primaryPercent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        primaryPercent.Font = new Font("Segoe UI", 20, FontStyle.Bold);
        primaryPercent.ForeColor = TextColor;
        Controls.Add(primaryPercent);

        primaryBar.SetBounds(15, 77, 221, 5);
        primaryBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(primaryBar);
        AddReset(primaryReset, new Label(), 94);

        secondaryName.SetBounds(15, 132, 120, 19);
        secondaryName.Font = new Font("Segoe UI", 8.5f);
        secondaryName.ForeColor = Muted;
        secondaryName.Visible = false;
        Controls.Add(secondaryName);

        secondaryPercent.SetBounds(130, 126, 106, 35);
        secondaryPercent.TextAlign = ContentAlignment.MiddleRight;
        secondaryPercent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        secondaryPercent.Font = new Font("Segoe UI", 20, FontStyle.Bold);
        secondaryPercent.ForeColor = TextColor;
        secondaryPercent.Visible = false;
        Controls.Add(secondaryPercent);

        secondaryBar.SetBounds(15, 165, 221, 5);
        secondaryBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        secondaryBar.Visible = false;
        Controls.Add(secondaryBar);
        AddReset(secondaryReset, secondaryResetCaption, 182);

        status.SetBounds(15, 133, 221, 16);
        status.Font = new Font("Segoe UI", 7.5f);
        status.AutoSize = true;
        status.ForeColor = Muted;
        Controls.Add(status);
        foreach (var label in Controls.OfType<Label>()) label.BackColor = Color.Transparent;
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(UsageForm).Assembly.GetManifestResourceStream("CodexUsageWidget.Widget.ico")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void AddReset(Label label, Label caption, int top)
    {
        caption.Text = "초기화";
        caption.Font = new Font("Segoe UI", 8);
        caption.ForeColor = Muted;
        caption.Location = new Point(15, top);
        caption.AutoSize = true;
        caption.Visible = top < 120;
        label.Font = new Font("Segoe UI", 7.5f);
        label.ForeColor = TextColor;
        label.Location = new Point(58, top - 1);
        label.AutoSize = true;
        label.Visible = top < 120;
        Controls.Add(caption);
        Controls.Add(label);
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
            var usage = await Task.Run(ReadUsage);
            if (IsDisposed) return;
            SetWindow(primaryName, primaryPercent, primaryBar, primaryReset, usage.Primary);
            hasSecondary = usage.Secondary is not null;
            if (usage.Secondary is { } secondary)
            {
                SetWindow(secondaryName, secondaryPercent, secondaryBar, secondaryReset, secondary);
                secondaryName.Visible = secondaryPercent.Visible = secondaryBar.Visible = secondaryResetCaption.Visible = secondaryReset.Visible = true;
            }
            else
            {
                secondaryName.Visible = secondaryPercent.Visible = secondaryBar.Visible = secondaryResetCaption.Visible = secondaryReset.Visible = false;
            }
            var alerts = new List<string>();
            foreach (var window in new[] { usage.Primary, usage.Secondary }.OfType<UsageWindow>())
            {
                var threshold = RecordAlert(settings, window);
                if (threshold > 0) alerts.Add($"{WindowName(window.DurationMinutes)} 잔여량이 {threshold}% 미만입니다. (현재 {100 - Math.Clamp(window.UsedPercent, 0, 100)}%)");
            }
            var updateText = "3분마다 갱신 · " + DateTime.Now.ToString("HH:mm");
            status.Text = updateText;
            FitUsageLayout();
            KeepOnScreen(Screen.FromControl(this));
            var settingsSaved = SavePosition();
            if (alerts.Count > 0) tray.ShowBalloonTip(10000, "Codex 잔여량 알림", string.Join("\n", alerts), ToolTipIcon.Warning);
            tray.Text = $"Codex · {primaryName.Text} {primaryPercent.Text}";
            taskbarBadge.Update($"{WindowName(usage.Primary.DurationMinutes).Replace(" 한도", "")} {100 - Math.Clamp(usage.Primary.UsedPercent, 0, 100)}% · {ResetRemainingText(usage.Primary)}\n"
                + (usage.Secondary is { } second ? $"{WindowName(second.DurationMinutes).Replace(" 한도", "")} {100 - Math.Clamp(second.UsedPercent, 0, 100)}% · {ResetRemainingText(second)}" : "Codex 잔여량"));
            if (!settingsSaved)
            {
                status.Text = updateText + " · 설정 저장 실패";
                FitUsageLayout();
            }
        }
        catch
        {
            if (!IsDisposed)
            {
                status.Text = "갱신 실패 · " + DateTime.Now.ToString("HH:mm");
                taskbarBadge.MarkStale();
            }
        }
        finally
        {
            refreshing = false;
            if (!IsDisposed) refreshButton.Enabled = true;
        }
    }

    private static void SetWindow(Label name, Label percent, RemainingBar bar, Label reset, UsageWindow window)
    {
        var remaining = 100 - Math.Clamp(window.UsedPercent, 0, 100);
        name.Text = WindowName(window.DurationMinutes) + " 남음";
        percent.Text = $"{remaining}%";
        percent.ForeColor = RemainingColor(remaining);
        bar.Remaining = remaining;
        bar.Invalidate();
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt);
        var timeLeft = resetAt - DateTimeOffset.Now;
        if (timeLeft < TimeSpan.Zero) timeLeft = TimeSpan.Zero;
        var remainingText = ResetRemainingText(window, timeLeft);
        reset.Text = $"{resetAt.LocalDateTime:M/d HH:mm} ({remainingText})";
    }

    private static string ResetRemainingText(UsageWindow window)
    {
        var timeLeft = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt) - DateTimeOffset.Now;
        return ResetRemainingText(window, timeLeft < TimeSpan.Zero ? TimeSpan.Zero : timeLeft);
    }

    private static string ResetRemainingText(UsageWindow window, TimeSpan timeLeft) => window.DurationMinutes == 300
        ? $"{(int)Math.Ceiling(timeLeft.TotalMinutes)}분 남음"
        : $"{(int)timeLeft.TotalDays}일 {timeLeft.Hours:00}시간 남음";

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
        var baseSize = LogicalToDeviceUnits(new Size(252, hasSecondary ? 223 : 158));
        var margin = LogicalToDeviceUnits(15);
        var lastReset = hasSecondary ? secondaryReset : primaryReset;
        status.Top = Math.Max(baseSize.Height - LogicalToDeviceUnits(25), lastReset.Bottom + LogicalToDeviceUnits(14));
        ClientSize = new Size(Math.Max(baseSize.Width, Math.Max(status.Right, Math.Max(primaryReset.Right, hasSecondary ? secondaryReset.Right : 0)) + margin),
            Math.Max(baseSize.Height, status.Bottom + LogicalToDeviceUnits(9)));
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

    private static int RecordAlert(WidgetSettings settings, UsageWindow window)
    {
        var key = window.DurationMinutes.ToString();
        if (!settings.Alerts.TryGetValue(key, out var state) || state.ResetsAt != window.ResetsAt)
            settings.Alerts[key] = state = new AlertState { ResetsAt = window.ResetsAt };
        var remaining = 100 - Math.Clamp(window.UsedPercent, 0, 100);
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
        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)RestorePosition);
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
            return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath)) ?? new() : new();
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

    private void ShowOpacityPicker()
    {
        var picker = new OpacityPicker(Opacity, value => { Opacity = value; SavePosition(); }, IsStartupEnabled(), SetStartupEnabled,
            TopMost, value => { TopMost = settings.AlwaysOnTop = value; SavePosition(); });
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

    private sealed class RemainingBar : Control
    {
        public int Remaining { get; set; } = 100;

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
                using var brush = new SolidBrush(RemainingColor(Remaining));
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

        public OpacityPicker(double current, Action<double> onChange, bool startupEnabled, Action<bool> onStartupChange,
            bool alwaysOnTop, Action<bool> onTopMostChange)
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
            ResumeLayout(true);
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);
    }
}
