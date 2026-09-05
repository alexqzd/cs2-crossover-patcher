# Cities Skylines 2 — CrossOver / Wine Fix (macOS)

> [!WARNING]
> This repo is no longer actively maintained. For a more complete, actively
> maintained patcher (more fixes, guided installer, automatic bottle detection), use
> **[alien-agent/cs2-macos-patcher](https://github.com/alien-agent/cs2-macos-patcher)**
> instead — it builds on the fixes from this repo and adds several more.

Fixes crashes and **enables Paradox Mods** for Cities Skylines 2 running under CrossOver / Wine on macOS.

Confirmed working on **v1.6.0f1** and **v1.5.5f1** / CrossOver 26 / macOS 26 (Apple Silicon).

### What this fixes

| Problem | Status |
|---------|--------|
| Game crashes on launch | ✅ Fixed |
| **Paradox Mods — browse, subscribe, download & install from in-game** | ✅ Fixed |
| Mod downloads stuck at 4% (lock deadlock under Wine) | ✅ Fixed |
| Mods fail to load ("Failed to add Mod") | ✅ Fixed |

---

## Quick Fix — Download & copy the patched DLLs

**1. Download the patched DLLs**

Go to the [Releases](../../releases) page and download the zip for your game version.

> Don't know your game version? Check `Player.log` — look for the line starting with `Game version:`:
> ```
> ~/Library/Application Support/CrossOver/Bottles/Steam/drive_c/users/crossover/AppData/LocalLow/Colossal Order/Cities Skylines II/Player.log
> ```

**2. Back up your originals**

Navigate to the game's `Managed` folder:
```
~/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/
```

Rename these files (add `.bak` to the end). The v1.6.0f1 zip contains 4 DLLs;
older releases (v1.5.5f1) contain 3 — rename whichever ones are in your zip:
- `PDX.SDK.dll` → `PDX.SDK.dll.bak`
- `Colossal.IO.dll` → `Colossal.IO.dll.bak`
- `Colossal.IO.AssetDatabase.dll` → `Colossal.IO.AssetDatabase.dll.bak`
- `Colossal.PSI.Common.dll` → `Colossal.PSI.Common.dll.bak` *(v1.6.0f1+)*

**3. Copy the patched files**

Extract the zip and copy all the DLLs into the `Managed` folder.

**4. Clean up macOS invisible files**

Open Terminal and run (adjust the path to your drive):
```bash
find ~/Library/Application\ Support/CrossOver/Bottles/Steam/drive_c/Program\ Files\ \(x86\)/Steam/steamapps/common/Cities\ Skylines\ II -name '.DS_Store' -type f -delete
```

**5. Launch the game** — Paradox Mods should now work from the in-game mod browser. Browse, subscribe, and install mods just like on Windows.

> **Note:** If Steam updates the game or you verify game files, you'll need to redo steps 2–3.

---

## Advanced — Patch it yourself

If there's no pre-built DLL for your game version, you can patch your own.

<details>
<summary>Click to expand patcher instructions</summary>

**Requirements:** [.NET SDK](https://dotnet.microsoft.com/download) (`brew install --cask dotnet-sdk`)

```bash
# Clone this repo
git clone https://github.com/alexqzd/cs2-crossover-patcher
cd cs2-crossover-patcher
```

Set your Managed directory path (adjust the drive name):
```bash
MANAGED="$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed"
```

**Patch 1 — Colossal.IO.dll** (fixes launch crash):
```bash
cp "$MANAGED/Colossal.IO.dll" "$MANAGED/Colossal.IO.dll.bak"
dotnet run -- "$MANAGED/Colossal.IO.dll"          # dry run
dotnet run -- "$MANAGED/Colossal.IO.dll" --patch   # apply
```

**Patch 2 — PDX.SDK.dll + Colossal.IO.AssetDatabase.dll** (enables Paradox Mods):
```bash
cp "$MANAGED/PDX.SDK.dll" "$MANAGED/PDX.SDK.dll.bak"
cp "$MANAGED/Colossal.IO.AssetDatabase.dll" "$MANAGED/Colossal.IO.AssetDatabase.dll.bak"
dotnet run --project pdxpatcher -- "$MANAGED"          # dry run
dotnet run --project pdxpatcher -- "$MANAGED" --patch   # apply
```

Clean up macOS artifacts:
```bash
find "$MANAGED/../../.." -name '.DS_Store' -type f -delete
```

The patchers match by IL instruction patterns (not byte offsets), so they should work across game versions.

</details>

---

## How it works

The patchers use [Mono.Cecil](https://github.com/jbevain/cecil) to apply IL-level binary patches to 3 game DLLs, fixing Wine/CrossOver compatibility bugs.

### Colossal.IO.dll — Launch crash fix

Wine returns unexpected error codes from `FindNextFile`. The game throws an `IOException` for anything other than `ERROR_NO_MORE_FILES`, crashing at startup. The fix NOPs the error-check block.

Original research: [presidenzo on r/CitiesSkylines2](https://www.reddit.com/r/CitiesSkylines2/comments/1j06llw/cs2_macoswhisky_123f1/).

### PDX.SDK.dll + Colossal.IO.AssetDatabase.dll — Paradox Mods fix

Wine's `GetFileAttributesW` lies — it reports files exist when they don't (if the parent directory exists). This breaks the entire mod download pipeline. The patcher applies 87 targeted IL fixes:

<details>
<summary>Full fix table (15 fixes across 2 DLLs)</summary>

| # | DLL | Description |
|---|-----|-------------|
| 1 | PDX.SDK | NOP `IOException` throws after P/Invoke calls |
| 2 | PDX.SDK | `PathExists` → always `false` in `CreateDirectory` |
| 3 | PDX.SDK | Backslash → forward slash in path operations |
| 4 | PDX.SDK | `PathExists` → always `false` in `CreateWriteStream` |
| 5 | PDX.SDK | `PathExists` → always `false` in `CreateReadStream` |
| 6 | PDX.SDK | `PathExists` → always `false` in `DeleteFile` |
| 7 | PDX.SDK | Backslash → forward slash in `MoveFile` |
| 8 | PDX.SDK | NOP `IOException` throws in `MoveFile` |
| 9 | PDX.SDK | Bypass `PathExists` check in `DownloadFilesInManifest` |
| 10 | PDX.SDK | NOP `CancellationToken.ThrowIfCancellationRequested` (Wine spurious cancellation) |
| 11 | PDX.SDK | NOP `IsCancelledOperation` checks |
| 12 | PDX.SDK | NOP `ThrowIfCancellationRequested` in download state machines |
| 13 | PDX.SDK | Always create new file in `PerformDownload` |
| 14 | PDX.SDK | `FileAlreadyDownloaded` → always returns `false` |
| 15 | AssetDatabase | Skip `.priority` file check in `PopulateFromDirectory` |

</details>
