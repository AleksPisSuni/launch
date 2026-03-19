using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LaunchpadMapper.Services
{
    // ElevenLabs TTS provider (REST). Produces MP3 files by default and caches them.
    public class ElevenLabsTtsService : ITtsService
    {
        private static readonly HttpClient _http = new HttpClient();
        private readonly string _apiKey;
        private string _voiceId;
        private readonly string _cacheDir;

        // Use mp3_44100_128 for broad compatibility with NAudio
        private const string OutputFormat = "mp3_44100_128";
        private const string ModelId = "eleven_multilingual_v2";

        public ElevenLabsTtsService(string apiKey, string? voiceId)
        {
            _apiKey = apiKey ?? string.Empty;
            _voiceId = string.IsNullOrWhiteSpace(voiceId) ? "21m00Tcm4TlvDq8ikWAM" /* default-ish */ : voiceId!;
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchpadMapper", "tts_cache");
            Directory.CreateDirectory(baseDir);
            _cacheDir = baseDir;
        }

        public void SetVoiceById(string? voiceId)
        {
            if (!string.IsNullOrWhiteSpace(voiceId)) _voiceId = voiceId!;
        }

        public async Task<string?> SynthesizeToFileAsync(string id, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_voiceId)) return null;
            try
            {
                string hash = HashToString(_voiceId + "\n" + text + "\n" + OutputFormat);
                string cachePath = Path.Combine(_cacheDir, hash + ".mp3");
                if (File.Exists(cachePath)) return cachePath;

                var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);
                req.Headers.TryAddWithoutValidation("Accept", "audio/mpeg");
                var payload = new
                {
                    text = text,
                    model_id = ModelId,
                    voice_settings = new { stability = 0.5, similarity_boost = 0.75 },
                    output_format = OutputFormat
                };
                var json = JsonSerializer.Serialize(payload);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                return cachePath;
            }
            catch
            {
                return null;
            }
        }

        private static string HashToString(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public void Dispose() { }
    }
}
