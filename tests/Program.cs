using System.Reflection;

internal static class Program
{
    private static readonly Type FormType = Assembly.Load("CodexUsageWidget").GetType("CodexUsageWidget.UsageForm")!;
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
            foreach (var screen in Screen.AllScreens)
            {
                form.Location = new Point(screen.WorkingArea.Left + 30, screen.WorkingArea.Top + 30);
                Application.DoEvents();
                var dpi = form.DeviceDpi;
                Console.WriteLine($"모니터 {screen.DeviceName}, 실제 DPI {dpi}");
                foreach (var twoWindows in new[] { false, true })
                {
                    var windowType = FormType.GetNestedType("UsageWindow", BindingFlags.NonPublic)!;
                    foreach (var (prefix, used, minutes) in new[] { ("primary", 27, 300), ("secondary", 46, 10080) })
                        FormType.GetMethod("SetWindow", Private)!.Invoke(null, new object[]
                        {
                            Field<Label>(form, prefix + "Name"), Field<Label>(form, prefix + "Percent"),
                            Field<Control>(form, prefix + "Bar"), Field<Label>(form, prefix + "Reset"),
                            Activator.CreateInstance(windowType, new object[] { used, minutes, DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() })!
                        });
                    FormType.GetField("hasSecondary", Private)!.SetValue(form, twoWindows);
                    foreach (var name in new[] { "secondaryName", "secondaryPercent", "secondaryBar", "secondaryResetCaption", "secondaryReset" })
                        Field<Control>(form, name).Visible = twoWindows;
                    var primary = Field<Label>(form, "primaryReset");
                    var secondary = Field<Label>(form, "secondaryReset");
                    var original = (Font)primary.Font.Clone();
                    retainedFonts.Add(original);
                    var fonts = new List<Font>();
                    foreach (var fontScale in new[] { 1f, 1.25f, 1.5f, 2f })
                    {
                        var font = new Font(original.FontFamily, original.Size * fontScale, original.Style);
                        fonts.Add(font);
                        primary.Font = secondary.Font = font;
                        primary.Text = secondary.Text = "12/31 23:59 (999일 23시간 남음)";
                        Fit(form);
                        CheckText(form, primary);
                        if (twoWindows) CheckText(form, secondary);
                        Check(Field<Label>(form, "status").Top >= (twoWindows ? secondary : primary).Bottom,
                            "초기화 문구와 상태줄 겹침 없음");
                    }
                    primary.Font = secondary.Font = original;
                    foreach (var font in fonts) font.Dispose();
                    primary.Text = secondary.Text = "10/16 15:30 (0일 03시간 남음)";
                    Field<Label>(form, "status").Text = "3분마다 갱신 · 12:30";
                    Fit(form);
                    SaveImage(form, $"main-{dpi}-{twoWindows}.png");
                }
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

    private static void CheckPicker(Form owner, int dpi)
    {
        var type = FormType.GetNestedType("OpacityPicker", BindingFlags.NonPublic)!;
        foreach (var fontScale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var toggled = false;
            var alwaysOnTop = true;
            using var picker = (Form)Activator.CreateInstance(type,
                new object[] { 0.8, (Action<double>)(_ => { }), false, (Action<bool>)(value => toggled = value),
                    true, (Action<bool>)(value => alwaysOnTop = value) })!;
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
                foreach (Control child in layout.Controls)
                    Check(layout.ClientRectangle.Contains(child.Bounds), "설정 항목이 창 내부에 표시");
                var slider = layout.Controls.OfType<TrackBar>().Single();
                var check = layout.Controls.OfType<CheckBox>().Single(control => control.Text == "윈도우 시작 시 자동 실행");
                var topMost = layout.Controls.OfType<CheckBox>().Single(control => control.Text == "항상 위");
                Check(slider.Bottom + slider.Margin.Bottom <= topMost.Top && topMost.Bottom + topMost.Margin.Bottom <= check.Top, "슬라이더와 두 체크박스 겹침 없음");
                foreach (var checkbox in new[] { check, topMost })
                {
                    var preferred = checkbox.GetPreferredSize(Size.Empty);
                    Check(checkbox.Width >= preferred.Width && checkbox.Height >= preferred.Height, "체크박스 문구·높이 확보");
                }
                Check(layout.Controls.OfType<Label>().Single().Height >= layout.Controls.OfType<Label>().Single().PreferredHeight,
                    "투명도 문구 높이 확보");
                check.Checked = true;
                Check(toggled, "자동 실행 체크 이벤트 유지");
                topMost.Checked = false;
                Check(!alwaysOnTop && !picker.TopMost, "항상 위 해제 즉시 적용");
                topMost.Checked = true;
                Check(alwaysOnTop && picker.TopMost, "항상 위 선택 즉시 적용");
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
        var oldSettings = System.Text.Json.JsonSerializer.Deserialize("{\"Opacity\":0.8}", settingsType)!;
        Check((bool)settingsType.GetProperty("AlwaysOnTop")!.GetValue(oldSettings)!, "기존 설정에는 항상 위 기본값 유지");
        Check(form.Region is { } region && !region.IsVisible(0, 0) && region.IsVisible(form.ClientSize.Width / 2, form.ClientSize.Height / 2), "메인 창 둥근 모서리와 클릭 영역");
        Check(form.BackColor.GetBrightness() > 0.8 && Field<Label>(form, "primaryName").ForeColor.GetBrightness() < 0.5, "밝은 배경과 짙은 글자 색상");
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
