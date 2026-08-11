using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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
