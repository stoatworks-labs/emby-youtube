// Controller for the YouTube plugin's configuration page.
//
// This lives in a separate file, served by YouTubeService at /emby/YouTube/configpage.js, because
// Emby 4.9 will not run an inline <script> in a plugin configuration page. Verified against the
// 4.9.5.0 web client:
//
//   * approuter registers `/configurationpage` with NO `controller` property, so
//     getControllerFactory() resolves to undefined and no view controller is ever constructed.
//   * viewmanager's normalizeNewView() un-comments `<!--<script` and then assigns the markup with
//     innerHTML — which by spec never executes a script element.
//
// The only remaining hook is the view root's `data-require` attribute, whose value is handed
// straight to require(). RequireJS treats an id beginning with "/" or ending in ".js" as a URL, so
// pointing it at this endpoint loads this module. Nothing consumes the return value, so the module
// wires itself up instead.
define([], function () {
    'use strict';

    var pluginId = '6b1e2f8a-3c47-4d5b-9a10-7e2c4f8d1b93';

    function initialise(view) {
        // viewshow fires on every visit; only wire the handlers once per view element.
        if (view.__youtubeConfigBound) {
            view.__youtubeLoad();
            return;
        }
        view.__youtubeConfigBound = true;

        var authPollTimer = null;

        function el(selector) { return view.querySelector(selector); }

        function setText(selector, text) {
            var node = el(selector);
            if (node) node.textContent = text || '';
        }

        function parseSearches(text) {
            return (text || '').split('\n')
                .map(function (line) { return line.trim(); })
                .filter(function (line) { return line.length > 0; })
                .map(function (line) {
                    var separator = line.indexOf('|');
                    if (separator === -1) return { Name: line, Query: line, SortBy: 'relevance' };
                    return {
                        Name: line.substring(0, separator).trim(),
                        Query: line.substring(separator + 1).trim(),
                        SortBy: 'relevance'
                    };
                })
                .filter(function (s) { return s.Query.length > 0; });
        }

        function formatSearches(searches) {
            return (searches || []).map(function (s) {
                return (s.Name || s.Query) + ' | ' + s.Query;
            }).join('\n');
        }

        function load(config) {
            el('.txtClientId').value = config.ClientId || '';
            el('.txtClientSecret').value = config.ClientSecret || '';
            el('.txtCookies').value = config.CookiesTxt || '';
            el('.txtRegion').value = config.RegionCode || 'GB';
            el('.txtSearches').value = formatSearches(config.SavedSearches);
            el('.txtMaxHeight').value = config.MaxHeight;
            el('.txtMinDuration').value = config.MinimumDurationSeconds;
            el('.txtMaxPerChannel').value = config.MaxVideosPerChannel;
            el('.txtMaxLatest').value = config.MaxLatestVideos;
            el('.txtYtDlpPath').value = config.YtDlpPath || '';
            el('.txtPlayerClient').value = config.PlayerClient || '';

            el('.chkLatest').checked = config.EnableLatest;
            el('.chkSubscriptions').checked = config.EnableSubscriptions;
            el('.chkRecommendations').checked = config.EnableRecommendations;
            el('.chkTrending').checked = config.EnableTrending;
            el('.chkSavedSearches').checked = config.EnableSavedSearches;
            el('.chkAutoUpdate').checked = config.AutoUpdateYtDlp;
            el('.chkDebug').checked = config.EnableDebugLogging;
        }

        function applyToConfig(config) {
            config.ClientId = el('.txtClientId').value.trim();
            config.ClientSecret = el('.txtClientSecret').value.trim();
            config.CookiesTxt = el('.txtCookies').value;
            config.RegionCode = el('.txtRegion').value.trim() || 'GB';
            config.SavedSearches = parseSearches(el('.txtSearches').value);
            config.MaxHeight = parseInt(el('.txtMaxHeight').value, 10) || 0;
            config.MinimumDurationSeconds = parseInt(el('.txtMinDuration').value, 10) || 0;
            config.MaxVideosPerChannel = parseInt(el('.txtMaxPerChannel').value, 10) || 50;
            config.MaxLatestVideos = parseInt(el('.txtMaxLatest').value, 10) || 100;
            config.YtDlpPath = el('.txtYtDlpPath').value.trim();
            config.PlayerClient = el('.txtPlayerClient').value.trim();

            config.EnableLatest = el('.chkLatest').checked;
            config.EnableSubscriptions = el('.chkSubscriptions').checked;
            config.EnableRecommendations = el('.chkRecommendations').checked;
            config.EnableTrending = el('.chkTrending').checked;
            config.EnableSavedSearches = el('.chkSavedSearches').checked;
            config.AutoUpdateYtDlp = el('.chkAutoUpdate').checked;
            config.EnableDebugLogging = el('.chkDebug').checked;
            return config;
        }

        function save() {
            setText('.saveResult', 'Saving…');
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                ApiClient.updatePluginConfiguration(pluginId, applyToConfig(config)).then(function () {
                    setText('.saveResult', 'Saved.');
                    refreshAuthStatus();
                }, function () {
                    setText('.saveResult', 'Save failed — check the server log.');
                });
            });
        }

        function refreshAuthStatus() {
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('YouTube/Auth/Status'),
                dataType: 'json'
            }).then(function (status) {
                var message = status.Message || '';
                if (status.State === 'linked') {
                    message = 'Linked. Subscriptions and uploads are available.';
                } else if (status.State === 'unlinked') {
                    message = 'Not linked. Subscriptions and the Latest feed will be empty.';
                }
                if (!status.HasCookies) {
                    message += ' No cookies saved, so the Recommended feed is unavailable.';
                }
                setText('.authStatus', message);

                var codeBox = el('.authCode');
                if (status.State === 'pending' && status.UserCode) {
                    codeBox.style.display = '';
                    setText('.authUserCode', status.UserCode);
                    var link = el('.authUrl');
                    link.textContent = status.VerificationUrl;
                    link.href = status.VerificationUrl;
                } else {
                    codeBox.style.display = 'none';
                    if (authPollTimer) { clearInterval(authPollTimer); authPollTimer = null; }
                }
            }, function () {
                setText('.authStatus', 'Could not read the link status.');
            });
        }

        function refreshYtDlpStatus() {
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('YouTube/YtDlp/Status'),
                dataType: 'json'
            }).then(function (status) {
                setText('.ytDlpStatus', status.Installed
                    ? 'yt-dlp ' + (status.Version || 'unknown') + ' at ' + status.Path
                    : (status.Message || 'yt-dlp is not installed.'));
            }, function () {
                setText('.ytDlpStatus', 'Could not read the yt-dlp status.');
            });
        }

        function startLink() {
            setText('.authStatus', 'Starting…');
            // Save first so the server has the client id and secret this flow needs.
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                ApiClient.updatePluginConfiguration(pluginId, applyToConfig(config)).then(function () {
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('YouTube/Auth/Start'),
                        dataType: 'json'
                    }).then(function (status) {
                        if (status.State === 'error') {
                            setText('.authStatus', status.Message || 'Could not start the device flow.');
                            return;
                        }
                        refreshAuthStatus();
                        if (authPollTimer) clearInterval(authPollTimer);
                        authPollTimer = setInterval(refreshAuthStatus, 5000);
                    }, function () {
                        setText('.authStatus', 'Could not start the device flow.');
                    });
                });
            });
        }

        function loadAll() {
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                load(config);
                refreshAuthStatus();
                refreshYtDlpStatus();
            }, function () {
                setText('.saveResult', 'Could not load the plugin configuration.');
            });
        }
        view.__youtubeLoad = loadAll;

        el('.youtubeConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            save();
            return false;
        });

        el('.btnLink').addEventListener('click', startLink);

        el('.btnUnlink').addEventListener('click', function () {
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('YouTube/Auth/Unlink'),
                dataType: 'json'
            }).then(refreshAuthStatus);
        });

        el('.btnUpdateYtDlp').addEventListener('click', function () {
            setText('.ytDlpStatus', 'Downloading…');
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('YouTube/YtDlp/Update'),
                dataType: 'json'
            }).then(function (status) {
                setText('.ytDlpStatus', status.Installed
                    ? 'yt-dlp ' + (status.Version || 'unknown') + ' at ' + status.Path
                    : (status.Message || 'Update failed.'));
            }, function () {
                setText('.ytDlpStatus', 'The update failed. Check the server log.');
            });
        });

        el('.btnTestSearch').addEventListener('click', function () {
            setText('.testResult', 'Searching…');
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('YouTube/TestSearch', { Query: 'lofi' }),
                dataType: 'json'
            }).then(function (result) {
                setText('.testResult', result.Success
                    ? 'OK — ' + result.Count + ' results: ' + result.Titles.join('; ')
                    : (result.Message || 'Search failed.'));
            }, function () {
                setText('.testResult', 'The test search request failed.');
            });
        });

        view.addEventListener('viewdestroy', function () {
            if (authPollTimer) { clearInterval(authPollTimer); authPollTimer = null; }
        });

        loadAll();
    }

    // viewshow is dispatched on the view element and bubbles, so a capturing document listener
    // catches it without needing a reference Emby never hands us.
    document.addEventListener('viewshow', function (e) {
        var target = e.target;
        if (target && target.querySelector && target.querySelector('.youtubeConfigForm')) {
            initialise(target);
        }
    }, true);

    // The view may already be attached by the time require() resolves this module, in which case
    // its viewshow has been and gone — so pick it up directly as well.
    var existing = document.querySelector('.youtubeConfigForm');
    if (existing) {
        var root = existing.closest('.view') || existing.parentElement;
        if (root) initialise(root);
    }

    return {};
});
