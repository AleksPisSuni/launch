using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;

namespace LaunchpadX.Services
{
    public class NoteEventArgs : EventArgs
    {
        public int Note { get; }
        public int Velocity { get; }
        public int Channel { get; }
        public NoteEventArgs(int note, int velocity, int channel)
        {
            Note = note;
            Velocity = velocity;
            Channel = channel;
        }
    }

    public class MidiService : IDisposable
    {
        private MidiIn? _midiIn;
        private MidiOut? _midiOut;

        public string? InputName { get; private set; }
        public string? OutputName { get; private set; }
        public bool IsConnected => _midiIn != null && _midiOut != null;

        public event EventHandler<NoteEventArgs>? NoteOn;
        public event EventHandler<NoteEventArgs>? NoteOff;
        public event EventHandler<string>? Log;

        // Finds all available MIDI device names for display/debugging
        public static (string[] inputs, string[] outputs) ListDevices()
        {
            var ins = new string[MidiIn.NumberOfDevices];
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                ins[i] = MidiIn.DeviceInfo(i).ProductName;

            var outs = new string[MidiOut.NumberOfDevices];
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
                outs[i] = MidiOut.DeviceInfo(i).ProductName;

            return (ins, outs);
        }

        // Connect to Launchpad X — opens the best matching input and output ports
        // Silent version used by auto-reconnect timer — no log output on failure
        public bool ConnectSilent()
        {
            try
            {
                int inIdx  = FindInputPort();
                int outIdx = FindOutputPort();
                if (inIdx < 0 || outIdx < 0) return false;
                return Connect();
            }
            catch { return false; }
        }

        public bool Connect()
        {
            Disconnect();

            int inIdx  = FindInputPort();
            int outIdx = FindOutputPort();

            if (inIdx < 0)  { Emit("No Launchpad X input port found.");  return false; }
            if (outIdx < 0) { Emit("No Launchpad X output port found."); return false; }

            InputName  = MidiIn.DeviceInfo(inIdx).ProductName;
            OutputName = MidiOut.DeviceInfo(outIdx).ProductName;

            _midiIn = new MidiIn(inIdx);
            _midiIn.MessageReceived += OnMidiMessage;
            _midiIn.ErrorReceived   += (_, _) => Emit("MIDI receive error.");
            _midiIn.Start();

            _midiOut = new MidiOut(outIdx);

            Emit($"IN  → {InputName}");
            Emit($"OUT → {OutputName}");
            return true;
        }

        // Enter Launchpad X Programmer Layout
        // SysEx: F0 00 20 29 02 0C 0E 01 F7
        // In Programmer Layout the host controls all LEDs via SysEx and
        // the pads report NoteOn/NoteOff on channel 1 (0-indexed: ch 0).
        public void SetProgrammerLayout()
        {
            if (_midiOut == null) return;
            byte[] sysex = { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0C, 0x0E, 0x01, 0xF7 };
            _midiOut.SendBuffer(sysex);
            Emit("Programmer layout set.");
        }

        // Set a single pad RGB colour by its LED ID (Programmer Layout note number)
        // LED ID = 11 + row * 10 + col  (row 0 = bottom, col 0 = left)
        // r, g, b: 0-255  (scaled internally to 7-bit 0-127 for the SysEx payload)
        public void SetPadColor(int ledId, byte r, byte g, byte b)
        {
            if (_midiOut == null) return;
            byte id7 = (byte)(ledId & 0x7F);
            byte r7  = (byte)(r * 127 / 255);
            byte g7  = (byte)(g * 127 / 255);
            byte b7  = (byte)(b * 127 / 255);
            // SysEx: F0 00 20 29 02 0C 03 03 <led> <r> <g> <b> F7
            byte[] sysex = { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0C, 0x03, 0x03, id7, r7, g7, b7, 0xF7 };
            _midiOut.SendBuffer(sysex);
        }

