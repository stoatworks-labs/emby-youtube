using System;
using System.Text;

namespace Emby.Plugins.YouTube.Util
{
    /// <summary>
    /// Folder/item identifiers handed to and from Emby.
    ///
    /// Emby treats these as opaque strings and round-trips them: the id we put on a
    /// <c>ChannelItemInfo</c> comes back as <c>InternalChannelItemQuery.FolderId</c> when the user
    /// opens that folder, and as the <c>id</c> argument to <c>GetChannelItemMediaInfo</c> when a
    /// video is played. They are also persisted in the library database, so the scheme has to stay
    /// stable across plugin versions — changing a prefix orphans everything already indexed.
    /// </summary>
    public static class ItemId
    {
        public const string Subscriptions = "subs";
        public const string Latest = "latest";
        public const string Recommended = "recs";
        public const string Trending = "trending";
        public const string Searches = "searches";

        private const string ChannelPrefix = "ch:";
        private const string VideoPrefix = "v:";
        private const string QueryPrefix = "q:";
        private const string PlaylistPrefix = "pl:";

        public static string ForChannel(string channelId) => ChannelPrefix + channelId;
        public static string ForVideo(string videoId) => VideoPrefix + videoId;
        public static string ForPlaylist(string playlistId) => PlaylistPrefix + playlistId;

        /// <summary>
        /// Search queries are base64url-encoded so that arbitrary user text (spaces, colons,
        /// slashes, non-ASCII) can never collide with the prefix delimiters above.
        /// </summary>
        public static string ForQuery(string query) => QueryPrefix + Base64UrlEncode(query);

        public static bool IsChannel(string id, out string channelId) => TryStrip(id, ChannelPrefix, out channelId);
        public static bool IsVideo(string id, out string videoId) => TryStrip(id, VideoPrefix, out videoId);
        public static bool IsPlaylist(string id, out string playlistId) => TryStrip(id, PlaylistPrefix, out playlistId);

        public static bool IsQuery(string id, out string query)
        {
            query = null;
            if (!TryStrip(id, QueryPrefix, out var encoded)) return false;
            try
            {
                query = Base64UrlDecode(encoded);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryStrip(string id, string prefix, out string rest)
        {
            if (!string.IsNullOrEmpty(id) && id.StartsWith(prefix, StringComparison.Ordinal))
            {
                rest = id.Substring(prefix.Length);
                return rest.Length > 0;
            }
            rest = null;
            return false;
        }

        private static string Base64UrlEncode(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Base64UrlDecode(string value)
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Invalid base64url length.");
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
    }
}
