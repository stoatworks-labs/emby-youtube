using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Emby.Plugins.YouTube.Configuration;
using Emby.Plugins.YouTube.Util;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.YouTube.Api
{
    /// <summary>
    /// YouTube Data API v3 client, used for the operations that are both reliable and quota-cheap:
    /// subscriptions (1 unit), playlist items (1 unit) and video details (1 unit for up to 50 ids).
    ///
    /// Deliberately does NOT implement search: search.list costs 100 units against a 10,000/day
    /// default, which caps the whole server at roughly 100 searches a day. Search and the
    /// recommendation feed go through yt-dlp instead — see <see cref="YtDlp.YtDlpClient"/>.
    /// </summary>
    public class YouTubeDataClient
    {
        private const string Base = "https://www.googleapis.com/youtube/v3/";

        /// <summary>The API caps every list call at 50 regardless of what we ask for.</summary>
        private const int PageSize = 50;

        private readonly HttpClient _http;
        private readonly GoogleOAuth _oauth;
        private readonly ILogger _logger;

        public YouTubeDataClient(HttpClient http, GoogleOAuth oauth, ILogger logger)
        {
            _http = http;
            _oauth = oauth;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        public bool IsLinked => !string.IsNullOrEmpty(Config.RefreshToken);

        /// <summary>Every channel the linked account subscribes to, paged out in full.</summary>
        public async Task<List<YouTubeChannelInfo>> GetSubscriptions(CancellationToken cancellationToken)
        {
            var results = new List<YouTubeChannelInfo>();
            string pageToken = null;

            do
            {
                var query = new Dictionary<string, string>
                {
                    ["part"] = "snippet",
                    ["mine"] = "true",
                    ["maxResults"] = PageSize.ToString(CultureInfo.InvariantCulture),
                    ["order"] = "alphabetical"
                };
                if (pageToken != null) query["pageToken"] = pageToken;

                var json = await Get("subscriptions", query, cancellationToken).ConfigureAwait(false);
                if (json == null) break;

                foreach (var item in json["items"].Array)
                {
                    var snippet = item["snippet"];
                    var channelId = snippet["resourceId"]["channelId"].AsString;
                    if (string.IsNullOrEmpty(channelId)) continue;

                    results.Add(new YouTubeChannelInfo
                    {
                        Id = channelId,
                        Title = snippet["title"].AsString,
                        Description = snippet["description"].AsString,
                        ThumbnailUrl = BestThumbnail(snippet["thumbnails"])
                    });
                }

                pageToken = json["nextPageToken"].AsString;
            }
            while (!string.IsNullOrEmpty(pageToken) && !cancellationToken.IsCancellationRequested);

            _logger.Info("YouTube: loaded {0} subscriptions.", results.Count);
            return results;
        }

        /// <summary>
        /// Videos from a channel's uploads playlist, newest first.
        ///
        /// playlistItems gives us ids and titles but not duration, so we follow up with a single
        /// videos.list batch to fill those in — still only 1 unit per 50 videos.
        /// </summary>
        public async Task<List<YouTubeVideo>> GetChannelUploads(string channelId, int maxResults, CancellationToken cancellationToken)
        {
            var uploadsPlaylist = new YouTubeChannelInfo { Id = channelId }.UploadsPlaylistId;
            if (uploadsPlaylist == null)
            {
                _logger.Warn("YouTube: cannot derive an uploads playlist for channel id '{0}'.", channelId);
                return new List<YouTubeVideo>();
            }
            return await GetPlaylistItems(uploadsPlaylist, maxResults, cancellationToken).ConfigureAwait(false);
        }

        public async Task<List<YouTubeVideo>> GetPlaylistItems(string playlistId, int maxResults, CancellationToken cancellationToken)
        {
            var videos = new List<YouTubeVideo>();
            string pageToken = null;

            do
            {
                var query = new Dictionary<string, string>
                {
                    ["part"] = "snippet,contentDetails",
                    ["playlistId"] = playlistId,
                    ["maxResults"] = Math.Min(PageSize, maxResults - videos.Count).ToString(CultureInfo.InvariantCulture)
                };
                if (pageToken != null) query["pageToken"] = pageToken;

                var json = await Get("playlistItems", query, cancellationToken).ConfigureAwait(false);
                if (json == null) break;

                foreach (var item in json["items"].Array)
                {
                    var snippet = item["snippet"];
                    var videoId = item["contentDetails"]["videoId"].AsString ?? snippet["resourceId"]["videoId"].AsString;
                    if (string.IsNullOrEmpty(videoId)) continue;

                    // Deleted and private entries stay in the playlist as tombstones with no
                    // usable metadata; they would otherwise show up as unplayable rows.
                    var title = snippet["title"].AsString;
                    if (string.IsNullOrEmpty(title)
                        || string.Equals(title, "Deleted video", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(title, "Private video", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    videos.Add(new YouTubeVideo
                    {
                        Id = videoId,
                        Title = title,
                        Description = snippet["description"].AsString,
                        ThumbnailUrl = BestThumbnail(snippet["thumbnails"]),
                        ChannelId = snippet["videoOwnerChannelId"].AsString ?? snippet["channelId"].AsString,
                        ChannelTitle = snippet["videoOwnerChannelTitle"].AsString ?? snippet["channelTitle"].AsString,
                        PublishedAt = ParseDate(item["contentDetails"]["videoPublishedAt"].AsString ?? snippet["publishedAt"].AsString)
                    });
                }

                pageToken = json["nextPageToken"].AsString;
            }
            while (!string.IsNullOrEmpty(pageToken) && videos.Count < maxResults && !cancellationToken.IsCancellationRequested);

            await FillVideoDetails(videos, cancellationToken).ConfigureAwait(false);
            return videos;
        }

        /// <summary>The most popular chart for a region — the only "discovery" feed the official API still offers.</summary>
        public async Task<List<YouTubeVideo>> GetTrending(string regionCode, int maxResults, CancellationToken cancellationToken)
        {
            var json = await Get("videos", new Dictionary<string, string>
            {
                ["part"] = "snippet,contentDetails,statistics",
                ["chart"] = "mostPopular",
                ["regionCode"] = string.IsNullOrWhiteSpace(regionCode) ? "US" : regionCode,
                ["maxResults"] = Math.Min(PageSize, maxResults).ToString(CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);

            var videos = new List<YouTubeVideo>();
            if (json == null) return videos;

            foreach (var item in json["items"].Array)
            {
                var video = FromVideoResource(item);
                if (video != null) videos.Add(video);
            }
            return videos;
        }

        /// <summary>
        /// Batch-fills duration, view count and rating. Costs 1 unit per 50 videos, so this is
        /// cheap enough to always run — durations matter because Emby shows them and because the
        /// Shorts filter depends on them.
        /// </summary>
        public async Task FillVideoDetails(List<YouTubeVideo> videos, CancellationToken cancellationToken)
        {
            if (videos == null || videos.Count == 0) return;

            var byId = new Dictionary<string, YouTubeVideo>(StringComparer.Ordinal);
            foreach (var v in videos)
            {
                if (!string.IsNullOrEmpty(v.Id)) byId[v.Id] = v;
            }

            foreach (var batch in Batch(byId.Keys.ToList(), PageSize))
            {
                var json = await Get("videos", new Dictionary<string, string>
                {
                    ["part"] = "contentDetails,statistics",
                    ["id"] = string.Join(",", batch)
                }, cancellationToken).ConfigureAwait(false);
                if (json == null) return;

                foreach (var item in json["items"].Array)
                {
                    var id = item["id"].AsString;
                    if (id == null || !byId.TryGetValue(id, out var video)) continue;

                    video.Duration = ParseIso8601Duration(item["contentDetails"]["duration"].AsString);
                    video.ViewCount = item["statistics"]["viewCount"].AsLong;

                    var likes = item["statistics"]["likeCount"].AsLong;
                    if (likes.HasValue && video.ViewCount.HasValue && video.ViewCount.Value > 0)
                    {
                        // There is no public rating any more, so approximate one from the
                        // like-to-view ratio. 5% likes is exceptional, so scale that to 10/10.
                        var ratio = (double)likes.Value / video.ViewCount.Value;
                        video.Rating = (float)Math.Min(10.0, ratio * 200.0);
                    }
                }
            }
        }

        private YouTubeVideo FromVideoResource(JsonValue item)
        {
            var id = item["id"].AsString;
            if (string.IsNullOrEmpty(id)) return null;

            var snippet = item["snippet"];
            return new YouTubeVideo
            {
                Id = id,
                Title = snippet["title"].AsString,
                Description = snippet["description"].AsString,
                ThumbnailUrl = BestThumbnail(snippet["thumbnails"]),
                ChannelId = snippet["channelId"].AsString,
                ChannelTitle = snippet["channelTitle"].AsString,
                PublishedAt = ParseDate(snippet["publishedAt"].AsString),
                Duration = ParseIso8601Duration(item["contentDetails"]["duration"].AsString),
                ViewCount = item["statistics"]["viewCount"].AsLong,
                IsLive = string.Equals(snippet["liveBroadcastContent"].AsString, "live", StringComparison.OrdinalIgnoreCase)
            };
        }

        private async Task<JsonValue> Get(string endpoint, Dictionary<string, string> query, CancellationToken cancellationToken)
        {
            var token = await _oauth.GetAccessToken(Config, cancellationToken).ConfigureAwait(false);
            if (token == null)
            {
                _logger.Warn("YouTube: no linked account, skipping Data API call to '{0}'.", endpoint);
                return null;
            }

            var url = Base + endpoint + "?" + string.Join("&",
                query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var parsed = Json.Parse(body);
                        var reason = parsed["error"]["errors"][0]["reason"].AsString
                                     ?? parsed["error"]["status"].AsString
                                     ?? response.StatusCode.ToString();

                        if (string.Equals(reason, "quotaExceeded", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Error("YouTube: daily Data API quota exhausted. Subscription feeds will be empty until it resets at midnight Pacific.");
                        }
                        else if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            _logger.Error("YouTube: Data API rejected the access token ({0}).", reason);
                        }
                        else
                        {
                            _logger.Error("YouTube: Data API call to '{0}' failed: {1} {2}", endpoint, (int)response.StatusCode, reason);
                        }
                        return null;
                    }

                    if (Config.EnableDebugLogging)
                        _logger.Debug("YouTube: {0} returned {1} bytes.", endpoint, body?.Length ?? 0);

                    return Json.Parse(body);
                }
            }
        }

        private static string BestThumbnail(JsonValue thumbnails)
        {
            // Ordered largest first; YouTube omits the bigger sizes on older or low-res uploads.
            foreach (var size in new[] { "maxres", "standard", "high", "medium", "default" })
            {
                var url = thumbnails[size]["url"].AsString;
                if (!string.IsNullOrEmpty(url)) return url;
            }
            return null;
        }

        private static DateTimeOffset? ParseDate(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        /// <summary>Durations arrive as ISO 8601 ("PT4M13S"). Live streams report "P0D".</summary>
        internal static TimeSpan? ParseIso8601Duration(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                var parsed = XmlConvert.ToTimeSpan(value);
                return parsed == TimeSpan.Zero ? (TimeSpan?)null : parsed;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static IEnumerable<List<T>> Batch<T>(List<T> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }
}
