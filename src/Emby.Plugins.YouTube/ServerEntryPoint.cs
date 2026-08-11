using System;
using System.Net.Http;
using Emby.Plugins.YouTube.YtDlp;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.YouTube
{
    /// <summary>
    /// Startup work for the plugin.
    ///
    /// The cookie jar is stored in the plugin configuration (so it round-trips with Emby's own
    /// backup and config handling) but yt-dlp needs it as a file on disk. Writing it out at startup
    /// keeps the two in step across restarts and container recreations.
    /// </summary>
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ILogger _logger;
        private readonly HttpClient _http;

        public ServerEntryPoint(ILogManager logManager)
        {
            _logger = logManager.GetLogger("YouTubeEntryPoint");
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public void Run()
        {
            try
            {
                var client = new YtDlpClient(new YtDlpBootstrap(_http, _logger), _logger);
                client.SyncCookiesToDisk();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: startup initialisation failed.", ex);
            }
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }
}
