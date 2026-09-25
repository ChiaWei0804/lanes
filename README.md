# Lanes

**English** · [繁體中文](README.zh-TW.md) · [简体中文](README.zh-CN.md)

Lanes is a portable Windows app that makes the official Bilibili desktop client download videos over many connections and CDN nodes at once. It is a standalone interface for **[Bilibili-thread-ripper (BTR)](https://github.com/MrTangLuyao/Bilibili-thread-ripper)**: the acceleration itself is BTR's page code from **[Bilibili-thread-ripper-desktop](https://github.com/MrTangLuyao/Bilibili-thread-ripper-desktop)**, unmodified. Lanes does not change any file of the Bilibili client, and when Lanes is closed the client downloads natively again.

## Features

- Live accelerated download speed, with a per-thread view (hover a cell to see its CDN node).
- On/off switch and thread count: Auto (BTR picks 8–32) or 8 / 16 / 32 / 64.
- Opening Lanes never opens Bilibili. While Lanes runs, a Bilibili you open the usual way is connected automatically (it restarts once, within seconds of opening, so Lanes can attach).
- Connects to a Bilibili that was already open before Lanes, too, by restarting it (interrupts playback). On by default; turn it off in settings.
- Start at sign-in, keep running in the notification area when the window closes.
- English (default), Traditional Chinese and Simplified Chinese interface.
- CDN region: Auto (default; tries the nodes outside and inside mainland China and uses the fastest), outside China, or mainland China.
- Looks for a new GitHub release 30 seconds after start and once a day, and tells you; it installs with one click when you choose.
- `lanes.log` in the Lanes folder, cleared at every start.

## Use

1. Put the `Lanes` folder anywhere (it is portable) and run `Lanes.exe`.
2. Open Bilibili as usual and play a video.

Requirements: Windows 10 or 11 (64-bit) and the official Bilibili desktop client. Nothing else to install: `node.exe` ships in the folder, and the window uses the .NET Framework built into Windows.

## Good to know

- Lanes attaches through the client's debugging port on `127.0.0.1:39229`, which only this computer can reach. While the client runs with it, other programs on this computer could use it too; it closes with the client.
- Whether acceleration helps depends on your network. When a single connection is already fast, the difference is small; BTR helps most when single connections are slow or stall.
- High thread counts are not always better: too many connections can make CDN nodes refuse them, and BTR then stops using those nodes until the next video. Auto is recommended.
- The update check asks `api.github.com` for this repository's latest release and sends nothing about you or your videos. If GitHub cannot be reached, the automatic check fails quietly.
- The web version of Bilibili is not covered by Lanes; for browsers, use [BTR's browser extension](https://github.com/MrTangLuyao/Bilibili-thread-ripper).

## Build from source

Needs Windows and Node.js 22 or later. In this folder:

```
node build.cjs             # builds Lanes.exe; copies the running node.exe here if none is present
node build.cjs --release   # also writes dist/Lanes-<version>.zip, the asset a GitHub release needs for in-app updates
node check.cjs             # self-checks
```

| Path | What it is |
| --- | --- |
| `btr-local.cjs` | Controller: attaches to the client, injects BTR, owns `settings.json`, takeover, updates, local API for the window |
| `app/Lanes.cs`, `app/ui.xaml` | The window (WPF), tray icon, settings page, log |
| `app/lang/*.json` | Interface text in English, Traditional Chinese and Simplified Chinese |
| `vendor/btr/` | BTR 0.9.4.2-d1 page files (commit 80ff272), unmodified, MIT license in `vendor/btr/LICENSE` |
| `version.json` | Version and the GitHub repository checked for updates |

To publish version X.Y.Z: set it in `version.json`, run `node build.cjs --release`, create a GitHub release tagged `vX.Y.Z`, and attach `dist/Lanes-X.Y.Z.zip`.

## Credits

All download logic — splitting, CDN node selection, retries, automatic thread count — comes from [Bilibili-thread-ripper](https://github.com/MrTangLuyao/Bilibili-thread-ripper) and [Bilibili-thread-ripper-desktop](https://github.com/MrTangLuyao/Bilibili-thread-ripper-desktop) by LouieTang (MIT). Lanes is an independent project and is not affiliated with BTR or Bilibili.

## License

Lanes' own code is under the [MIT License](LICENSE). The BTR files in `vendor/btr/` keep their own MIT license (`vendor/btr/LICENSE`).
