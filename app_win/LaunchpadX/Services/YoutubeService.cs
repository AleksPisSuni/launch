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
        /// Downloads the best M4A/AAC audio to a temp file and returns its path.
        /// Format 140 (M4A 128k) is always available on YouTube/YT Music and plays
        /// reliably with NAudio MediaFoundationReader from disk.
        /// Returns null if yt-dlp fails.
        /// </summary>
        public static async Task<string?> DownloadToTempAsync(
            string ytDlpPath, string youtubeUrl, CancellationToken ct = default)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"launchpadx_{Guid.NewGuid():N}.m4a");

            // Format 140 = YouTube M4A/AAC 128kbps — no ffmpeg needed, always available
            var args = $"--format \"140/bestaudio[ext=m4a]/bestaudio/best\" " +
                       $"--no-playlist --no-part " +
                       $"-o \"{tempFile}\" \"{youtubeUrl}\"";

            var psi = new ProcessStartInfo(ytDlpPath, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0 && File.Exists(tempFile))
                return tempFile;

            // Clean up on failure
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            return null;
        }
    }
}
