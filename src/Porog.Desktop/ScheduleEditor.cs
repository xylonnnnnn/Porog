using Porog.Core;

namespace Porog.Desktop;

internal sealed class ScheduleEditor : Form
{
    private readonly TextBox title = new() { Width = 610, MaxLength = 80 };
    private readonly TextBox intention = new() { Width = 610, MaxLength = 300 };
    private readonly DateTimePicker start = new() { Width = 280, Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy  HH:mm" };
    private readonly DateTimePicker end = new() { Width = 280, Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy  HH:mm" };
    private readonly TextBox domains = new() { Width = 610, Height = 88, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox processes = new() { Width = 610, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox enabled = new() { Text = "Промежуток включён", Checked = true, AutoSize = true };
    private readonly Label error = Theme.Label("", 10);
    private readonly Guid id;
    public Schedule? Result { get; private set; }

    public ScheduleEditor(Schedule? schedule)
    {
        Text = schedule is null ? "Новый промежуток · Порог" : "Изменить промежуток · Порог";
        Font = Theme.Font(); BackColor = Theme.Paper;
        ClientSize = new Size(690, 760); MinimumSize = new Size(700, 650);
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        id = schedule?.Id ?? Guid.NewGuid();
        var stack = Theme.Stack(); stack.Padding = new Padding(28); Controls.Add(stack);
        stack.Controls.Add(Theme.Label(schedule is null ? "Новый рабочий промежуток" : "Редактор промежутка", 22, FontStyle.Bold));
        stack.Controls.Add(Theme.Label("Время местное: " + TimeZoneInfo.Local.DisplayName, 9));
        Add("Название", title); Add("Что вы хотите закончить", intention);
        var times = new FlowLayoutPanel { Width = 620, Height = 64 };
        var a = new FlowLayoutPanel { Width = 300, Height = 64, FlowDirection = FlowDirection.TopDown };
        a.Controls.Add(Theme.Label("Начало", 10)); a.Controls.Add(start);
        var b = new FlowLayoutPanel { Width = 300, Height = 64, FlowDirection = FlowDirection.TopDown };
        b.Controls.Add(Theme.Label("Окончание", 10)); b.Controls.Add(end);
        times.Controls.AddRange([a, b]); stack.Controls.Add(times);
        Add("Сайты — один домен на строку", domains);
        stack.Controls.Add(Theme.Label("Без https:// и путей. Поддомены указывайте отдельно: youtube.com и www.youtube.com.", 9));
        Add("Приложения — одно имя .exe на строку", processes);
        stack.Controls.Add(Theme.Label("Например, Telegram.exe. Перед стартом сохраните работу: выбранные процессы будут завершаться.", 9));
        stack.Controls.Add(enabled);
        error.ForeColor = Theme.Orange; error.MaximumSize = new Size(610, 0); stack.Controls.Add(error);
        var actions = new FlowLayoutPanel { Width = 620, AutoSize = true };
        actions.Controls.Add(Theme.Button("Сохранить", (_, _) => Save()));
        actions.Controls.Add(Theme.Button("Отменить правки", (_, _) => Close(), false));
        stack.Controls.Add(actions);
        title.Text = schedule?.Name ?? ""; intention.Text = schedule?.Intention ?? "";
        start.Value = schedule?.StartAt.LocalDateTime ?? DateTime.Now.AddMinutes(5);
        end.Value = schedule?.EndAt.LocalDateTime ?? DateTime.Now.AddMinutes(55);
        domains.Text = string.Join(Environment.NewLine, schedule?.DomainTargets ?? []);
        processes.Text = string.Join(Environment.NewLine, schedule?.ProcessTargets ?? []);
        enabled.Checked = schedule?.Enabled ?? true;
        return;
        void Add(string caption, Control control)
        {
            stack.Controls.Add(Theme.Label(caption, 10, FontStyle.Bold));
            control.Margin = new Padding(0, 0, 0, 12); stack.Controls.Add(control);
        }
    }

    private void Save()
    {
        try
        {
            var a = DateTime.SpecifyKind(start.Value, DateTimeKind.Unspecified);
            var b = DateTime.SpecifyKind(end.Value, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(a) || TimeZoneInfo.Local.IsInvalidTime(b) ||
                TimeZoneInfo.Local.IsAmbiguousTime(a) || TimeZoneInfo.Local.IsAmbiguousTime(b))
                throw Validation.Invalid("startAt", "Выберите время вне перехода на летнее или зимнее время.");
            var schedule = new Schedule(id, title.Text, enabled.Checked,
                new(a, TimeZoneInfo.Local.GetUtcOffset(a)), new(b, TimeZoneInfo.Local.GetUtcOffset(b)),
                Lines(domains.Text), Lines(processes.Text), intention.Text);
            Result = Validation.Config(AppConfig.Empty with { Schedules = [schedule] }).Schedules[0];
            DialogResult = DialogResult.OK; Close();
        }
        catch (PorogException e) { error.Text = e.Message; }
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
