using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.YouTube.Api;
using Emby.Plugins.YouTube.YtDlp;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.YouTube.Services
{
    [Route("/YouTube/Auth/Start", "POST", Summary = "Begins the OAuth device-code flow")]
    public class StartAuthRequest : IReturn<AuthStatusResponse> { }

    [Route("/YouTube/Auth/Status", "GET", Summary = "Reports the current link status")]
    public class AuthStatusRequest : IReturn<AuthStatusResponse> { }

    [Route("/YouTube/Auth/Unlink", "POST", Summary = "Discards the stored OAuth tokens")]
    public class UnlinkRequest : IReturn<AuthStatusResponse> { }

    [Route("/YouTube/YtDlp/Status", "GET", Summary = "Reports the installed yt-dlp version")]
    public class YtDlpStatusRequest : IReturn<YtDlpStatusResponse> { }

    [Route("/YouTube/YtDlp/Update", "POST", Summary = "Downloads the latest yt-dlp build")]
    public class YtDlpUpdateRequest : IReturn<YtDlpStatusResponse> { }

    [Route("/YouTube/TestSearch", "GET", Summary = "Runs a live search to verify the setup")]
    public class TestSearchRequest : IReturn<TestSearchResponse>
    {
        public string Query { get; set; }
    }

    public class AuthStatusResponse
    {
        /// <summary>pending, linked, unlinked, or error.</summary>
        public string State { get; set; }
        public string UserCode { get; set; }
        public string VerificationUrl { get; set; }
        public string Message { get; set; }
        public bool HasCookies { get; set; }
    }

    public class YtDlpStatusResponse
    {
        public bool Installed { get; set; }
        public string Version { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
    }

    public class TestSearchResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public string Message { get; set; }
        public List<string> Titles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Endpoints backing the plugin's configuration page.
    ///
    /// The device-code flow is inherently multi-step — the user has to go and approve the code on
    /// another device — so the polling loop runs detached and the page polls Status until the
    /// state flips. Nothing here is user-specific, so a single static slot is enough.
    /// </summary>
    public class YouTubeService : IService
    {
        private static readonly object Gate = new object();
        private static AuthStatusResponse _authState = new AuthStatusResponse { State = "unlinked" };
        private static CancellationTokenSource _authCancellation;

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly GoogleOAuth _oauth;
        private readonly YtDlpBootstrap _bootstrap;
        private readonly YtDlpClient _ytDlp;

        public YouTubeService(ILogManager logManager)
        {
            _logger = logManager.GetLogger("YouTubeService");
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _oauth = new GoogleOAuth(_http, _logger);
            _bootstrap = new YtDlpBootstrap(_http, _logger);
            _ytDlp = new YtDlpClient(_bootstrap, _logger);
        }

        public object Post(StartAuthRequest request)
        {
            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrWhiteSpace(config.ClientId) || string.IsNullOrWhiteSpace(config.ClientSecret))
            {
                return new AuthStatusResponse
                {
                    State = "error",
                    Message = "Save an OAuth client id and secret first."
                };
            }

            DeviceCodeResult device;
            try
            {
                device = _oauth.RequestDeviceCode(config.ClientId, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: could not start the device flow.", ex);
                return new AuthStatusResponse { State = "error", Message = ex.Message };
            }

            lock (Gate)
            {
                // Abandon any earlier attempt so two codes are never live at once.
                _authCancellation?.Cancel();
                _authCancellation = new CancellationTokenSource();

                _authState = new AuthStatusResponse
                {
                    State = "pending",
                    UserCode = device.UserCode,
                    VerificationUrl = device.VerificationUrl,
                    Message = "Open the link, enter the code, then return here."
                };
            }

            var token = _authCancellation.Token;
            Task.Run(async () =>
            {
                try
                {
                    await _oauth.PollForToken(Plugin.Instance.Configuration, device, token).ConfigureAwait(false);
                    lock (Gate)
                    {
                        _authState = new AuthStatusResponse { State = "linked", Message = "Account linked." };
                    }
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer attempt; leave whatever state that one set.
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("YouTube: device flow failed.", ex);
                    lock (Gate)
                    {
                        _authState = new AuthStatusResponse { State = "error", Message = ex.Message };
                    }
                }
            }, token);

            lock (Gate) { return Clone(_authState); }
        }

        public object Get(AuthStatusRequest request)
        {
            var config = Plugin.Instance.Configuration;

            lock (Gate)
            {
                var state = Clone(_authState);

                // The stored refresh token is the source of truth; the in-memory state only
                // matters while a device flow is actually in flight.
                if (!string.IsNullOrEmpty(config.RefreshToken) && state.State != "pending")
                {
                    state.State = "linked";
                    state.Message = "Account linked.";
                }
                else if (string.IsNullOrEmpty(config.RefreshToken) && state.State == "linked")
                {
                    state.State = "unlinked";
                    state.Message = "Not linked.";
                }

                state.HasCookies = _ytDlp.HasCookies;
                return state;
            }
        }

        public object Post(UnlinkRequest request)
        {
            var config = Plugin.Instance.Configuration;
            config.AccessToken = null;
            config.RefreshToken = null;
            config.AccessTokenExpiresAtTicks = 0;
            config.LinkedAccountName = null;
            Plugin.Instance.SaveConfiguration();

            lock (Gate)
            {
                _authCancellation?.Cancel();
                _authState = new AuthStatusResponse { State = "unlinked", Message = "Tokens discarded." };
                return Clone(_authState);
            }
        }

        public object Get(YtDlpStatusRequest request)
        {
            var path = _bootstrap.ManagedBinaryPath;
            var configured = Plugin.Instance.Configuration.YtDlpPath;
            if (!string.IsNullOrWhiteSpace(configured) && System.IO.File.Exists(configured)) path = configured;

            if (!System.IO.File.Exists(path))
            {
                return new YtDlpStatusResponse
                {
                    Installed = false,
                    Path = path,
                    Message = "Not installed yet — it downloads automatically on first use."
                };
            }

            var version = _bootstrap.GetInstalledVersion(path, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();

            return new YtDlpStatusResponse
            {
                Installed = true,
                Version = version,
                Path = path,
                Message = version == null ? "Installed, but the binary would not run." : "Installed."
            };
        }

        public object Post(YtDlpUpdateRequest request)
        {
            try
            {
                _bootstrap.Download(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: yt-dlp update failed.", ex);
                return new YtDlpStatusResponse { Installed = false, Message = ex.Message };
            }
            return Get(new YtDlpStatusRequest());
        }

        public object Get(TestSearchRequest request)
        {
            var query = string.IsNullOrWhiteSpace(request?.Query) ? "test" : request.Query;

            try
            {
                _ytDlp.SyncCookiesToDisk();
                var results = _ytDlp.Search(query, 5, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                return new TestSearchResponse
                {
                    Success = results.Count > 0,
                    Count = results.Count,
                    Titles = results.Select(v => v.Title).Where(t => t != null).ToList(),
                    Message = results.Count > 0
                        ? "Search is working."
                        : "yt-dlp returned nothing — check the server log for details."
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("YouTube: test search failed.", ex);
                return new TestSearchResponse { Success = false, Message = ex.Message };
            }
        }

        private static AuthStatusResponse Clone(AuthStatusResponse source)
        {
            return new AuthStatusResponse
            {
                State = source.State,
                UserCode = source.UserCode,
                VerificationUrl = source.VerificationUrl,
                Message = source.Message,
                HasCookies = source.HasCookies
            };
        }
    }
}
