using System;
using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;

namespace LaunchpadMapper.Services
{
    // Simple loop stream wrapper
    public class LoopStream : WaveStream
    {
        private readonly WaveStream _sourceStream;

        public LoopStream(WaveStream sourceStream)
        {
            _sourceStream = sourceStream;
            EnableLooping = true;
        }

        public bool EnableLooping { get; set; }

        public override WaveFormat WaveFormat => _sourceStream.WaveFormat;

        public override long Length => _sourceStream.Length;

        public override long Position
        {
            get => _sourceStream.Position;
            set => _sourceStream.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = _sourceStream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0)
                {
                    if (_sourceStream.Position == 0 || !EnableLooping)
                    {
                        break;
                    }
                    _sourceStream.Position = 0;
                }
                totalRead += read;
            }
            return totalRead;
        }
    }

    public class PlaybackService : IDisposable
    {
        // Keyed by logical id (we use note number as string)
        private readonly ConcurrentDictionary<string, (IWavePlayer player, WaveStream stream)> _players = new();

        public event Action<string>? PlaybackStarted;
        public event Action<string>? PlaybackStopped;

        public bool IsPlaying(string id)
        {
            return _players.ContainsKey(id);
        }

        public void PlayKey(string id, string path, bool loop = false, double volume = 1.0)
        {
            if (!File.Exists(path))
            {
                // If sample is missing, create a tiny silent WAV so users can test mappings without shipping audio files.
                try { CreateSilentWav(path, 0.25); } catch { }
            }
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            StopKey(id);

            WaveStream source = CreateReader(path);
            WaveStream stream = source;
            if (loop)
            {
                stream = new LoopStream(source);
            }

            // Apply volume. Prefer AudioFileReader's built-in Volume; otherwise wrap with WaveChannel32.
            float vol = (float)Math.Max(0.0, Math.Min(1.0, volume));
            if (source is AudioFileReader afr)
            {
                try { afr.Volume = vol; } catch { }
            }
            else
            {
                try
                {
                    // Wrap current stream in WaveChannel32 to control volume
                    var vc = new WaveChannel32(stream) { Volume = vol };
                    stream = vc;
                }
                catch { }
            }

            var wo = new WaveOutEvent();
            wo.Init(stream);
            // Hook stop to cleanup + event
            wo.PlaybackStopped += (s, e) =>
            {
                if (_players.TryRemove(id, out var v))
                {
                    try { v.player.Dispose(); } catch { }
                    try { v.stream.Dispose(); } catch { }
                }
                try { PlaybackStopped?.Invoke(id); } catch { }
            };
            _players[id] = (wo, stream);
            wo.Play();
            try { PlaybackStarted?.Invoke(id); } catch { }
        }

        private WaveStream CreateReader(string path)
        {
            // Prefer AudioFileReader (MediaFoundation) for broad format support (wav/mp3/wma/m4a/aac).
            try
            {
                return new AudioFileReader(path);
            }
            catch
            {
                // Fallbacks by extension
                var ext = Path.GetExtension(path)?.ToLowerInvariant();
                try
                {
                    if (ext == ".aif" || ext == ".aiff") return new AiffFileReader(path);
                    if (ext == ".wav") return new WaveFileReader(path);
                }
                catch { }
                // Last resort: rethrow original by trying AudioFileReader again to surface meaningful error
                return new AudioFileReader(path);
            }
        }

        public void StopKey(string id)
        {
            if (_players.TryRemove(id, out var v))
            {
                try { v.player.Stop(); } catch { }
                try { v.player.Dispose(); } catch { }
                try { v.stream.Dispose(); } catch { }
                try { PlaybackStopped?.Invoke(id); } catch { }
            }
        }

        public void Dispose()
        {
            foreach (var k in _players.Keys)
            {
                StopKey(k);
            }
        }

        private void CreateSilentWav(string path, double seconds)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            const int sampleRate = 22050;
            const short bitsPerSample = 16;
            const short channels = 1;
            int bytesPerSample = bitsPerSample / 8;
            int totalSamples = (int)(sampleRate * seconds);
            int dataSize = totalSamples * bytesPerSample * channels;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // RIFF header
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * channels * bytesPerSample);
            bw.Write((short)(channels * bytesPerSample));
            bw.Write(bitsPerSample);

            // data chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);

            // write silence
            for (int i = 0; i < totalSamples; i++)
            {
                bw.Write((short)0);
            }
            bw.Flush();
        }
    }
}
