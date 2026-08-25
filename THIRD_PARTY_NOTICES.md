# Third-Party Notices

## WPF UI

- Official project: https://github.com/lepoco/wpfui
- Package: WPF-UI 4.3.0
- License: MIT
- Copyright: 2021-2025 Leszek Pomianowski and WPF UI Contributors

SteamSwitch uses WPF UI's maintained Fluent control themes and selected controls to provide consistent Windows 11 interaction states while preserving SteamSwitch's own layout and visual tokens. The complete license is included in `licenses/WPF-UI-LICENSE.md`.

## Lyricify Lyrics Helper

- Official project: https://github.com/WXRIW/Lyricify-Lyrics-Helper
- License: Apache License 2.0
- Copyright: Lyricify contributors

SteamSwitch's optional taskbar-lyrics experience is inspired by the user-facing workflow of Lyricify Lyrics Helper. SteamSwitch's media-session integration, taskbar UI, settings, and lyrics synchronization were written independently and do not copy or redistribute Lyricify source code or binaries.

## LRCLIB

- Official project: https://github.com/tranxuanthang/lrclib
- Service: https://lrclib.net/

When the user enables online lyrics, SteamSwitch sends the current media title, artist, album, and duration to LRCLIB's public API to locate synchronized lyrics. This integration is optional and falls back to local Windows media-session metadata when unavailable.

## TranslucentTB

- Official project: https://github.com/TranslucentTB/TranslucentTB
- License: GNU General Public License v3.0 (GPL-3.0)
- Copyright: TranslucentTB contributors

SteamSwitch's optional system-taskbar appearance feature is inspired by the user-facing functionality of TranslucentTB: normal, opaque, clear, blur, acrylic, color customization, and dynamic maximized-window appearance.

The SteamSwitch implementation was written independently against Windows window-composition APIs. It does not copy, bundle, link, modify, or redistribute TranslucentTB source code or binaries. TranslucentTB is not required to use SteamSwitch. If TranslucentTB is already running, SteamSwitch releases taskbar appearance control to avoid conflicts.

## TcNo Account Switcher

- Official project: https://github.com/TCNOco/TcNo-Acc-Switcher
- License: GNU General Public License v3.0 (GPL-3.0)
- Copyright: TcNo Account Switcher contributors

SteamSwitch's password-free per-account desktop shortcut is inspired by the user-facing shortcut and command-line workflow of TcNo Account Switcher. SteamSwitch's implementation was written independently using Windows shortcut automation, its existing Steam account model, and a current-user-only named pipe. It does not copy, bundle, link, modify, or redistribute TcNo Account Switcher source code or binaries.

## Ludusavi

- Official project: https://github.com/mtkennerly/ludusavi
- License: MIT
- Copyright: Ludusavi contributors

Ludusavi's backup preview, custom entries, multi-store coverage, and retention capabilities were considered during competitive analysis. SteamSwitch independently implements Steam-account-aware `userdata` change analysis, content deduplication, integrity verification, and per-type retention. No Ludusavi source code or data manifests are bundled.
