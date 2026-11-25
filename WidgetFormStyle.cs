namespace CodexUsageWidget;

internal static class WidgetFormStyle
{
    internal static readonly Color Muted = Color.FromArgb(89, 108, 134);
    internal static readonly Color TextColor = Color.FromArgb(31, 45, 67);
    internal static readonly Color Accent = Color.FromArgb(18, 137, 105);
    internal static readonly Color BrandColor = Color.FromArgb(53, 102, 195);
    internal static readonly Color FieldColor = Color.FromArgb(239, 245, 254);
    internal static readonly Color RuleColor = Color.FromArgb(206, 220, 238);

    internal static Panel CreateHeader(string title, int formWidth, Action close)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.Transparent };
        var heading = new Label
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Left,
            Padding = new Padding(15, 4, 0, 0),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = Color.Transparent
        };
        var closeButton = new Button
        {
            Text = "×",
            AccessibleName = "창 닫기",
            Dock = DockStyle.Right,
            Width = 35,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Symbol", 9),
            ForeColor = Muted,
            BackColor = Color.Transparent
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 229, 248);
        closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(197, 216, 243);
        closeButton.Click += (_, _) => close();
        header.Controls.Add(heading);
        header.Controls.Add(closeButton);
        return header;
    }

    internal static Label CreateFilterLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
        ForeColor = Muted,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 3)
    };

    internal static ComboBox CreateComboBox()
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 104,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = TextColor,
            BackColor = FieldColor
        };
        return box;
    }

    internal static Button CreateButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(8, 2, 8, 2),
        Font = new Font("Segoe UI", 8.5f),
        ForeColor = TextColor,
        FlatStyle = FlatStyle.Flat,
        BackColor = FieldColor
    };
}
