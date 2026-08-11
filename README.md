# emby-youtube

> **AI-assisted project.** This codebase was created with [Claude Code](https://claude.com/claude-code)
> (Anthropic), directed and reviewed by a human author. It has been **deployed to a real Emby
> 4.9.5.0 server**, where browsing, search and the configuration page are confirmed working.
> Playback is blocked by YouTube's bot check unless cookies are supplied. See
> [Verification](#verification) for exactly what is and is not proven.

A YouTube channel plugin for Emby Server. Browse your subscriptions, see recent uploads from all of
them in one feed, get your real recommendation feed, and run saved searches — all from any Emby
client.

![The YouTube channel in Emby, showing its Latest from Subscriptions, Recommended, Searches and
Subscriptions folders](docs/screenshots/channel-root.png)

---

## What it does

| Section                        | Source                          | Needs                 |
| ------------------------------ | ------------------------------- | --------------------- |
| **Latest from Subscriptions**  | Data API uploads, merged        | Google account linked |
| **Subscriptions**              | Data API `subscriptions.list`   | Google account linked |
| **Recommended**                | Signed-in YouTube home feed     | Cookies               |
| **Trending**                   | Data API `chart=mostPopular`    | Google account linked |
| **Saved searches**             | yt-dlp `ytsearch`               | Nothing               |

Playback resolves through yt-dlp at press-play time.

## Why it is built this way

Three constraints drove every significant decision. They are worth understanding before changing
anything.

### 1. The Data API cannot do search or recommendations

`search.list` costs **100 quota units** against a default of **10,000 per day** — roughly 100
searches per day for the entire server, shared by every user. And there is no recommendations
endpoint at all: YouTube has never exposed the home-feed recommender, and
`search.list?relatedToVideoId` — the closest substitute — was **removed on 7 August 2023**.

So the plugin splits its sources. Subscriptions and uploads go through the official API, where they
cost 1 unit per call and are reliable. Search and recommendations go through yt-dlp, which is
unmetered and can read the real signed-in home feed.

### 2. Emby 4.9 removed the interfaces this kind of plugin used to rely on

`ISearchableChannel`, `ISupportsLatestMedia` and `IHasCacheKey` still exist in the SDK but are all
marked obsolete — *"no longer supported and ignored by the server."* Older channel plugins and
almost every tutorial online still use them.

The practical consequence: **there is no hook for live search from the client's search box.** Emby
crawls a channel's folders, indexes what it finds, and searches its own index. Anything a user can
reach has to be reachable by walking folders from the root.

Saved searches are the way around this. Each one you configure becomes a folder; opening it runs a
real query through yt-dlp, and the results are then indexed and become searchable like any other
library item.

### 3. An Emby media source carries exactly one URL

Above 720p, YouTube serves video and audio as separate DASH streams. `MediaSourceInfo` has a single
`Path`, and there is no way to hand Emby two URLs and have it mux them. Selecting a DASH video
stream would play silently — worse than a resolution cap.

So the plugin selects the best single file that already contains both video and audio, which in
practice means **720p**. Raising `MaxHeight` only helps for videos that happen to offer a higher
combined stream. Genuinely fixing this needs a download-and-mux step, which is a different feature.

## Installing

Build produces one self-contained DLL with no extra dependencies:

```bash
dotnet build -c Release src/Emby.Plugins.YouTube/Emby.Plugins.YouTube.csproj
```

Copy `Emby.Plugins.YouTube.dll` into Emby's `plugins` folder and restart the server. On
Docker/Unraid that is the `plugins` directory inside your mapped config volume.

Then open **Dashboard → Plugins → YouTube** and configure it.

### yt-dlp

You do not install it. The official Emby images ship neither yt-dlp nor Python, and anything
installed into a container by hand is destroyed on the next image pull — so the plugin downloads a
**self-contained** yt-dlp build into its own data folder, which lives on the mapped config volume
and survives image rebuilds. A daily scheduled task keeps it current.

Keeping it current matters: YouTube changes its player often, and a stale yt-dlp is by far the most
common reason playback suddenly stops working.

### Linking a Google account

You need your own Google Cloud project — the plugin ships no API credentials.

1. Create a project and enable **YouTube Data API v3**.
2. Create an OAuth client of type **TV and Limited Input**. Web and Desktop clients are rejected by
   the device flow outright.
3. Paste the client id and secret into the plugin settings, click **Link account**, and enter the
   code shown on the page.

The device-code flow is used because a media server is headless: there is no browser on the box and
no reliable redirect URI to come back to.

### Cookies (needed for playback, not just Recommended)

Two things need cookies:

1. **Recommended**, because YouTube only serves a personalised home feed to a signed-in session.
2. **Playback** — and this one is not optional in practice. On a server whose IP YouTube has
   flagged, resolving a video's streams fails with *"Sign in to confirm you're not a bot"*. Browsing
   and search still work unauthenticated; playback does not. Every `player_client` was tried against
   a flagged IP (`tv`, `ios`, `web_safari`, `android_vr`, `mweb`) and each returned either the bot
   check or storyboard-only formats. Cookies were the only thing that worked.

Export a `cookies.txt` in Netscape format from a browser signed in to your account and paste it into
the settings.

> These cookies grant access to your Google account. They are stored in Emby's plugin configuration
> and written to the plugin data folder on the server. Anyone with access to either can use them.
> If that is not a tradeoff you want, leave Recommended disabled — everything else works without it.

## Verification

Honest status, because "it compiles" is not "it works":

| Area                                            | Status                                          |
| ----------------------------------------------- | ----------------------------------------------- |
| Compiles against Emby 4.9.1.90 API              | **Verified** — clean build, zero warnings       |
| Single-DLL output, no stray dependencies        | **Verified** — enforced by CI                   |
| API surface matches the real 4.9 assemblies     | **Verified** — dumped by reflection, not guessed |
| Loads into a running Emby server                | **Verified** — Emby 4.9.5.0, zero errors        |
| Configuration page loads and saves              | **Verified** — round-trips to disk              |
| yt-dlp self-download + ELF interpreter patch    | **Verified** — runs in the stock Emby container |
| Live search via yt-dlp                          | **Verified** — 50 videos indexed from a search  |
| Channel appears with its folders                | **Verified** — visible in the Emby UI           |
| Subscriptions / Latest / Recommended feeds      | **Not tested** — needs OAuth and cookies        |
| Playback                                        | **Blocked without cookies** — see above         |

The API surface was dumped from `MediaBrowser.Controller.dll` and `MediaBrowser.Model.dll` 4.9.1.90
via `MetadataLoadContext` rather than copied from tutorials — which is how the obsolete-interface
problem above was caught. Everything else needs a real server.

## Legal

This plugin resolves YouTube stream URLs for personal playback, in the same category as the Kodi
YouTube add-on, NewPipe and Invidious. That is at odds with YouTube's Terms of Service, which
restrict accessing content other than through the official player. It is your call whether to run
it. No warranty, and nothing here is legal advice.

Not affiliated with, endorsed by, or connected to YouTube, Google, or Emby.

## Licence

MIT — see [LICENSE](LICENSE).
