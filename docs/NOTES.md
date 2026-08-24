# Notes

Working notes for this repo: status, decisions, and the traps that have actually bitten.
Migrated out of Claude Code's memory on 2026-08-24, so they are written in the first
person and dated by when each thing was learned — that date is usually the useful part.

Cross-cutting notes that are not specific to this repo live in
[fleet-notes](https://github.com/stoatworks-labs/fleet-notes).

*emby-youtube — PUBLIC Emby channel plugin for YouTube subs/uploads/recommendations/search*

**emby-youtube** — PUBLIC repo `stoatworks-labs/emby-youtube`, at `~/Projects/emby-youtube`.
Emby Server channel plugin: subscriptions, merged recent-uploads feed, signed-in recommendation
feed, trending, saved searches. C#, `netstandard2.0`, single DLL, MIT. Created 2026-08-11.

**Deliberate two-source split** (do not "simplify" it): **Data API v3 + OAuth** for
subscriptions/uploads/trending, **yt-dlp** for search + recommendations + stream resolution.
Forced by quota and missing endpoints — see [youtube api limits](https://github.com/stoatworks-labs/fleet-notes/blob/main/notes/reference_youtube_api_limits.md).

**Load-bearing constraints:**
- Live search from the client search box is **impossible** in Emby 4.9 (searchable-channel interface
  is obsolete/ignored). Saved searches are exposed as folders as the workaround.
  See [emby plugin api traps](https://github.com/stoatworks-labs/fleet-notes/blob/main/notes/reference_emby_plugin_api_traps.md).
- **720p ceiling is intentional.** `MediaSourceInfo` carries exactly ONE url; above 720p YouTube
  serves separate DASH video+audio. Making the format selector prefer highest resolution gives
  **silent playback**. Do not "fix" it.
- **Resolve at play time, never at scan time** — googlevideo URLs expire in hours and are IP-locked.
- yt-dlp is self-managed into the plugin data folder: the Emby Docker image ships neither yt-dlp
  nor Python, and hand-installed binaries die on the next image pull. Daily update task; a stale
  yt-dlp is the usual cause of "nothing plays".
- Item ids (`subs`, `ch:`, `v:`, `q:base64url`) are persisted in Emby's library DB — treat the
  scheme in `Util/ItemId.cs` as a wire format.

**Status (2026-08-11): DEPLOYED to `lilnasx` Emby 4.9.5.0** (container `EmbyServer`, plugins dir
`/mnt/user/appdata/EmbyServer/plugins/`). **Loads cleanly** — assembly loaded by name/version,
`ServerEntryPoint` ran, zero errors/exceptions in the log. **Still unverified:** whether the channel
is visible in the UI, any feed, playback, the OAuth flow. Those need Allan's Google credentials +
dashboard access. Rollback = delete the DLL and restart the container.

**Deploying immediately exposed a real blocker** the local build could never have caught: the Emby
container has **no `/lib64`**, so yt-dlp's binaries fail with a bare "not found" — see
[emby container no lib64](https://github.com/stoatworks-labs/fleet-notes/blob/main/notes/reference_emby_container_no_lib64.md). Fixed by patching PT_INTERP at download time. **Fix deployed
and PROVEN on the server**: a patched binary ran natively in a fresh container with `NO_LIB64`
confirmed, reporting `2026.07.04`.

Mid-deploy the NAS hit an unrelated `/boot` flash failure that looked like ssh rate limiting —
see [unraid flash config loss](https://github.com/stoatworks-labs/fleet-notes/blob/main/notes/reference_unraid_flash_config_loss.md). Not caused by the plugin work.

Cookies (needed for Recommended) grant Google account access and are stored in plugin config +
written to the server's plugin data folder.
