using Porog.Core;

namespace Porog.Desktop;

internal sealed class MainForm : Form
{
    private readonly PipeClient client = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly Label connection = Theme.Label("Проверяем службу…", 9);
    private ServiceStatus? status;
    private AppConfig? config;
    private readonly List<BreakForm> breakWindows = [];
    private readonly HashSet<string> notified = [];
    private readonly string notificationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Porog", "notifications.txt");
    private Label? heroTitle, heroTime, heroDetail, footer;
    private TextBox? diagnosticText;
    private bool polling, exit, busy;
    private string page = "status";
    private Guid? lastCompleted;
    private readonly bool startHidden;

    public MainForm(bool startHidden, string? capturePath = null)
    {
        this.startHidden = startHidden;
        Text = "Порог — время для своего дела"; BackColor = Theme.Paper; Font = Theme.Font();
        ForeColor = Theme.Ink; ClientSize = new Size(1100, 740); MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen; Icon = SystemIcons.Application;
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 206, BackColor = Theme.Ink, Padding = new Padding(22, 30, 16, 20) };
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var logo = Theme.Label("ПОРОГ", 25, FontStyle.Bold); logo.ForeColor = Color.White; logo.Margin = new Padding(0, 0, 0, 8);
        var caption = Theme.Label("Время для своего дела", 9); caption.ForeColor = Color.LightGray; caption.Margin = new Padding(0, 0, 0, 38);
        navigation.Controls.AddRange([logo, caption]);
        Nav("Обзор", () => ShowStatus()); Nav("Расписание", async () => await ShowSchedules());
        Nav("Отход", ShowBreak); Nav("Диагностика", ShowDiagnostics);
        connection.ForeColor = Color.LightGray; connection.MaximumSize = new Size(165, 0); connection.Margin = new Padding(0, 50, 0, 0);
        navigation.Controls.Add(connection); sidebar.Controls.Add(navigation);
        Controls.Add(content); Controls.Add(sidebar);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Порог", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Отход", null, (_, _) => { Show(); ShowBreak(); });
        menu.Items.Add("Выход из интерфейса", null, (_, _) =>
        {
            if (breakWindows.Any(w => !w.IsDisposed)) return;
            exit = true; Close();
        });
        tray = new NotifyIcon { Text = "Порог", Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) => { if (!exit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); tray.Dispose(); };
        timer.Tick += async (_, _) => await Poll();
        Shown += async (_, _) =>
        {
            if (this.startHidden) Hide();
            await Poll();
            if (capturePath is not null)
            {
                try { await CaptureDocs(capturePath); }
                finally { exit = true; Close(); }
            }
            else timer.Start();
        };
        try { if (File.Exists(notificationPath)) notified.UnionWith(File.ReadAllLines(notificationPath)); }
        catch (IOException) { /* Notification failure cannot claim protection. */ }
        ShowStatus();
        return;
        void Nav(string title, Action action)
        {
            var button = Theme.Button(title, (_, _) => action(), false);
            button.Width = 165; button.AutoSize = false; button.Height = 48; button.TextAlign = ContentAlignment.MiddleLeft;
            button.BackColor = Theme.Ink; button.ForeColor = Color.White; navigation.Controls.Add(button);
        }
    }

    private FlowLayoutPanel BeginPage(string name, string eyebrow, string title, string subtitle)
    {
        page = name; heroTitle = heroTime = heroDetail = footer = null; diagnosticText = null;
        // Dispose old controls, not just their collection handles.
        foreach (Control control in content.Controls.Cast<Control>().ToArray()) control.Dispose();
        var stack = Theme.Stack(); content.Controls.Add(stack);
        var label = Theme.Label(eyebrow.ToUpperInvariant(), 9, FontStyle.Bold); label.ForeColor = Theme.Muted;
        stack.Controls.Add(label); stack.Controls.Add(Theme.Label(title, 29, FontStyle.Bold));
        stack.Controls.Add(Theme.Label(subtitle, 10));
        return stack;
    }

    private void ShowStatus()
    {
        var stack = BeginPage("status", "Локально · без аккаунта", "Один промежуток. Одно дело.", "Назначьте время — Порог удержит границу, пока вы работаете.");
        var card = new FlowLayoutPanel { Width = 750, Height = 270, BackColor = Theme.Pale,
            Padding = new Padding(28), FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 20, 0, 24) };
        heroTitle = Theme.Label("Проверяем службу…", 17, FontStyle.Bold);
        heroTime = Theme.Label("—", 45, FontStyle.Bold);
        heroDetail = Theme.Label("Состояние защиты ещё не подтверждено.", 11);
        heroDetail.MaximumSize = new Size(680, 0); card.Controls.AddRange([heroTitle, heroTime, heroDetail]); stack.Controls.Add(card);
        var actions = new FlowLayoutPanel { Width = 750, AutoSize = true };
        actions.Controls.Add(Theme.Button("Настроить расписание", async (_, _) => await ShowSchedules()));
        actions.Controls.Add(Theme.Button("Сделать отход", (_, _) => ShowBreak(), false)); stack.Controls.Add(actions);
        footer = Theme.Label("", 10); stack.Controls.Add(footer);
        stack.Controls.Add(Theme.Label("Закрытие окна оставляет приложение в трее.\nСлужба продолжает выполнять расписание без открытого окна.", 10));
        UpdateStatus();
    }

    private async Task ShowSchedules()
    {
        var stack = BeginPage("schedules", "Ваш ритм", "Расписание", "Предупреждение за минуту. Активный промежуток защищён от изменений.");
        try
        {
            config = await client.Send<AppConfig>("GetConfig", new { });
            if (page != "schedules" || stack.IsDisposed) return;
            stack.Controls.Add(Theme.Button("+ Новый промежуток", async (_, _) => await Edit(null)));
            if (config.Schedules.Length == 0)
            {
                stack.Controls.Add(Theme.Label("Пока здесь свободно", 21, FontStyle.Bold));
                stack.Controls.Add(Theme.Label("Создайте первый промежуток и выберите отвлекающие сайты или приложения."));
            }
            foreach (var schedule in config.Schedules.OrderBy(s => s.StartAt))
            {
                var row = new FlowLayoutPanel { Width = 750, AutoSize = true, BackColor = Color.White,
                    Padding = new Padding(20), FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
                row.Controls.Add(Theme.Label(schedule.Name + (schedule.Enabled ? "" : " · выключен"), 16, FontStyle.Bold));
                row.Controls.Add(Theme.Label($"{schedule.StartAt.ToLocalTime():dd.MM.yyyy HH:mm} — {schedule.EndAt.ToLocalTime():dd.MM.yyyy HH:mm}", 11));
                row.Controls.Add(Theme.Label($"{schedule.DomainTargets.Length} доменов · {schedule.ProcessTargets.Length} приложений" +
                    (schedule.Intention.Length == 0 ? "" : "\n" + schedule.Intention), 10));
                var buttons = new FlowLayoutPanel { Width = 690, AutoSize = true };
                var locked = schedule.Enabled && schedule.StartAt <= DateTimeOffset.Now && DateTimeOffset.Now < schedule.EndAt;
                var edit = Theme.Button(locked ? "До окончания изменить нельзя" : "Изменить", async (_, _) => await Edit(schedule), false);
                edit.Enabled = !locked;
                var remove = Theme.Button("Удалить", async (_, _) => await Delete(schedule), false); remove.Enabled = !locked;
                buttons.Controls.AddRange([edit, remove]); row.Controls.Add(buttons); stack.Controls.Add(row);
            }
        }
        catch (PorogException e) { if (!stack.IsDisposed) stack.Controls.Add(Theme.Label(e.Message)); }
    }

    private async Task Edit(Schedule? original)
    {
        if (busy || config is null) return;
        using var editor = new ScheduleEditor(original);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is null) return;
        var list = config.Schedules.Where(s => s.Id != editor.Result.Id).Append(editor.Result).ToArray();
        await Save(list);
    }

    private async Task Delete(Schedule schedule)
    {
        if (busy || config is null) return;
        if (MessageBox.Show(this, $"Удалить промежуток «{schedule.Name}»?", "Порог", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await Save(config.Schedules.Where(s => s.Id != schedule.Id).ToArray());
    }

    private async Task Save(Schedule[] schedules)
    {
        if (config is null) return;
        busy = true;
        try
        {
            Validation.Config(config with { Schedules = schedules });
            config = await client.Send<AppConfig>("SaveConfig", new SaveConfigRequest(config.ConfigVersion, schedules, config.Settings));
        }
        catch (PorogException e) { MessageBox.Show(this, e.Message + "\n\n" + e.Code, "Настройки не сохранены", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; }
        await ShowSchedules();
    }

    private void ShowBreak()
    {
        var stack = BeginPage("break", "Перерыв вне экрана", "Пора отойти", "Несколько минут для воды, движения и взгляда в окно.");
        stack.Controls.Add(Theme.Label("03 — 12 минут", 42, FontStyle.Bold));
        stack.Controls.Add(Theme.Label("«Отход» откроется на всех мониторах. Кнопки пропуска нет.\nСлужба хранит время окончания; перезапуск окна не обнуляет таймер.", 12));
        var minutes = new NumericUpDown { Minimum = 3, Maximum = 12, Value = config?.Settings.BreakDurationMinutes ?? 5,
            Width = 160, Font = Theme.Font(24), Margin = new Padding(0, 12, 0, 24) };
        stack.Controls.Add(minutes);
        var start = Theme.Button("Начать отход", async (sender, _) =>
        {
            if (busy) return;
            busy = true; ((Button)sender!).Enabled = false;
            try
            {
                var session = await client.Send<Session>("StartBreak", new StartBreakRequest((int)minutes.Value));
                OpenBreak(session);
            }
            catch (PorogException e) { MessageBox.Show(this, e.Message, "Не удалось начать отход"); }
            finally { busy = false; if (sender is Button button && !button.IsDisposed) button.Enabled = true; }
        });
        stack.Controls.Add(start);
        stack.Controls.Add(Theme.Label("Полноэкранный режим требует работающего интерфейса.\nСистемные сочетания Windows, диспетчер задач и перезагрузка могут его обойти.", 10));
    }

    private void ShowDiagnostics()
    {
        var stack = BeginPage("diagnostics", "Состояние системы", "Диагностика", "Порог показывает ошибки защиты и не скрывает ограничения Windows.");
        diagnosticText = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Theme.Paper, Width = 750, Height = 285, Font = Theme.Font(11), ScrollBars = ScrollBars.Vertical };
        stack.Controls.Add(diagnosticText);
        stack.Controls.Add(Theme.Button("Повторить подключение", async (_, _) => await Poll()));
        stack.Controls.Add(Theme.Label("Secure DNS / DoH и кэш браузера могут обходить hosts. Поддомены указываются явно.\nСчётчик считает только завершённые процессы; посещения сайтов не регистрируются.\nУстановка и удаление службы требуют прав администратора.\nНет защиты от администратора, остановки службы и системных сочетаний клавиш.", 10));
        UpdateStatus();
    }

    private async Task Poll()
    {
        foreach (var window in breakWindows.Where(w => !w.IsDisposed).ToArray()) window.RefreshCountdown(DateTimeOffset.UtcNow);
        breakWindows.RemoveAll(w => w.IsDisposed);
        if (polling) return;
        polling = true;
        try
        {
            status = await client.Send<ServiceStatus>("GetStatus", new { });
            if (IsDisposed) return;
            connection.Text = status.ServiceState == "Running" ? "● Служба работает" : "● Проверьте защиту";
            if (status.BreakSession is { } rest && rest.EndsAt > status.Now) OpenBreak(rest);
            if (status.NextSchedule is { } upcoming && status.WarningKeys.Contains(ScheduleEngine.Key(upcoming)))
                Notify("warning:" + ScheduleEngine.Key(upcoming), "Скоро начнётся блокировка", $"«{upcoming.Name}» до {upcoming.EndAt.ToLocalTime():HH:mm}. Сохраните работу в выбранных приложениях.");
            if (status.LastCompleted is { } complete && lastCompleted != complete.SessionId)
            {
                lastCompleted = complete.SessionId;
                if (status.Now - complete.EndsAt < TimeSpan.FromSeconds(10))
                    Notify("complete:" + complete.SessionId, "Промежуток завершён", $"«{complete.Name}». Завершено процессов: {complete.Attempts}.");
            }
            UpdateStatus();
        }
        catch (PorogException e)
        {
            status = null; connection.Text = "● Служба недоступна";
            if (heroTitle is not null) heroTitle.Text = "Защита не подтверждена";
            if (heroTime is not null) heroTime.Text = "Нет связи";
            if (heroDetail is not null) heroDetail.Text = e.Message;
            if (diagnosticText is not null) diagnosticText.Text = e.Code + Environment.NewLine + e.Message;
        }
        finally { polling = false; }
    }

    private void UpdateStatus()
    {
        if (status is null) return;
        if (diagnosticText is not null)
            diagnosticText.Text = $"Служба: {status.ServiceState}\r\nВерсия конфигурации: {status.ConfigVersion}\r\n\r\n" +
                string.Join("\r\n", status.Capabilities.Select(c => $"{c.Name}: {(c.Available ? "доступно" : "не подтверждено")}\r\n{c.Detail}\r\n")) +
                string.Join("\r\n", status.Issues.Select(i => $"{i.Code}: {i.Message}"));
        if (heroTitle is null || heroTime is null || heroDetail is null) return;
        if (status.ServiceState != "Running")
        {
            heroTitle.Text = "Защита требует внимания"; heroTime.Text = "Проверьте";
            heroDetail.Text = status.Issues.FirstOrDefault()?.Message ?? "Служба проверяет доступ к механизмам блокировки.";
        }
        else if (status.ActiveSession is { } work)
        {
            heroTitle.Text = $"Закрыто до {work.EndsAt.ToLocalTime():HH:mm}";
            var left = work.EndsAt - status.Now;
            heroTime.Text = $"{Math.Max(0, (int)left.TotalHours):00}:{Math.Max(0, left.Minutes):00}:{Math.Max(0, left.Seconds):00}";
            heroDetail.Text = $"«{work.Name}» · завершено процессов: {work.Attempts}\nПромежуток закончится автоматически. Штатной отмены нет.";
        }
        else
        {
            heroTitle.Text = "Всё готово к работе";
            heroTime.Text = status.NextSchedule is { } next ? next.StartAt.ToLocalTime().ToString("HH:mm") : "Свободно";
            heroDetail.Text = status.NextSchedule is { } upcoming ? $"Следующий блок: «{upcoming.Name}»\n{upcoming.StartAt.ToLocalTime():dd.MM.yyyy HH:mm} — {upcoming.EndAt.ToLocalTime():HH:mm}" : "Расписания пока нет. Создайте первый промежуток.";
        }
        if (footer is not null) footer.Text = "Сайты через hosts · приложения по имени .exe · все данные на этом компьютере";
    }

    private void OpenBreak(Session session)
    {
        if (breakWindows.Any(w => !w.IsDisposed && w.SessionId == session.SessionId)) return;
        foreach (var screen in Screen.AllScreens)
        {
            var window = new BreakForm(session, screen); breakWindows.Add(window); window.Show();
            window.RefreshCountdown(DateTimeOffset.UtcNow);
        }
    }

    private void Notify(string key, string title, string message)
    {
        if (!notified.Add(key)) return;
        tray.ShowBalloonTip(10000, title, message, ToolTipIcon.Info);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(notificationPath)!);
            File.WriteAllLines(notificationPath, notified.TakeLast(2000));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* OS notifications are best effort. */ }
    }

    private async Task CaptureDocs(string path)
    {
        Directory.CreateDirectory(path);
        Save(this, "01-status.png");
        await ShowSchedules(); Save(this, "02-schedules.png");
        using (var editor = new ScheduleEditor(null))
        {
            editor.Show(); editor.Refresh(); Save(editor, "03-editor.png"); editor.Close();
        }
        ShowBreak(); Save(this, "04-break-settings.png");
        ShowDiagnostics(); Save(this, "05-diagnostics.png");
        // Render the actual break form offscreen. This command never starts a break session.
        using var rest = new BreakForm(new(Guid.NewGuid(), null, "Break", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), Name: "Отход"), Screen.PrimaryScreen!);
        rest.CreateControl(); rest.RefreshCountdown(DateTimeOffset.UtcNow); Save(rest, "06-break.png");
        return;
        void Save(Form form, string name)
        {
            form.PerformLayout(); form.Refresh();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(path, name), System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
