namespace CodexUsageWidget;

internal sealed class AlertPolicyForm : GlassForm
{
    private readonly AlertPolicySettings draftPolicies;
    private readonly AlertQuietHours draftQuietHours;
    private readonly Func<AlertPolicySettings, AlertQuietHours, string?> onSave;
    private readonly List<AlertPolicyRow> rows = new();
    private readonly CheckBox quietEnabled = new();
    private readonly ComboBox quietStart = WidgetFormStyle.CreateComboBox();
    private readonly ComboBox quietEnd = WidgetFormStyle.CreateComboBox();
    private readonly Label errorLabel = new();

    internal AlertPolicyForm(AlertPolicySettings policies, AlertQuietHours quietHours,
        Func<AlertPolicySettings, AlertQuietHours, string?> onSave)
    {
        draftPolicies = policies.Clone();
        draftQuietHours = quietHours.Clone();
        this.onSave = onSave;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "알림 정책";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 430);
        ForeColor = WidgetFormStyle.TextColor;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(15, 8, 15, 8), BackColor = Color.Transparent };
        var save = WidgetFormStyle.CreateButton("저장");
        var cancel = WidgetFormStyle.CreateButton("취소");
        save.Margin = new Padding(0, 0, 6, 0);
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        errorLabel.AutoSize = true;
        errorLabel.Font = new Font("Segoe UI", 7.5f);
        errorLabel.ForeColor = Color.FromArgb(173, 109, 20);
        errorLabel.BackColor = Color.Transparent;
        errorLabel.Margin = new Padding(6, 6, 0, 0);
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(errorLabel);
        Controls.Add(buttons);

        var quiet = new Panel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(15, 8, 15, 0), BackColor = Color.Transparent };
        quietEnabled.Text = "방해 금지 사용";
        quietEnabled.Font = new Font("Segoe UI", 8.5f);
        quietEnabled.ForeColor = WidgetFormStyle.TextColor;
        quietEnabled.BackColor = Color.Transparent;
        quietEnabled.AutoSize = true;
        quietEnabled.Location = new Point(quiet.Padding.Left, quiet.Padding.Top);
        quietEnabled.Checked = draftQuietHours.Enabled;
        quiet.Controls.Add(quietEnabled);
        var quietStartLabel = WidgetFormStyle.CreateFilterLabel("시작");
        quietStartLabel.Location = new Point(quiet.Padding.Left, quiet.Padding.Top + 30);
        var quietEndLabel = WidgetFormStyle.CreateFilterLabel("종료");
        quietEndLabel.Location = new Point(quiet.Padding.Left + 125, quiet.Padding.Top + 30);
        quietStart.Location = new Point(quiet.Padding.Left + 38, quiet.Padding.Top + 25);
        quietEnd.Location = new Point(quiet.Padding.Left + 163, quiet.Padding.Top + 25);
        quietStart.Width = quietEnd.Width = 72;
        for (var hour = 0; hour < 24; hour++) quietStart.Items.Add($"{hour:00}시");
        for (var hour = 0; hour < 24; hour++) quietEnd.Items.Add($"{hour:00}시");
        quietStart.SelectedIndex = quietEnd.SelectedIndex = 0;
        quietStart.SelectedIndex = draftQuietHours.StartHour;
        quietEnd.SelectedIndex = draftQuietHours.EndHour;
        quiet.Controls.Add(quietStartLabel);
        quiet.Controls.Add(quietEndLabel);
        quiet.Controls.Add(quietStart);
        quiet.Controls.Add(quietEnd);
        Controls.Add(quiet);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 252,
            ColumnCount = 5,
            RowCount = 6,
            Padding = new Padding(15, 4, 15, 4),
            BackColor = Color.Transparent
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        foreach (var heading in new[] { "구간", "알림", "경고", "위험", "반복" })
            table.Controls.Add(WidgetFormStyle.CreateFilterLabel(heading));
        AddRow(table, "Codex 5시간", "codex:300", draftPolicies);
        AddRow(table, "Codex 주간", "codex:10080", draftPolicies);
        AddRow(table, "Go 5시간", "go:rolling", draftPolicies);
        AddRow(table, "Go 주간", "go:weekly", draftPolicies);
        AddRow(table, "Go 월간", "go:monthly", draftPolicies);
        Controls.Add(table);

        var header = WidgetFormStyle.CreateHeader("알림 정책", 520, Close);
        Controls.Add(header);
        ResumeLayout(true);
    }

    private void AddRow(TableLayoutPanel table, string label, string key, AlertPolicySettings policies)
    {
        var policy = policies.Get(key);
        var rowIndex = rows.Count + 1;
        var name = new Label
        {
            Text = label,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = WidgetFormStyle.TextColor,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left
        };
        var enabled = new CheckBox
        {
            Checked = policy.Enabled,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left
        };
        var warning = CreateNumber(policy.WarningPercent, 1, 100);
        var critical = CreateNumber(policy.CriticalPercent, 0, 99);
        var repeat = CreateNumber(policy.RepeatMinutes, 0, 1440);
        table.Controls.Add(name, 0, rowIndex);
        table.Controls.Add(enabled, 1, rowIndex);
        table.Controls.Add(warning, 2, rowIndex);
        table.Controls.Add(critical, 3, rowIndex);
        table.Controls.Add(repeat, 4, rowIndex);
        rows.Add(new AlertPolicyRow(key, enabled, warning, critical, repeat));
    }

    private static NumericUpDown CreateNumber(int value, int minimum, int maximum)
    {
        var number = new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = 58,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = WidgetFormStyle.TextColor,
            BackColor = WidgetFormStyle.FieldColor,
            TextAlign = HorizontalAlignment.Center
        };
        return number;
    }

    private void Save()
    {
        foreach (var row in rows)
        {
            if ((int)row.Warning.Value <= (int)row.Critical.Value)
            {
                errorLabel.Text = "경고 임계값은 위험 임계값보다 커야 합니다.";
                return;
            }
        }
        var policies = new AlertPolicySettings();
        foreach (var row in rows)
            policies.Set(row.Key, new AlertPolicy(row.Enabled.Checked, (int)row.Warning.Value, (int)row.Critical.Value, (int)row.Repeat.Value));
        var quietHours = new AlertQuietHours
        {
            Enabled = quietEnabled.Checked,
            StartHour = quietStart.SelectedIndex,
            EndHour = quietEnd.SelectedIndex
        };
        var error = onSave(policies, quietHours);
        if (error is not null)
        {
            errorLabel.Text = error;
            return;
        }
        Close();
    }

    private sealed record AlertPolicyRow(string Key, CheckBox Enabled, NumericUpDown Warning, NumericUpDown Critical, NumericUpDown Repeat);
}
