**Automatic Clay Former 2.0.0** for Vintage Story 1.22. Place clay, pick a recipe, hold clay of the same type, and the form shapes itself.

Based on [Automatic Clay Former](https://mods.vintagestory.at/show/mod/26337) by rekimchuk13 and its [1.22 update](https://github.com/prescko/Automatic-Clay-Former-V1.22) by prescko.

**Install:** put the zip in your `Mods` folder. In multiplayer the server needs it too, because the server is what takes clay from your inventory. On a server without it, automatic forming stays off rather than forming for free.

**What's new:**
- **Clay is used properly.** When the form's voxel pool runs out, one clay is taken from your hand for 25 more voxels, the same as forming by hand. Before, only the clay used to place the form was ever taken.
- **No more crash when leaving a world.**
- **Much less flickering of "ghost" voxels.** Every click now moves the form towards the recipe, and the Duplicate Layer tool is used the way the game actually applies it.
- **Respects your reach.** Game 1.22.4 and later reject clay forming beyond your reach; the mod no longer keeps retrying there.
- **One form at a time**, with clay of the form's own type.
- **Settings:** `.clayformer` shows them; `.clayformer on`/`off`, `range <blocks>`, `speed <clicks per second>` (default 20) and `messages on`/`off` change them. They're saved in `ModConfig/AutomaticClayFormer.json`.
- A mod icon for the in-game mod list.
