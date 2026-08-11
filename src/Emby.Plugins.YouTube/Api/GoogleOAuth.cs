using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.YouTube.Configuration;
using Emby.Plugins.YouTube.Util;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.YouTube.Api
{
    public class DeviceCodeResult
    {
        public string DeviceCode { get; set; }
        public string UserCode { get; set; }
        public string VerificationUrl { get; set; }
        public int IntervalSeconds { get; set; }
        public int ExpiresInSeconds { get; set; }
    }

    /// <summary>
    /// OAuth 2.0 device authorisation flow.
    ///
    /// This is the only sane flow for a headless media server: there is no browser on the box and
    /// no reliable public redirect URI to come back to. The user opens a short URL on any device,
    /// types a code, and the server polls until Google hands over a refresh token.
    ///
    /// Requires an OAuth client of type "TV and Limited Input" — Web and Desktop clients are
    /// rejected by the device endpoint outright.
    /// </summary>
    public class GoogleOAuth
    {
        public const string Scope = "https://www.googleapis.com/auth/youtube.readonly";

        private const string DeviceCodeUrl = "https://oauth2.googleapis.com/device/code";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

        /// <summary>Refresh this far before actual expiry to avoid racing a long-running request.</summary>
        private static readonly TimeSpan ExpiryGuard = TimeSpan.FromMinutes(2);

        private readonly HttpClient _http;
        private readonly ILogger _logger;

        /// <summary>Serialises refreshes so a burst of parallel calls performs exactly one token exchange.</summary>
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        public GoogleOAuth(HttpClient http, ILogger logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<DeviceCodeResult> RequestDeviceCode(string clientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("No OAuth client id is configured.");

            var json = await PostForm(DeviceCodeUrl, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = Scope
            }, cancellationToken).ConfigureAwait(false);

            var error = json["error"].AsString;
            if (error != null)
                throw new InvalidOperationException("Device code request rejected: " + error + " — " + json["error_description"].AsString);

            return new DeviceCodeResult
            {
                DeviceCode = json["device_code"].AsString,
                UserCode = json["user_code"].AsString,
                VerificationUrl = json["verification_url"].AsString ?? json["verification_uri"].AsString,
                IntervalSeconds = json["interval"].AsInt ?? 5,
                ExpiresInSeconds = json["expires_in"].AsInt ?? 1800
            };
        }

        /// <summary>
        /// Polls until the user approves, the code expires, or we are cancelled. Honours Google's
        /// slow_down by backing the interval off, otherwise repeated polls get the request throttled.
        /// </summary>
        public async Task PollForToken(PluginConfiguration config, DeviceCodeResult device, CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, device.IntervalSeconds));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresInSeconds);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                var json = await PostForm(TokenUrl, new Dictionary<string, string>
                {
                    ["client_id"] = config.ClientId,
                    ["client_secret"] = config.ClientSecret,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = DeviceGrantType
                }, cancellationToken).ConfigureAwait(false);

                var error = json["error"].AsString;
                if (error == null)
                {
                    StoreTokens(config, json, keepExistingRefreshToken: false);
                    _logger.Info("YouTube: account linked successfully.");
                    return;
                }

                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "access_denied":
                        throw new InvalidOperationException("Authorisation was denied on the device.");
                    case "expired_token":
                        throw new InvalidOperationException("The device code expired before it was approved.");
                    default:
                        throw new InvalidOperationException("Token exchange failed: " + error);
                }
            }

            throw new InvalidOperationException("Timed out waiting for the device code to be approved.");
        }

        /// <summary>
        /// Returns a valid access token, refreshing it if it is missing or about to expire.
        /// Returns null when the plugin has never been linked, which callers treat as
        /// "the official API is unavailable" rather than as an error.
        /// </summary>
        public async Task<string> GetAccessToken(PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(config.RefreshToken)) return null;

            if (!string.IsNullOrEmpty(config.AccessToken)
                && config.AccessTokenExpiresAt - ExpiryGuard > DateTimeOffset.UtcNow)
            {
                return config.AccessToken;
            }

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check: another caller may have refreshed while we waited on the lock.
                if (!string.IsNullOrEmpty(config.AccessToken)
                    && config.AccessTokenExpiresAt - ExpiryGuard > DateTimeOffset.UtcNow)
                {
                    return config.AccessToken;
                }

                var json = await PostForm(TokenUrl, new Dictionary<string, string>
                {
                    ["client_id"] = config.ClientId,
                    ["client_secret"] = config.ClientSecret,
                    ["refresh_token"] = config.RefreshToken,
                    ["grant_type"] = "refresh_token"
                }, cancellationToken).ConfigureAwait(false);

                var error = json["error"].AsString;
                if (error != null)
                {
                    // invalid_grant means the refresh token is dead for good (revoked, password
                    // change, or 6 months idle). Clear it so the UI prompts to re-link instead of
                    // retrying a token that will never work again.
                    if (string.Equals(error, "invalid_grant", StringComparison.Ordinal))
                    {
                        _logger.Error("YouTube: refresh token is no longer valid; the account must be re-linked.");
                        config.AccessToken = null;
                        config.RefreshToken = null;
                        config.AccessTokenExpiresAtTicks = 0;
                        Plugin.Instance.SaveConfiguration();
                    }
                    return null;
                }

                StoreTokens(config, json, keepExistingRefreshToken: true);
                return config.AccessToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static void StoreTokens(PluginConfiguration config, JsonValue json, bool keepExistingRefreshToken)
        {
            config.AccessToken = json["access_token"].AsString;

            // A refresh response usually omits refresh_token; keep the one we already hold.
            var refresh = json["refresh_token"].AsString;
            if (!string.IsNullOrEmpty(refresh)) config.RefreshToken = refresh;
            else if (!keepExistingRefreshToken) config.RefreshToken = null;

            var expiresIn = json["expires_in"].AsInt ?? 3600;
            config.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            Plugin.Instance.SaveConfiguration();
        }

        private async Task<JsonValue> PostForm(string url, Dictionary<string, string> fields, CancellationToken cancellationToken)
        {
            using (var content = new FormUrlEncodedContent(fields))
            using (var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false))
            {
                // Google returns useful error bodies with 4xx codes, so parse before checking status.
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Json.Parse(body);
            }
        }
    }
}
