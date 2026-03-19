using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace LaunchpadX.Services
{
    public class PlaybackService : IDisposable
    {
        private readonly Dictionary<int, (WaveOutEvent player, AudioFileReader reader)> _active = new();
        private readonly object _lock = new();

        public int DeviceNumber { get; set; } = -1;

        // Fired when a sound stops — either naturally (non-loop) or via Stop/FadeAndStop
        public event Action<int>? PlaybackEnded;

        public void Play(int note, string path, bool loop, float volume, bool stopOnRetrigger, bool fadeOnRetrigger = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            lock (_lock)
            {
                if (_active.TryGetValue(note, out var existing))
                {
                    if (stopOnRetrigger)
                    {
                        if (fadeOnRetrigger)
                        {
                            // FadeAndStop acquires the lock itself — release first
                            Monitor.Exit(_lock);
                            try { FadeAndStop(note); }
                            finally { Monitor.Enter(_lock); }
                        }
                        else
                        {
                            existing.player.Stop();
                            existing.player.Dispose();
                            existing.reader.Dispose();
                            _active.Remove(note);
                            Monitor.Exit(_lock);
                            try { PlaybackEnded?.Invoke(note); }
                            finally { Monitor.Enter(_lock); }
                        }
                        return;
                    }
                    else
                    {
                        return; // already playing, don't retrigger
                    }
                }

                try
                {
                    var reader = new AudioFileReader(path) { Volume = volume };
                    var player = new WaveOutEvent { DeviceNumber = DeviceNumber };

                    if (loop)
                    {
                        var loopStream = new LoopStream(reader);
                        player.Init(loopStream);
                    }
                    else
                    {
                        player.Init(reader);
                        player.PlaybackStopped += (_, _) =>
                        {
                            Action? onEnded = null;
                            lock (_lock)
                            {
                                if (_active.TryGetValue(note, out var entry))
                                {
                                    entry.player.Dispose();
                                    entry.reader.Dispose();
                                    _active.Remove(note);
                                }
                                onEnded = () => PlaybackEnded?.Invoke(note);
                            }
                            onEnded?.Invoke();
                        };
                    }

                    player.Play();
                    _active[note] = (player, reader);
                }
                catch { }
            }
        }

        public void Stop(int note)
        {
            Action? onEnded = null;
            lock (_lock)
            {
                if (_active.TryGetValue(note, out var entry))
                {
                    entry.player.Stop();
                    entry.player.Dispose();
                    entry.reader.Dispose();
                    _active.Remove(note);
                    onEnded = () => PlaybackEnded?.Invoke(note);
                }
            }
            onEnded?.Invoke();
        }

        public void FadeAndStop(int note, int durationMs = 500)
        {
            AudioFileReader? reader;
            lock (_lock)
            {
                if (!_active.TryGetValue(note, out var entry)) return;
                reader = entry.reader;
            }

            float startVolume = reader.Volume;
            const int steps = 20;
            int stepDelay = durationMs / steps;

            Task.Run(async () =>
            {
                for (int i = 1; i <= steps; i++)
                {
                    await Task.Delay(stepDelay).ConfigureAwait(false);
                    try { reader.Volume = startVolume * (1f - (float)i / steps); }
                    catch { break; }
                }
                Stop(note);
            });
        }

        public bool IsPlaying(int note)
        {
            lock (_lock) { return _active.ContainsKey(note); }
        }

        public void StopAll()
        {
            List<int> notes;
            lock (_lock) { notes = new List<int>(_active.Keys); }
            foreach (var note in notes) Stop(note);
        }

        public void Dispose() => StopAll();
    }

    internal class LoopStream : WaveStream
    {
        private readonly WaveStream _source;
        public LoopStream(WaveStream source) => _source = source;
        public override WaveFormat WaveFormat => _source.WaveFormat;
        public override long Length => long.MaxValue;
        public override long Position { get => _source.Position; set => _source.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int chunk = _source.Read(buffer, offset + read, count - read);
                if (chunk == 0)
                {
                    _source.Position = 0;
                    chunk = _source.Read(buffer, offset + read, count - read);
                    if (chunk == 0) break;
                }
                read += chunk;
            }
            return read;
        }
    }
}
