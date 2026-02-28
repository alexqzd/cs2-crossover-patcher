# Cities Skylines 2 — CrossOver Fix (macOS)

Fixes the crash that prevents Cities Skylines 2 from launching under CrossOver on macOS.

Confirmed working on **v1.5.5f1 / CrossOver 26 / macOS 26 (Apple Silicon)**.

---

## Quick Fix — Just copy a file

**1. Find your game version**

Open this file in a text editor and look for the line that starts with `Game version:`:
```
/Users/<you>/Library/Application Support/CrossOver/Bottles/Steam/drive_c/users/crossover/AppData/LocalLow/Colossal Order/Cities Skylines II/Player.log
```

**2. Download the patched DLL**

Go to the [Releases](../../releases) page and download the zip for your game version. It contains a single file: `Colossal.IO.dll`.

**3. Back up and replace**

Navigate to:
```
<CrossOver Bottle>/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/
```

- Rename the existing `Colossal.IO.dll` to `Colossal.IO.dll.bak`
- Copy the downloaded `Colossal.IO.dll` into that folder

**4. Two more things before launching**

Remove invisible files macOS creates in the game folder (open Terminal and paste this, replacing the path with yours):
```bash
find "/Volumes/<Drive>/CrossOver Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II" -name '.DS_Store' -type f -delete
```

Create marker files in your save data folder:
```bash
find "/Volumes/<Drive>/CrossOver Bottles/Steam/drive_c/users/crossover/AppData/LocalLow/Colossal Order/Cities Skylines II" -type d -exec touch {}/.priority \;
```

**5. Launch the game** — it should work now.

> **Note:** If Steam updates the game or you use "Verify Files", you'll need to redo step 3. Steps 4 are one-time only.

> **Mods:** Paradox Mods in-game is broken for CrossOver users. You can still install mods manually from [paradoxmods.net](https://mods.paradoxplaza.com/games/cities_skylines_2).

---

## My version isn't listed — Patcher tool

If there's no pre-built DLL for your game version, you can patch your own in a few commands.

**Requirements:** [.NET SDK](https://dotnet.microsoft.com/download) (`brew install --cask dotnet-sdk`)

```bash
# Clone this repo
git clone https://github.com/alexqzd/cs2-crossover-patcher
cd cs2-crossover-patcher

# Dry run first — shows what it will change without touching anything
dotnet run -- "/Volumes/<Drive>/CrossOver Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Colossal.IO.dll"

# Apply the patch
dotnet run -- "/Volumes/<Drive>/CrossOver Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Colossal.IO.dll" --patch
```

The patcher matches by IL pattern (not hardcoded byte offsets), so it works across game versions.

---

## Technical details

Wine returns unexpected error codes from the Win32 `FindNextFile` API. Inside `Colossal.IO.dll`, the `LongDirectory` class uses a custom filesystem iterator that checks this error code and throws an `IOException` for anything other than `ERROR_NO_MORE_FILES` (18). Under Wine, valid calls randomly return other codes, crashing the game at startup before the main menu.

The fix removes the error-check block in both iterator state machines (`EnumerateFileSystemIterator` and `EnumerateFileSystemIteratorRecursive`), so unexpected error codes are silently ignored and the iterator simply stops — which is the correct behavior.

Specifically, in each `MoveNext()` method, the following IL block is replaced with NOPs:

```
call  Marshal::GetLastWin32Error()
stloc errorCode
ldloc errorCode
ldc.i4.s 18                         // ERROR_NO_MORE_FILES
beq.s [cleanup]                     // skip throw if normal end
ldloc errorCode
ldnull
ldstr "path"
call  Helper::GetExceptionFromWin32Error(...)
throw                               // ← this is what crashes under Wine
```

Original research and fix: [presidenzo on r/CitiesSkylines2](https://www.reddit.com/r/CitiesSkylines2/comments/1j06llw/cs2_macoswhisky_123f1/).
