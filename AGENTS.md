# AGENTS.md — bringing an LLM up to speed on emby-youtube

Orientation for an AI assistant (or a new human) picking this project up cold. This file explains
the model, the traps, and an honest account of what is verified and what is not.

---

## 1. What this is

An Emby Server channel plugin exposing YouTube: subscriptions, a merged recent-uploads feed, the
signed-in recommendation feed, trending, and saved searches. C#, `netstandard2.0`, single DLL.
Public repo, MIT.

## 2. Read this before writing any Emby code

**Do not trust Emby plugin tutorials, the `MediaBrowser/Emby.Channels` samples, or Jellyfin code.**
All three are years out of date relative to the 4.9 API, and they will produce code that compiles
against nothing or silently does nothing.

Concretely, things that are wrong in the samples:

| Sample says                                        | Emby 4.9 reality                                     |
| -------------------------------------------------- | ---------------------------------------------------- |
| `GetChannelItemMediaInfo` returns `ChannelMediaInfo` | Returns `MediaSourceInfo`                            |
| `ISearchableChannel` gives you live search          | `[Obsolete]`, **ignored by the server**              |
| `ISupportsLatestMedia` gives you a Latest row       | `[Obsolete]`, **ignored by the server**              |
| `IHasCacheKey` invalidates the cache                | `[Obsolete]`, **ignored by the server**              |
| `IChannel` has `DataVersion` / `GetChannelFeatures` | Neither exists on the interface                      |
| `MediaBrowser.Model.Logging` old logger             | `ILogger` via `ILogManager.GetLogger(name)`          |

**The reliable method** — and how the table above was produced — is to dump the actual assemblies by
reflection. Do this rather than guessing:

```bash
curl -sL -o mbsc.nupkg "https://www.nuget.org/api/v2/package/MediaBrowser.Server.Core/4.9.1.90"
curl -sL -o mbcommon.nupkg "https://www.nuget.org/api/v2/package/MediaBrowser.Common/4.9.1.90"
# unzip both, then load lib/netstandard2.0/*.dll with System.Reflection.MetadataLoadContext
```

The interfaces live in `MediaBrowser.Controller.dll`; the DTOs and enums in `MediaBrowser.Model.dll`.

## 3. Architecture and why

Three hard constraints shape everything. Do not "simplify" past them.

### Metadata is deliberately split across two sources

`search.list` costs **100 quota units** of a **10,000/day** budget — about 100 searches per day for
the whole server. There is **no recommendations endpoint**, and `relatedToVideoId` was removed on
7 August 2023.

So: **Data API** for subscriptions/uploads/trending (1 unit per call), **yt-dlp** for search and
recommendations (unmetered, and the only route to the real home feed). If you find yourself adding
`search.list`, stop — you will burn the day's quota in a hundred searches.

### Everything must be reachable by walking folders

Because the searchable-channel interface is ignored, Emby crawls folders from the root, indexes what
it finds, and searches its own index. There is no callback for a live query from the client's search
box. Saved searches exist as folders precisely to work around this.

### One URL per media source

`MediaSourceInfo.Path` is a single URL. Above 720p YouTube only serves separate DASH video and audio
streams, so the format selector deliberately picks a muxed progressive file. **Do not "improve" the
selector to pick the highest resolution** — you will get silent playback, and it will look like a
codec bug.

## 4. Layout

```
src/Emby.Plugins.YouTube/
  Plugin.cs                 BasePlugin<PluginConfiguration>; owns PluginDataPath
  YouTubeChannel.cs         IChannel + IRequiresMediaInfoCallback; folder routing lives here
  ServerEntryPoint.cs       Writes cookies to disk at startup
  Api/
    GoogleOAuth.cs          Device-code flow, refresh, invalid_grant handling
    YouTubeDataClient.cs    subscriptions / playlistItems / videos. No search, on purpose
    Models.cs               YouTubeVideo, YouTubeChannelInfo
  YtDlp/
    YtDlpBootstrap.cs       Downloads + updates the managed binary
    YtDlpClient.cs          Search, recommendations, stream resolution
    ProcessRunner.cs        Subprocess handling
  Services/YouTubeService.cs  REST endpoints for the config page
  ScheduledTasks/           Daily yt-dlp update
  Util/
    ItemId.cs               Folder/item id scheme — see below
    Json.cs                 Hand-rolled JSON reader
  Configuration/            PluginConfiguration + configPage.html
```

## 5. Traps

**Item ids are persisted in Emby's library database.** `Util/ItemId.cs` defines the scheme
(`subs`, `ch:{id}`, `v:{id}`, `q:{base64url}`, …). Changing a prefix orphans everything already
indexed. Treat it as a wire format.

**The subprocess runner must drain both pipes concurrently.** Reading stdout to completion before
stderr deadlocks the moment the child fills the unread pipe buffer, and yt-dlp writes plenty to
both. `ProcessRunner` also redirects stdin and closes it immediately, so a child that decides to
prompt fails fast instead of hanging forever.

**`ProcessStartInfo.ArgumentList` does not exist in `netstandard2.0`** — it arrived in 2.1. Arguments
are escaped by hand in `ProcessRunner.BuildArgumentString`. That function's backslash-doubling rules
are not decorative: search queries are user input, and sloppy quoting is an argument-injection bug.

**`System.Memory` must be pinned to 4.6.x.** `MediaBrowser.Model` binds against assembly version
`4.0.2.0`; the widely-suggested 4.5.5 package only carries `4.0.1.2` and fails the build with
CS1705. It is reference-only — the server ships it at runtime.

**The plugin must stay a single DLL.** Emby loads a bare assembly from its plugins folder and does
**not** resolve NuGet dependencies at runtime. Every `PackageReference` is
`PrivateAssets="all" ExcludeAssets="runtime"`, and the JSON reader is hand-rolled rather than pulling
in `Newtonsoft.Json`. If you add a dependency that lands in the output folder, the plugin will fail
to load on a real server with a type-load error.

**Resolve at play time, never at scan time.** googlevideo URLs expire in hours and are tied to the
requesting IP. A URL cached during a library scan is dead before anyone presses play.

**A stale yt-dlp is the usual cause of "nothing plays".** Check its version before debugging
anything else.

## 6. Build

```bash
dotnet build -c Release src/Emby.Plugins.YouTube/Emby.Plugins.YouTube.csproj
```

Output must be exactly `Emby.Plugins.YouTube.dll` (plus pdb/deps). Any other assembly in that folder
is a bug — see the single-DLL trap above.

## 7. Verified vs not

**Verified:** compiles clean against Emby 4.9.1.90 with zero warnings; single-DLL output confirmed
by inspecting the build folder; the API surface used was dumped from the real assemblies.

**Not verified — no Emby server was available:** loading into a server, folder browsing, any feed,
playback, the OAuth device flow end to end, the yt-dlp download path, and stream resolution.

There is no container runtime on the development machine, so CI is the only build proof. Do not
claim any runtime behaviour works until someone has actually run it.
