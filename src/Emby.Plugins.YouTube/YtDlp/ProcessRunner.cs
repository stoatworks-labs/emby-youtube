using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.YouTube.YtDlp
{
    public class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
        public bool TimedOut { get; set; }
    }

    public static class ProcessRunner
    {
        /// <summary>
        /// Escapes arguments into the single command line that .NET's parser expects.
        ///
        /// Rules: a run of backslashes immediately before a quote (or before the closing quote of a
        /// quoted argument) must itself be doubled, otherwise it escapes the quote and the argument
        /// boundary shifts. This matters here because search queries and cookie paths are attacker-
        /// adjacent user input that must never be able to inject an extra argument.
        /// </summary>
        internal static string BuildArgumentString(IReadOnlyList<string> arguments)
        {
            var builder = new StringBuilder();

            foreach (var argument in arguments)
            {
                if (builder.Length > 0) builder.Append(' ');

                var value = argument ?? string.Empty;
                var needsQuotes = value.Length == 0 || value.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) >= 0;

                if (!needsQuotes)
                {
                    builder.Append(value);
                    continue;
                }

                builder.Append('"');
                for (var i = 0; i < value.Length; i++)
                {
                    var backslashes = 0;
                    while (i < value.Length && value[i] == '\\') { backslashes++; i++; }

                    if (i == value.Length)
                    {
                        // Trailing backslashes precede the closing quote, so double them.
                        builder.Append('\\', backslashes * 2);
                        break;
                    }

                    if (value[i] == '"')
                    {
                        builder.Append('\\', backslashes * 2 + 1).Append('"');
                    }
                    else
                    {
                        builder.Append('\\', backslashes).Append(value[i]);
                    }
                }
                builder.Append('"');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Runs a child process and captures both streams.
        ///
        /// Two traps this deliberately avoids:
        ///
        /// 1. Reading one stream to completion before the other deadlocks as soon as the child
        ///    fills the pipe buffer on the stream nobody is draining. yt-dlp writes plenty to both,
        ///    so both are consumed concurrently via the async event handlers.
        ///
        /// 2. Redirecting stdin and leaving it open can leave the child waiting on input forever.
        ///    stdin is redirected and closed immediately so any prompt fails fast instead of hanging.
        /// </summary>
        public static Task<ProcessResult> Run(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // ProcessStartInfo.ArgumentList only exists from .NET Standard 2.1, and Emby
                // plugins target 2.0 — so arguments are escaped into a single string by hand.
                Arguments = BuildArgumentString(arguments)
            };

            var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Exited can fire before the final stream reads complete, so wait for all three.
            var pending = 3;
            var settled = 0;
            CancellationTokenRegistration cancelRegistration = default;
            Timer timeoutTimer = null;

            void Finish(bool timedOut)
            {
                if (Interlocked.Exchange(ref settled, 1) != 0) return;

                try { timeoutTimer?.Dispose(); } catch { /* already disposed */ }
                try { cancelRegistration.Dispose(); } catch { /* nothing registered */ }

                int exitCode;
                try { exitCode = process.HasExited ? process.ExitCode : -1; }
                catch { exitCode = -1; }

                var result = new ProcessResult
                {
                    ExitCode = exitCode,
                    StandardOutput = stdout.ToString(),
                    StandardError = stderr.ToString(),
                    TimedOut = timedOut
                };

                try { process.Dispose(); } catch { /* ignore */ }
                completion.TrySetResult(result);
            }

            void SignalPart()
            {
                if (Interlocked.Decrement(ref pending) == 0) Finish(false);
            }

            void Kill(bool timedOut)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception ex)
                {
                    logger?.Debug("YouTube: could not kill child process: {0}", ex.Message);
                }
                Finish(timedOut);
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) SignalPart(); else stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) SignalPart(); else stderr.AppendLine(e.Data);
            };
            process.Exited += (_, __) => SignalPart();

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                try { process.Dispose(); } catch { /* ignore */ }
                completion.TrySetException(ex);
                return completion.Task;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Close stdin right away: nothing is ever piped in, and an open handle can stall the child.
            try { process.StandardInput.Close(); } catch { /* ignore */ }

            if (timeout > TimeSpan.Zero)
                timeoutTimer = new Timer(_ => Kill(true), null, timeout, Timeout.InfiniteTimeSpan);

            if (cancellationToken.CanBeCanceled)
                cancelRegistration = cancellationToken.Register(() => Kill(false));

            return completion.Task;
        }
    }
}
