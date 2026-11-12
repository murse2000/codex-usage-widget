using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static class TaskbarVerification
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Type Badge = Assembly.Load("CodexUsageWidget").GetType("CodexUsageWidget.TaskbarBadge")!;
    private static int checks;

    public static void RunPublished(int processId)
    {
        var lookup = Task.Run(() =>
        {
            var taskbars = new List<IntPtr> { FindWindow("Shell_TrayWnd", null) };
            var secondary = IntPtr.Zero;
            while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) taskbars.Add(secondary);
            return taskbars.Where(window => window != IntPtr.Zero).Select(window => AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))).FirstOrDefault(element => element is not null);
        });
        Wait(() => lookup.IsCompleted);
        var element = lookup.GetAwaiter().GetResult()!;
        Check(element is not null && element.Current.Name.Contains('%'), "배포 EXE의 작업표시줄에 실제 잔여량 표시");
        var button = new IntPtr(element!.Current.NativeWindowHandle);
        var main = FindWindow(null, "Codex Usage");
        GetWindowThreadProcessId(main, out var owner);
        Check(main != IntPtr.Zero && owner == processId, "배포 EXE의 메인 창 확인");
        ShowWindow(main, 0);
        Check(!IsWindowVisible(main), "배포 EXE 창 숨김");
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Wait(() => IsWindowVisible(main));
        Check(GetWindowRect(button, out var rect), "배포 EXE의 작업표시줄 표시 영역 확인");
        Directory.CreateDirectory("obj/taskbar-verification");
        using var image = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, image.Size);
        image.Save("obj/taskbar-verification/published.png");
        ShowWindow(main, 0);
        var point = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        RightClickAt(button, point);
        var popupLookup = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                var windows = new List<IntPtr>();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var owner);
                    if (owner == processId && IsWindowVisible(window)) windows.Add(window);
                    return true;
                }, IntPtr.Zero);
                var result = windows.Select(AutomationElement.FromHandle).FirstOrDefault(element => element.Current.ControlType == ControlType.Menu);
                if (result is not null) return result;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);
            return null;
        });
        Wait(() => popupLookup.IsCompleted);
        var popup = popupLookup.GetAwaiter().GetResult();
        Check(popup is not null, "배포 EXE에서 작업표시줄 우클릭으로 메뉴 표시");
        var show = popup!.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "위젯 보이기"));
        Check(show is not null, "배포 EXE 메뉴에 실제 창 상태 반영");
        ((InvokePattern)show!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Wait(() => IsWindowVisible(main));
        Console.WriteLine($"배포 실행 파일 검증 {checks}개 통과");
    }

    public static void Run()
    {
        var bounds = new Rectangle(-2400, 1300, 2400, 50);
        var occupied = new[] { new Rectangle(-2400, 1300, 150, 50), new Rectangle(-1200, 1300, 600, 50) };
        Check(Find(bounds, occupied, new Size(130, 38)) == new Point(-2244, 1306), "음수 좌표에서 날씨 옆 첫 빈 공간 선택");
        Check(Find(bounds, new[] { bounds }, new Size(130, 38)) is null, "공간 부족 시 다른 아이콘과 겹치지 않음");
        Check(Find(bounds, occupied, new Size(130, 50)) is null, "높이 부족 시 표시하지 않음");
        var gapBounds = new Rectangle(0, 1000, 1000, 48);
        var gapItems = new[] { new Rectangle(0, 1000, 200, 48), new Rectangle(600, 1000, 400, 48) };
        Check((int)Badge.GetMethod("LargestGap", Flags)!.Invoke(null, new object[] { gapBounds, gapItems, 4 })! == 396,
            "아이콘 사이 가장 넓은 빈 공간 계산");
        using (var measure = Graphics.FromHwnd(IntPtr.Zero))
        using (var font = new Font("Segoe UI", 8.5f))
        {
            var tableRows = Rows(
                ("Codex", new object?[] { Cell("5시간", 73, "(245분)"), Cell("주간", 54, "(59시간)"), null }),
                ("Go", new object?[] { Cell("5시간", 98, "(142분)"), Cell("주간", 100, "(155시간)"), Cell("월간", 27, "(10일 19시간)") }));
            var measureTable = Badge.GetMethod("MeasureTable", Flags)!;
            var modeType = Badge.GetNestedType("BadgeMode", Flags)!;
            int Width(string mode)
            {
                var layout = measureTable.Invoke(null,
                    new object[] { measure, font, tableRows, false, 40, 96, Enum.Parse(modeType, mode) })!;
                return (int)layout.GetType().GetProperty("Width")!.GetValue(layout)!;
            }
            Check(Width("Full") > Width("Compact") && Width("Compact") > Width("Values"), "공간이 좁을수록 좁은 표시 단계 선택");
            var micro = Badge.GetMethod("MeasureMicro", Flags)!.Invoke(null, new object[] { measure, font, tableRows, 96 })!;
            var microWidth = (int)micro.GetType().GetProperty("Width", Flags)!.GetValue(micro)!;
            var microLines = ((System.Collections.IEnumerable)micro.GetType().GetProperty("Lines", Flags)!.GetValue(micro)!)
                .Cast<object>().Select(line => (string)line.GetType().GetProperty("Text", Flags)!.GetValue(line)!).ToList();
            Check(microWidth < Width("Values"), "아주 좁은 공간에서는 더 좁은 표시 단계 선택");
            Check(microLines.Count == 2 && microLines[0] == "C54%" && microLines[1] == "G27%",
                "좁은 공간에서는 서비스별 최저 잔여량만 표시");
        }
        using var icon = new Icon("assets/Widget.ico");
        using var menu = new ContextMenuStrip();
        menu.Items.Add("검증 메뉴");
        var clicked = false;
        using var badge = (IDisposable)Activator.CreateInstance(Badge, Flags, null, new object[] { icon, menu, (Action)(() => clicked = true) }, null)!;
        Invoke(badge, "Update", Rows(("Codex", new object?[] { Cell("5시간", 73, "(245분)"), Cell("주간", 54, "(59시간)"), null })));
        Invoke(badge, "Start");
        Wait(() => Button(badge) is { Visible: true } first && Math.Abs(first.Font.Size - 8.5f) < 0.01f);
        var button = Button(badge)!;
        Check(ReferenceEquals(button.ContextMenuStrip, menu), "작업표시줄 버튼에 공유 메뉴 연결");
        var probe = Badge.GetMethod("ReadLayout", Flags)!.Invoke(null, new object?[] { null })!;
        Check(!(bool)probe.GetType().GetProperty("Transient")!.GetValue(probe)!, "작업표시줄 레이아웃 조회 가능 상태");
        var layout = probe.GetType().GetProperty("Layout")!.GetValue(probe)
            ?? throw new InvalidOperationException("작업표시줄 레이아웃을 조회하지 못했습니다.");
        var taskbar = (IntPtr)layout.GetType().GetProperty("Taskbar")!.GetValue(layout)!;
        var area = (Rectangle)layout.GetType().GetProperty("Bounds")!.GetValue(layout)!;
        var obstacles = (Rectangle[])layout.GetType().GetProperty("Occupied")!.GetValue(layout)!;
        Check(GetParent(button.Handle) == taskbar, "실제 Windows 작업표시줄의 자식 창으로 표시");
        Check(GetWindowRect(button.Handle, out var rect) && area.Contains(rect.Rectangle), "버튼이 작업표시줄 내부에 표시");
        Check(!obstacles.Any(item => item.IntersectsWith(rect.Rectangle)), "실제 날씨·시작·앱·트레이 아이콘과 겹침 없음");
        Check(WindowFromPoint(new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)) == button.Handle,
            "작업표시줄 위에서 실제 클릭 가능한 위치");
        Check(button.Text == "Codex 5시간 73% (245분) · 주간 54% (59시간)", "서비스 한 줄 잔여량 표시");
        Check(Math.Abs(button.Font.Size - 8.5f) < 0.01f, "한 줄일 때도 기존 글자 크기");
        Check(button.Height >= 18, "한 줄 글자 높이 확보");

        Invoke(badge, "Update", Rows(
            ("Codex", new object?[] { Cell("5시간", 73, "(245분)"), Cell("주간", 54, "(59시간)"), null }),
            ("Go", new object?[] { Cell("5시간", 98, "(142분)"), Cell("주간", 100, "(155시간)"), Cell("월간", 27, "(10일 19시간)") })));
        Wait(() => Button(badge) is { IsDisposed: false, Visible: true } current
            && current.Text.Split('\n').Length == 2 && Math.Abs(current.Font.Size - 8.5f) < 0.01f);
        var twoRows = Button(badge)!;
        Check(Math.Abs(twoRows.Font.Size - 8.5f) < 0.01f, "두 줄일 때 기존 글자 크기");
        Check(twoRows.Text == "Codex 5시간 73% (245분) · 주간 54% (59시간)\n"
            + "Go 5시간 98% (142분) · 주간 100% (155시간) · 월간 27% (10일 19시간)",
            "Codex·Go 두 줄 표시");
        var valueColor = twoRows.GetType().GetMethod("ValueColor", Flags)!;
        var warning = (Color)twoRows.GetType().GetProperty("WarningColor", Flags)!.GetValue(twoRows)!;
        var danger = (Color)twoRows.GetType().GetProperty("DangerColor", Flags)!.GetValue(twoRows)!;
        Check((Color)valueColor.Invoke(twoRows, new object[] { 50 })! == twoRows.ForeColor, "30% 이상은 기본 색");
        Check((Color)valueColor.Invoke(twoRows, new object[] { 29 })! == warning, "30% 미만은 노란색");
        Check((Color)valueColor.Invoke(twoRows, new object[] { 9 })! == danger, "10% 미만은 빨간색");
        Check(GetWindowRect(twoRows.Handle, out var twoRect) && area.Contains(twoRect.Rectangle)
            && twoRect.Bottom - twoRect.Top <= area.Height, "두 줄 배지가 작업표시줄 안에 표시");
        var settle = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < settle) { Application.DoEvents(); Thread.Sleep(20); }
        Check(Button(badge)!.Text.Split('\n').Length == 2 && Button(badge)!.Height >= 30,
            "두 줄 배지가 잘리거나 줄어들지 않고 유지");

        button = Button(badge)!;
        button.PerformClick();
        Check(clicked, "버튼 클릭 시 창 열기 콜백 호출");
        button.Refresh();
        var paintDeadline = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < paintDeadline) { Application.DoEvents(); Thread.Sleep(20); }
        Directory.CreateDirectory("obj/taskbar-verification");
        Check(GetWindowRect(button.Handle, out var badgeRect), "배지 표시 영역 확인");
        using (var image = new Bitmap(badgeRect.Right - badgeRect.Left, badgeRect.Bottom - badgeRect.Top))
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(badgeRect.Left, badgeRect.Top, 0, 0, image.Size);
            image.Save("obj/taskbar-verification/taskbar.png");
            using var rendered = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(rendered, button.ClientRectangle);
            var matching = 0;
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    if (image.GetPixel(x, y).ToArgb() == rendered.GetPixel(x, y).ToArgb()) matching++;
            Check(matching > image.Width * image.Height * 0.75, "실제 화면에서도 버튼 내용이 가려지지 않고 표시");
        }
        Invoke(badge, "MarkStale");
        Check(button.Text == "Codex 5시간 73% ! (245분) · 주간 54% ! (59시간)\n"
            + "Go 5시간 98% ! (142분) · 주간 100% ! (155시간) · 월간 27% ! (10일 19시간)",
            "조회 실패 시 마지막 확인값과 실패 표시 유지");
        Invoke(badge, "Update", Rows(("Codex", new object?[] { Cell("5시간", 73, "(245분)"), Cell("주간", 54, "(59시간)"), null })));
        Check(!button.Text.Contains('!'), "조회 성공 시 실패 표시 해제");
        button.Dispose();
        Wait(() => Button(badge) is { IsDisposed: false, Visible: true } replacement && !ReferenceEquals(replacement, button));
        Check(Button(badge)!.Text == "Codex 5시간 73% (245분) · 주간 54% (59시간)", "표시 창 재생성 시 잔여량 유지");
        Check(ReferenceEquals(Button(badge)!.ContextMenuStrip, menu), "표시 창 재생성 시 공유 메뉴 유지");
        Invoke(badge, "Update", Rows());
        Wait(() => Button(badge) is { IsDisposed: false, Visible: false });
        Check(Button(badge) is { Visible: false }, "표시할 서비스가 없으면 배지 숨김");
        var handle = Button(badge)!.Handle;
        badge.Dispose();
        Check(!IsWindow(handle), "종료 시 작업표시줄 표시 제거");
        VerifyMainForm();
        Console.WriteLine($"작업표시줄 검증 {checks}개 통과");
    }

    private static void VerifyMainForm()
    {
        var type = Badge.Assembly.GetType("CodexUsageWidget.UsageForm")!;
        var settings = (string)type.GetField("SettingsPath", Flags)!.GetValue(null)!;
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        using var form = (Form)Activator.CreateInstance(type, true)!;
        try
        {
            form.Show();
            var badge = (IDisposable)type.GetField("taskbarBadge", Flags)!.GetValue(form)!;
            Wait(() => Button(badge) is { Visible: true });
            Wait(() => !(bool)type.GetField("refreshing", Flags)!.GetValue(form)!);
            var row = type.GetField("codexPrimary", Flags)!.GetValue(form)!;
            var percent = (Label)row.GetType().GetProperty("Percent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
            Check(percent.Text.EndsWith('%') && Button(badge)!.Text.Contains(percent.Text), "메인 창 실제 잔여량과 작업표시줄 값 일치");
            Check(form.DeviceDpi == (int)GetDpiForWindow(form.Handle), "버튼 생성 후 메인 창 DPI 유지");
            form.Hide();
            Button(badge)!.PerformClick();
            Wait(() => form.Visible);
            Check(!form.ShowInTaskbar, "클릭 시 기존 창을 표시하고 일반 앱 버튼을 추가하지 않음");
            VerifyContextMenu(form, Button(badge)!);
            VerifyMonitorSwitch(form, badge);
        }
        finally
        {
            form.Close();
            if (backup is null) File.Delete(settings); else File.WriteAllBytes(settings, backup);
        }
    }

    private static void VerifyMonitorSwitch(Form form, IDisposable badge)
    {
        var menu = form.ContextMenuStrip!;
        foreach (var screen in Screen.AllScreens)
        {
            menu.Show(form, Point.Empty);
            var monitors = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "표시 모니터");
            monitors.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text?.StartsWith(screen.DeviceName + " ") == true).PerformClick();
            menu.Close();
            Wait(() => Button(badge) is { IsDisposed: false, Visible: true } button
                && Screen.FromHandle(GetParent(button.Handle)).DeviceName == screen.DeviceName);
            Check(Screen.FromControl(form).DeviceName == screen.DeviceName, "선택한 모니터로 바탕화면 위젯 이동");
            var button = Button(badge)!;
            var point = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
            Check(screen.Bounds.Contains(point), "선택한 모니터의 작업표시줄로 표시 이동");
            RightClick(button, point);
            Check(menu.Visible && screen.Bounds.Contains(menu.Bounds), "이동한 작업표시줄에서도 실제 우클릭 메뉴 표시");
            menu.Close();
        }
    }

    private static void VerifyContextMenu(Form form, Button button)
    {
        var tray = (NotifyIcon)form.GetType().GetField("tray", Flags)!.GetValue(form)!;
        var menu = tray.ContextMenuStrip!;
        Check(ReferenceEquals(form.ContextMenuStrip, menu) && ReferenceEquals(button.ContextMenuStrip, menu), "위젯 창·작업표시줄·트레이에서 같은 메뉴 인스턴스 공유");
        var point = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
        form.Hide();
        RightClick(button, point);
        Check(menu.Items[0].Text == "위젯 보이기" && !form.Visible, "숨겨진 창을 강제로 열지 않고 우클릭 메뉴 표시");
        Check(GetForegroundWindow() == menu.Handle, "탐색기 대신 우클릭 메뉴에 입력 포커스 부여");
        var anchor = new Point(point.X, Math.Min(point.Y, Screen.FromPoint(point).WorkingArea.Bottom - 1));
        Check(Rectangle.Inflate(menu.Bounds, 8, 8).Contains(anchor), "작업표시줄 바로 위에 메뉴 표시");
        Check(Screen.FromPoint(point).Bounds.Contains(menu.Bounds), "작업표시줄 메뉴가 화면 밖으로 잘리지 않음");
        var monitorMenu = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "표시 모니터");
        Check(monitorMenu.DropDownItems.Count == Screen.AllScreens.Length, "공유 메뉴의 모니터 목록 갱신");
        var item = menu.Items[0].Bounds;
        var click = new IntPtr(((item.Top + item.Height / 2) << 16) | (item.Left + item.Width / 2));
        SendMessage(menu.Handle, 0x0201, new IntPtr(1), click);
        SendMessage(menu.Handle, 0x0202, IntPtr.Zero, click);
        Wait(() => form.Visible);
        Check(form.Visible, "공유 메뉴의 위젯 보이기 실행");
        var source = form.Controls.OfType<Label>().First();
        point = source.PointToScreen(new Point(source.Width / 2, source.Height / 2));
        RightClick(source, point);
        Check(menu.Items[0].Text == "위젯 숨기기", "바탕화면 위젯의 글자 위에서도 같은 메뉴 표시");
        menu.Items[0].PerformClick();
        menu.Close();
        Check(!form.Visible, "공유 메뉴의 위젯 숨기기 실행");
        RightClick(button, button.PointToScreen(new Point(button.Width / 2, button.Height / 2)));
        menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "위젯 설정").PerformClick();
        menu.Close();
        Check(form.Visible && form.OwnedForms.Length == 1, "공유 메뉴의 설정 창 열기 실행");
        form.OwnedForms.Single().Close();
    }

    private static void RightClick(Control control, Point screen)
    {
        RightClickAt(control.Handle, screen);
        Wait(() => Application.OpenForms.Cast<Form>().Any(form => form.ContextMenuStrip?.Visible == true));
    }

    private static void RightClickAt(IntPtr window, Point screen)
    {
        Check(WindowFromPoint(screen) == window, "실제 우클릭 위치가 대상 위젯에 해당");
        Check(SetCursorPos(screen.X, screen.Y), "마우스 포인터를 위젯 위치로 이동");
        var inputs = new[] { new Input { Mouse = new MouseInput { Flags = 8 } }, new Input { Mouse = new MouseInput { Flags = 16 } } };
        Check(SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2, "운영체제에 실제 오른쪽 버튼 누름·뗌 입력 전달");
    }

    private static Point? Find(Rectangle bounds, Rectangle[] occupied, Size size)
        => (Point?)Badge.GetMethod("FindSpace", Flags)!.Invoke(null, new object[] { bounds, occupied, size, 6 });
    private static readonly Type CellType = Badge.GetNestedType("Cell", Flags)!;
    private static readonly Type RowType = Badge.GetNestedType("Row", Flags)!;

    private static object Cell(string label, int remaining, string reset)
        => Activator.CreateInstance(CellType, new object[] { label, remaining, reset })!;

    private static object Rows(params (string Service, object?[] Cells)[] rows)
    {
        var cellList = typeof(List<>).MakeGenericType(CellType);
        var rowList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(RowType))!;
        foreach (var (service, cells) in rows)
        {
            var list = (IList)Activator.CreateInstance(cellList)!;
            foreach (var cell in cells) list.Add(cell);
            rowList.Add(Activator.CreateInstance(RowType, new object?[] { service, list })!);
        }
        return rowList;
    }
    private static Button? Button(IDisposable badge) => (Button?)Badge.GetField("button", Flags)!.GetValue(badge);
    private static void Invoke(IDisposable badge, string method, params object[] args) => Badge.GetMethod(method, Flags)!.Invoke(badge, args);
    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
        Check(condition(), "작업표시줄 표시 준비");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
        Console.WriteLine("통과: " + message);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { internal uint Type; internal MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { internal int X, Y; internal uint MouseData, Flags, Time; internal UIntPtr Extra; }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
