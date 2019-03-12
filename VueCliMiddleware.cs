using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.NodeServices.Npm;
using Microsoft.AspNetCore.NodeServices.Util;
using Microsoft.AspNetCore.SpaServices.Extensions.Util;
using Microsoft.AspNetCore.SpaServices.Util;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.SpaServices.VueCli
{
    internal static class VueCliMiddleware
    {
        private const string LogCategoryName = "Microsoft.AspNetCore.SpaServices";
        private static TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(5); // This is a development-time only feature, so a very long timeout is fine

        public static void Attach(
            ISpaBuilder spaBuilder,
            string npmScriptName)
        {
            var sourcePath = spaBuilder.Options.SourcePath;
            if (string.IsNullOrEmpty(sourcePath))
            {
                throw new ArgumentException("Cannot be null or empty", nameof(sourcePath));
            }

            if (string.IsNullOrEmpty(npmScriptName))
            {
                throw new ArgumentException("Cannot be null or empty", nameof(npmScriptName));
            }

            // Start Vue CLI and attach to middleware pipeline
            var appBuilder = spaBuilder.ApplicationBuilder;
            var logger = LoggerFinder.GetOrCreateLogger(appBuilder, LogCategoryName);
            var VueCliServerInfoTask = StartVueCliServerAsync(sourcePath, npmScriptName, logger);

            // Everything we proxy is hardcoded to target http://localhost because:
            // - the requests are always from the local machine (we're not accepting remote
            //   requests that go directly to the Vue CLI middleware server)
            // - given that, there's no reason to use https, and we couldn't even if we
            //   wanted to, because in general the Vue CLI server has no certificate
            var targetUriTask = VueCliServerInfoTask.ContinueWith(
                task =>
                {
                    return new UriBuilder("http", "localhost", task.Result.Port).Uri;
                });

            SpaProxyingExtensions.UseProxyToSpaDevelopmentServer(spaBuilder, () =>
            {
                // On each request, we create a separate startup task with its own timeout. That way, even if
                // the first request times out, subsequent requests could still work.
                var timeout = spaBuilder.Options.StartupTimeout;
                return targetUriTask.WithTimeout(timeout,
                    $"The Vue CLI process did not start listening for requests " +
                    $"within the timeout period of {timeout.Seconds} seconds. " +
                    $"Check the log output for error information.");
            });
        }

        private static async Task<VueCliServerInfo> StartVueCliServerAsync(
            string sourcePath, string npmScriptName, ILogger logger)
        {
            var portNumber = TcpPortFinder.FindAvailablePort();
            logger.LogInformation($"Starting @Vue/cli on port {portNumber}...");

            var npmScriptRunner = new NpmScriptRunner(
                sourcePath, npmScriptName, $"--port {portNumber}", null);
            npmScriptRunner.AttachToLogger(logger);

            Match openBrowserLine;
            using (var stdErrReader = new EventedStreamStringReader(npmScriptRunner.StdErr))
            {
                try
                {
                    openBrowserLine = await npmScriptRunner.StdOut.WaitForMatch(
                        new Regex("DONE", RegexOptions.None, RegexMatchTimeout));
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidOperationException(
                        $"The NPM script '{npmScriptName}' exited without indicating that the " +
                        $"Vue CLI was listening for requests. The error output was: " +
                        $"{stdErrReader.ReadAsString()}", ex);
                }
            }

            var uri = new Uri($"http://localhost:{portNumber}");
            var serverInfo = new VueCliServerInfo { Port = uri.Port };

            // Even after the Vue CLI claims to be listening for requests, there's a short
            // period where it will give an error if you make a request too quickly
            await WaitForVueCliServerToAcceptRequests(uri);

            return serverInfo;
        }

        private static async Task WaitForVueCliServerToAcceptRequests(Uri cliServerUri)
        {
            // To determine when it's actually ready, try making HEAD requests to '/'. If it
            // produces any HTTP response (even if it's 404) then it's ready. If it rejects the
            // connection then it's not ready. We keep trying forever because this is dev-mode
            // only, and only a single startup attempt will be made, and there's a further level
            // of timeouts enforced on a per-request basis.
            var timeoutMilliseconds = 1000;
            using (var client = new HttpClient())
            {
                while (true)
                {
                    try
                    {
                        // If we get any HTTP response, the CLI server is ready
                        await client.SendAsync(
                            new HttpRequestMessage(HttpMethod.Head, cliServerUri),
                            new CancellationTokenSource(timeoutMilliseconds).Token);
                        return;
                    }
                    catch (Exception)
                    {
                        await Task.Delay(500);

                        // Depending on the host's networking configuration, the requests can take a while
                        // to go through, most likely due to the time spent resolving 'localhost'.
                        // Each time we have a failure, allow a bit longer next time (up to a maximum).
                        // This only influences the time until we regard the dev server as 'ready', so it
                        // doesn't affect the runtime perf (even in dev mode) once the first connection is made.
                        // Resolves https://github.com/aspnet/JavaScriptServices/issues/1611
                        if (timeoutMilliseconds < 10000)
                        {
                            timeoutMilliseconds += 3000;
                        }
                    }
                }
            }
        }


    }
}
