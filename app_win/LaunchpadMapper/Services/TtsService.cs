using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace LaunchpadMapper.Services
{
    // Windows 10+ TTS using WinRT SpeechSynthesizer.
    // Produces a temporary WAV file path for playback.
    public class TtsService : ITtsService
    {
        private readonly SpeechSynthesizer _synth = new SpeechSynthesizer();
        private string? _voiceId;

        public async Task<string?> SynthesizeToFileAsync(string id, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                // Apply selected voice if set
                if (!string.IsNullOrEmpty(_voiceId))
                {
                    try
                    {
                        foreach (var v in SpeechSynthesizer.AllVoices)
                        {
                            if (string.Equals(v.Id, _voiceId, StringComparison.OrdinalIgnoreCase))
                            {
                                _synth.Voice = v;
                                break;
                            }
                        }
                    }
                    catch { }
                }
                using SpeechSynthesisStream stream = await _synth.SynthesizeTextToStreamAsync(text);
                string file = Path.Combine(Path.GetTempPath(), $"lp_tts_{SanitizeId(id)}_{Guid.NewGuid():N}.wav");
                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var reader = new DataReader(stream);
                uint toLoad = (uint)Math.Min(int.MaxValue, stream.Size);
                await reader.LoadAsync(toLoad);
                byte[] data = new byte[toLoad];
                reader.ReadBytes(data);
                await fs.WriteAsync(data, 0, data.Length);
                await fs.FlushAsync();
                return file;
            }
            catch
            {
                return null;
            }
        }

        public void SetVoiceById(string? voiceId)
        {
            _voiceId = voiceId;
            if (string.IsNullOrEmpty(voiceId)) return;
            try
            {
                foreach (var v in SpeechSynthesizer.AllVoices)
                {
                    if (string.Equals(v.Id, voiceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _synth.Voice = v;
                        break;
                    }
                }
            }
            catch { }
        }

        private static string SanitizeId(string id)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) id = id.Replace(ch, '_');
            return id;
        }

        public void Dispose()
        {
            try { _synth.Dispose(); } catch { }
        }
    }
}
