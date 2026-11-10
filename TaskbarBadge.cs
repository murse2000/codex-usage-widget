using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Automation;
using Microsoft.Win32;

namespace CodexUsageWidget;

internal sealed class TaskbarBadge : IDisposable
{
    /// <summary>공간이 좁을 때 덜어내는 표시 단계. Full → Compact → Values → Micro 순서로 좁아진다.</summary>
    internal enum BadgeMode { Full, Compact, Values, Micro }

    /// <summary>작업표시줄 표의 한 칸. Label은 구간 이름, Remaining은 남은 비율, Reset은 초기화까지 남은 시간이다.</summary>
    internal sealed record Cell(string Label, int Remaining, string Reset);

    /// <summary>서비스 한 줄. Cells는 5시간·주간·월간 순서로 고정해 줄마다 열이 맞도록 한다.</summary>
    internal sealed record Row(string Service, IReadOnlyList<Cell?> Cells);

    private const float BadgeFontSize = 8.5f;
    private const int Columns = 3;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Action openWidget;
    private readonly Icon icon;
    private readonly ContextMenuStrip menu;
    private BadgeButton? button;
    private IReadOnlyList<Row> rows = Array.Empty<Row>();
    private string text = "Codex\n확인 중…";
    private BadgeMode mode = BadgeMode.Full;
    private bool empty;
    private bool refreshing;
    private bool disposed;
    private bool stale;
    private string? monitor;

    internal TaskbarBadge(Icon icon, ContextMenuStrip menu, Action openWidget)
    {
        this.icon = icon;
        this.menu = menu;
        // 탐색기와 배율이 달라도 메뉴는 앱의 좌표계에서 생성한다.
        _ = menu.Handle;
        this.openWidget = openWidget;
        timer.Tick += async (_, _) => await RefreshAsync();
    }

    internal void Start()
    {
        timer.Start();
        _ = RefreshAsync();
    }

    internal void SetMonitor(string value)
    {
        if (monitor == value) return;
        monitor = value;
        if (timer.Enabled) _ = RefreshAsync();
    }

    internal void Update(IReadOnlyList<Row> value)
    {
        rows = value;
        empty = value.Count == 0;
        text = BuildText(value);
        stale = false;
        if (empty) button?.Hide();
        else ApplyText();
        // 열 너비와 높이를 즉시 다시 맞춘다.
        _ = RefreshAsync();
    }

    internal void MarkStale()
    {
        stale = true;
        ApplyText();
    }

    private static string BuildText(IReadOnlyList<Row> value) => value.Count == 0
        ? "Codex\n확인 중…"
        : string.Join("\n", value.Select(row =>
        {
            var cells = string.Join(" · ", row.Cells.Where(cell => cell is not null)
                .Select(cell => $"{cell!.Label} {cell.Remaining}% {cell.Reset}"));
            return string.IsNullOrEmpty(row.Service) ? cells : row.Service + " " + cells;
        }));

    private void ApplyText()
    {
        if (button is not { IsDisposed: false }) return;
        button.Text = stale ? Decorate(text) : text;
        button.Rows = rows;
        button.Mode = mode;
        button.Stale = stale;
        button.AccessibleName = (stale ? "갱신 실패 · 마지막 확인값. " : "") + $"Codex 잔여량 {text}. 클릭하면 위젯을 엽니다.";
        button.Invalidate();
    }

    private static string Decorate(string value) => value.Contains('%') ? value.Replace("%", "% !") : "사용량\n확인 실패";

