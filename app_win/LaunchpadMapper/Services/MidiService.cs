using System;
using NAudio.Midi;

namespace LaunchpadMapper.Services
{
    public class NoteEventArgs : EventArgs
    {
        public int Note { get; set; }
        public int Velocity { get; set; }
        // MIDI channel (0..15) the event was received on
        public int Channel { get; set; }
    }

    public class MidiService : IDisposable
    {
        private MidiIn? _midiIn;
        private MidiOut? _midiOut;
        // raw MIDI message bytes for diagnostics
        public event EventHandler<byte[]?>? RawMessageReceived;
        // outgoing MIDI bytes (logged when we send messages)
        public event EventHandler<byte[]?>? OutgoingMessageSent;

        public event EventHandler<NoteEventArgs>? NoteOn;
        public event EventHandler<NoteEventArgs>? NoteOff;

        public string? OpenInputName { get; private set; }
        public string? OpenOutputName { get; private set; }

        public void OpenInputByName(string name)
        {
            CloseInput();
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                var info = MidiIn.DeviceInfo(i);
                if (info.ProductName == name || info.ProductName.Contains(name))
                {
                    _midiIn = new MidiIn(i);
                    _midiIn.MessageReceived += MidiIn_MessageReceived;
                    _midiIn.ErrorReceived += MidiIn_ErrorReceived;
                    _midiIn.Start();
                    OpenInputName = info.ProductName;
                    return;
                }
            }
            throw new InvalidOperationException($"MIDI input '{name}' not found.");
        }

