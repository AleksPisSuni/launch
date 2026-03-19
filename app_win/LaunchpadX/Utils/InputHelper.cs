using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LaunchpadX.Utils
{
    public static class InputHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
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
            public int dx, dy, mouseData;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private static readonly Dictionary<string, ushort> _vkMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"]   = 0x11, ["control"] = 0x11,
            ["shift"]  = 0x10,
            ["alt"]    = 0x12,
            ["win"]    = 0x5B, ["windows"] = 0x5B,
            ["enter"]  = 0x0D, ["return"]  = 0x0D,
            ["esc"]    = 0x1B, ["escape"]  = 0x1B,
            ["space"]  = 0x20,
            ["tab"]    = 0x09,
            ["back"]   = 0x08, ["backspace"] = 0x08,
            ["delete"] = 0x2E, ["del"]     = 0x2E,
            ["insert"] = 0x2D, ["ins"]     = 0x2D,
            ["home"]   = 0x24,
            ["end"]    = 0x23,
            ["pgup"]   = 0x21, ["pageup"]  = 0x21,
            ["pgdn"]   = 0x22, ["pagedown"] = 0x22,
            ["left"]   = 0x25,
            ["right"]  = 0x27,
            ["up"]     = 0x26,
            ["down"]   = 0x28,
            ["f1"]  = 0x70, ["f2"]  = 0x71, ["f3"]  = 0x72, ["f4"]  = 0x73,
            ["f5"]  = 0x74, ["f6"]  = 0x75, ["f7"]  = 0x76, ["f8"]  = 0x77,
            ["f9"]  = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        };

        public static void SendHotkey(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys)) return;

            var parts = keys.Split('+');
            var vks = new List<ushort>();

            foreach (var part in parts)
            {
                var token = part.Trim();
                if (_vkMap.TryGetValue(token, out ushort vk))
                {
                    vks.Add(vk);
                }
                else if (token.Length == 1)
                {
                    // Single character — use VkKeyScan
                    short scan = VkKeyScan(token[0]);
                    ushort baseVk = (ushort)(scan & 0xFF);
                    if (baseVk != 0xFF)
                        vks.Add(baseVk);
                }
            }

            if (vks.Count == 0) return;

            var inputs = new INPUT[vks.Count * 2];
            for (int i = 0; i < vks.Count; i++)
            {
                inputs[i] = MakeKeyInput(vks[i], false);
                inputs[vks.Count * 2 - 1 - i] = MakeKeyInput(vks[i], true);
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var inputs = new INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[i * 2]     = MakeUnicodeInput(text[i], false);
                inputs[i * 2 + 1] = MakeUnicodeInput(text[i], true);
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
                }
            }
        };

        private static INPUT MakeUnicodeInput(char c, bool keyUp) => new()
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0)
                }
            }
        };

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);
    }
}