    private async Task RefreshAsync()
    {
        if (disposed || refreshing) return;
        refreshing = true;
        try
        {
            var target = monitor;
            var layout = await Task.Run(() => ReadLayout(target));
            if (disposed) return;
            if (target != monitor) return;
            if (empty)
            {
                if (button is not null) button.Visible = false;
                return;
            }
            if (layout is null)
            {
                if (button is not null) button.Visible = false;
                return;
            }
            if (button is null || button.IsDisposed || !button.IsHandleCreated || button.Taskbar != layout.Taskbar || button.Dpi != layout.Dpi)
            {
                button?.Dispose();
                using var context = new DpiContext(layout.Taskbar);
                button = new BadgeButton(layout.Taskbar, layout.Dpi, icon) { ContextMenuStrip = menu };
                button.Click += (_, _) => openWidget();
                _ = button.Handle;
            }
            var margin = layout.Dpi * 4 / 96;
            var available = Math.Max(layout.Dpi * 18 / 96, layout.Bounds.Height - 2 * margin);
            using var graphics = button.CreateGraphics();
            var left = layout.Dpi * 40 / 96;
            var free = LargestGap(layout.Bounds, layout.Occupied, margin);
            Size size;
            if (rows.Count == 0)
            {
                mode = BadgeMode.Full;
                var width = Math.Max(TextRenderer.MeasureText(graphics, "5시간 100%\n주간 100%", button.Font).Width,
                    TextRenderer.MeasureText(graphics, Decorate(text), button.Font).Width) + layout.Dpi * 52 / 96;
                if (width + margin > free) { button.Visible = false; return; }
                var height = Math.Min(available, Measure(graphics, text, button.Font).Height + 4);
                size = new Size(width, height);
            }
            else
            {
                // 창이 많아 빈 공간이 좁으면 초기화 시간 → 구간 이름 → 서비스 이름 순으로 덜어낸다.
                // 한 서비스가 두 줄로 나뉜 경우에는 값만 남기면 열 의미가 사라지므로 Values 단계를 건너뛴다.
                var split = rows.Any(row => string.IsNullOrEmpty(row.Service));
                TableLayout? chosen = null;
                foreach (var candidate in new[] { BadgeMode.Full, BadgeMode.Compact, BadgeMode.Values })
                {
                    if (candidate == BadgeMode.Values && split) continue;
                    var measured = MeasureTable(graphics, button.Font, rows, stale, left, layout.Dpi, candidate);
                    if (measured.Width + margin > free) continue;
                    mode = candidate;
                    chosen = measured;
                    break;
                }
                if (chosen is not null)
                {
                    size = new Size(chosen.Width, Math.Min(available, chosen.Height));
                }
                else
                {
                    // 그래도 자리가 없으면 서비스별 최저 잔여량만 남긴다.
                    var micro = MeasureMicro(graphics, button.Font, rows, layout.Dpi);
                    if (micro.Lines.Count == 0 || micro.Width + margin > free) { button.Visible = false; return; }
                    mode = BadgeMode.Micro;
                    size = new Size(micro.Width, Math.Min(available, micro.Height));
                }
            }
            var location = FindSpace(layout.Bounds, layout.Occupied, size, margin);
            if (location is null) { button.Visible = false; return; }
            button.SetBounds(location.Value.X - layout.Bounds.X, location.Value.Y - layout.Bounds.Y, size.Width, size.Height);
            using var outline = GlassForm.RoundedRectangle(button.ClientRectangle, layout.Dpi * 8f / 96);
            var previous = button.Region;
            button.Region = new Region(outline);
            previous?.Dispose();
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            button.BackColor = light ? Color.FromArgb(236, 242, 250) : Color.FromArgb(39, 43, 51);
            button.ForeColor = light ? Color.FromArgb(38, 60, 90) : Color.FromArgb(234, 241, 250);
            button.WarningColor = light ? Color.FromArgb(173, 109, 20) : Color.FromArgb(230, 169, 74);
            button.DangerColor = light ? Color.FromArgb(199, 58, 69) : Color.FromArgb(240, 120, 130);
            button.FlatAppearance.MouseOverBackColor = light ? Color.FromArgb(213, 227, 248) : Color.FromArgb(62, 74, 91);
            ApplyText();
            button.Visible = true;
            Native.SetWindowPos(button.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException or InvalidOperationException or Win32Exception)
        {
            // 탐색기 전환 중에는 다음 조회에서 다시 연결한다.
            if (!disposed && button is { IsDisposed: false }) button.Visible = false;
        }
        finally { refreshing = false; }
    }

    internal sealed record Layout(IntPtr Taskbar, Rectangle Bounds, int Dpi, Rectangle[] Occupied);

    private sealed record TableLayout(int Width, int Height, int LineHeight, int ServiceWidth, int Gap, int HalfGap,
        int[] LabelWidths, int[] ValueWidths, int[] TimeWidths, int[] BlockWidths, bool ShowLabels, bool ShowTimes);

    /// <summary>
    /// 서비스 이름, 구간 이름, 남은 비율, 초기화까지 남은 시간을 열로 맞춰 그리기 위한 크기를 계산한다.
    /// 열마다 가장 넓은 칸을 기준으로 하므로 줄이 달라도 같은 열 위치를 쓰고, 구간별로 남는 여백은 생기지 않는다.
    /// </summary>
    private static TableLayout MeasureTable(Graphics graphics, Font font, IReadOnlyList<Row> rows, bool stale, int left, int dpi, BadgeMode mode)
    {
        var scale = dpi / 96f;
        var gap = Math.Max(4, (int)Math.Round(10 * scale));
        var halfGap = Math.Max(3, (int)Math.Round(5 * scale));
        var showLabels = mode != BadgeMode.Values;
        var showTimes = mode == BadgeMode.Full;
        var serviceWidth = 0;
        var columns = 0;
        foreach (var row in rows)
        {
            serviceWidth = Math.Max(serviceWidth, Measure(graphics, row.Service, font).Width);
            for (var index = 0; index < row.Cells.Count && index < Columns; index++)
                if (row.Cells[index] is not null) columns = Math.Max(columns, index + 1);
        }
        var labelWidths = new int[columns];
        var valueWidths = new int[columns];
        var timeWidths = new int[columns];
        var blockWidths = new int[columns];
        foreach (var row in rows)
        {
            for (var index = 0; index < row.Cells.Count && index < columns; index++)
            {
                var cell = row.Cells[index];
                if (cell is null) continue;
                if (showLabels) labelWidths[index] = Math.Max(labelWidths[index], Measure(graphics, cell.Label, font).Width);
                valueWidths[index] = Math.Max(valueWidths[index], Measure(graphics, ValueText(cell.Remaining, stale), font).Width);
                if (showTimes) timeWidths[index] = Math.Max(timeWidths[index], Measure(graphics, cell.Reset, font).Width);
            }
        }
        var blocks = 0;
        var hasBlock = false;
        for (var index = 0; index < columns; index++)
        {
            blockWidths[index] = (showLabels ? labelWidths[index] + halfGap : 0) + valueWidths[index]
                + (showTimes ? halfGap + timeWidths[index] : 0);
            if (blockWidths[index] == 0) continue;
            blocks += (hasBlock ? gap : 0) + blockWidths[index];
            hasBlock = true;
        }
        var width = left + serviceWidth + gap + blocks + (int)Math.Round(8 * scale);
        var lineHeight = Math.Max(1, Measure(graphics, "Ag", font).Height);
        var height = rows.Count * lineHeight + (int)Math.Round(4 * scale);
        return new(width, height, lineHeight, serviceWidth, gap, halfGap, labelWidths, valueWidths, timeWidths, blockWidths, showLabels, showTimes);
    }

    private sealed record MicroLine(string Tag, int Remaining)
    {
        internal string Text => $"{Tag}{Remaining}%";
    }

    private sealed record MicroLayout(int Width, int Height, int LineHeight, int TextWidth, int LeftPad, List<MicroLine> Lines);

    /// <summary>
    /// 아주 좁은 빈 공간에서는 서비스별로 가장 적게 남은 구간 하나만 "C 46 / G 27"처럼 표시한다.
    /// 한 서비스가 두 줄로 나뉜 경우(이름 없는 줄)는 앞 줄에 합쳐 최저값을 쓴다.
    /// </summary>
    private static MicroLayout MeasureMicro(Graphics graphics, Font font, IReadOnlyList<Row> rows, int dpi)
    {
        var scale = dpi / 96f;
        var lines = new List<MicroLine>();
        foreach (var row in rows)
        {
            var cell = row.Cells.Where(cell => cell is not null).OrderBy(cell => cell!.Remaining).FirstOrDefault();
            if (cell is null) continue;
            if (string.IsNullOrEmpty(row.Service) && lines.Count > 0)
            {
                if (cell.Remaining < lines[^1].Remaining) lines[^1] = new MicroLine(lines[^1].Tag, cell.Remaining);
                continue;
            }
            lines.Add(new MicroLine(Tag(row.Service), cell.Remaining));
        }
        var textWidth = lines.Count == 0 ? 0 : lines.Max(line => Measure(graphics, line.Text, font).Width);
        var leftPad = Math.Max(2, (int)Math.Round(4 * scale));
        var lineHeight = Math.Max(1, Measure(graphics, "Ag", font).Height);
        var height = lines.Count * lineHeight + (int)Math.Round(4 * scale);
        return new(textWidth + leftPad * 2, height, lineHeight, textWidth, leftPad, lines);
    }

    private static string Tag(string service) => service switch
    {
        "Codex" => "C",
        "Go" => "G",
        _ => service.Length > 0 ? service[..1] : string.Empty
    };

    /// <summary>작업표시줄에서 아이콘 사이로 비어 있는 가장 넓은 폭.</summary>
    private static int LargestGap(Rectangle taskbar, IReadOnlyList<Rectangle> occupied, int margin)
    {
        var cursor = taskbar.Left + margin;
        var best = 0;
        foreach (var item in occupied.Where(rect => rect.IntersectsWith(taskbar)).OrderBy(rect => rect.Left)
            .Append(new Rectangle(taskbar.Right, taskbar.Top, 0, taskbar.Height)))
        {
            best = Math.Max(best, item.Left - cursor);
            cursor = Math.Max(cursor, item.Right + margin);
        }
        return best;
    }

    private static Size Measure(Graphics graphics, string value, Font font)
        => TextRenderer.MeasureText(graphics, value, font, new Size(10000, 10000), TextFormatFlags.NoPadding);

    private static string ValueText(int remaining, bool stale) => $"{remaining}%{(stale ? " !" : "")}";

    internal static Layout? ReadLayout(string? monitor)
    {
        var screen = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == monitor) ?? Screen.PrimaryScreen!;
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || Screen.FromHandle(taskbar).DeviceName != screen.DeviceName)
        {
            taskbar = IntPtr.Zero;
            while ((taskbar = Native.FindWindowEx(IntPtr.Zero, taskbar, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (Screen.FromHandle(taskbar).DeviceName == screen.DeviceName) break;
        }
        if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar)) return null;
        using var context = new DpiContext(taskbar);
        if (!Native.GetWindowRect(taskbar, out var bounds) || bounds.Width <= bounds.Height || bounds.Height < 20) return null;
        var root = AutomationElement.FromHandle(taskbar);
        // 버튼·탭 항목뿐 아니라 검색 상자처럼 편집·그룹·이미지로 노출되는 요소도 자리를 차지한 것으로 본다.
        var elements = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image))));
        var occupied = new List<Rectangle>();
        foreach (AutomationElement element in elements)
        {
            if (element.Current.ProcessId == Environment.ProcessId) continue;
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty) continue;
            var item = Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
            // 작업표시줄 전체를 감싸는 컨테이너는 빈 공간을 모두 덮으므로 제외한다.
            if (item.Width > bounds.Width * 3 / 4) continue;
            if (item.IntersectsWith(bounds.Rectangle)) occupied.Add(item);
        }
        // 작업표시줄 구조를 확인할 수 없으면 다른 아이콘 위에 강제로 표시하지 않는다.
        if (occupied.Count == 0) return null;
        var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var trayBounds)) occupied.Add(trayBounds.Rectangle);
        return new(taskbar, bounds.Rectangle, (int)Native.GetDpiForWindow(taskbar), occupied.ToArray());
    }

    internal static Point? FindSpace(Rectangle taskbar, IReadOnlyList<Rectangle> occupied, Size size, int margin)
    {
        if (size.Height <= 0 || size.Height + 2 * margin > taskbar.Height || size.Width + 2 * margin > taskbar.Width) return null;
        var cursor = taskbar.Left + margin;
        foreach (var item in occupied.Where(rect => rect.IntersectsWith(taskbar)).OrderBy(rect => rect.Left)
            .Append(new Rectangle(taskbar.Right, taskbar.Top, 0, taskbar.Height)))
        {
            if (item.Left - cursor >= size.Width + margin)
                return new Point(cursor, taskbar.Top + (taskbar.Height - size.Height) / 2);
            cursor = Math.Max(cursor, item.Right + margin);
        }
        return null;
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        timer.Dispose();
        button?.Dispose();
    }

    private sealed class BadgeButton : Button
    {
        internal IntPtr Taskbar { get; }
        internal int Dpi { get; }
        internal IReadOnlyList<Row> Rows { get; set; } = Array.Empty<Row>();
        internal BadgeMode Mode { get; set; } = BadgeMode.Full;
        internal bool Stale { get; set; }
        internal Color WarningColor { get; set; } = Color.FromArgb(173, 109, 20);
        internal Color DangerColor { get; set; } = Color.FromArgb(199, 58, 69);
        private readonly Font textFont;
        private readonly Bitmap iconImage;

        internal BadgeButton(IntPtr taskbar, int dpi, Icon icon)
        {
            Taskbar = taskbar;
            Dpi = dpi;
            textFont = new Font("Segoe UI", BadgeFontSize);
            Font = textFont;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
            using var source = icon.ToBitmap();
            iconImage = new Bitmap(source, new Size(dpi * 24 / 96, dpi * 24 / 96));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Parent = Taskbar;
                parameters.Style = (parameters.Style | 0x40000000) & ~unchecked((int)0x80000000);
                parameters.ExStyle |= 0x08080080;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 탐색기의 합성 표면에 가려지지 않도록 별도의 계층 표면으로 그린다.
            if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ClientRectangle.Contains(PointToClient(MousePosition)) ? FlatAppearance.MouseOverBackColor : BackColor);
            var left = Dpi * 40 / 96;
            if (Rows.Count == 0)
            {
                e.Graphics.DrawImage(iconImage, Dpi * 8 / 96, (Height - iconImage.Height) / 2);
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(left, 0, Width - left - Dpi * 8 / 96, Height),
                    ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            if (Mode == BadgeMode.Micro)
            {
                var micro = MeasureMicro(e.Graphics, Font, Rows, Dpi);
                var microTop = Math.Max(0, (Height - micro.Lines.Count * micro.LineHeight) / 2);
                var microFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                for (var index = 0; index < micro.Lines.Count; index++)
                    TextRenderer.DrawText(e.Graphics, micro.Lines[index].Text, Font,
                        new Rectangle(micro.LeftPad, microTop + index * micro.LineHeight, micro.TextWidth, micro.LineHeight),
                        ValueColor(micro.Lines[index].Remaining), microFlags);
                return;
            }
            e.Graphics.DrawImage(iconImage, Dpi * 8 / 96, (Height - iconImage.Height) / 2);

            var table = MeasureTable(e.Graphics, Font, Rows, Stale, left, Dpi, Mode);
            var top = Math.Max(0, (Height - Rows.Count * table.LineHeight) / 2);
            var leftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            var rightFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            for (var index = 0; index < Rows.Count; index++)
            {
                var row = Rows[index];
                var y = top + index * table.LineHeight;
                TextRenderer.DrawText(e.Graphics, row.Service, Font, new Rectangle(left, y, table.ServiceWidth, table.LineHeight), ForeColor, leftFlags);
                var x = left + table.ServiceWidth + table.Gap;
                for (var block = 0; block < row.Cells.Count && block < table.BlockWidths.Length; block++)
                {
                    if (table.BlockWidths[block] == 0) continue;
                    var cell = row.Cells[block];
                    if (cell is not null)
                    {
                        var valueX = table.ShowLabels ? x + table.LabelWidths[block] + table.HalfGap : x;
                        if (table.ShowLabels)
                            TextRenderer.DrawText(e.Graphics, cell.Label, Font, new Rectangle(x, y, table.LabelWidths[block], table.LineHeight), ForeColor, leftFlags);
                        TextRenderer.DrawText(e.Graphics, ValueText(cell.Remaining, Stale), Font,
                            new Rectangle(valueX, y, table.ValueWidths[block], table.LineHeight), ValueColor(cell.Remaining), rightFlags);
                        if (table.ShowTimes)
                            TextRenderer.DrawText(e.Graphics, cell.Reset, Font,
                                new Rectangle(valueX + table.ValueWidths[block] + table.HalfGap, y, table.TimeWidths[block], table.LineHeight), ForeColor, leftFlags);
                    }
                    x += table.BlockWidths[block] + table.Gap;
                }
            }
        }

        private Color ValueColor(int remaining) => remaining < 10 ? DangerColor : remaining < 30 ? WarningColor : ForeColor;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007B && ContextMenuStrip is { } menu)
            {
                var keyboard = m.LParam == new IntPtr(-1);
                // 메뉴 창의 배율 문맥에서 위치를 읽어 탐색기와의 배율 차이를 해소한다.
                menu.BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || menu.IsDisposed) return;
                    menu.Show(this, keyboard ? new Point(Width / 2, Height / 2) : PointToClient(Cursor.Position));
                    Native.SetForegroundWindow(menu.Handle);
                }));
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { textFont.Dispose(); iconImage.Dispose(); }
        }
    }

    private readonly struct DpiContext : IDisposable
    {
        private readonly IntPtr previous;
        internal DpiContext(IntPtr window)
            => previous = Native.SetThreadDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(window));
        public void Dispose()
        {
            if (previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal readonly int Width => Right - Left;
            internal readonly int Height => Bottom - Top;
            internal readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    }
}
