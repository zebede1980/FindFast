using System.Runtime.InteropServices;
using System.Windows.Input;

namespace FastFind.App.Services;

/// <summary>System-wide hotkey registered against the main window. Parses text such as "Ctrl+Alt+F" or "Win+Space".</summary>
public sealed class GlobalHotkey : IDisposable
{
    public const int WM_HOTKEY = 0x0312;
    private const int Id = 0x4646; // "FF"
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private bool _registered;

    public string Text { get; private set; } = "";
    public string? Error { get; private set; }

    public GlobalHotkey(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Registers the combination; returns false (with <see cref="Error"/>) if it is malformed or already taken by another app.</summary>
    public bool Register(string text)
    {
        Unregister();
        Text = text?.Trim() ?? "";
        Error = null;
        if (Text.Length == 0) return true;
        if (!TryParse(Text, out uint modifiers, out uint vk, out string? parseError))
        {
            Error = parseError;
            return false;
        }
        if (!RegisterHotKey(_hwnd, Id, modifiers | MOD_NOREPEAT, vk))
        {
            Error = $"{Text} is already in use by another application";
            return false;
        }
        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_hwnd, Id);
        _registered = false;
    }

    public bool IsMessage(int msg, IntPtr wParam) => msg == WM_HOTKEY && wParam.ToInt32() == Id;

    public static bool TryParse(string text, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0; vk = 0; error = null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { error = "Empty hotkey"; return false; }
        Key key = Key.None;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": case "windows": modifiers |= MOD_WIN; break;
                default:
                    string name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse(name, ignoreCase: true, out key) || key == Key.None)
                    {
                        error = $"Unknown key '{part}' (use e.g. Ctrl+Alt+F, Win+Space, Ctrl+Shift+F12)";
                        return false;
                    }
                    break;
            }
        }
        if (key == Key.None) { error = "Hotkey needs a key as well as modifiers"; return false; }
        if (modifiers == 0 && key < Key.F1) { error = "Add at least one modifier (Ctrl, Alt, Shift or Win)"; return false; }
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return true;
    }

    public void Dispose() => Unregister();
}
