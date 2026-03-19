using System;
using System.Collections.Generic;
using NAudio.Midi;

namespace LaunchpadMapper.Services
{
    // Opens all available MIDI input ports and forwards raw messages and parsed note events.
    public class ParallelMidiListener : IDisposable
    {
        private List<MidiIn> _instances = new();
        public List<string> OpenedPorts { get; } = new();
        public List<string> LastOpenErrors { get; } = new();

        public event EventHandler<byte[]?>? RawMessageReceived;
        public event EventHandler<NoteEventArgs>? NoteOn;
        public event EventHandler<NoteEventArgs>? NoteOff;

        public void Start()
        {
            Stop();
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                try
                {
                    var mi = new MidiIn(i);
                    mi.MessageReceived += Mi_MessageReceived;
                    mi.ErrorReceived += (s, e) => { /* swallow */ };
                    mi.Start();
                    _instances.Add(mi);
                    OpenedPorts.Add(MidiIn.DeviceInfo(i).ProductName);
                }
                catch
                {
                    // record failures opening some ports
                    try
                    {
                        LastOpenErrors.Add($"Failed to open MIDI in port {i}: {MidiIn.DeviceInfo(i).ProductName}");
                    }
                    catch { LastOpenErrors.Add($"Failed to open MIDI in port {i}: unknown"); }
                }
            }
        }

        private void Mi_MessageReceived(object? sender, MidiInMessageEventArgs e)
        {
            try
            {
                var bytes = BitConverter.GetBytes((int)e.RawMessage);
                RawMessageReceived?.Invoke(this, bytes);

                var me = MidiEvent.FromRawMessage((int)e.RawMessage);
                if (me is NoteOnEvent no)
                {
                    if (no.Velocity == 0)
                        NoteOff?.Invoke(this, new NoteEventArgs { Note = no.NoteNumber, Velocity = 0 });
                    else
                        NoteOn?.Invoke(this, new NoteEventArgs { Note = no.NoteNumber, Velocity = no.Velocity });
                }
                else if (me is NoteEvent ne && ne.CommandCode == MidiCommandCode.NoteOff)
                {
                    NoteOff?.Invoke(this, new NoteEventArgs { Note = ne.NoteNumber, Velocity = ne.Velocity });
                }
            }
            catch
            {
                // ignore
            }
        }

        public void Stop()
        {
            foreach (var mi in _instances)
            {
                try
                {
                    mi.Stop();
                    mi.MessageReceived -= Mi_MessageReceived;
                    mi.Dispose();
                }
                catch { }
            }
            _instances.Clear();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
