# Automatic Clay Former

A Vintage Story 1.22 mod that shapes clay forms for you. Place clay, pick a recipe, hold clay of the same type, and the form shapes itself. It uses clay exactly like forming by hand.

Based on [Automatic Clay Former](https://mods.vintagestory.at/show/mod/26337) by **rekimchuk13** and the [1.22 update](https://github.com/prescko/Automatic-Clay-Former-V1.22) by **prescko**.

## How to use

1. Place clay on the ground (sneak + right click) and pick a recipe, as usual.
2. Hold clay of the same type as the form.
3. The nearest clay form in range gets shaped, a few clicks at a time. Run out of clay and it pauses until you hold more.

## Install

Put the mod zip in your `Mods` folder.

- **Single player:** that's all.
- **Multiplayer:** the server needs the mod too, because the server is what takes clay from your inventory. If a server doesn't have it, automatic forming stays off there (you'll get a chat message) instead of forming for free. Players without the mod can still join a server that has it.
- **Servers without the mod, if you choose:** turn on `WorkOnUnmoddedServers` (or `/clayformer unmodded` in single player) and automatic forming works there too. Nothing takes clay for it there, so it's free, and the server may count that as cheating: you get a warning in chat the first time it works on such a server. It can't change your clay tool there, so it uses the one you selected (pick the single-voxel tool to finish any shape). Off by default.

## Settings

Type `/clayformer` in chat to see your current settings, and `/clayformer help` for the commands.

| Command | What it does |
| --- | --- |
| `/clayformer on` / `/clayformer off` | Turn automatic forming on or off |
| `/clayformer range <blocks>` | How far away forms get shaped, 1 to 16. The game only accepts clay forming within your reach (4.5 blocks in survival), so higher values only help if your reach is longer. |
| `/clayformer speed <clicks per second>` | How fast it works, 1 to 100. Default 20. Lower it if you see voxels flicker on a laggy server. |
| `/clayformer messages` | Switches chat messages when forming starts and finishes on or off. Add `on` or `off` to choose. Problems like running out of clay are always shown. |
| `/clayformer unmodded` | Switches whether automatic forming also works on servers without the mod (off by default; see Install). Add `on` or `off` to choose. |
| `/clayformer server on` / `off` | **Server admins:** allow or stop automatic forming for everyone on the server. Without `on`/`off` it switches. |
| `/clayformer help` | How to use the mod, and these commands |

**For server admins:** `/clayformer server off` turns automatic forming off for everyone on the server: every player's game (2.3.0 or newer) stops forming automatically there, whatever their own settings, and says so in chat, also when they join. `/clayformer server on` allows it again. It's saved in the server's `ModConfig/AutomaticClayFormerServer.json` (`"AllowAutomaticForming": true`), which you can also edit while the server is stopped. Needs the `controlserver` privilege.

Each player's settings are their own: the server passes `/clayformer` on to your game, which keeps the settings. That needs 2.1.0 or newer on both. (Before 2.1.0 the commands started with a dot: `.clayformer`.)

Settings are saved to `ModConfig/AutomaticClayFormer.json`, which you can also edit while the game is closed:

```json
{
  "Enabled": true,
  "Range": 4.5,
  "ActionsPerSecond": 20.0,
  "ChatMessages": true,
  "WorkOnUnmoddedServers": false
}
```

## Changes from the 1.22 update by prescko

- **Clay is used properly.** Each add costs clay the same way forming by hand does: when the form's voxel pool is empty, one clay is taken from your hand for 25 more voxels. Before, only the clay used to place the form was ever taken.
- **No more crash when leaving a world.** The old cleanup code called itself forever, which overflowed the stack and closed the game without an error.
- **Much less flickering of "ghost" voxels.** The old version used the Duplicate Layer tool as if it worked where you click (it fills the first 4 cells in the layer), placed voxels it then had to remove, and replayed cached clicks from a different starting shape. Every click now only moves the form towards the recipe.
- **Respects your reach.** Game 1.22.4 and later reject clay forming beyond your reach. The old version worked forms up to 6 blocks away, so the server kept undoing its changes in an endless loop. Range is now capped at your reach, and if the server keeps rejecting changes it backs off instead of spamming.
- **One form at a time, with the right clay.** With two forms in range, the old version ran two workers that kept switching each other's tool, and it accepted any item with "clay" in its name. Now it works the nearest form whose clay type matches what you hold.
- **Configurable range and speed** (see above).
- **Tool mode synced through the mod's own channel** instead of a hand-built internal game packet that could break with game updates.
- Messages are actually translated (the old ones showed raw keys like `clayformer:msg-started`).

## Building

You need the .NET 10 SDK and a Vintage Story install (client or server) to compile against.

```bash
# Point at your game folder, e.g. %APPDATA%\Vintagestory on Windows
dotnet build AutomaticClayFormer.csproj -c Release -p:VintageStoryDir="/path/to/Vintagestory"
```

A Release build writes the mod zip to `release/`. You can also set the `VINTAGE_STORY` environment variable instead of passing `VintageStoryDir`.

GitHub Actions builds every push against the official Vintage Story server files, runs the tests, and uploads the zip as a build artifact. A push to `main` with a new version in `modinfo.json` (one with no release yet) also publishes it once it has built and passed the tests: it tags `v` + the version and puts the zip on the release, with `RELEASE_NOTES.md` as its notes. **Actions → Build → Run workflow** with **Publish** ticked, or pushing a tag like `v2.1.0`, does the same (and replaces the zip of an existing release).

### Tests

`tests/PlannerTests` runs the forming logic against every vanilla clay forming recipe, plus hundreds of random shapes, using a copy of the game's clay form rules. It checks that every click only moves the form towards the recipe, that every recipe gets finished, and that clay use is in line with the game's own estimate.

```bash
dotnet run --project tests/PlannerTests/PlannerTests.csproj -c Release -p:VintageStoryDir="/path/to/Vintagestory" -- "/path/to/Vintagestory"
```

## Credits

- Original mod: **rekimchuk13**
- 1.22 update: **prescko**
- Clay charging, bug fixes and settings: **demonic69**
