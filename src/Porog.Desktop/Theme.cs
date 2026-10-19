namespace Porog.Desktop;

internal static class Theme
{
    public static readonly Color Paper = ColorTranslator.FromHtml("#F4F2ED");
    public static readonly Color Ink = ColorTranslator.FromHtml("#202A27");
    public static readonly Color Muted = ColorTranslator.FromHtml("#66716B");
    public static readonly Color Green = ColorTranslator.FromHtml("#32634B");
    public static readonly Color Pale = ColorTranslator.FromHtml("#E4EBE0");
    public static readonly Color Orange = ColorTranslator.FromHtml("#9B4C28");
    public static Font Font(float size = 11, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
    public static Label Label(string text, float size = 11, FontStyle style = FontStyle.Regular) =>
        new() { Text = text, Font = Font(size, style), ForeColor = Ink, AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(0, 0, 0, 12) };
    public static Button Button(string text, EventHandler action, bool primary = true)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 9, 18, 9), FlatStyle = FlatStyle.Flat, Font = Font(10, FontStyle.Bold),
            BackColor = primary ? Green : Color.White, ForeColor = primary ? Color.White : Ink,
            Cursor = Cursors.Hand, Margin = new Padding(0, 0, 12, 12) };
        button.FlatAppearance.BorderSize = 0;
        button.Click += action;
        return button;
    }
    public static FlowLayoutPanel Stack() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, AutoScroll = true, Padding = new Padding(32), BackColor = Paper };
}
