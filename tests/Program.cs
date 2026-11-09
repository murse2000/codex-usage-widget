using System.Collections;
using System.Reflection;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("CodexUsageWidget");
    private static readonly Type FormType = App.GetType("CodexUsageWidget.UsageForm")!;
    private static readonly Type RowType = FormType.GetNestedType("UsageRow", BindingFlags.NonPublic)!;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();
        if (args.Contains("--update-worker"))
        {
            try { UpdateVerification.InstallWorker(args[1], args[2]); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--updates") || args.Contains("--release-live") || args.Contains("--taskbar") || args.Contains("--published-taskbar"))
        {
            Exception? failure = null;
            using var host = new Form { ShowInTaskbar = false, Opacity = 0 };
            host.Shown += (_, _) =>
            {
                try
                {
                    if (args.Contains("--published-taskbar")) TaskbarVerification.RunPublished(int.Parse(args[1]));
                    else if (args.Contains("--taskbar")) TaskbarVerification.Run();
                    else if (args.Contains("--release-live")) UpdateVerification.RunLive(args[1]);
                    else UpdateVerification.Run();
                }
                catch (Exception error) { failure = error; }
                finally { host.Close(); }
            };
            Application.Run(host);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }
        if (args.Contains("--tibo-delivery"))
        {
            TiboVerification.VerifyDelivery();
            return;
        }
        if (args.Contains("--tibo"))
        {
            TiboVerification.Run(args.Contains("--live"));
            return;
        }
        if (args.Contains("--go"))
        {
            GoVerification.Run(args.Contains("--live"));
            return;
        }
        var settingsPath = (string)FormType.GetField("SettingsPath", Private)!.GetValue(null)!;
        var backup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        Directory.CreateDirectory("obj/layout-verification");
        using var form = (Form)Activator.CreateInstance(FormType, true)!;
        var retainedFonts = new List<Font>();
        try
        {
            form.Show();
            Application.DoEvents();
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while ((bool)FormType.GetField("refreshing", Private)!.GetValue(form)! && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
            Check(!(bool)FormType.GetField("refreshing", Private)!.GetValue(form)!, "초기 조회 종료");
            CheckAppearance(form, settingsPath);
            CheckBadgeTime();
            foreach (var screen in Screen.AllScreens)
            {
                form.Location = new Point(screen.WorkingArea.Left + 30, screen.WorkingArea.Top + 30);
                Application.DoEvents();
                var dpi = form.DeviceDpi;
                Console.WriteLine($"모니터 {screen.DeviceName}, 실제 DPI {dpi}");
                foreach (var (codexRows, goRows) in new[] { (1, 0), (2, 0), (2, 3) })
                {
                    SetState(form, codexRows, goRows);
                    var primary = ResetLabel(form, "codexPrimary");
                    var secondary = ResetLabel(form, "codexSecondary");
                    var goMonthly = ResetLabel(form, "goMonthly");
                    var original = (Font)primary.Font.Clone();
                    retainedFonts.Add(original);
                    var fonts = new List<Font>();
                    try
                    {
                        foreach (var fontScale in new[] { 1f, 1.25f, 1.5f, 2f })
                        {
                            var font = new Font(original.FontFamily, original.Size * fontScale, original.Style);
                            fonts.Add(font);
                            foreach (var label in new[] { primary, secondary, goMonthly }) label.Font = font;
                            foreach (var label in new[] { primary, secondary, goMonthly })
                                label.Text = "12/31 23:59 (999일 23시간 남음)";
                            Fit(form);
                            CheckRows(form);
                            foreach (var label in new[] { primary, secondary, goMonthly }.Where(label => label.Visible))
                                CheckText(form, label);
                            Check(Status(form).Top >= LastReset(form).Bottom, "초기화 문구와 상태줄 겹침 없음");
                        }
                    }
                    finally
                    {
                        primary.Font = secondary.Font = goMonthly.Font = original;
                        foreach (var font in fonts) font.Dispose();
                    }
                    SetState(form, codexRows, goRows);
                    Field<Label>(form, "status").Text = "3분마다 갱신 · 12:30";
                    Fit(form);
                    SaveImage(form, $"main-{dpi}-{codexRows}-{goRows}.png");
                }
                CheckGoHidden(form);
                CheckPicker(form, dpi);
            }
        }
        finally
        {
            form.Close();
            foreach (var font in retainedFonts) font.Dispose();
            if (backup is null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath, backup);
        }
        Console.WriteLine($"레이아웃 검증 {checks}개 통과");
    }

    private static void SetState(Form form, int codexRows, int goRows)
    {
        var windowType = FormType.GetNestedType("UsageWindow", BindingFlags.NonPublic)!;
        var snapshotType = FormType.GetNestedType("UsageSnapshot", BindingFlags.NonPublic)!;
        var now = DateTimeOffset.UtcNow;
        var primary = Activator.CreateInstance(windowType, new object[] { 27, 300, now.AddHours(3).ToUnixTimeSeconds() })!;
        object? secondary = codexRows > 1
            ? Activator.CreateInstance(windowType, new object[] { 46, 10080, now.AddDays(4).ToUnixTimeSeconds() })
            : null;
        FormType.GetField("codexUsage", Private)!.SetValue(form, Activator.CreateInstance(snapshotType, new[] { primary, secondary }));

        var goType = App.GetType("CodexUsageWidget.GoUsageWindow")!;
        if (goRows == 0)
        {
            FormType.GetField("goWindows", Private)!.SetValue(form, null);
        }
        else
        {
            var listType = typeof(List<>).MakeGenericType(goType);
            var list = (IList)Activator.CreateInstance(listType)!;
            var add = listType.GetMethod("Add")!;
            add.Invoke(list, new[] { Activator.CreateInstance(goType, new object[] { "rolling", "5시간 한도", 1, 300, now.AddHours(6).ToUnixTimeSeconds() }) });
            add.Invoke(list, new[] { Activator.CreateInstance(goType, new object[] { "weekly", "주간 한도", 40, 10080, now.AddDays(6).ToUnixTimeSeconds() }) });
            add.Invoke(list, new[] { Activator.CreateInstance(goType, new object[] { "monthly", "월간 한도", 72, 0, now.AddDays(10).ToUnixTimeSeconds() }) });
            FormType.GetField("goWindows", Private)!.SetValue(form, list);
        }
        FormType.GetMethod("RenderCodex", Private)!.Invoke(form, null);
        FormType.GetMethod("RenderGo", Private)!.Invoke(form, null);
        Fit(form);
    }

    private static void CheckRows(Form form)
    {
        var rows = new[] { "codexPrimary", "codexSecondary", "goRolling", "goWeekly", "goMonthly" };
        foreach (var name in rows)
        {
            var row = Row(form, name);
            if (!(bool)row.GetType().GetProperty("Shown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!) continue;
            foreach (var part in new[] { "Name", "Percent", "Bar", "ResetCaption", "Reset" })
            {
                var control = (Control)row.GetType().GetProperty(part, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
                Check(control.Width > 0 && control.Height > 0, $"{name}.{part} 크기 확보");
                Check(form.ClientRectangle.Contains(control.Bounds), $"{name}.{part} 창 내부 표시");
            }
        }
    }

    private static void CheckBadgeTime()
    {
        var method = FormType.GetMethod("BadgeResetText", Private)!;
        Check((string)method.Invoke(null, new object[] { 10080, TimeSpan.FromHours(59.5) })! == "(59시간)", "주간 초기화는 시간 단위 표시");
        Check((string)method.Invoke(null, new object[] { 300, TimeSpan.FromMinutes(245) })! == "(245분)", "5시간 초기화는 분 단위 표시");
        Check((string)method.Invoke(null, new object[] { 0, TimeSpan.FromHours(10 * 24 + 19) })! == "(10일 19시간)", "월간 초기화는 일·시간 표시");
    }

    private static void CheckGoHidden(Form form)
    {
        var settings = FormType.GetField("settings", Private)!.GetValue(form)!;
        var goSection = Field<Label>(form, "goSection");
        SetState(form, 2, 0);
        Check(!goSection.Visible && !Shown(form, "goRolling"), "키 미설정·인증 실패 시 Go 목록 숨김");

        SetState(form, 2, 3);
        Check(goSection.Visible && Shown(form, "goRolling") && Shown(form, "goMonthly"), "인증된 키의 Go 목록 표시");

        settings.GetType().GetProperty("ShowOpenCodeGo")!.SetValue(settings, false);
        Fit(form);
        Check(!goSection.Visible && !Shown(form, "goMonthly"), "표시 서비스에서 Go를 끄면 목록 숨김");
        settings.GetType().GetProperty("ShowOpenCodeGo")!.SetValue(settings, true);

        settings.GetType().GetProperty("ShowCodex")!.SetValue(settings, false);
        SetState(form, 2, 3);
        Fit(form);
        Check(!Field<Label>(form, "codexSection").Visible && goSection.Visible, "Codex만 끄면 Go 목록은 유지");
        settings.GetType().GetProperty("ShowCodex")!.SetValue(settings, true);
        Fit(form);

        SetState(form, 2, 3);
        var rows = TaskbarRows(form);
        Check(rows.Count == 2 && Service(rows[0]) == "Codex" && Service(rows[1]) == "Go", "작업표시줄에 Codex·Go 두 줄");
        settings.GetType().GetProperty("ShowOpenCodeGo")!.SetValue(settings, false);
        Fit(form);
        rows = TaskbarRows(form);
        Check(rows.Count == 2 && Service(rows[0]) == "Codex" && Service(rows[1]) == string.Empty, "Go를 끄면 Codex를 두 줄로 표시");
        settings.GetType().GetProperty("ShowOpenCodeGo")!.SetValue(settings, true);
        settings.GetType().GetProperty("ShowCodex")!.SetValue(settings, false);
        Fit(form);
        rows = TaskbarRows(form);
        Check(rows.Count == 2 && Service(rows[0]) == "Go" && Service(rows[1]) == string.Empty, "Codex를 끄면 Go를 두 줄로 표시");
        settings.GetType().GetProperty("ShowCodex")!.SetValue(settings, true);
        Fit(form);
    }

    private static List<object> TaskbarRows(Form form)
        => ((IEnumerable)FormType.GetMethod("TaskbarRows", Private)!.Invoke(form, null)!).Cast<object>().ToList();
    private static string Service(object row) => (string)row.GetType().GetProperty("Service")!.GetValue(row)!;

    private static void CheckPicker(Form owner, int dpi)
    {
        var type = FormType.GetNestedType("OpacityPicker", BindingFlags.NonPublic)!;
        foreach (var fontScale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var toggled = false;
            var alwaysOnTop = true;
            var codexEnabled = true;
            var goEnabled = true;
            var cleared = false;
            using var picker = (Form)Activator.CreateInstance(type, new object[]
            {
                0.8, (Action<double>)(_ => { }), false, (Action<bool>)(value => toggled = value),
                true, (Action<bool>)(value => alwaysOnTop = value),
                true, (Action<bool>)(value => codexEnabled = value),
                true, (Action<bool>)(value => goEnabled = value),
                (Func<string, Task<string?>>)(_ => Task.FromResult<string?>(null)),
                (Action)(() => cleared = true),
                "키 없음 · OpenCode Go 숨김"
            })!;
            picker.Location = new Point(owner.Left, owner.Top);
            picker.Show(owner);
            Application.DoEvents();
            var layout = picker.Controls.OfType<FlowLayoutPanel>().Single();
            var fonts = new List<Font>();
            try
            {
                foreach (Control child in layout.Controls)
                {
                    var font = new Font(child.Font.FontFamily, child.Font.Size * fontScale, child.Font.Style);
                    fonts.Add(font);
                    child.Font = font;
                }
                picker.PerformLayout();
                layout.PerformLayout();
                var children = layout.Controls.Cast<Control>().ToArray();
                foreach (var child in children)
                    Check(layout.ClientRectangle.Contains(child.Bounds), "설정 항목이 창 내부에 표시");
                for (var index = 1; index < children.Length; index++)
                    Check(children[index - 1].Bottom <= children[index].Top, "설정 항목 겹침 없음");

                var slider = layout.Controls.OfType<TrackBar>().Single();
                var startup = layout.Controls.OfType<CheckBox>().Single(control => control.Text == "윈도우 시작 시 자동 실행");
                var topMost = layout.Controls.OfType<CheckBox>().Single(control => control.Text == "항상 위");
                var codex = layout.Controls.OfType<CheckBox>().Single(control => control.Text == "Codex");
                var go = layout.Controls.OfType<CheckBox>().Single(control => control.Text == "OpenCode Go");
                var keyBox = layout.Controls.OfType<TextBox>().Single();
                var keyButtons = layout.Controls.OfType<FlowLayoutPanel>().Single(panel => panel != layout);
                Check(slider.Bottom + slider.Margin.Bottom <= topMost.Top && topMost.Bottom + topMost.Margin.Bottom <= startup.Top,
                    "슬라이더와 체크박스 겹침 없음");
                foreach (var checkbox in new[] { startup, topMost, codex, go })
                {
                    var preferred = checkbox.GetPreferredSize(Size.Empty);
                    Check(checkbox.Width >= preferred.Width && checkbox.Height >= preferred.Height, "체크박스 문구·높이 확보");
                }
                Check(keyBox.Width >= 200 && keyBox.UseSystemPasswordChar, "API 키 입력란 크기와 마스킹");
                Check(keyButtons.Controls.OfType<Button>().Count() == 2, "API 키 저장·삭제 버튼");
                Check(layout.Controls.OfType<Label>().Any(label => label.Text.Contains("키 없음")), "API 키 상태 문구 표시");
                Check(layout.Controls.OfType<Label>().Single(label => label.Text == "투명도  80%").Height
                    >= layout.Controls.OfType<Label>().Single(label => label.Text == "투명도  80%").PreferredHeight, "투명도 문구 높이 확보");

                startup.Checked = true;
                Check(toggled, "자동 실행 체크 이벤트 유지");
                topMost.Checked = false;
                Check(!alwaysOnTop && !picker.TopMost, "항상 위 해제 즉시 적용");
                topMost.Checked = true;
                Check(alwaysOnTop && picker.TopMost, "항상 위 선택 즉시 적용");
                go.Checked = false;
                Check(!goEnabled, "서비스 표시 체크 이벤트 유지");
                go.Checked = true;
                Check(goEnabled, "서비스 표시 다시 켜기");
                codex.Checked = false;
                Check(!codexEnabled, "Codex 표시 체크 이벤트 유지");
                codex.Checked = true;
                keyButtons.Controls.OfType<Button>().Single(button => button.Text == "키 지우기").PerformClick();
                Check(cleared, "API 키 지우기 실행");
                Check(picker.Region is { } region && !region.IsVisible(0, 0) && region.IsVisible(picker.ClientSize.Width / 2, picker.ClientSize.Height / 2), "설정 창 둥근 모서리와 클릭 영역");
                SaveImage(picker, $"picker-{dpi}-{fontScale}.png");
            }
            finally
            {
                picker.Close();
                foreach (var font in fonts) font.Dispose();
            }
        }
    }

    private static void CheckAppearance(Form form, string settingsPath)
    {
        var settingsType = FormType.GetNestedType("WidgetSettings", BindingFlags.NonPublic)!;
        var oldSettings = System.Text.Json.JsonSerializer.Deserialize(
            "{\"Opacity\":0.8,\"Alerts\":{\"300\":{\"ResetsAt\":1,\"Level\":2}}}", settingsType)!;
        Check((bool)settingsType.GetProperty("AlwaysOnTop")!.GetValue(oldSettings)!, "기존 설정에는 항상 위 기본값 유지");
        Check((bool)settingsType.GetProperty("ShowCodex")!.GetValue(oldSettings)!, "기존 설정에는 Codex 표시 기본값 유지");
        Check((bool)settingsType.GetProperty("ShowOpenCodeGo")!.GetValue(oldSettings)!, "기존 설정에는 OpenCode Go 표시 기본값 유지");
        Check(settingsType.GetProperty("OpenCodeGoKey")!.GetValue(oldSettings) is null, "기존 설정에는 API 키 없음");
        Check(form.Region is { } region && !region.IsVisible(0, 0) && region.IsVisible(form.ClientSize.Width / 2, form.ClientSize.Height / 2), "메인 창 둥근 모서리와 클릭 영역");
        Check(form.BackColor.GetBrightness() > 0.8 && NameLabel(form, "codexPrimary").ForeColor.GetBrightness() < 0.5, "밝은 배경과 짙은 글자 색상");
        var previous = form.TopMost;
        FormType.GetMethod("ShowOpacityPicker", Private)!.Invoke(form, null);
        using var picker = form.OwnedForms.Single();
        var check = picker.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<CheckBox>().Single(control => control.Text == "항상 위");
        check.Checked = true;
        check.Checked = false;
        Check(!form.TopMost && !picker.TopMost, "체크박스로 메인 창과 설정 창 최상위 해제");
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath)))
            Check(!json.RootElement.GetProperty("AlwaysOnTop").GetBoolean(), "항상 위 해제 상태 저장");
        using (var restored = (Form)Activator.CreateInstance(FormType, true)!)
            Check(!restored.TopMost, "재실행 시 항상 위 해제 복원");
        check.Checked = true;
        Check(form.TopMost && picker.TopMost, "체크박스로 두 창 최상위 적용");
        using (var restored = (Form)Activator.CreateInstance(FormType, true)!)
            Check(restored.TopMost, "재실행 시 항상 위 선택 복원");
        check.Checked = previous;
        picker.Close();
    }

    private static object Row(Form form, string name) => FormType.GetField(name, Private)!.GetValue(form)!;
    private static Label NameLabel(Form form, string row) => Part<Label>(Row(form, row), "Name");
    private static Label ResetLabel(Form form, string row) => Part<Label>(Row(form, row), "Reset");
    private static bool Shown(Form form, string row) => (bool)Row(form, row).GetType()
        .GetProperty("Shown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Row(form, row))!;
    private static T Part<T>(object row, string name) => (T)row.GetType()
        .GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
    private static Label Status(Form form) => Field<Label>(form, "status");
    private static Label LastReset(Form form) => new[] { "goMonthly", "goWeekly", "goRolling", "codexSecondary", "codexPrimary" }
        .Select(name => ResetLabel(form, name)).First(label => label.Visible);
    private static T Field<T>(Form form, string name) => (T)FormType.GetField(name, Private)!.GetValue(form)!;
    private static void Fit(Form form) => FormType.GetMethod("FitUsageLayout", Private)!.Invoke(form, null);

    private static void CheckText(Form form, Label label)
    {
        var preferred = label.GetPreferredSize(Size.Empty);
        Check(label.Width >= preferred.Width && label.Height >= preferred.Height, "초기화 문구 전체 크기 확보");
        Check(form.ClientRectangle.Contains(label.Bounds), "초기화 문구 끝부분이 창 내부에 표시");
    }

    private static void SaveImage(Form form, string name)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine("obj/layout-verification", name));
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }
}
