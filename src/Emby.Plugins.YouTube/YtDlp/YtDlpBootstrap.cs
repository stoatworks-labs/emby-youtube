using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.YouTube.YtDlp
{
    /// <summary>
    /// Obtains and updates the yt-dlp binary.
    ///
    /// The official Emby Docker images ship neither yt-dlp nor Python, and anything installed into
    /// the container by hand is destroyed on the next image pull. So the plugin manages its own
    /// copy inside its data folder, which lives on the mounted config volume and therefore
    /// survives image rebuilds.
    ///
    /// The release assets used here are self-contained single-file builds with no Python runtime
    /// requirement, which is what makes this viable inside a stock Emby container.
    /// </summary>
    public class YtDlpBootstrap
    {
        private const string LatestDownloadBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
        private const string LatestReleaseApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public YtDlpBootstrap(HttpClient http, ILogger logger)
        {
            _http = http;
            _logger = logger;
        }

        /// <summary>Asset name for the current OS and architecture.</summary>
        private static string AssetName
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "yt-dlp.exe";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "yt-dlp_macos";

                // Linux: the aarch64 build matters for Unraid on ARM and for Synology/Pi hosts.
                switch (RuntimeInformation.OSArchitecture)
                {
                    case Architecture.Arm64: return "yt-dlp_linux_aarch64";
                    case Architecture.Arm: return "yt-dlp_linux_armv7l";
                    default: return "yt-dlp_linux";
                }
            }
        }

        public string ManagedBinaryPath => Path.Combine(Plugin.Instance.PluginDataPath, AssetName);

        /// <summary>
        /// Resolves the binary to run: an explicit override if configured, otherwise the managed
        /// copy, downloading it on first use.
        /// </summary>
        public async Task<string> GetBinaryPath(CancellationToken cancellationToken)
        {
            var configured = Plugin.Instance.Configuration.YtDlpPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured)) return configured;
                _logger.Warn("YouTube: configured yt-dlp path '{0}' does not exist; falling back to the managed copy.", configured);
            }

            var managed = ManagedBinaryPath;
            if (File.Exists(managed)) return managed;

            await Download(cancellationToken).ConfigureAwait(false);
            return File.Exists(managed) ? managed : null;
        }

        /// <summary>Downloads the latest build, replacing any existing managed copy.</summary>
        public async Task Download(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var target = ManagedBinaryPath;
                var url = LatestDownloadBase + AssetName;
                _logger.Info("YouTube: downloading yt-dlp from {0}", url);

                // Stage to a temp file so a failed or partial download never leaves a corrupt
                // binary in place that would then fail every resolve.
                var temp = target + ".tmp";
                using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                MakeExecutable(target);
                FixElfInterpreterIfNeeded(target);

                _logger.Info("YouTube: yt-dlp installed at {0}", target);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Updates only when GitHub reports a newer tag than the installed binary, so the scheduled
        /// task does not re-download ~30MB every night for nothing.
        /// </summary>
        public async Task UpdateIfNeeded(CancellationToken cancellationToken)
        {
            var target = ManagedBinaryPath;
            if (!File.Exists(target))
            {
                await Download(cancellationToken).ConfigureAwait(false);
                return;
            }

            string latestTag;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi))
                {
                    // GitHub's API rejects requests without a User-Agent.
                    request.Headers.UserAgent.ParseAdd("Emby.Plugins.YouTube");
                    using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        latestTag = Util.Json.Parse(body)["tag_name"].AsString;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: could not check for a yt-dlp update; keeping the installed copy.", ex);
                return;
            }

            if (string.IsNullOrEmpty(latestTag)) return;

            var installed = await GetInstalledVersion(target, cancellationToken).ConfigureAwait(false);
            if (string.Equals(installed, latestTag, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info("YouTube: yt-dlp is up to date ({0}).", installed);
                return;
            }

            _logger.Info("YouTube: updating yt-dlp {0} -> {1}.", installed ?? "unknown", latestTag);
            await Download(cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> GetInstalledVersion(string binaryPath, CancellationToken cancellationToken)
        {
            try
            {
                var result = await ProcessRunner.Run(binaryPath, new[] { "--version" }, TimeSpan.FromSeconds(30), _logger, cancellationToken)
                    .ConfigureAwait(false);
                return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: could not read the installed yt-dlp version.", ex);
                return null;
            }
        }

        /// <summary>
        /// Repoints the binary's ELF interpreter when the one it asks for is missing.
        ///
        /// Found the hard way on a real deployment: the official `emby/embyserver` image has no
        /// `/lib64` directory at all, but yt-dlp's release binaries hard-code
        /// `/lib64/ld-linux-x86-64.so.2` as their interpreter. The kernel cannot find the loader and
        /// the exec fails with a bare "not found" — which looks exactly like a missing file and
        /// sends you hunting in the wrong direction entirely.
        ///
        /// The loader itself is present, just at `/lib/ld-linux-x86-64.so.2`. Invoking it explicitly
        /// (`ld-linux… yt-dlp`) does not work: PyInstaller resolves its own executable path and then
        /// fails to find its embedded archive. Symlinking `/lib64` inside the container does work,
        /// but lives in the container's writable layer and is destroyed on every image update.
        ///
        /// So the fix is applied to our own copy of the binary, which lives on the persistent config
        /// volume. `/lib/…` is one byte shorter than `/lib64/…`, so the new path fits in the existing
        /// PT_INTERP slot and the segment size never changes — no offsets move.
        /// </summary>
        private void FixElfInterpreterIfNeeded(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (!TryFindInterpreter(stream, out var interpOffset, out var interpSize, out var current))
                        return;

                    if (string.IsNullOrEmpty(current) || File.Exists(current))
                        return; // The interpreter it wants is present; nothing to do.

                    var candidate = FindExistingLoader(current);
                    if (candidate == null)
                    {
                        _logger.Warn("YouTube: yt-dlp wants ELF interpreter '{0}', which is missing, and no replacement was found. It will probably fail to start.", current);
                        return;
                    }

                    // Needs room for the path plus its NUL terminator.
                    var bytes = Encoding.ASCII.GetBytes(candidate);
                    if (bytes.Length + 1 > interpSize)
                    {
                        _logger.Warn("YouTube: replacement interpreter '{0}' does not fit the existing slot; leaving the binary alone.", candidate);
                        return;
                    }

                    var buffer = new byte[interpSize];
                    Array.Copy(bytes, buffer, bytes.Length); // remainder stays zero-filled

                    stream.Position = interpOffset;
                    stream.Write(buffer, 0, buffer.Length);
                    stream.Flush();

                    _logger.Info("YouTube: repointed yt-dlp's ELF interpreter from '{0}' to '{1}'.", current, candidate);
                }
            }
            catch (Exception ex)
            {
                // Never fatal: on a normal host the interpreter is already correct.
                _logger.ErrorException("YouTube: could not inspect or patch the yt-dlp ELF interpreter.", ex);
            }
        }

        /// <summary>Locates the PT_INTERP segment of a little-endian ELF64 image.</summary>
        private static bool TryFindInterpreter(FileStream stream, out long offset, out int size, out string current)
        {
            const int PT_INTERP = 3;
            offset = 0;
            size = 0;
            current = null;

            var header = new byte[64];
            stream.Position = 0;
            if (stream.Read(header, 0, header.Length) != header.Length) return false;

            // Magic, then EI_CLASS == 2 (64-bit) and EI_DATA == 1 (little endian).
            if (header[0] != 0x7F || header[1] != 'E' || header[2] != 'L' || header[3] != 'F') return false;
            if (header[4] != 2 || header[5] != 1) return false;

            var phoff = BitConverter.ToInt64(header, 0x20);
            var phentsize = BitConverter.ToUInt16(header, 0x36);
            var phnum = BitConverter.ToUInt16(header, 0x38);
            if (phoff <= 0 || phentsize < 56 || phnum == 0) return false;

            var entry = new byte[phentsize];
            for (var i = 0; i < phnum; i++)
            {
                stream.Position = phoff + (long)i * phentsize;
                if (stream.Read(entry, 0, entry.Length) != entry.Length) return false;

                if (BitConverter.ToUInt32(entry, 0) != PT_INTERP) continue;

                var segmentOffset = BitConverter.ToInt64(entry, 0x08);
                var segmentSize = BitConverter.ToInt64(entry, 0x20);
                if (segmentSize <= 0 || segmentSize > 4096) return false;

                var raw = new byte[segmentSize];
                stream.Position = segmentOffset;
                if (stream.Read(raw, 0, raw.Length) != raw.Length) return false;

                var terminator = Array.IndexOf(raw, (byte)0);
                current = Encoding.ASCII.GetString(raw, 0, terminator < 0 ? raw.Length : terminator);
                offset = segmentOffset;
                size = (int)segmentSize;
                return true;
            }

            return false;
        }

        /// <summary>Looks for the same loader filename in the usual library directories.</summary>
        private static string FindExistingLoader(string missingPath)
        {
            var name = Path.GetFileName(missingPath);
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var directory in new[] { "/lib", "/lib64", "/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu" })
            {
                var candidate = Path.Combine(directory, name);
                if (candidate != missingPath && File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// chmod +x. .NET Standard 2.0 has no API for file modes, so shell out to chmod — which is
        /// present in the Emby container. A downloaded file is not executable by default, and
        /// without this every resolve fails with a permission error.
        /// </summary>
        private void MakeExecutable(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            try
            {
                var result = ProcessRunner.Run("/bin/chmod", new[] { "755", path }, TimeSpan.FromSeconds(15), _logger, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (result.ExitCode != 0)
                    _logger.Warn("YouTube: chmod on yt-dlp exited {0}: {1}", result.ExitCode, result.StandardError);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: failed to mark yt-dlp executable.", ex);
            }
        }
    }
}
