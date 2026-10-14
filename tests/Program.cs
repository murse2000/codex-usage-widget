using System.Reflection;

internal static class Program
{
    private static readonly Type FormType = Assembly.Load("CodexUsageWidget").GetType("CodexUsageWidget.UsageForm")!;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
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
            foreach (var screen in Screen.AllScreens)
            {
                form.Location = new Point(screen.WorkingArea.Left + 30, screen.WorkingArea.Top + 30);
                Application.DoEvents();
                var dpi = form.DeviceDpi;
                Console.WriteLine($"모니터 {screen.DeviceName}, 실제 DPI {dpi}");
                foreach (var twoWindows in new[] { false, true })
                {
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
            using var picker = (Form)Activator.CreateInstance(type,
                new object[] { 0.8, (Action<double>)(_ => { }), false, (Action<bool>)(value => toggled = value) })!;
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
                var check = layout.Controls.OfType<CheckBox>().Single();
                Check(slider.Bottom + slider.Margin.Bottom <= check.Top, "슬라이더와 체크박스 겹침 없음");
                var preferred = check.GetPreferredSize(Size.Empty);
                Check(check.Width >= preferred.Width && check.Height >= preferred.Height, "체크박스 문구·높이 확보");
                Check(layout.Controls.OfType<Label>().Single().Height >= layout.Controls.OfType<Label>().Single().PreferredHeight,
                    "투명도 문구 높이 확보");
                check.Checked = true;
                Check(toggled, "자동 실행 체크 이벤트 유지");
                SaveImage(picker, $"picker-{dpi}-{fontScale}.png");
            }
            finally
            {
                picker.Close();
                foreach (var font in fonts) font.Dispose();
            }
        }
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
