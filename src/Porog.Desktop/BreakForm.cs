using Porog.Core;

namespace Porog.Desktop;

internal sealed class BreakForm : Form
{
    private readonly Label countdown = Theme.Label("", 72, FontStyle.Bold);
    private bool finished;
    public Guid SessionId { get; }
    private readonly DateTimeOffset endsAt;
    public BreakForm(Session session, Screen screen)
    {
        SessionId = session.SessionId; endsAt = session.EndsAt;
        Text = "Отход · Порог"; FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; Bounds = screen.Bounds; TopMost = true;
        ShowInTaskbar = false; BackColor = Theme.Ink; ForeColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Percent, 50)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 50));
        var center = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(40) };
        foreach (var label in new[] { Theme.Label("П О Р О Г   /   О Т Х О Д", 14), countdown,
            Theme.Label("Встаньте из-за стола", 28, FontStyle.Bold),
            Theme.Label("Пройдитесь, выпейте воды, посмотрите в окно.\nВернитесь, когда закончится таймер.", 15),
            Theme.Label("Перерыв закончится в " + endsAt.ToLocalTime().ToString("HH:mm") + " · штатной отмены нет", 10) })
        { label.ForeColor = Color.White; label.MaximumSize = new Size(Math.Max(300, screen.Bounds.Width - 120), 0); center.Controls.Add(label); }
        layout.Controls.Add(center, 0, 1); Controls.Add(layout);
        FormClosing += (_, e) => { if (!finished && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true; };
    }
    public void RefreshCountdown(DateTimeOffset now)
    {
        var left = endsAt - now;
        if (left <= TimeSpan.Zero) { finished = true; Close(); return; }
        countdown.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        if (!ContainsFocus) { BringToFront(); Activate(); }
    }
}
