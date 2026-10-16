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
        var lookup = Task.Run(() => AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null)).FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))));
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
        Console.WriteLine($"배포 실행 파일 검증 {checks}개 통과");
    }

    public static void Run()
    {
        var bounds = new Rectangle(-2400, 1300, 2400, 50);
        var occupied = new[] { new Rectangle(-2400, 1300, 150, 50), new Rectangle(-1200, 1300, 600, 50) };
        Check(Find(bounds, occupied, new Size(130, 38)) == new Point(-2244, 1306), "음수 좌표에서 날씨 옆 첫 빈 공간 선택");
        Check(Find(bounds, new[] { bounds }, new Size(130, 38)) is null, "공간 부족 시 다른 아이콘과 겹치지 않음");
        Check(Find(bounds, occupied, new Size(130, 50)) is null, "높이 부족 시 표시하지 않음");
        using var icon = new Icon("assets/Widget.ico");
        var clicked = false;
        using var badge = (IDisposable)Activator.CreateInstance(Badge, Flags, null, new object[] { icon, (Action)(() => clicked = true) }, null)!;
        Invoke(badge, "Update", "5시간 73%\n주간 54%");
        Invoke(badge, "Start");
        Wait(() => Button(badge) is { Visible: true });
        var button = Button(badge)!;
        var layout = Badge.GetMethod("ReadLayout", Flags)!.Invoke(null, null)!;
        var taskbar = (IntPtr)layout.GetType().GetProperty("Taskbar")!.GetValue(layout)!;
        var area = (Rectangle)layout.GetType().GetProperty("Bounds")!.GetValue(layout)!;
        var obstacles = (Rectangle[])layout.GetType().GetProperty("Occupied")!.GetValue(layout)!;
        Check(GetParent(button.Handle) == taskbar, "실제 Windows 작업표시줄의 자식 창으로 표시");
        Check(GetWindowRect(button.Handle, out var rect) && area.Contains(rect.Rectangle), "버튼이 작업표시줄 내부에 표시");
        Check(!obstacles.Any(item => item.IntersectsWith(rect.Rectangle)), "실제 날씨·시작·앱·트레이 아이콘과 겹침 없음");
        Check(WindowFromPoint(new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)) == button.Handle,
            "작업표시줄 위에서 실제 클릭 가능한 위치");
        Check(button.Text == "5시간 73%\n주간 54%", "두 줄 잔여량 표시");
        button.PerformClick();
        Check(clicked, "버튼 클릭 시 창 열기 콜백 호출");
        button.Refresh();
        var paintDeadline = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < paintDeadline) { Application.DoEvents(); Thread.Sleep(20); }
        Directory.CreateDirectory("obj/taskbar-verification");
        using (var image = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, image.Size);
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
        Check(button.Text == "5시간 73% !\n주간 54% !", "조회 실패 시 마지막 확인값과 실패 표시 유지");
        Invoke(badge, "Update", "5시간 73%\n주간 54%");
        Check(!button.Text.Contains('!'), "조회 성공 시 실패 표시 해제");
        button.Dispose();
        Wait(() => Button(badge) is { IsDisposed: false, Visible: true } replacement && !ReferenceEquals(replacement, button));
        Check(Button(badge)!.Text == "5시간 73%\n주간 54%", "표시 창 재생성 시 잔여량 유지");
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
            var percent = (Label)type.GetField("primaryPercent", Flags)!.GetValue(form)!;
            Check(percent.Text.EndsWith('%') && Button(badge)!.Text.Contains(percent.Text), "메인 창 실제 잔여량과 작업표시줄 값 일치");
            Check(form.DeviceDpi == (int)GetDpiForWindow(form.Handle), "버튼 생성 후 메인 창 DPI 유지");
            form.Hide();
            Button(badge)!.PerformClick();
            Wait(() => form.Visible);
            Check(!form.ShowInTaskbar, "클릭 시 기존 창을 표시하고 일반 앱 버튼을 추가하지 않음");
        }
        finally
        {
            form.Close();
            if (backup is null) File.Delete(settings); else File.WriteAllBytes(settings, backup);
        }
    }

    private static Point? Find(Rectangle bounds, Rectangle[] occupied, Size size)
        => (Point?)Badge.GetMethod("FindSpace", Flags)!.Invoke(null, new object[] { bounds, occupied, size, 6 });
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
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