        // Set multiple pad colours in a single SysEx message
        // Much more reliable at startup than sending one-by-one
        public void SetMultiplePadColors(IEnumerable<(int ledId, byte r, byte g, byte b)> pads)
        {
            if (_midiOut == null) return;
            var list = pads.ToList();
            if (list.Count == 0) return;

            // F0 00 20 29 02 0C 03 [03 led r g b]+ F7
            // Type byte 03 (RGB) must precede each LED entry
            var msg = new List<byte> { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0C, 0x03 };
            foreach (var (ledId, r, g, b) in list)
            {
                msg.Add(0x03); // type: RGB
                msg.Add((byte)(ledId & 0x7F));
                msg.Add((byte)(r * 127 / 255));
                msg.Add((byte)(g * 127 / 255));
                msg.Add((byte)(b * 127 / 255));
            }
            msg.Add(0xF7);
            _midiOut.SendBuffer(msg.ToArray());
        }

        // Turn off all pads
        // SysEx: F0 00 20 29 02 0C 0E 01 F7  (re-entering Programmer Layout resets all LEDs)
        public void ClearAllPads()
        {
            SetProgrammerLayout();
        }

        public void Disconnect()
        {
            if (_midiIn != null)
            {
                _midiIn.Stop();
                _midiIn.MessageReceived -= OnMidiMessage;
                _midiIn.Dispose();
                _midiIn = null;
            }
            if (_midiOut != null)
            {
                _midiOut.Dispose();
                _midiOut = null;
            }
            InputName = null;
            OutputName = null;
        }

        public void Dispose() => Disconnect();

        // ----- private helpers -----

        private void OnMidiMessage(object? sender, MidiInMessageEventArgs e)
        {
            try
            {
                var ev = MidiEvent.FromRawMessage(e.RawMessage);
                if (ev is NoteOnEvent noteOn)
                {
                    if (noteOn.Velocity > 0)
                        NoteOn?.Invoke(this, new NoteEventArgs(noteOn.NoteNumber, noteOn.Velocity, noteOn.Channel));
                    else
                        NoteOff?.Invoke(this, new NoteEventArgs(noteOn.NoteNumber, 0, noteOn.Channel));
                }
                else if (ev is NoteEvent noteOff && noteOff.CommandCode == MidiCommandCode.NoteOff)
                {
                    NoteOff?.Invoke(this, new NoteEventArgs(noteOff.NoteNumber, 0, noteOff.Channel));
                }
            }
            catch { }
        }

        // Prefer the main "Launchpad X" input (used in Programmer Layout).
        // Fall back to the utility "LPX MIDI" port.
        private static int FindInputPort()
        {
            int lpx = -1, lpxMidi = -1, any = -1;
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                string name = MidiIn.DeviceInfo(i).ProductName ?? "";
                if (IsMainLpxPort(name))      { lpx = i;     break; }
                if (IsUtilityLpxPort(name))   { lpxMidi = i; }
                if (any < 0 && IsLaunchpad(name)) any = i;
            }
            return lpx >= 0 ? lpx : lpxMidi >= 0 ? lpxMidi : any;
        }

        // Prefer the main "Launchpad X" output for SysEx Programmer Layout control.
        // Fall back to the utility port.
        private static int FindOutputPort()
        {
            int lpx = -1, lpxMidi = -1, any = -1;
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                string name = MidiOut.DeviceInfo(i).ProductName ?? "";
                if (IsMainLpxPort(name))      { lpx = i;     break; }
                if (IsUtilityLpxPort(name))   { lpxMidi = i; }
                if (any < 0 && IsLaunchpad(name)) any = i;
            }
            return lpx >= 0 ? lpx : lpxMidi >= 0 ? lpxMidi : any;
        }

        // "Launchpad X" — the main port (SysEx, Programmer Layout)
        private static bool IsMainLpxPort(string name) =>
            name.IndexOf("launchpad x", StringComparison.OrdinalIgnoreCase) >= 0 &&
            name.IndexOf("midi",        StringComparison.OrdinalIgnoreCase) < 0;

        // "MIDIIN2 (LPX MIDI)" / "MIDIOUT2 (LPX MIDI)" — utility port
        private static bool IsUtilityLpxPort(string name) =>
            name.IndexOf("lpx",  StringComparison.OrdinalIgnoreCase) >= 0 &&
            name.IndexOf("midi", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsLaunchpad(string name) =>
            name.IndexOf("launchpad", StringComparison.OrdinalIgnoreCase) >= 0;

        private void Emit(string msg) => Log?.Invoke(this, msg);
    }
}
