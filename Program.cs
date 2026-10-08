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

internal sealed class UsageForm : Form
{
    private static readonly Color Background = Color.FromArgb(22, 25, 30);
    private static readonly Color HeaderBackground = Color.FromArgb(28, 32, 38);
    private static readonly Color Muted = Color.FromArgb(155, 164, 176);
    private static readonly Color TextColor = Color.FromArgb(239, 242, 246);
    private static readonly Color Accent = Color.FromArgb(128, 229, 174);
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
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "Codex 사용량", Visible = true };
    private readonly WidgetSettings settings = LoadSettings();
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "settings.json");
    private bool refreshing;

    public UsageForm()
    {
        Text = "Codex Usage";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        ClientSize = new Size(252, 158);
        BackColor = Background;
        ForeColor = TextColor;
        BuildUi();
        BuildTrayMenu();
        Opacity = Math.Clamp(settings.Opacity, 0.45, 1);
        RestorePosition();
        ResizeEnd += (_, _) => SavePosition();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        timer.Tick += async (_, _) => await RefreshUsageAsync();
        Shown += async (_, _) => await RefreshUsageAsync();
        FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        };
        timer.Start();
    }

    private void BuildUi()
    {
        var header = new Panel { Location = new Point(1, 1), Size = new Size(250, 25), BackColor = HeaderBackground };
        Controls.Add(header);
        header.MouseDown += DragWindow;

        var brand = new Label { Text = "CODEX", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Accent, Location = new Point(14, 3), Size = new Size(54, 18) };
        var heading = new Label { Text = "사용량", Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(211, 216, 224), Location = new Point(68, 3), Size = new Size(58, 18) };
        header.Controls.Add(brand);
        header.Controls.Add(heading);
        brand.MouseDown += DragWindow;
        heading.MouseDown += DragWindow;

        StyleButton(refreshButton, new Point(202, 2), new Size(20, 20));
        refreshButton.Click += async (_, _) => await RefreshUsageAsync();
        header.Controls.Add(refreshButton);

        var opacityButton = new Button { Text = "◐" };
        StyleButton(opacityButton, new Point(180, 2), new Size(20, 20), 9);
        opacityButton.Click += (_, _) => ShowOpacityPicker();
        header.Controls.Add(opacityButton);

        var close = new IconButton(IconButton.Icon.Close);
        StyleButton(close, new Point(225, 2), new Size(20, 20));
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(71, 45, 48);
        close.AccessibleName = "트레이로 숨기기";
        close.Click += (_, _) => Hide();
        header.Controls.Add(close);

        primaryName.SetBounds(15, 44, 120, 19);
        primaryName.Font = new Font("Segoe UI", 8.5f);
        primaryName.ForeColor = Muted;
        Controls.Add(primaryName);

        primaryPercent.SetBounds(130, 38, 106, 35);
        primaryPercent.TextAlign = ContentAlignment.MiddleRight;
        primaryPercent.Font = new Font("Segoe UI", 20, FontStyle.Bold);
        primaryPercent.ForeColor = TextColor;
        Controls.Add(primaryPercent);

        primaryBar.SetBounds(15, 77, 221, 5);
        Controls.Add(primaryBar);
        AddReset(primaryReset, new Label(), 94);

        secondaryName.SetBounds(15, 132, 120, 19);
        secondaryName.Font = new Font("Segoe UI", 8.5f);
        secondaryName.ForeColor = Muted;
        secondaryName.Visible = false;
        Controls.Add(secondaryName);

        secondaryPercent.SetBounds(130, 126, 106, 35);
        secondaryPercent.TextAlign = ContentAlignment.MiddleRight;
        secondaryPercent.Font = new Font("Segoe UI", 20, FontStyle.Bold);
        secondaryPercent.ForeColor = TextColor;
        secondaryPercent.Visible = false;
        Controls.Add(secondaryPercent);

        secondaryBar.SetBounds(15, 165, 221, 5);
        secondaryBar.Visible = false;
        Controls.Add(secondaryBar);
        AddReset(secondaryReset, secondaryResetCaption, 182);

        status.SetBounds(15, 133, 221, 16);
        status.Font = new Font("Segoe UI", 7.5f);
        status.ForeColor = Color.FromArgb(128, 137, 149);
        Controls.Add(status);
    }

    private void AddReset(Label label, Label caption, int top)
    {
        caption.Text = "초기화";
        caption.Font = new Font("Segoe UI", 8);
        caption.ForeColor = Color.FromArgb(128, 137, 149);
        caption.Location = new Point(15, top);
        caption.Size = new Size(42, 17);
        caption.Visible = top < 120;
        label.Font = new Font("Segoe UI", 7.5f);
        label.ForeColor = Color.FromArgb(211, 216, 224);
        label.Location = new Point(58, top - 1);
        label.Size = new Size(178, 18);
        label.Visible = top < 120;
        Controls.Add(caption);
        Controls.Add(label);
    }

    private void StyleButton(Button button, Point location, Size size, float fontSize = 9)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 54, 62);
        button.Font = new Font("Segoe UI Symbol", fontSize);
        button.ForeColor = Color.FromArgb(184, 192, 203);
        button.BackColor = HeaderBackground;
        button.Location = location;
        button.Size = size;
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
            if (usage.Secondary is { } secondary)
            {
                SetWindow(secondaryName, secondaryPercent, secondaryBar, secondaryReset, secondary);
                secondaryName.Visible = secondaryPercent.Visible = secondaryBar.Visible = secondaryResetCaption.Visible = secondaryReset.Visible = true;
                ClientSize = new Size(252, 223);
            }
            else
            {
                secondaryName.Visible = secondaryPercent.Visible = secondaryBar.Visible = secondaryResetCaption.Visible = secondaryReset.Visible = false;
                ClientSize = new Size(252, 158);
            }
            KeepOnScreen(Screen.FromControl(this));
            var alerts = new List<string>();
            foreach (var window in new[] { usage.Primary, usage.Secondary }.OfType<UsageWindow>())
            {
                var threshold = RecordAlert(settings, window);
                if (threshold > 0) alerts.Add($"{WindowName(window.DurationMinutes)} 잔여량이 {threshold}% 미만입니다. (현재 {100 - Math.Clamp(window.UsedPercent, 0, 100)}%)");
            }
            var settingsSaved = SavePosition();
            if (alerts.Count > 0) tray.ShowBalloonTip(10000, "Codex 잔여량 알림", string.Join("\n", alerts), ToolTipIcon.Warning);
            tray.Text = $"Codex · {primaryName.Text} {primaryPercent.Text}";
            status.Top = ClientSize.Height - 25;
            status.Text = "3분마다 갱신 · " + DateTime.Now.ToString("HH:mm") + (settingsSaved ? "" : " · 설정 저장 실패");
        }
        catch
        {
            if (!IsDisposed) status.Text = "갱신 실패 · " + DateTime.Now.ToString("HH:mm");
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
        reset.Text = $"{resetAt.LocalDateTime:M/d HH:mm} ({(int)timeLeft.TotalDays}일 {timeLeft.Hours:00}시간 남음)";
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
        ? Color.FromArgb(244, 105, 105)
        : remaining < 30 ? Color.FromArgb(241, 194, 91) : Accent;

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
        menu.Items.Add("투명도 / 자동 실행", null, (_, _) => { ShowWidget(); ShowOpacityPicker(); });
        var monitors = new ToolStripMenuItem("표시 모니터");
        menu.Items.Add(monitors);
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
        public Dictionary<string, AlertState> Alerts { get; set; } = new();
    }

    private sealed class AlertState
    {
        public long ResetsAt { get; set; }
        public int Level { get; set; }
    }

    private void ShowOpacityPicker()
    {
        var picker = new OpacityPicker(Opacity, value => { Opacity = value; SavePosition(); }, IsStartupEnabled(), SetStartupEnabled);
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
            BackColor = Color.FromArgb(49, 55, 63);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var width = (int)(ClientSize.Width * Math.Clamp(Remaining, 0, 100) / 100d);
            if (width > 0)
            {
                using var brush = new SolidBrush(RemainingColor(Remaining));
                e.Graphics.FillRectangle(brush, 0, 0, width, ClientSize.Height);
            }
        }
    }

    private sealed class OpacityPicker : Form
    {
        private readonly TrackBar slider = new();
        private readonly Label valueLabel = new();
        private readonly CheckBox startupCheck = new();

        public OpacityPicker(double current, Action<double> onChange, bool startupEnabled, Action<bool> onStartupChange)
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = HeaderBackground;
            ForeColor = TextColor;
            ClientSize = new Size(230, 106);
            Text = "투명도";
            valueLabel.Text = $"투명도  {Math.Round(current * 100)}%";
            valueLabel.Font = new Font("Segoe UI", 8.5f);
            valueLabel.ForeColor = TextColor;
            valueLabel.Location = new Point(10, 7);
            valueLabel.Size = new Size(210, 17);
            Controls.Add(valueLabel);

            slider.Minimum = 45;
            slider.Maximum = 100;
            slider.TickStyle = TickStyle.None;
            slider.SmallChange = 5;
            slider.LargeChange = 10;
            slider.Value = Math.Clamp((int)Math.Round(current * 100), slider.Minimum, slider.Maximum);
            slider.SetBounds(6, 27, 218, 34);
            slider.ValueChanged += (_, _) =>
            {
                valueLabel.Text = $"투명도  {slider.Value}%";
                onChange(slider.Value / 100d);
            };
            Controls.Add(slider);

            startupCheck.Text = string.Empty;
            startupCheck.Font = new Font("Segoe UI", 9);
            startupCheck.ForeColor = TextColor;
            startupCheck.SetBounds(9, 68, 22, 24);
            startupCheck.AutoSize = false;
            startupCheck.AccessibleName = "윈도우 시작 시 자동 실행";
            startupCheck.Checked = startupEnabled;
            var startupLabel = new Label
            {
                Text = "윈도우 시작 시 자동 실행",
                Font = new Font("Segoe UI", 9),
                ForeColor = TextColor,
                Location = new Point(34, 64),
                Size = new Size(190, 32),
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand
            };
            startupLabel.Click += (_, _) => startupCheck.Checked = !startupCheck.Checked;
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
            Controls.Add(startupCheck);
            Controls.Add(startupLabel);
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);
    }
}
