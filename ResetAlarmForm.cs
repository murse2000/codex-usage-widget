using System.Diagnostics;

namespace CodexUsageWidget;

internal sealed class ResetAlarmForm : Form
{
    private bool acknowledged;

    public ResetAlarmForm(TiboNotice notice)
    {
        Text = notice.NotificationTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(460, 340);
        MinimumSize = new Size(360, 280);
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caution = notice.IsWatch
            ? "확정 예고 아님" + (notice.ChancePercent is { } chance ? $" · 서비스 추정 {chance}%" : "") + "\r\n\r\n"
            : "";
        var reference = notice.ReferenceAt is { } time
            ? $"\r\n{(notice.IsWatch ? "관찰 기한" : "참고 시각")}: {time.LocalDateTime:M/d HH:mm}" : "";
        layout.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = SystemColors.Window,
            Text = $"{caution}{notice.Text}\r\n\r\n게시: {notice.AnnouncedAt.LocalDateTime:M/d HH:mm}{reference}\r\n출처: codex-resets.com (비공식)"
        }, 0, 0);
        var original = new LinkLabel { Text = "X 원문 보기", AutoSize = true, Margin = new Padding(0, 12, 0, 12) };
        original.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(notice.Url.AbsoluteUri) { UseShellExecute = true });
        layout.Controls.Add(original, 0, 1);
        var confirm = new Button { Text = "확인", AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(16, 4, 16, 4) };
        confirm.Click += (_, _) => { acknowledged = true; Close(); };
        layout.Controls.Add(confirm, 0, 2);
        AcceptButton = confirm;
        Controls.Add(layout);
        Shown += (_, _) => confirm.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4도 확인으로 처리하지 않는다. 앱 종료와 Windows 종료는 허용한다.
        if (e.CloseReason == CloseReason.UserClosing && !acknowledged) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
