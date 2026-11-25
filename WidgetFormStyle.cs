using System.Runtime.InteropServices;

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
        var header = new DraggablePanel { Dock = DockStyle.Top, Height = 30, BackColor = Color.Transparent };
        var heading = new DraggableLabel
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
        EnableDrag(header, heading);
        return header;
    }

    internal static void EnableDrag(Control header, Control title)
    {
        if (header is IDragSource source) source.DragRequested += (_, e) => DragWindow(header, e);
        else header.MouseDown += DragWindow;
        if (title is IDragSource sourceTitle) sourceTitle.DragRequested += (_, e) => DragWindow(title, e);
        else title.MouseDown += DragWindow;
    }

    private static void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not Control control || control.FindForm() is not { IsDisposed: false } form)
            return;
        ReleaseCapture();
        SendMessage(form.Handle, 0xA1, 0x2, 0);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

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

internal interface IDragSource
{
    event MouseEventHandler? DragRequested;
    bool HasDragHandler { get; }
}

internal sealed class DraggablePanel : Panel, IDragSource
{
    public event MouseEventHandler? DragRequested;
    public bool HasDragHandler => DragRequested is not null;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) DragRequested?.Invoke(this, e);
    }
}

internal sealed class DraggableLabel : Label, IDragSource
{
    public event MouseEventHandler? DragRequested;
    public bool HasDragHandler => DragRequested is not null;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) DragRequested?.Invoke(this, e);
    }
}
