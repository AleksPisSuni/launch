using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        /// Returns the persistent cache directory for downloaded audio files.
        /// Created on first call.
        /// </summary>
        public static string GetCacheDir()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LaunchpadX", "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Returns the cache file path for a given URL (keyed by MD5 hash of the URL).
        /// </summary>
        public static string GetCachePath(string url)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(url));
            var hex  = Convert.ToHexString(hash).ToLowerInvariant();
            return Path.Combine(GetCacheDir(), hex + ".m4a");
        }

        /// <summary>
        /// Downloads the best M4A/AAC audio to the persistent cache and returns its path.
        /// If the file is already cached, returns immediately without downloading.
        /// Format 140 (M4A 128k) is always available on YouTube/YT Music and plays
        /// reliably with NAudio MediaFoundationReader from disk.
        /// Returns null if yt-dlp fails.
        /// </summary>
        public static async Task<string?> DownloadToCacheAsync(
            string ytDlpPath, string youtubeUrl, CancellationToken ct = default)
        {
            var cachePath = GetCachePath(youtubeUrl);

            // Already cached — play immediately
            if (File.Exists(cachePath))
                return cachePath;

            // Download to a .tmp file first, then move atomically
            var tmpPath = cachePath + ".tmp";
            try { File.Delete(tmpPath); } catch { }

            // Format 140 = YouTube M4A/AAC 128kbps — no ffmpeg needed, always available
            var args = $"--format \"140/bestaudio[ext=m4a]/bestaudio/best\" " +
                       $"--no-playlist --no-part " +
                       $"-o \"{tmpPath}\" \"{youtubeUrl}\"";

            var psi = new ProcessStartInfo(ytDlpPath, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0 && File.Exists(tmpPath))
            {
                File.Move(tmpPath, cachePath, overwrite: true);
                return cachePath;
            }

            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            return null;
        }
    }
}
