using System;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugins.YouTube.Configuration
{
    public class SavedSearch
    {
        public string Name { get; set; }
        public string Query { get; set; }

        /// <summary>Optional yt-dlp/YouTube sort: relevance, date, views, rating.</summary>
        public string SortBy { get; set; } = "relevance";
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // ---- YouTube Data API v3 (subscriptions + uploads) --------------------------------

        /// <summary>
        /// OAuth client id/secret from a Google Cloud project with the YouTube Data API enabled.
        /// The client must be of type "TV and Limited Input" for the device-code flow to work —
        /// a Web or Desktop client will be rejected by the device endpoint.
        /// </summary>
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }

        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }

        /// <summary>UTC ticks. 0 means "no token". Stored as ticks because BasePluginConfiguration
        /// is serialised by Emby and DateTimeOffset round-trips inconsistently across versions.</summary>
        public long AccessTokenExpiresAtTicks { get; set; }

        /// <summary>Account label captured at link time, purely so the config page can show who is linked.</summary>
        public string LinkedAccountName { get; set; }

        // ---- Feeds -------------------------------------------------------------------------

        public int MaxVideosPerChannel { get; set; } = 50;
        public int MaxLatestVideos { get; set; } = 100;
        public int MaxRecommendations { get; set; } = 50;
        public int MaxSearchResults { get; set; } = 50;

        public bool EnableSubscriptions { get; set; } = true;
        public bool EnableLatest { get; set; } = true;
        public bool EnableRecommendations { get; set; } = true;
        public bool EnableTrending { get; set; }
        public bool EnableSavedSearches { get; set; } = true;

        /// <summary>ISO 3166-1 alpha-2, used for the trending chart.</summary>
        public string RegionCode { get; set; } = "GB";

        /// <summary>Skip anything shorter than this, which removes the Shorts flood from feeds.</summary>
        public int MinimumDurationSeconds { get; set; } = 0;

        public SavedSearch[] SavedSearches { get; set; } = new SavedSearch[0];

        // ---- yt-dlp ------------------------------------------------------------------------

        /// <summary>Absolute path override. Empty means "use the managed copy in the plugin data folder".</summary>
        public string YtDlpPath { get; set; }

        public bool AutoUpdateYtDlp { get; set; } = true;

        /// <summary>
        /// Netscape-format cookie jar contents. Required for the personalised recommendation feed,
        /// and it lets age-restricted videos resolve. Written to the plugin data folder on save.
        /// </summary>
        public string CookiesTxt { get; set; }

        /// <summary>Cap on resolved stream height. 0 means no cap.</summary>
        public int MaxHeight { get; set; } = 1080;

        /// <summary>
        /// Prefer a single progressive file over separate video+audio streams. Progressive tops out
        /// at 360p/720p but plays without the server having to mux, so it direct-plays far more widely.
        /// </summary>
        public bool PreferProgressive { get; set; }

        public int ResolveTimeoutSeconds { get; set; } = 60;
        public int MetadataTimeoutSeconds { get; set; } = 120;

        public bool EnableDebugLogging { get; set; }

        public DateTimeOffset AccessTokenExpiresAt
        {
            get => AccessTokenExpiresAtTicks == 0
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(AccessTokenExpiresAtTicks, TimeSpan.Zero);
            set => AccessTokenExpiresAtTicks = value == DateTimeOffset.MinValue ? 0 : value.UtcTicks;
        }
    }
}
