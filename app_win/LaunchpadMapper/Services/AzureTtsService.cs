using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace LaunchpadMapper.Services
{
    // Azure Cognitive Services TTS (REST). Produces a WAV to a cache/temp folder.
    public class AzureTtsService : ITtsService
    {
        private static readonly HttpClient _http = new HttpClient();
        private readonly string _key;
        private readonly string _region;
        private string _voiceName;
        private readonly string _cacheDir;

        // output format: 24kHz mono PCM WAV
        private const string OutputFormat = "riff-24khz-16bit-mono-pcm";

        public AzureTtsService(string key, string region, string? voiceName)
        {
            _key = key ?? string.Empty;
            _region = region ?? string.Empty;
            _voiceName = string.IsNullOrWhiteSpace(voiceName) ? "en-US-JennyNeural" : voiceName!;
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchpadMapper", "tts_cache");
            Directory.CreateDirectory(baseDir);
            _cacheDir = baseDir;
        }

        public void SetVoiceById(string? voiceId)
        {
            // For Azure, we treat VoiceId as the voice short name if provided via settings.
            if (!string.IsNullOrWhiteSpace(voiceId)) _voiceName = voiceId!;
        }

        public async Task<string?> SynthesizeToFileAsync(string id, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (string.IsNullOrWhiteSpace(_key) || string.IsNullOrWhiteSpace(_region)) return null;
            try
            {
                // cache key: voice + text
                string hash = HashToString(_voiceName + "\n" + text);
                string cachePath = Path.Combine(_cacheDir, hash + ".wav");
                if (File.Exists(cachePath)) return cachePath;

                var ssml = BuildSsml(_voiceName, text);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{_region}.tts.speech.microsoft.com/cognitiveservices/v1");
                req.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _key);
                req.Headers.TryAddWithoutValidation("User-Agent", "LaunchpadMapper/1.0");
                req.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", OutputFormat);
                req.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
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

        private static string BuildSsml(string voice, string text)
        {
            string escaped = System.Security.SecurityElement.Escape(text) ?? string.Empty;
            return $"<speak version=\"1.0\" xml:lang=\"en-US\"><voice name=\"{voice}\">{escaped}</voice></speak>";
        }

        private static string HashToString(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public void Dispose()
        {
            // HttpClient is static; nothing to dispose
        }
    }
}
