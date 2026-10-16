using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Automation;
using Microsoft.Win32;

namespace CodexUsageWidget;

internal sealed class TaskbarBadge : IDisposable
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Action openWidget;
    private readonly Icon icon;
    private readonly ContextMenuStrip menu;
    private BadgeButton? button;
    private string text = "Codex\n확인 중…";
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

    internal void Update(string value)
    {
        text = value;
        stale = false;
        ApplyText();
    }

    internal void MarkStale()
    {
        stale = true;
        ApplyText();
    }

    private void ApplyText()
    {
        if (button is { IsDisposed: false })
        {
            button.Text = stale ? text.Contains('%') ? text.Replace("%", "% !") : "Codex\n확인 실패" : text;
            button.AccessibleName = (stale ? "갱신 실패 · 마지막 확인값. " : "") + $"Codex 잔여량 {text}. 클릭하면 위젯을 엽니다.";
        }
    }

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
            using var graphics = button.CreateGraphics();
            var width = Math.Max(TextRenderer.MeasureText(graphics, "5시간 100%\n주간 100%", button.Font).Width,
                TextRenderer.MeasureText(graphics, stale ? text.Replace("%", "% !") : text, button.Font).Width) + layout.Dpi * 52 / 96;
            var height = Math.Min(layout.Dpi * 40 / 96, layout.Bounds.Height - 2 * (layout.Dpi * 6 / 96));
            var location = FindSpace(layout.Bounds, layout.Occupied, new Size(width, height), layout.Dpi * 6 / 96);
            if (location is null) { button.Visible = false; return; }
            button.SetBounds(location.Value.X - layout.Bounds.X, location.Value.Y - layout.Bounds.Y, width, height);
            using var outline = GlassForm.RoundedRectangle(button.ClientRectangle, layout.Dpi * 8f / 96);
            var previous = button.Region;
            button.Region = new Region(outline);
            previous?.Dispose();
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            button.BackColor = light ? Color.FromArgb(236, 242, 250) : Color.FromArgb(39, 43, 51);
            button.ForeColor = light ? Color.FromArgb(38, 60, 90) : Color.FromArgb(234, 241, 250);
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
        var elements = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))));
        var occupied = new List<Rectangle>();
        foreach (AutomationElement element in elements)
        {
            if (element.Current.ProcessId == Environment.ProcessId) continue;
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty) continue;
            var item = Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
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
        private readonly Font textFont = new("Segoe UI", 8.5f);
        private readonly Bitmap iconImage;

        internal BadgeButton(IntPtr taskbar, int dpi, Icon icon)
        {
            Taskbar = taskbar;
            Dpi = dpi;
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
            e.Graphics.DrawImage(iconImage, Dpi * 8 / 96, (Height - iconImage.Height) / 2);
            var left = Dpi * 40 / 96;
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(left, 0, Width - left - Dpi * 8 / 96, Height),
                ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

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
