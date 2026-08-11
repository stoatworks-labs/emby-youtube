using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.YouTube.Api;
using Emby.Plugins.YouTube.Configuration;
using Emby.Plugins.YouTube.Util;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Plugins.YouTube.YtDlp
{
    /// <summary>
    /// Everything the official Data API cannot do:
    ///
    /// * <b>Search</b> — search.list costs 100 quota units, capping the whole server at ~100
    ///   searches per day. yt-dlp's ytsearch is unmetered.
    /// * <b>Recommendations</b> — the Data API has never exposed the recommender, and
    ///   search.list?relatedToVideoId was removed on 7 August 2023. Reading the signed-in home
    ///   feed with cookies is the only way to get the real thing.
    /// * <b>Stream resolution</b> — Emby clients cannot embed the YouTube player, so the server has
    ///   to hand them a real media URL.
    /// </summary>
    public class YtDlpClient
    {
        private readonly YtDlpBootstrap _bootstrap;
        private readonly ILogger _logger;

        public YtDlpClient(YtDlpBootstrap bootstrap, ILogger logger)
        {
            _bootstrap = bootstrap;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        public string CookiesFilePath => Path.Combine(Plugin.Instance.PluginDataPath, "cookies.txt");

        public bool HasCookies => File.Exists(CookiesFilePath) && new FileInfo(CookiesFilePath).Length > 0;

        /// <summary>Writes the configured cookie jar to disk, or removes it when cleared.</summary>
        public void SyncCookiesToDisk()
        {
            var path = CookiesFilePath;
            var contents = Config.CookiesTxt;

            if (string.IsNullOrWhiteSpace(contents))
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { _logger.ErrorException("YouTube: could not remove the cookie file.", ex); }
                return;
            }

            try
            {
                // yt-dlp requires the Netscape header line and rejects the file without it.
                if (!contents.StartsWith("# Netscape", StringComparison.OrdinalIgnoreCase)
                    && !contents.StartsWith("# HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
                {
                    contents = "# Netscape HTTP Cookie File" + Environment.NewLine + contents;
                }
                File.WriteAllText(path, contents);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: could not write the cookie file.", ex);
            }
        }

        // ---- Feeds -------------------------------------------------------------------------

        public Task<List<YouTubeVideo>> Search(string query, int maxResults, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(new List<YouTubeVideo>());

            // ytsearchN: is a yt-dlp pseudo-URL; the query is passed as a single argv entry, so it
            // cannot break out into extra arguments however it is punctuated.
            var target = "ytsearch" + Math.Max(1, maxResults).ToString(CultureInfo.InvariantCulture) + ":" + query;
            return GetFlatPlaylist(target, maxResults, requireCookies: false, cancellationToken);
        }

        /// <summary>
        /// The signed-in home feed. Without cookies YouTube serves a generic non-personalised page,
        /// so this returns nothing rather than passing off generic results as recommendations.
        /// </summary>
        public Task<List<YouTubeVideo>> GetRecommendations(int maxResults, CancellationToken cancellationToken)
        {
            return GetFlatPlaylist("https://www.youtube.com/feed/recommended", maxResults, requireCookies: true, cancellationToken);
        }

        /// <summary>
        /// The signed-in subscriptions feed. Used as a fallback for the "recent uploads" row when
        /// no Google API project is configured — it needs only cookies, not OAuth.
        /// </summary>
        public Task<List<YouTubeVideo>> GetSubscriptionFeed(int maxResults, CancellationToken cancellationToken)
        {
            return GetFlatPlaylist("https://www.youtube.com/feed/subscriptions", maxResults, requireCookies: true, cancellationToken);
        }

        private async Task<List<YouTubeVideo>> GetFlatPlaylist(string target, int maxResults, bool requireCookies, CancellationToken cancellationToken)
        {
            var videos = new List<YouTubeVideo>();

            if (requireCookies && !HasCookies)
            {
                _logger.Warn("YouTube: '{0}' needs cookies to return a personalised feed; skipping. Paste a cookies.txt in the plugin settings.", target);
                return videos;
            }

            List<string> BuildArguments(bool useCookies)
            {
                var arguments = new List<string>
                {
                    target,
                    "--flat-playlist",       // metadata only; do not resolve each video's streams
                    "--dump-single-json",
                    "--no-warnings",
                    "--ignore-errors",
                    "--no-progress",
                    "--playlist-end", Math.Max(1, maxResults).ToString(CultureInfo.InvariantCulture)
                };
                AddCommonArguments(arguments, useCookies);
                return arguments;
            }

            var result = await Execute(BuildArguments(true), TimeSpan.FromSeconds(Config.MetadataTimeoutSeconds), cancellationToken).ConfigureAwait(false);

            // Same stale-cookie trap as Resolve. Feeds that genuinely need a signed-in session are
            // excluded — retrying those anonymously would quietly return generic results and pass
            // them off as personalised.
            if (result == null && HasCookies && !requireCookies)
            {
                _logger.Warn("YouTube: '{0}' failed using the saved cookies; retrying without them. If this "
                           + "succeeds, the cookies are stale — re-export from a private window and close it "
                           + "immediately.", target);
                result = await Execute(BuildArguments(false), TimeSpan.FromSeconds(Config.MetadataTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            }

            if (result == null) return videos;

            JsonValue json;
            try
            {
                json = Json.Parse(result.StandardOutput);
            }
            catch (FormatException ex)
            {
                _logger.ErrorException("YouTube: could not parse yt-dlp output for '" + target + "'.", ex);
                return videos;
            }

            foreach (var entry in json["entries"].Array)
            {
                var video = FromFlatEntry(entry);
                if (video != null) videos.Add(video);
                if (videos.Count >= maxResults) break;
            }

            _logger.Info("YouTube: '{0}' returned {1} items.", target, videos.Count);
            return videos;
        }

        private static YouTubeVideo FromFlatEntry(JsonValue entry)
        {
            var id = entry["id"].AsString;
            if (string.IsNullOrEmpty(id)) return null;

            // Flat playlists also contain channel and playlist rows; keep only videos.
            var type = entry["_type"].AsString;
            if (type != null && !string.Equals(type, "url", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (id.StartsWith("UC", StringComparison.Ordinal) || id.StartsWith("PL", StringComparison.Ordinal)) return null;

            var duration = entry["duration"].AsDouble;

            return new YouTubeVideo
            {
                Id = id,
                Title = entry["title"].AsString,
                Description = entry["description"].AsString,
                ThumbnailUrl = BestThumbnail(entry),
                ChannelId = entry["channel_id"].AsString ?? entry["uploader_id"].AsString,
                ChannelTitle = entry["channel"].AsString ?? entry["uploader"].AsString,
                Duration = duration.HasValue && duration.Value > 0 ? TimeSpan.FromSeconds(duration.Value) : (TimeSpan?)null,
                ViewCount = entry["view_count"].AsLong,
                PublishedAt = ParseUploadDate(entry["upload_date"].AsString),
                IsLive = entry["is_live"].AsBool
            };
        }

        // ---- Stream resolution ---------------------------------------------------------------

        /// <summary>
        /// Resolves a video id to something an Emby client can actually play.
        ///
        /// Resolution happens at play time rather than at browse time on purpose: googlevideo URLs
        /// are time-limited and tied to the requesting IP, so a URL cached during a library scan
        /// would be dead by the time anyone pressed play.
        /// </summary>
        public async Task<List<MediaSourceInfo>> Resolve(string videoId, CancellationToken cancellationToken)
        {
            var sources = new List<MediaSourceInfo>();

            var attempt = await TryResolve(videoId, useCookies: true, cancellationToken).ConfigureAwait(false);

            // Stale cookies are worse than none: YouTube rotates a cookie set as soon as the browser
            // session that exported it keeps being used, and then answers requests carrying them with
            // "No video formats found" — even for videos that resolve perfectly anonymously. So the
            // retry hangs off "we got no usable URL", not "the process failed": yt-dlp still writes
            // JSON to stdout when only the format selector came up empty, so an exit-code check
            // would never fire here.
            if (attempt.Url == null && HasCookies)
            {
                var anonymous = await TryResolve(videoId, useCookies: false, cancellationToken).ConfigureAwait(false);
                if (anonymous.Url != null)
                {
                    _logger.Warn("YouTube: {0} resolved only after dropping the saved cookies, so those cookies are "
                               + "stale. Re-export them from a private browser window and close it immediately, "
                               + "before YouTube can rotate them.", videoId);
                    attempt = anonymous;
                }
            }

            var json = attempt.Json;
            var url = attempt.Url;

            if (string.IsNullOrEmpty(url))
            {
                // The bot check is by far the most common reason a resolve fails, and the raw
                // stderr buries the one thing that actually fixes it. Call it out explicitly.
                if (LooksLikeBotCheck(attempt.StandardError))
                {
                    _logger.Error(
                        "YouTube: {0} could not be resolved because YouTube is challenging this server with its " +
                        "\"Sign in to confirm you're not a bot\" check. Add a cookies.txt in the plugin settings — " +
                        "browsing and search work without cookies, but playback from a flagged IP does not.",
                        videoId);
                }
                else
                {
                    _logger.Error("YouTube: no playable single-file stream for {0}. stderr: {1}", videoId, Truncate(attempt.StandardError, 500));
                }
                return sources;
            }

            var isLive = json["is_live"].AsBool;
            var durationSeconds = json["duration"].AsDouble;
            var height = json["height"].AsInt;
            var width = json["width"].AsInt;

            var source = new MediaSourceInfo
            {
                Id = videoId,
                Path = url,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                Name = height.HasValue ? height.Value + "p" : "YouTube",
                Container = json["ext"].AsString,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsInfiniteStream = isLive,
                RunTimeTicks = !isLive && durationSeconds.HasValue && durationSeconds.Value > 0
                    ? (long)(durationSeconds.Value * TimeSpan.TicksPerSecond)
                    : (long?)null,
                Bitrate = json["tbr"].AsDouble is double tbr && tbr > 0 ? (int)(tbr * 1000) : (int?)null,

                // googlevideo rejects or throttles requests whose UA does not match the one that
                // negotiated the URL, so the headers must travel with the source.
                RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = json["http_headers"]["User-Agent"].AsString
                                     ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
                }
            };

            var referer = json["http_headers"]["Referer"].AsString;
            if (!string.IsNullOrEmpty(referer)) source.RequiredHttpHeaders["Referer"] = referer;

            sources.Add(source);
            _logger.Info("YouTube: resolved {0} to a {1} stream ({2}x{3}).", videoId, source.Container, width, height);
            return sources;
        }

        /// <summary>One resolve attempt: the parsed response, the single playable URL (if any), and stderr.</summary>
        private class ResolveAttempt
        {
            public JsonValue Json = new JsonValue(null);
            public string Url;
            public string StandardError = string.Empty;
        }

        private async Task<ResolveAttempt> TryResolve(string videoId, bool useCookies, CancellationToken cancellationToken)
        {
            var attempt = new ResolveAttempt();

            var arguments = new List<string>
            {
                "https://www.youtube.com/watch?v=" + videoId,
                "--dump-single-json",
                "--no-warnings",
                "--no-progress",
                "--format", BuildFormatSelector()
            };
            AddCommonArguments(arguments, useCookies);

            var result = await Execute(arguments, TimeSpan.FromSeconds(Config.ResolveTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            if (result == null) return attempt;

            attempt.StandardError = result.StandardError ?? string.Empty;

            try
            {
                attempt.Json = Json.Parse(result.StandardOutput);
            }
            catch (FormatException ex)
            {
                _logger.ErrorException("YouTube: could not parse the resolve response for " + videoId + ".", ex);
                return attempt;
            }

            var url = attempt.Json["url"].AsString;

            // When the selector lands on a DASH pair, yt-dlp reports requested_formats instead of a
            // single url. Emby can only be handed one URL per source, so take an entry only if it
            // carries both streams; otherwise there is nothing playable here.
            if (string.IsNullOrEmpty(url))
            {
                foreach (var format in attempt.Json["requested_formats"].Array)
                {
                    if (!string.Equals(format["acodec"].AsString, "none", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(format["vcodec"].AsString, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        url = format["url"].AsString;
                        break;
                    }
                }
            }

            attempt.Url = string.IsNullOrEmpty(url) ? null : url;
            return attempt;
        }

        /// <summary>
        /// Prefers a single progressive file that already contains both video and audio.
        ///
        /// This is a deliberate quality ceiling. Above 720p YouTube only serves DASH, i.e. separate
        /// video and audio streams, and a MediaSourceInfo carries exactly one URL — there is no way
        /// to hand Emby two and have it mux them. Selecting a DASH video stream would play silently,
        /// which is worse than capping the resolution.
        /// </summary>
        private static string BuildFormatSelector()
        {
            var maxHeight = Config.MaxHeight;
            var heightFilter = maxHeight > 0
                ? "[height<=" + maxHeight.ToString(CultureInfo.InvariantCulture) + "]"
                : string.Empty;

            // best[...] is already restricted to muxed formats; the explicit codec guards keep
            // yt-dlp from falling back to a video-only stream when nothing matches.
            var progressive = "best[vcodec!=none][acodec!=none]" + heightFilter;

            return Config.PreferProgressive
                ? progressive + "/best[vcodec!=none][acodec!=none]"
                : progressive + "/best[vcodec!=none][acodec!=none]/best" + heightFilter + "/best";
        }

        // ---- Plumbing ------------------------------------------------------------------------

        private void AddCommonArguments(List<string> arguments, bool useCookies = true)
        {
            if (useCookies && HasCookies)
            {
                arguments.Add("--cookies");
                arguments.Add(CookiesFilePath);
            }

            // Emby servers are frequently headless and behind IPv6-less networks; forcing IPv4
            // avoids long stalls when a AAAA route exists but does not work.
            arguments.Add("--force-ipv4");

            var playerClient = Config.PlayerClient;
            if (!string.IsNullOrWhiteSpace(playerClient))
            {
                arguments.Add("--extractor-args");
                arguments.Add("youtube:player_client=" + playerClient.Trim());
            }
        }

        private async Task<ProcessResult> Execute(List<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var binary = await _bootstrap.GetBinaryPath(cancellationToken).ConfigureAwait(false);
            if (binary == null)
            {
                _logger.Error("YouTube: yt-dlp is not available and could not be downloaded.");
                return null;
            }

            if (Config.EnableDebugLogging)
                _logger.Debug("YouTube: running yt-dlp {0}", ProcessRunner.BuildArgumentString(arguments));

            ProcessResult result;
            try
            {
                result = await ProcessRunner.Run(binary, arguments, timeout, _logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: failed to start yt-dlp.", ex);
                return null;
            }

            if (result.TimedOut)
            {
                _logger.Error("YouTube: yt-dlp timed out after {0}s.", timeout.TotalSeconds);
                return null;
            }

            // --ignore-errors makes yt-dlp exit non-zero while still emitting usable JSON, so a
            // non-zero code is only fatal when there is no output to parse.
            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                _logger.Error("YouTube: yt-dlp exited {0}: {1}", result.ExitCode, Truncate(result.StandardError, 500));
                return null;
            }

            return result;
        }

        private static string BestThumbnail(JsonValue entry)
        {
            var direct = entry["thumbnail"].AsString;
            if (!string.IsNullOrEmpty(direct)) return direct;

            // thumbnails[] is ordered smallest first, so the last entry is the largest.
            string best = null;
            foreach (var thumbnail in entry["thumbnails"].Array)
            {
                var url = thumbnail["url"].AsString;
                if (!string.IsNullOrEmpty(url)) best = url;
            }
            return best;
        }

        private static DateTimeOffset? ParseUploadDate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 8) return null;
            return DateTimeOffset.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        /// <summary>
        /// Recognises YouTube's anti-bot challenge. Matched on several fragments because the exact
        /// wording (and its curly apostrophe) has changed more than once.
        /// </summary>
        private static bool LooksLikeBotCheck(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return false;
            return stderr.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("confirm you", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }
    }
}
