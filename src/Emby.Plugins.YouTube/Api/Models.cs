using System;

namespace Emby.Plugins.YouTube.Api
{
    /// <summary>
    /// Normalised video metadata. Both the Data API path and the yt-dlp path produce these, so the
    /// channel code never has to care which source a given feed came from.
    /// </summary>
    public class YouTubeVideo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string ThumbnailUrl { get; set; }
        public string ChannelId { get; set; }
        public string ChannelTitle { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public long? ViewCount { get; set; }

        /// <summary>Present only when the source gave us one; used as the Emby community rating.</summary>
        public float? Rating { get; set; }

        public bool IsLive { get; set; }
    }

    public class YouTubeChannelInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string ThumbnailUrl { get; set; }

        /// <summary>
        /// The channel's "uploads" playlist. Every channel has one, and its id is the channel id
        /// with the UC prefix swapped for UU — deriving it saves a channels.list call per channel.
        /// </summary>
        public string UploadsPlaylistId =>
            !string.IsNullOrEmpty(Id) && Id.StartsWith("UC", StringComparison.Ordinal)
                ? "UU" + Id.Substring(2)
                : null;
    }
}
