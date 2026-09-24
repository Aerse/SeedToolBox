using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
