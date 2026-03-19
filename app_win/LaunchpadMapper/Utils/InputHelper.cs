using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // Clipboard
using LaunchpadMapper.Models;

namespace LaunchpadMapper.Utils
{
    public static class InputHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private static readonly object _logLock = new object();
        public static bool Debug = false; // enable to write input_debug.log
        private static void AppendLog(string msg)
        {
            if (!Debug) return;
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "input_debug.log");
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
                lock (_logLock) System.IO.File.AppendAllText(path, line);
            }
            catch { }
        }

        public static void SendKey(ushort vk, bool keyDown)
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = keyDown ? 0u : KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            var inputs = new[] { input };
            var ret = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (ret == 0) AppendLog($"SendKey vk=0x{vk:X} keyDown={keyDown} -> ret=0 err={Marshal.GetLastWin32Error()}");
        }

        public static void SendUnicodeChar(char ch)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            var ret = SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
            if (ret == 0) AppendLog($"SendUnicodeChar '{ch}' -> ret=0 err={Marshal.GetLastWin32Error()}");
        }

        private static void SetClipboardTextSta(string text)
        {
            Exception? exStored = null;
            var t = new Thread(() =>
            {
                try { Clipboard.SetText(text); }
                catch (Exception ex) { exStored = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (exStored != null) throw exStored;
        }

        private static async Task PasteTextViaClipboardAsync(string text)
        {
            try
            {
                SetClipboardTextSta(text);
                // Send Ctrl+V
                ushort VK_CONTROL = 0x11;
                ushort VK_V = (ushort)'V';
                AppendLog($"Clipboard paste run len={text.Length}");
                SendKey(VK_CONTROL, true);
                await Task.Delay(2);
                SendKey(VK_V, true);
                SendKey(VK_V, false);
                await Task.Delay(2);
                SendKey(VK_CONTROL, false);
                await Task.Delay(6);
            }
            catch
            {
                // Fallback: type char-by-char if clipboard fails
                foreach (var ch in text)
                {
                    SendUnicodeChar(ch);
                    await Task.Delay(1);
                }
            }
        }

        // Play a hotkey sequence where each HotkeyEvent.Key is a single key name (or ASCII) and DelayMs is delay before this event
        public static async Task PlaySequenceAsync(System.Collections.Generic.List<HotkeyEvent> seq)
        {
            if (seq == null) return;
            foreach (var ev in seq)
            {
                if (ev.DelayMs > 0) await Task.Delay(ev.DelayMs);
                // For simplicity map single-char keys and some named keys
                ushort vk = MapKeyToVk(ev.Key);
                if (vk == 0) continue;
                SendKey(vk, true);
                SendKey(vk, false);
            }
        }

        // Types text with macro tokens like {ENTER}, {TAB}, {ESC}, {WAIT 200}, {CTRL+S}
        public static async Task TypeTextMacroAsync(string macro)
        {
            if (string.IsNullOrEmpty(macro)) return;
            AppendLog($"TypeTextMacro start: '{macro}'");
            int i = 0;
            while (i < macro.Length)
            {
                if (macro[i] == '{')
                {
                    int j = macro.IndexOf('}', i + 1);
                    if (j > i)
                    {
                        var token = macro.Substring(i + 1, j - i - 1).Trim();
                        AppendLog($"Token: {token}");
                        if (token.StartsWith("WAIT", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            if (parts.Length == 2 && int.TryParse(parts[1], out int ms) && ms >= 0)
                            {
                                AppendLog($"WAIT {ms}ms");
                                await Task.Delay(ms);
                            }
                        }
                        else if (token.Contains('+'))
                        {
                            // chord like CTRL+S
                            var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            ushort? main = null; var mods = new System.Collections.Generic.List<ushort>();
                            foreach (var p in parts)
                            {
                                var vk = MapKeyToVk(p);
                                if (vk == 0) continue;
                                if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("control", StringComparison.OrdinalIgnoreCase) || p.Equals("alt", StringComparison.OrdinalIgnoreCase) || p.Equals("shift", StringComparison.OrdinalIgnoreCase) || p.Equals("win", StringComparison.OrdinalIgnoreCase))
                                    mods.Add(vk);
                                else main = vk;
                            }
                            AppendLog($"Chord mods={string.Join('+', mods)} main={(main.HasValue?main.Value:0)}");
                            foreach (var m in mods) SendKey(m, true);
                            await Task.Delay(2);
                            if (main.HasValue) { SendKey(main.Value, true); SendKey(main.Value, false); }
                            await Task.Delay(2);
                            for (int k = mods.Count - 1; k >= 0; k--) SendKey(mods[k], false);
                        }
                        else
                        {
                            var vk = MapKeyToVk(token);
                            AppendLog($"Key token vk=0x{vk:X}");
                            if (vk != 0) { SendKey(vk, true); SendKey(vk, false); }
                        }
                        i = j + 1; continue;
                    }
                }
                // literal run until next '{'
                int next = macro.IndexOf('{', i);
                string run = next == -1 ? macro.Substring(i) : macro.Substring(i, next - i);
                if (!string.IsNullOrEmpty(run))
                {
                    AppendLog($"Literal run len={run.Length}");
                    await PasteTextViaClipboardAsync(run);
                    i += run.Length;
                }
                else
                {
                    // safety advance to avoid infinite loop
                    SendUnicodeChar(macro[i]);
                    i++;
                }
            }
            AppendLog("TypeTextMacro end");
        }

        private static ushort MapKeyToVk(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            key = key.Trim();
            if (key.Length == 1)
            {
                var ch = char.ToUpperInvariant(key[0]);
                return (ushort)ch;
            }
            switch (key.ToLowerInvariant())
            {
                case "enter": case "return": return 0x0D;
                case "space": return 0x20;
                case "tab": return 0x09;
                case "esc": case "escape": return 0x1B;
                case "ctrl": return 0x11;
                case "alt": return 0x12;
                case "shift": return 0x10;
                case "win": case "lwin": return 0x5B;
                case "backspace": case "bksp": case "bs": return 0x08;
                case "delete": case "del": return 0x2E;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                default:
                    return 0; // unknown
            }
        }
    }
}
