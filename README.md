# Cities Skylines 2 — CrossOver DLL Patcher

Patches `Colossal.IO.dll` from any version of Cities Skylines 2 to fix the crash that prevents the game from launching under Wine/CrossOver on macOS.

## What it fixes

Wine returns unexpected error codes from `FindNextFile`, which triggers an `IOException` inside `LongDirectory.EnumerateFileSystemIterator` and `LongDirectory.EnumerateFileSystemIteratorRecursive`. The game crashes at startup before reaching the main menu.

The patcher removes the Win32 error-check block in both iterator state machines, so Wine's bogus error codes are silently ignored instead of crashing the game.

Confirmed working on:
- Cities Skylines 2 v1.5.5f1 / CrossOver 26 / macOS 26 (Apple Silicon)

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) (`brew install --cask dotnet-sdk`)

## Usage

```bash
# Dry run — inspect what will be patched without modifying anything
dotnet run -- "/path/to/bottle/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Colossal.IO.dll"

# Apply the patch
dotnet run -- "/path/to/bottle/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Colossal.IO.dll" --patch
```

For a default CrossOver Steam bottle on an external drive, the path is typically:

```bash
dotnet run -- "/Volumes/<DriveName>/CrossOver Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Colossal.IO.dll" --patch
```

## Re-patching after game updates

Steam updates or "Verify files" will overwrite `Colossal.IO.dll`. Just run the patch command above again — the patcher matches by IL pattern, not hardcoded byte offsets, so it works across game versions.

## Other required steps

The DLL patch alone may not be enough. You also need:

1. **Remove `.DS_Store` files** from the game folder (Finder creates these silently):
   ```bash
   find "/path/to/Cities Skylines II" -name '.DS_Store' -type f -delete
   ```

2. **`.priority` files** in every subfolder of your user data directory:
   ```bash
   find "/path/to/bottle/drive_c/users/crossover/AppData/LocalLow/Colossal Order/Cities Skylines II" -type d -exec touch {}/.priority \;
   ```

## Notes

- Mods via Paradox Mods are still broken (different DLL, `PDX.SDK.dll`). Install mods manually via [Paradox Mods](https://mods.paradoxplaza.com/games/cities_skylines_2) if needed.
- Original research and fix by [presidenzo](https://www.reddit.com/r/CitiesSkylines2/comments/1j06llw/cs2_macoswhisky_123f1/).
