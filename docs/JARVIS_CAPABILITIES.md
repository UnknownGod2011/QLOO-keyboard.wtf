# Jarvis Capability Boundaries

keyboard.wtf favors verified actions over brittle UI clicking. Every tool must report what actually happened, and sensitive actions must remain locally confirmation-gated.

## Shipped

| Area | Capability |
| --- | --- |
| Windows startup | Current-user `Run` registration, settings/model reload, and a single-instance guard |
| Desktop | Open common apps and Start menu entries; switch, minimize, maximize, or restore windows |
| Browser | Open safe URLs; control tabs in the foreground browser; open stable service destinations |
| Media | Open Spotify desktop search or YouTube search; open Spotify Liked Songs |
| YouTube | Open Liked Videos, Subscriptions, or History |
| Amazon | Open an Amazon India product search |
| Camera | Open Windows Camera after confirmation |
| Capture | One-shot Gemini screen guidance, desktop screenshots, and webcam photos |
| Windows recording | Xbox Game Bar toggle and Windows screen-region recording shortcuts |
| Productivity | Notes, to-dos, timers, selected text, clipboard, file search, and workflows |
| System | Volume, mute, settings pages, battery, charging, uptime, OS, machine, and user status |

## Requires Provider Authorization

- Spotify direct playback requires a Spotify developer app, OAuth, the `user-modify-playback-state` scope, an active playback device, and Spotify Premium.
- Removing YouTube likes is available through `videos.rate` with `rating=none`, but requires Google OAuth and a known video ID. Bulk changes must show a preview and require confirmation.
- Discord messaging requires a registered Discord app or Social SDK integration, OAuth communication scopes, and a stable Discord user ID. Standard user-account self-bot automation is not allowed.
- Webcam photos use a one-shot OpenCV capture after the user's explicit request and save under `Pictures\keyboard.wtf\Camera`. Windows desktop camera privacy access remains authoritative.
- `Win+Alt+R` is a toggle owned by Xbox Game Bar. keyboard.wtf can send it but cannot independently prove the resulting recording state.

## Requires A Browser Companion

Amazon cart changes and other authenticated page actions need a browser extension or equivalent companion that can:

1. Read the signed-in page DOM on an allowlisted domain.
2. Return candidate products or targets to Jarvis.
3. Show an action preview with exact item, price, quantity, account, and destination.
4. Require confirmation immediately before cart, purchase, message, delete, or bulk account changes.
5. Execute one versioned site adapter and return a verifiable receipt.

Generic screen-coordinate or keystroke automation is intentionally excluded because layout changes, ads, focus loss, and ambiguous targets can cause the wrong external action.

## References

- Windows Run keys: https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys
- Spotify playback: https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback
- YouTube ratings: https://developers.google.com/youtube/v3/docs/videos/rate
- Discord OAuth and bot accounts: https://discord.com/developers/docs/topics/oauth2
- Windows desktop camera capture UI: https://learn.microsoft.com/windows/apps/develop/camera/cameracaptureui
