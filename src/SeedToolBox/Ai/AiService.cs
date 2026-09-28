using System;
using System.Collections.Generic;
using System.Linq;
using SeedToolBox.Reminders;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Ai;

/// <summary>The AI assistant: settings, prompt templates and the entry points (selection, screenshot, launcher).</summary>
sealed class AiService
{
    const string SettingsName = "ai";
    readonly ISettingsStore _store;

    public AiService(ISettingsStore store)
    {
        _store = store;
        Settings = store.Load<AiSettings>(SettingsName);
    }

    public AiSettings Settings { get; }
    /// <summary>What the automation mode changed; set by the app.</summary>
    public Automation.OperationLog Operations { get; set; } = null!;
    public Reminders.ReminderService? Reminders { get; set; }
    public Notes.NoteStore? Notes { get; set; }
    /// <summary>Opens the AI section of the settings page.</summary>
    public Action OpenSettings { get; set; } = () => { };

    public void Save() => _store.Save(SettingsName, Settings);

    public const string ImageQuestion = "这张截图里是什么？如果有文字，请提取出来并说明要点。";

    /// <summary>False (after telling the user) when the assistant is off or pi isn't installed.</summary>
    public bool Ready()
    {
        string? problem = !Settings.Enabled ? "AI 助手还没有开启。" : PiRuntime.Find(Settings) == null ? "还没有安装 pi。" : null;
        if (problem == null) return true;
        if (MessageBox.Show(problem + "\n\n现在去设置里开启并安装吗？", "AI 助手", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            OpenSettings();
        return false;
    }

    public void Open(string text = "")
    {
        if (!Ready()) return;
        var window = new AiWindow(this, text, null);
        window.Show();
    }

    /// <summary>Opens the automation mode and hands it the task.</summary>
    public void Automate(string task, BitmapSource? image = null)
    {
        if (!Ready()) return;
        var window = new AiWindow(this, "", image, automation: true);
        window.Show();
        if (task.Trim().Length > 0) window.Send(task);
    }

    /// <summary>Opens the automation mode with the text in the input box, for the user to say what to do with it.</summary>
    public void AutomateDraft(string text, BitmapSource? image = null)
    {
        if (!Ready()) return;
        new AiWindow(this, text, image, automation: true).Show();
    }

    /// <summary>Opens the page listing what the automation mode changed.</summary>
    public Action OpenLog { get; set; } = () => { };
    /// <summary>Opens the page for pi plugins and skills.</summary>
    public Action OpenPlugins { get; set; } = () => { };

    public void RemoveTask(QuickTask task)
    {
        Settings.QuickTasks.RemoveAll(t => t.Id == task.Id);
        Unschedule(task);
        Save();
    }

    void Unschedule(QuickTask task)
    {
        if (Reminders == null) return;
        foreach (var r in Reminders.Items.Where(r => r.AiTask == task.Id).ToList()) Reminders.Remove(r);
        task.Schedule = "";
    }

    /// <summary>Asks when to run the task, in the same words as reminders; an empty answer turns the schedule off.</summary>
    public void ScheduleTask(QuickTask task, Window owner)
    {
        if (Reminders == null) return;
        var input = AskText(owner, "定时运行「" + task.Name + "」", "什么时候运行？例如「每天9点」「工作日18:00」「明天上午10点」；留空表示取消定时。\n到时间会打开自动化窗口运行，改动仍然要你确认。", task.Schedule);
        if (input == null) return;
        Unschedule(task);
        if (input.Trim().Length > 0)
        {
            if (!ReminderParser.TryParse(input.Trim() + " " + task.Name, DateTime.Now, out var reminder) || reminder.Due <= DateTime.Now)
            {
                MessageBox.Show(owner, "看不懂这个时间：" + input, "定时运行", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            reminder.Text = "AI 任务：" + task.Name;
            reminder.AiTask = task.Id;
            Reminders.Add(reminder);
            task.Schedule = input.Trim();
        }
        Save();
    }

    /// <summary>A scheduled task came due.</summary>
    public void RunScheduled(Reminder reminder)
    {
        var task = Settings.QuickTasks.FirstOrDefault(t => t.Id == reminder.AiTask);
        if (task == null) return;
        if (!Settings.Enabled || PiRuntime.Find(Settings) == null) return;
        Automate(task.Prompt);
    }

    /// <summary>One question, one answer, in a pi without tools; throws when pi fails.</summary>
    public async Task<string> CompleteAsync(string prompt)
    {
        using var client = PiClient.Start(Settings);
        var answer = new StringBuilder();
        var done = new TaskCompletionSource<string?>();
        client.TextDelta += d => answer.Append(d);
        client.Finished += error => done.TrySetResult(error);
        client.Exited += error => done.TrySetResult("pi 已退出" + (error.Length > 0 ? "：" + error : ""));
        await client.PromptAsync(prompt);
        var failed = await done.Task;
        if (failed != null) throw new InvalidOperationException(failed);
        return answer.ToString();
    }

    public static string? AskText(Window? owner, string title, string hint, string initial)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Style = Views.DialogWindow.TextBoxStyle, Width = 380, Margin = new Thickness(0, 10, 0, 0) };
        var body = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(16),
            Children = { new System.Windows.Controls.TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }, box },
        };
        var ok = Views.DialogWindow.OkButton();
        var window = Views.DialogWindow.Create(title, body, ok, Views.DialogWindow.CancelButton());
        if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }

    public void Ask(string question)
    {
        if (!Ready()) return;
        var window = new AiWindow(this, "", null);
        window.Show();
        window.Send(question);
    }

    /// <summary>Starts a screenshot whose result comes back through <see cref="AskImage"/>.</summary>
    public Action StartCapture { get; set; } = () => { };
    AiWindow? _captureTarget;

    /// <summary>Takes a screenshot for an open conversation instead of a new window.</summary>
    public void CaptureFor(AiWindow window)
    {
        _captureTarget = window;
        window.Closed += (_, _) => { if (_captureTarget == window) _captureTarget = null; };
        StartCapture();
    }

    public void AskImage(BitmapSource image)
    {
        if (!Ready()) return;
        var target = _captureTarget;
        _captureTarget = null;
        if (target != null && target.IsVisible) target.Attach(image);
        else new AiWindow(this, "", image).Show();
    }

    /// <summary>
    /// Copies the selection in the foreground window with Ctrl+C and opens the assistant with it.
    /// If nothing new reaches the clipboard, the window opens empty.
    /// </summary>
    public void FromSelection()
    {
        if (!Ready()) return;
        uint before = GetClipboardSequenceNumber();
        SendCtrlC();
        var started = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            bool changed = GetClipboardSequenceNumber() != before;
            if (!changed && DateTime.Now - started < TimeSpan.FromMilliseconds(600)) return;
            timer.Stop();
            string text = "";
            if (changed)
            {
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
                catch (COMException ex) { Log.Error("Failed to read the copied selection", ex); }
            }
            Open(text.Trim());
        };
        timer.Start();
    }

    static void SendCtrlC()
    {
        const byte VK_CONTROL = 0x11, VK_C = 0x43;
        const uint KEYUP = 0x0002;
        // Release modifiers still held from the hotkey, which would turn Ctrl+C into Ctrl+Alt+C
        foreach (byte vk in new byte[] { 0x10, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_C, 0, 0, UIntPtr.Zero);
        keybd_event(VK_C, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
