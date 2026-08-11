using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.YouTube.YtDlp;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.YouTube.ScheduledTasks
{
    /// <summary>
    /// Keeps yt-dlp current.
    ///
    /// This is not optional maintenance: YouTube changes its player and signature scheme often
    /// enough that a yt-dlp build more than a few weeks old routinely stops resolving streams
    /// entirely. An out-of-date binary is the single most likely cause of "nothing plays any more".
    /// </summary>
    public class YtDlpUpdateTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly YtDlpBootstrap _bootstrap;

        public YtDlpUpdateTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("YouTubeYtDlpUpdate");
            _bootstrap = new YtDlpBootstrap(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, _logger);
        }

        public string Name => "Update yt-dlp";
        public string Description => "Downloads the latest yt-dlp build used by the YouTube channel to resolve streams.";
        public string Category => "YouTube";
        public string Key => "YouTubeYtDlpUpdate";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress?.Report(0);

            if (!Plugin.Instance.Configuration.AutoUpdateYtDlp)
            {
                _logger.Info("YouTube: automatic yt-dlp updates are disabled; skipping.");
                progress?.Report(100);
                return;
            }

            try
            {
                await _bootstrap.UpdateIfNeeded(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never fail the task outright: a failed update leaves the previous working binary
                // in place, which is far better than a red task blocking the schedule.
                _logger.ErrorException("YouTube: yt-dlp update failed; keeping the installed copy.", ex);
            }

            progress?.Report(100);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                // Daily, offset into the small hours so it does not collide with library scans.
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
                    MaxRuntimeTicks = TimeSpan.FromMinutes(15).Ticks
                }
            };
        }
    }
}