        public void OpenOutputByName(string name)
        {
            CloseOutput();
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                var info = MidiOut.DeviceInfo(i);
                if (info.ProductName == name || info.ProductName.Contains(name))
                {
                    _midiOut = new MidiOut(i);
                    OpenOutputName = info.ProductName;
                    return;
                }
            }
            throw new InvalidOperationException($"MIDI output '{name}' not found.");
        }

        private void MidiIn_ErrorReceived(object? sender, MidiInMessageEventArgs e)
        {
            // swallow or log externally
        }

        private void MidiIn_MessageReceived(object? sender, MidiInMessageEventArgs e)
        {
            try
            {
                // Extract raw MIDI short message bytes (status, data1, data2)
                int raw = (int)e.RawMessage;
                byte status = (byte)(raw & 0xFF);
                byte data1 = (byte)((raw >> 8) & 0xFF);
                byte data2 = (byte)((raw >> 16) & 0xFF);

                // Forward raw bytes for diagnostics in status-note-velocity order
                try { RawMessageReceived?.Invoke(this, new byte[] { status, data1, data2, 0x00 }); } catch { RawMessageReceived?.Invoke(this, null); }

                int command = status & 0xF0;
                int channel = status & 0x0F;

                // Robust handling: treat Note On with velocity 0 as Note Off
                if (command == 0x90)
                {
                    if (data2 == 0)
                    {
                        NoteOff?.Invoke(this, new NoteEventArgs { Note = data1, Velocity = 0, Channel = channel });
                    }
                    else
                    {
                        NoteOn?.Invoke(this, new NoteEventArgs { Note = data1, Velocity = data2, Channel = channel });
                    }
                    return;
                }
                else if (command == 0x80)
                {
                    NoteOff?.Invoke(this, new NoteEventArgs { Note = data1, Velocity = data2, Channel = channel });
                    return;
                }

                // Fallback to NAudio parser for any other messages we care about
                var midiEvent = MidiEvent.FromRawMessage(raw);
                if (midiEvent is NoteOnEvent noteOn)
                {
                    if (noteOn.Velocity == 0)
                        NoteOff?.Invoke(this, new NoteEventArgs { Note = noteOn.NoteNumber, Velocity = 0, Channel = noteOn.Channel });
                    else
                        NoteOn?.Invoke(this, new NoteEventArgs { Note = noteOn.NoteNumber, Velocity = noteOn.Velocity, Channel = noteOn.Channel });
                }
                else if (midiEvent is NoteEvent noteEvent && noteEvent.CommandCode == MidiCommandCode.NoteOff)
                {
                    NoteOff?.Invoke(this, new NoteEventArgs { Note = noteEvent.NoteNumber, Velocity = noteEvent.Velocity, Channel = noteEvent.Channel });
                }
            }
            catch (Exception)
            {
                // ignore parse errors
            }
        }

        public void CloseInput()
        {
            if (_midiIn != null)
            {
                _midiIn.Stop();
                _midiIn.MessageReceived -= MidiIn_MessageReceived;
                _midiIn.Dispose();
                _midiIn = null;
                OpenInputName = null;
            }
        }

        public void CloseOutput()
        {
            if (_midiOut != null)
            {
                _midiOut.Dispose();
                _midiOut = null;
                OpenOutputName = null;
            }
        }

        public void SetPadColor(int note, int velocity)
        {
            try
            {
                if (_midiOut == null) return;
                // Create a Note On event on channel 0
                var ev = new NoteOnEvent(0, 0, note, velocity, 0);
                _midiOut.Send(ev.GetAsShortMessage());
                // Also notify listeners of the raw bytes sent (status, note, velocity)
                try
                {
                    byte status = (byte)(0x90 | (ev.Channel & 0x0F));
                    var outBytes = new byte[] { status, (byte)note, (byte)velocity };
                    OutgoingMessageSent?.Invoke(this, outBytes);
                }
                catch { }
            }
            catch
            {
                // ignore
            }
        }

        // Send NoteOn/NoteOff on a specific MIDI channel (0..15)
        public void SetPadColorOnChannel(int note, int velocity, int channel)
        {
            try
            {
                if (_midiOut == null) return;
                var ch = Math.Max(0, Math.Min(15, channel));
                var ev = new NoteOnEvent(0, ch, note, velocity, 0);
                _midiOut.Send(ev.GetAsShortMessage());
                try
                {
                    byte status = (byte)(0x90 | (ch & 0x0F));
                    var outBytes = new byte[] { status, (byte)note, (byte)velocity };
                    OutgoingMessageSent?.Invoke(this, outBytes);
                }
                catch { }
            }
            catch { }
        }

        // Attempt to set RGB color on devices that support SysEx (Launchpad X). This is a best-effort fallback
        // that currently maps to velocity-based colors if SysEx support is not implemented by device.
        public void SetPadRgb(int note, byte r, byte g, byte b)
        {
            try
            {
                if (_midiOut == null) return;
                // TODO: Implement Launchpad X SysEx RGB messages when exact spec is desired.
                // For now fall back to a velocity-based palette approximation.
                int vel = 21; // default
                // crude mapping: more red -> higher small velocities
                if (r > g && r > b) vel = 5;          // red
                else if (g > r && g > b) vel = 21;    // green
                else if (b > r && b > g) vel = 45;    // blue
                else if (r > 200 && g > 200) vel = 13; // yellow/orange
                var ev = new NoteOnEvent(0, 0, note, vel, 0);
                _midiOut.Send(ev.GetAsShortMessage());
            }
            catch { }
        }

        // Experimental: Send SysEx RGB for Launchpad X devices.
        // Warning: exact SysEx formats vary; this is best-effort and may not work on all firmware.
        public void SetPadRgbSysEx(int note, byte r, byte g, byte b)
        {
            try
            {
                if (_midiOut == null) return;
                // Novation manufacturer ID is 00 20 29. We'll send a small payload:
                // F0 00 20 29 02 10 <note> <r> <g> <b> F7
                // MIDI SysEx data bytes must be 7-bit (0..127). Map bytes into 0..127 safely.
                byte note7 = (byte)(note & 0x7F);
                byte r7 = (byte)Math.Min(127, (r * 127) / 255);
                byte g7 = (byte)Math.Min(127, (g * 127) / 255);
                byte b7 = (byte)Math.Min(127, (b * 127) / 255);
                var buf = new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x10, note7, r7, g7, b7, 0xF7 };
                try
                {
                    _midiOut.SendBuffer(buf);
                    OutgoingMessageSent?.Invoke(this, buf);
                }
                catch
                {
                    // Fallback: send velocity-mapped color if SendBuffer not supported
                    SetPadRgb(note, r, g, b);
                }
            }
            catch { }
        }

        // Launchpad X/MK3 family: Enter/Exit Programmer Mode
        // F0 00 20 29 02 0C 0E <01=enter,00=exit> F7
        public void EnterProgrammerMode(bool enable)
        {
            try
            {
                if (_midiOut == null) return;
                byte mode = enable ? (byte)0x01 : (byte)0x00;
                var buf = new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0C, 0x0E, mode, 0xF7 };
                _midiOut.SendBuffer(buf);
                OutgoingMessageSent?.Invoke(this, buf);
            }
            catch { }
        }

        // Launchpad X/MK3 family: Set single pad RGB by LED ID (often equals note in Programmer Mode)
        // F0 00 20 29 02 0C 0B <id> <r> <g> <b> F7
        public void SetPadRgbLaunchpadX(int id, byte r, byte g, byte b)
        {
            try
            {
                if (_midiOut == null) return;
                byte id7 = (byte)(id & 0x7F);
                byte r7 = (byte)Math.Min(127, (r * 127) / 255);
                byte g7 = (byte)Math.Min(127, (g * 127) / 255);
                byte b7 = (byte)Math.Min(127, (b * 127) / 255);
                var buf = new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0C, 0x0B, id7, r7, g7, b7, 0xF7 };
                _midiOut.SendBuffer(buf);
                OutgoingMessageSent?.Invoke(this, buf);
            }
            catch { }
        }

        public void Dispose()
        {
            CloseInput();
            CloseOutput();
        }
    }
}
