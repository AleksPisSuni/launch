using System;
using NAudio.Midi;

namespace LaunchpadX.Services
{
    public class MidiOutputService : IDisposable
    {
        private MidiOut? _out;

        public bool   IsOpen     => _out != null;
        public string DeviceName { get; private set; } = "";

        public static string[] GetDeviceNames()
        {
            var names = new string[MidiOut.NumberOfDevices];
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
                names[i] = MidiOut.DeviceInfo(i).ProductName;
            return names;
        }

        public bool Open(string deviceName)
        {
            Close();
            if (string.IsNullOrWhiteSpace(deviceName)) return false;
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                if (string.Equals(MidiOut.DeviceInfo(i).ProductName, deviceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _out       = new MidiOut(i);
                        DeviceName = deviceName;
                        return true;
                    }
                    catch { return false; }
                }
            }
            return false;
        }

        public void SendNoteOn(int channel, int note, int velocity)
        {
            if (_out == null) return;
            try { _out.Send(MidiMessage.StartNote(note & 0x7F, velocity & 0x7F, channel).RawData); }
            catch { }
        }

        public void SendNoteOff(int channel, int note)
        {
            if (_out == null) return;
            try { _out.Send(MidiMessage.StopNote(note & 0x7F, 0, channel).RawData); }
            catch { }
        }

        public void Close()
        {
            _out?.Dispose();
            _out = null;
            DeviceName = "";
        }

        public void Dispose() => Close();
    }
}
