using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.YouTube.Api;
using Emby.Plugins.YouTube.Configuration;
using Emby.Plugins.YouTube.Util;
using Emby.Plugins.YouTube.YtDlp;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.YouTube
{
    /// <summary>
    /// The YouTube channel.
    ///
    /// A note on how Emby 4.9 channels work, because it shapes everything below: Emby crawls a
    /// channel by calling <see cref="GetChannelItems"/> for each folder and indexes the results into
    /// its library database. Everything a user can reach must therefore be reachable by walking
    /// folders from the root.
    ///
    /// <c>ISearchableChannel</c>, <c>ISupportsLatestMedia</c> and <c>IHasCacheKey</c> are all marked
    /// obsolete in 4.9 — "no longer supported and ignored by the server" — so they are deliberately
    /// not implemented. The practical consequences:
    ///
    /// * There is no hook for live search from the client's search box. Saved searches are exposed
    ///   as folders instead; opening one runs a real query through yt-dlp, and the results are then
    ///   indexed and become searchable alongside everything else.
    /// * "Latest" is an ordinary folder rather than a server-driven row.
    /// </summary>
    public class YouTubeChannel : IChannel, IRequiresMediaInfoCallback
    {
        private readonly ILogger _logger;
        private readonly YouTubeDataClient _dataClient;
        private readonly YtDlpClient _ytDlp;

        /// <summary>
        /// Emby may fan out several folder requests at once during a refresh. These short-lived
        /// caches stop that turning into duplicate network work — the subscription list in
        /// particular is needed by both the Subscriptions folder and the Latest feed.
        /// </summary>
        private readonly TimedCache<List<YouTubeChannelInfo>> _subscriptionsCache =
            new TimedCache<List<YouTubeChannelInfo>>(TimeSpan.FromMinutes(30));

        private readonly TimedCache<List<YouTubeVideo>> _latestCache =
            new TimedCache<List<YouTubeVideo>>(TimeSpan.FromMinutes(15));

        public YouTubeChannel(ILogManager logManager)
        {
            _logger = logManager.GetLogger(GetType().Name);

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var oauth = new GoogleOAuth(http, _logger);
            _dataClient = new YouTubeDataClient(http, oauth, _logger);

            var bootstrap = new YtDlpBootstrap(http, _logger);
            _ytDlp = new YtDlpClient(bootstrap, _logger);
        }

        public string Name => "YouTube";

        public string Description => "Your YouTube subscriptions, uploads, recommendations and searches.";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Thumb, ImageType.Primary };

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var self = GetType();
            var stream = self.Assembly.GetManifestResourceStream(self.Namespace + ".Images.thumb.png");

            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Png,
                Stream = stream
            });
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var folderId = query.FolderId;

            try
            {
                if (string.IsNullOrEmpty(folderId)) return GetRootFolders();

                if (string.Equals(folderId, ItemId.Subscriptions, StringComparison.Ordinal))
                    return await GetSubscriptionFolders(cancellationToken).ConfigureAwait(false);

                if (string.Equals(folderId, ItemId.Latest, StringComparison.Ordinal))
                    return ToResult(await GetLatestVideos(cancellationToken).ConfigureAwait(false));

                if (string.Equals(folderId, ItemId.Recommended, StringComparison.Ordinal))
                    return ToResult(await _ytDlp.GetRecommendations(Config.MaxRecommendations, cancellationToken).ConfigureAwait(false));

                if (string.Equals(folderId, ItemId.Trending, StringComparison.Ordinal))
                    return ToResult(await _dataClient.GetTrending(Config.RegionCode, Config.MaxRecommendations, cancellationToken).ConfigureAwait(false));

                if (string.Equals(folderId, ItemId.Searches, StringComparison.Ordinal))
                    return GetSavedSearchFolders();

                if (ItemId.IsChannel(folderId, out var channelId))
                    return ToResult(await _dataClient.GetChannelUploads(channelId, Config.MaxVideosPerChannel, cancellationToken).ConfigureAwait(false));

                if (ItemId.IsQuery(folderId, out var searchQuery))
                    return ToResult(await _ytDlp.Search(searchQuery, Config.MaxSearchResults, cancellationToken).ConfigureAwait(false));

                if (ItemId.IsPlaylist(folderId, out var playlistId))
                    return ToResult(await _dataClient.GetPlaylistItems(playlistId, Config.MaxVideosPerChannel, cancellationToken).ConfigureAwait(false));

                _logger.Warn("YouTube: unrecognised folder id '{0}'.", folderId);
                return Empty();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Throwing here aborts the whole channel refresh and can leave the library empty,
                // so a failing folder degrades to empty rather than taking everything down.
                _logger.ErrorException("YouTube: failed to load folder '" + folderId + "'.", ex);
                return Empty();
            }
        }

        private ChannelItemResult GetRootFolders()
        {
            var items = new List<ChannelItemInfo>();
            var config = Config;

            if (config.EnableLatest)
                items.Add(Folder(ItemId.Latest, "Latest from Subscriptions", "Recent uploads from every channel you subscribe to."));

            if (config.EnableSubscriptions)
                items.Add(Folder(ItemId.Subscriptions, "Subscriptions", "Browse each channel you subscribe to."));

            if (config.EnableRecommendations)
                items.Add(Folder(ItemId.Recommended, "Recommended", "Your personalised YouTube home feed."));

            if (config.EnableTrending)
                items.Add(Folder(ItemId.Trending, "Trending", "Most popular on YouTube in " + config.RegionCode + "."));

            if (config.EnableSavedSearches && (config.SavedSearches?.Length ?? 0) > 0)
                items.Add(Folder(ItemId.Searches, "Searches", "Your saved searches."));

            if (items.Count == 0)
                _logger.Warn("YouTube: every section is disabled, so the channel will appear empty.");

            return ToResult(items);
        }

        private async Task<ChannelItemResult> GetSubscriptionFolders(CancellationToken cancellationToken)
        {
            var subscriptions = await GetSubscriptions(cancellationToken).ConfigureAwait(false);

            var items = subscriptions.Select(channel => new ChannelItemInfo
            {
                Id = ItemId.ForChannel(channel.Id),
                Name = channel.Title,
                Overview = channel.Description,
                Type = ChannelItemType.Folder,
                ImageUrl = channel.ThumbnailUrl,
                FolderType = ChannelFolderType.Container
            }).ToList();

            return ToResult(items);
        }

        private ChannelItemResult GetSavedSearchFolders()
        {
            var items = (Config.SavedSearches ?? new SavedSearch[0])
                .Where(s => !string.IsNullOrWhiteSpace(s.Query))
                .Select(s => new ChannelItemInfo
                {
                    Id = ItemId.ForQuery(s.Query),
                    Name = string.IsNullOrWhiteSpace(s.Name) ? s.Query : s.Name,
                    Overview = "Search results for: " + s.Query,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container
                })
                .ToList();

            return ToResult(items);
        }

        /// <summary>
        /// Recent uploads across every subscription, newest first.
        ///
        /// Prefers the Data API (one cheap call per channel) and falls back to scraping the
        /// signed-in subscriptions feed when no Google project is linked but cookies are present.
        /// </summary>
        private async Task<List<YouTubeVideo>> GetLatestVideos(CancellationToken cancellationToken)
        {
            if (_latestCache.TryGet(out var cached)) return cached;

            var videos = new List<YouTubeVideo>();

            if (_dataClient.IsLinked)
            {
                var subscriptions = await GetSubscriptions(cancellationToken).ConfigureAwait(false);

                // Pull a slice from each channel and merge, rather than exhausting one channel
                // first — otherwise a prolific uploader crowds out everything else.
                var perChannel = Math.Max(1, Math.Min(15, Config.MaxLatestVideos / Math.Max(1, subscriptions.Count) + 3));

                foreach (var subscription in subscriptions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var uploads = await _dataClient.GetChannelUploads(subscription.Id, perChannel, cancellationToken).ConfigureAwait(false);
                        foreach (var upload in uploads)
                        {
                            if (string.IsNullOrEmpty(upload.ChannelTitle)) upload.ChannelTitle = subscription.Title;
                            videos.Add(upload);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("YouTube: could not load uploads for '" + subscription.Title + "'.", ex);
                    }
                }
            }
            else if (_ytDlp.HasCookies)
            {
                _logger.Info("YouTube: no linked Google account; using the cookie-authenticated subscriptions feed.");
                videos = await _ytDlp.GetSubscriptionFeed(Config.MaxLatestVideos, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.Warn("YouTube: the Latest feed needs either a linked Google account or cookies.");
            }

            var ordered = videos
                .GroupBy(v => v.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderByDescending(v => v.PublishedAt ?? DateTimeOffset.MinValue)
                .Take(Config.MaxLatestVideos)
                .ToList();

            _latestCache.Set(ordered);
            return ordered;
        }

        private async Task<List<YouTubeChannelInfo>> GetSubscriptions(CancellationToken cancellationToken)
        {
            if (_subscriptionsCache.TryGet(out var cached)) return cached;

            var subscriptions = await _dataClient.GetSubscriptions(cancellationToken).ConfigureAwait(false);
            _subscriptionsCache.Set(subscriptions);
            return subscriptions;
        }

        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            if (!ItemId.IsVideo(id, out var videoId))
            {
                _logger.Warn("YouTube: asked to resolve a non-video id '{0}'.", id);
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(new List<MediaSourceInfo>());
            }

            return ResolveVideo(videoId, cancellationToken);
        }

        private async Task<IEnumerable<MediaSourceInfo>> ResolveVideo(string videoId, CancellationToken cancellationToken)
        {
            var sources = await _ytDlp.Resolve(videoId, cancellationToken).ConfigureAwait(false);
            return sources;
        }

        // ---- Mapping -------------------------------------------------------------------------

        private ChannelItemResult ToResult(List<YouTubeVideo> videos)
        {
            var minimum = Config.MinimumDurationSeconds;

            var items = videos
                .Where(v => !string.IsNullOrEmpty(v.Id))
                // Shorts are the main thing worth filtering; a video with unknown duration is kept
                // rather than dropped, since flat listings often omit it.
                .Where(v => minimum <= 0 || !v.Duration.HasValue || v.Duration.Value.TotalSeconds >= minimum)
                .Select(ToChannelItem)
                .ToList();

            return ToResult(items);
        }

        private ChannelItemInfo ToChannelItem(YouTubeVideo video)
        {
            var item = new ChannelItemInfo
            {
                Id = ItemId.ForVideo(video.Id),
                Name = video.Title,
                Overview = video.Description,
                Type = ChannelItemType.Media,
                ContentType = ChannelMediaContentType.Clip,
                MediaType = ChannelMediaType.Video,
                ImageUrl = video.ThumbnailUrl,
                IsLiveStream = video.IsLive,
                DateCreated = video.PublishedAt,
                PremiereDate = video.PublishedAt,
                ProductionYear = video.PublishedAt?.Year,
                CommunityRating = video.Rating,
                RunTimeTicks = video.Duration.HasValue ? (long)video.Duration.Value.Ticks : (long?)null
            };

            if (!string.IsNullOrEmpty(video.ChannelTitle))
            {
                // Surfacing the uploader as a studio makes it filterable in Emby, and as the series
                // name it shows underneath the title in most client layouts.
                item.Studios = new List<string> { video.ChannelTitle };
                item.SeriesName = video.ChannelTitle;
            }

            return item;
        }

        private static ChannelItemInfo Folder(string id, string name, string overview)
        {
            return new ChannelItemInfo
            {
                Id = id,
                Name = name,
                Overview = overview,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container
            };
        }

        private static ChannelItemResult ToResult(List<ChannelItemInfo> items)
        {
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        private static ChannelItemResult Empty() => ToResult(new List<ChannelItemInfo>());
    }

    /// <summary>Minimal single-value cache with a wall-clock expiry.</summary>
    internal class TimedCache<T> where T : class
    {
        private readonly TimeSpan _lifetime;
        private readonly object _gate = new object();
        private T _value;
        private DateTimeOffset _expiresAt;

        public TimedCache(TimeSpan lifetime) { _lifetime = lifetime; }

        public bool TryGet(out T value)
        {
            lock (_gate)
            {
                if (_value != null && DateTimeOffset.UtcNow < _expiresAt)
                {
                    value = _value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        public void Set(T value)
        {
            lock (_gate)
            {
                _value = value;
                _expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
            }
        }
    }
}
