# Tools Cloud

A feature-rich fork of [CloudRedirect](https://github.com/Selectively11/CloudRedirect) that brings Steam Cloud functionality to games without native support — syncing your saves to Google Drive, OneDrive, or a local folder.

> ⚠️ **This software is experimental.** It intercepts low-level Steam Client operations. Back up any saves you care about before using it.

---

## What it does

When you play a game through SteamTools, cloud saves don't work — Steam Cloud is simply unavailable for those titles. Tools Cloud fixes this by intercepting Steam's internal cloud save requests and routing them to your chosen cloud provider. Everything runs natively inside the Steam Client, fully transparent, and looks identical to normal Steam Cloud in the UI.

**Core features (inherited from CloudRedirect):**
- 🔄 Real cloud sync via Google Drive, OneDrive, or a local folder
- 🗂️ Isolated storage per game — no cross-game save conflicts
- ☁️ Full Steam AutoCloud support
- 🔧 Built-in cleanup tools for orphaned data left by SteamTools
- 📌 Manifest pinning to lock Steam Client versions

## What's different from CloudRedirect

Tools Cloud is built on top of CloudRedirect with several additional features and improvements:

| Feature | CloudRedirect | Tools Cloud |
|---|:---:|:---:|
| Cloud sync (GDrive/OneDrive/local) | ✅ | ✅ |
| Setup & patching | ✅ | ✅ |
| Cleanup tools | ✅ | ✅ |
| **Ludusavi integration** | ❌ | ✅ |
| **Hydra save import** | ❌ | ✅ |
| **Save variant management** | ❌ | ✅ |
| **Achievement viewer & translator** | ❌ | ✅ |
| **Save history & monitoring** | ❌ | ✅ |
| **API key encryption (DPAPI)** | ❌ | ✅ |
| **Multi-language UI (EN/PT-BR/ES)** | Partial | ✅ |

### New features in detail

- **Ludusavi Scan** — Automatically detect and import save games using [Ludusavi](https://github.com/mtkennerly/ludusavi), a game save backup tool. Scans your system for known save locations and integrates them as a save source.

- **Hydra Import** — Import save files from Hydra game manager backups directly into the cloud sync system, preserving timestamps and folder structure.

- **Save Variant Management** — Switch between multiple save sources (Steam Cloud, Ludusavi, Hydra) per game. Set a primary save provider and manage backups without manual file copying.

- **Achievement Tracking** — View unlocked achievements parsed directly from cloud save data. Translate achievement names and descriptions using the Steam Web API (supports PT-BR and other languages).

- **Save History** — Background monitoring that tracks all file changes: created, modified, and deleted saves. Filterable history log with timestamps for easy debugging.

- **Security** — API keys are encrypted with Windows DPAPI (per-user), not stored in plain text. Sensitive data never touches disk unencrypted.

## How it works

Tools Cloud consists of a C++ DLL and a WPF companion app:

1. The companion app patches the SteamTools payload to load the native DLL at startup.
2. The DLL hooks Steam's internal cloud save RPC handlers via vtable interception.
3. When a game reads or writes cloud save data, the DLL redirects the calls to a local cache.
4. The saves are synced to/from your chosen cloud provider — all visible in the Steam UI.

If a game is legitimately owned, it uses normal Steam Cloud as expected.

## Supported cloud providers

- **Google Drive**
- **OneDrive**
- **Local folder / mapped drive**

## Getting started

1. Download the latest release from the [Releases page](https://github.com/spiderjunior616/ToolsCloud/releases).
2. Run the executable. In **Setup**, hit **Run All Patches**.
3. Go to **Cloud Provider**, select your provider and sign in.
4. Launch Steam. Your games should start syncing.

> **Note:** Check the releases page for supported Steam Client versions.

## Building from source

### Prerequisites

- Visual Studio 2022 (or Build Tools) with C++ and .NET 8 workloads
- CMake 3.20+

### Build

```bash
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

This builds the C++ DLL (`build/Release/cloud_redirect.dll`) and publishes the WPF app (`ui/bin/publish/ToolsCloud.exe`). The DLL is automatically embedded into the executable.

## License

This project is a fork of [CloudRedirect](https://github.com/Selectively11/CloudRedirect) by Selectively11, used with explicit permission from the original author.

> *"The license for this version of CloudRedirect is 'AS-IS, do what you want, I don't even care enough to post a license file'."*
> — Selectively11

**AS-IS**: This software is provided without warranty of any kind. Use at your own risk.

## Credits

- Original project: [CloudRedirect](https://github.com/Selectively11/CloudRedirect) by Selectively11
