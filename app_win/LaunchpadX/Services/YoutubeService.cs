using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LaunchpadX.Services
{
    public static class YoutubeService
    {
        /// <summary>
        /// Finds yt-dlp.exe — checks next to the app exe first, then PATH.
        /// Returns null if not found.
        /// </summary>
        public static string? FindYtDlp()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
            if (File.Exists(local)) return local;

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), "yt-dlp.exe");
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Runs yt-dlp to extract a direct audio stream URL from a YouTube/YT Music URL.
        /// Prefers M4A (AAC) for best NAudio compatibility.
        /// Returns null if extraction fails.
        /// </summary>
        public static async Task<string?> GetAudioUrlAsync(
            string ytDlpPath, string youtubeUrl, CancellationToken ct = default)
        {
            // Format priority: M4A/AAC variants first (MediaFoundationReader compatible on Windows),
            // then fall back to anything available.
            // Format IDs: 140=M4A 128k, 141=M4A 256k, 250/251=WebM Opus (not natively supported).
            const string fmt = "140/141/bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio/best";
            var psi = new ProcessStartInfo(
                ytDlpPath,
                $"--get-url --format \"{fmt}\" --no-playlist \"{youtubeUrl}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            // yt-dlp may output multiple lines (DASH streams) — take the first HTTP URL
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }
            return null;
        }
    }
}
