**Automatic Clay Former 2.2.0** for Vintage Story 1.22. Place clay, pick a recipe, hold clay of the same type, and the form shapes itself.

**What's new:**
- **Optional: work on servers that don't have the mod.** Turn on `WorkOnUnmoddedServers` in `ModConfig/AutomaticClayFormer.json`, or `/clayformer unmodded` in single player. It's **off by default**: on a server without the mod nothing takes clay for automatic forming, so it's free there, and the server may count that as cheating. The first time it works on such a server you get a warning in chat.
- On those servers the mod can't change your clay tool, so it uses the one you selected. With the single-voxel tool it can finish any shape; with another tool it does what it can and then asks you to switch.

**Install:** put the zip in your `Mods` folder. In multiplayer the server should have it too, so automatic forming takes clay like forming by hand. The `/clayformer` commands need 2.1.0 or newer on both the server and your game; `/clayformer unmodded` needs 2.2.0 in your game.

---

**2.1.0:**

**What's new:**
- **Commands start with `/` now:** `/clayformer` instead of `.clayformer`. The subcommands are the same: `on`/`off`, `range <blocks>` and `speed <clicks per second>`.
- **`/clayformer messages`** on its own switches the chat messages on or off; `/clayformer messages on` and `off` still work.
- **`/clayformer help`** says how to use the mod and lists the commands.
- The mod's home is now [Comp-any/Automatic-Clay-Former-](https://github.com/Comp-any/Automatic-Clay-Former-) on GitHub.
- Each player's settings are still their own, kept in their game's `ModConfig/AutomaticClayFormer.json`: the server passes the command on to your game. That needs 2.1.0 on both the server and your game.

**Install:** put the zip in your `Mods` folder. In multiplayer the server needs it too, because the server is what takes clay from your inventory. On a server without it, automatic forming stays off rather than forming for free.

---

**2.0.0:** Place clay, pick a recipe, hold clay of the same type, and the form shapes itself.

Based on [Automatic Clay Former](https://mods.vintagestory.at/show/mod/26337) by rekimchuk13 and its [1.22 update](https://github.com/prescko/Automatic-Clay-Former-V1.22) by prescko.

**Install:** put the zip in your `Mods` folder. In multiplayer the server needs it too, because the server is what takes clay from your inventory. On a server without it, automatic forming stays off rather than forming for free.

**What's new:**
- **Clay is used properly.** When the form's voxel pool runs out, one clay is taken from your hand for 25 more voxels, the same as forming by hand. Before, only the clay used to place the form was ever taken.
- **No more crash when leaving a world.**
- **Much less flickering of "ghost" voxels.** Every click now moves the form towards the recipe, and the Duplicate Layer tool is used the way the game actually applies it.
- **Respects your reach.** Game 1.22.4 and later reject clay forming beyond your reach; the mod no longer keeps retrying there.
- **One form at a time**, with clay of the form's own type.
- **Settings:** `.clayformer` (from 2.1.0: `/clayformer`) shows them; `on`/`off`, `range <blocks>`, `speed <clicks per second>` (default 20) and `messages on`/`off` change them. They're saved in `ModConfig/AutomaticClayFormer.json`.
- A mod icon for the in-game mod list.
