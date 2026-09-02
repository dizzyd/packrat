using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Packrat;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

/// <summary>
/// Produces the screenshot set for the ModDB listing. Not part of the test suite - run it with
/// scripts/make-shots.sh, which boots a client with a 1080p window first.
///
/// Packrat is a UI mod, so unlike the block mods in this workspace almost every frame here is
/// of a dialog rather than of the world. That changes what the scene has to get right: the room
/// behind the window is set dressing, but it has to be *legible* set dressing, because the whole
/// claim of the mod is "all of that, in one window" and a viewer can only check it against a
/// room they can see the containers in.
/// </summary>
public class ModDbShots
{
    // The storeroom, in plot-local coordinates. Interior x 20..28, z 21..28, y 1..3 -
    // comfortably inside RoomRegistry's MAXROOMSIZE of 14 in any dimension, so the whole thing
    // registers as one enclosed room and the scan runs in room mode rather than falling back to
    // the 5-block outdoor radius.
    //
    // Everything here goes through P() or V(). A bare BlockPos is a *world* position, and the
    // runner hands each test a plot somewhere out in that world - build with raw coordinates and
    // the room lands near the world origin in an unloaded chunk, where SetBlock succeeds, no
    // block entity is ever created, and the frame comes back showing empty ground.
    const int MinX = 20, MaxX = 28, MinZ = 21, MaxZ = 28, Ceil = 4;

    /// <summary>Plot-local block position.</summary>
    static BlockPos At(int x, int y, int z) => P(x, y, z);

    /// <summary>Plot-local point, for camera and aim.</summary>
    static Vec3d V(double x, double y, double z)
    {
        var o = Plot.Origin;
        return new Vec3d(o.X + x, o.Y + y, o.Z + z);
    }

    const string Wall = "game:claybricks-good-fire";
    const string Floor = "game:planks-oak-ud";
    const string Torch = "game:torch-basic-lit-up";

    static string Out(string name) => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("VSTK_SHOT_DIR") ?? "/tmp", name);

    // ---------------------------------------------------------------- scene

    /// <summary>
    /// A brick storeroom with a plank floor and ceiling, lit by torches standing in the corners.
    ///
    /// Enclosed on purpose. Packrat opens every container in the room the player is standing in,
    /// so an open-topped set would quietly switch the scan to its outdoor rules and half these
    /// containers would not be in the window.
    /// </summary>
    static async Task BuildStoreroom()
    {
        for (int x = MinX - 1; x <= MaxX + 1; x++)
        for (int z = MinZ - 1; z <= MaxZ + 1; z++)
        for (int y = 0; y <= Ceil; y++)
        {
            bool edge = x < MinX || x > MaxX || z < MinZ || z > MaxZ;
            string code =
                y == 0 || y == Ceil ? Floor :
                edge ? Wall : "game:air";
            World.SetBlock(code, At(x, y, z));
        }

        // Torches on the floor rather than sconced: the wall variants attach to the block behind
        // them and SetBlock ignores placement rules, so a wall torch here renders half-buried in
        // the brick. They pair up beside the corners because every other cell along the side walls
        // has a container in it.
        World.SetBlock(Torch, At(MinX, 1, MinZ));
        World.SetBlock(Torch, At(MaxX, 1, MinZ));
        World.SetBlock(Torch, At(MinX, 1, MaxZ));
        World.SetBlock(Torch, At(MaxX, 1, MaxZ));
        World.SetBlock(Torch, At(MinX, 1, MinZ + 1));
        World.SetBlock(Torch, At(MaxX, 1, MinZ + 1));

        await Ticks(10);
    }

    /// <summary>
    /// The containers, and what is in them.
    ///
    /// Deliberately a mixture of types - chests, trunks, a labelled chest, baskets, a storage
    /// vessel and crates - because "every container" is the claim, and a wall of identical chests
    /// does not demonstrate it. Every one of these is a type Packrat's scan registry picks up:
    /// BlockEntityCrate, or something under BlockEntityGenericTypedContainer.
    ///
    /// They are also stocked deep rather than with a token stack apiece. The browser shows every
    /// slot of every container, empties included, so a lightly-stocked storeroom photographs as a
    /// window of blanks - which is the opposite of the thing being advertised.
    /// </summary>
    static async Task StockStoreroom()
    {
        // Back wall (north, low z), facing the player. One theme per container, so the coloured
        // container bands in the window read as "this lot came from the smithing chest".
        //
        // Mind the gaps. A trunk is two blocks long - it places a multiblock part in the cell
        // beside it, a south-facing one to its west and a west-facing one to its north - and that
        // part overwrites whatever is already there. Put a container in a trunk's second cell and
        // it silently disappears from the room and from the window; VerifyPlaced below is what
        // catches it when this layout is next moved around.
        Put("game:chest-south", 21, MinZ,
            ("game:ingot-copper", 12), ("game:ingot-tinbronze", 8), ("game:ingot-iron", 5),
            ("game:ingot-bismuthbronze", 6), ("game:nugget-nativecopper", 24),
            ("game:nugget-cassiterite", 9), ("game:nugget-nativegold", 3),
            ("game:metalplate-copper", 7), ("game:metalplate-tinbronze", 4),
            ("game:metalbit-copper", 41), ("game:metalbit-iron", 18), ("game:ore-quartz", 22),
            ("game:flint", 16), ("game:gear-temporal", 2));

        Put("game:chest-south", 22, MinZ,
            ("game:pickaxe-copper", 1), ("game:axe-felling-copper", 1), ("game:shovel-copper", 1),
            ("game:knife-generic-copper", 1), ("game:hammer-copper", 1), ("game:saw-copper", 1),
            ("game:scythe-copper", 1), ("game:chisel-copper", 1), ("game:hoe-copper", 1),
            ("game:prospectingpick-copper", 1), ("game:shears-copper", 1), ("game:wrench-copper", 1));

        // The larder. A storage vessel slows spoilage on its own, which is why Packrat sends
        // perishable food here ahead of the chest standing right next to it.
        Put("game:storagevessel-brown-fired", 23, MinZ,
            ("game:cheese-cheddar-1slice", 6), ("game:butter-salted", 3), ("game:honeycomb", 5),
            ("game:egg-chicken-raw", 8), ("game:redmeat-raw", 4), ("game:fish-raw", 3),
            ("game:salt", 22));

        Put("game:labeledchest-south", 24, MinZ,
            ("game:grain-flax", 32), ("game:grain-rye", 48), ("game:grain-spelt", 40),
            ("game:grain-amaranth", 26), ("game:grain-rice", 33), ("game:grain-sunflower", 19),
            ("game:flour-spelt", 12), ("game:flour-rye", 9), ("game:flour-flax", 6),
            ("game:bread-spelt-perfect", 4), ("game:bread-rye-perfect", 3), ("game:dough-spelt", 5));

        Put("game:stationarybasket-south", 25, MinZ,
            ("game:vegetable-onion", 15), ("game:vegetable-parsnip", 11), ("game:vegetable-turnip", 13),
            ("game:vegetable-cabbage", 4), ("game:fruit-cranberry", 9));

        // 26 is this trunk's second cell and must stay empty.
        Put("game:trunk-south", 27, MinZ,
            ("game:leather-normal-plain", 14), ("game:leather-sturdy-plain", 5),
            ("game:hide-raw-medium", 6), ("game:hide-prepared-medium", 4), ("game:cloth-plain", 12),
            ("game:cloth-red", 6), ("game:cloth-blue", 6), ("game:flaxfibers", 28),
            ("game:flaxtwine", 22), ("game:rope", 9), ("game:candle", 20), ("game:beeswax", 7),
            ("game:linensack", 3), ("game:sewingkit", 1));

        // West wall, facing east into the room. A crate holds one item type by design - putting
        // anything into an empty one locks the whole crate to that type, which is exactly why
        // Packrat ranks an empty crate last when it routes a shift-click.
        Put("game:crate", MinX, 23, ("game:vegetable-carrot", 48));
        Put("game:crate", MinX, 24, ("game:vegetable-cabbage", 12));
        Put("game:stationarybasket-east", MinX, 25,
            ("game:fruit-blueberry", 22), ("game:fruit-cranberry", 18), ("game:fruit-redcurrant", 14),
            ("game:mushroom-fieldmushroom-normal", 9), ("game:mushroom-kingbolete-normal", 4),
            ("game:vegetable-turnip", 13));
        Put("game:chest-east", MinX, 26,
            ("game:stick", 64), ("game:plank-oak", 32), ("game:plank-birch", 18),
            ("game:log-placed-oak-ud", 16), ("game:firewood", 24), ("game:charcoal", 28),
            ("game:coke", 12), ("game:resin", 9), ("game:drygrass", 40), ("game:clay-blue", 44),
            ("game:clay-fire", 30), ("game:torch-basic-lit-up", 6));

        // East wall, facing west into the room. 25 is the trunk's second cell.
        Put("game:crate", MaxX, 23, ("game:vegetable-onion", 32));
        Put("game:crate", MaxX, 24, ("game:vegetable-parsnip", 24));
        Put("game:trunk-west", MaxX, 26,
            ("game:arrow-copper", 24), ("game:arrow-flint", 18), ("game:arrow-bone", 12),
            ("game:flaxtwine", 10), ("game:hide-raw-medium", 3), ("game:leather-normal-plain", 5));

        await Ticks(20);
        VerifyPlaced();
    }

    /// <summary>
    /// Says so when a container is not where it was put.
    ///
    /// Placing by SetBlock skips every placement rule, so a block can be overwritten a moment
    /// later by a neighbour's multiblock part, or dropped again by its own behaviours. Either way
    /// it leaves a gap in the frame and one fewer container in the window, and nothing else in a
    /// green run says why.
    /// </summary>
    static void VerifyPlaced()
    {
        foreach (var (code, pos) in placed)
        {
            string family = code.Substring(code.IndexOf(':') + 1).Split('-')[0];
            var now = World.BlockCode(pos);
            if (now == null || !now.Contains(family))
                Log($"    {code} at {pos.X},{pos.Z} did not survive placement (now {now ?? "nothing"})");
        }
        placed.Clear();
    }

    static readonly List<(string code, BlockPos pos)> placed = new();

    /// <summary>Places a container at floor level and drops stacks into its first slots.</summary>
    static void Put(string code, int x, int z, params (string code, int qty)[] items)
    {
        var pos = At(x, 1, z);
        World.SetBlock(code, pos);
        placed.Add((code, pos));

        var be = World.BEOrNull<BlockEntityContainer>(pos);
        if (be?.Inventory == null) { Log($"  no container entity at {x},{z} for {code}"); return; }

        int slot = 0;
        foreach (var (item, qty) in items)
        {
            if (slot >= be.Inventory.Count) break;
            var stack = Maybe(item, qty);
            if (stack == null) continue;
            be.Inventory[slot].Itemstack = stack;
            be.Inventory[slot].MarkDirty();
            slot++;
        }

        be.MarkDirty(true);
    }

    /// <summary>
    /// A stack, or null with a note in the log.
    ///
    /// World.Stack throws on an unknown code, which is right for a test - a scene that cannot
    /// find its subject has proved nothing. Here it is wrong: item codes drift between game
    /// versions, and one renamed foodstuff should cost a shot an item, not the whole set.
    /// </summary>
    static ItemStack Maybe(string code, int qty)
    {
        try { return World.Stack(code, qty); }
        catch (AssertionException) { Log($"  skipped missing item {code}"); return null; }
    }

    // ---------------------------------------------------------------- camera

    /// <summary>
    /// Stands somewhere and looks at something.
    ///
    /// <paramref name="feet"/> is where the player stands, not where the camera is: the eye sits
    /// about 1.7 above it.
    /// </summary>
    static async Task Aim(Vec3d feet, Vec3d target, int settle = 40)
    {
        await Player.Teleport(feet);
        await Ticks(10);
        await Interact.LookAt(target);
        await Frames.Wait(settle);
    }

    /// <summary>Standing at the near end of the room, looking down it at the back wall.</summary>
    static Task StandInDoorway(int settle = 40) => Aim(
        V(24.5, 1, MinZ + 6.2),
        V(24.5, 1.35, MinZ + 0.5), settle);

    /// <summary>
    /// Closes the HUD for a clean frame. ICoreClientAPI.HideGuis is read-only, but the hotbar,
    /// stat bars, minimap and block tooltip are all dialogs and dialogs close on request.
    /// <paramref name="keep"/> holds back any whose type name contains one of the given strings,
    /// which is how the command shot keeps the chat log it is meant to show.
    /// </summary>
    static async Task HideHud(params string[] keep)
    {
        var hand = Player.Me?.InventoryManager?.ActiveHotbarSlot;
        if (hand != null) { hand.Itemstack = null; hand.MarkDirty(); }
        await Ticks(4);

        await OnClient();
        Capi.Settings.Int["cloudRenderMode"] = 0;
        // The white selection box the client draws on whatever the player is aiming at. It is not
        // a dialog and cannot be closed; taking the reach away is what stops it being drawn.
        // Packrat's own range rule is a fixed 5.1 blocks and does not read this, so the browser
        // still opens on everything it would open on normally.
        Capi.World.Player.WorldData.PickingRange = 0.1f;
        // Ordinary first person floats the seraph's hand in the corner of every frame; immersive
        // mode renders the body from the eyes instead, which keeps it out of shot.
        Capi.Settings.Bool["immersiveFpMode"] = true;

        var open = new List<GuiDialog>(Capi.Gui.OpenedGuis);
        foreach (var d in open)
        {
            string n = d.GetType().Name;
            bool held = false;
            foreach (string k in keep) if (n.Contains(k)) held = true;
            if (held) continue;

            // Never the chat. HudDialogChat does not come back once closed - and it is invisible
            // when idle anyway - so closing it in one scene would silently cost a later scene the
            // command output it exists to show.
            if (n == "HudDialogChat") continue;

            if (n.StartsWith("Hud") || n == "GuiDialogWorldMap") d.TryClose();
        }

        await OnServer();
        await Frames.Wait(10);
    }

    // ---------------------------------------------------------------- the mod

    /// <summary>
    /// Fires the mod's own hotkey and waits for the window.
    ///
    /// Through the hotkey rather than by constructing the dialog: opening the browser is a round
    /// trip - the client asks the server to open every container, and the window is only shown
    /// once every one of them has answered - so a shot that built the dialog directly would be
    /// photographing something a player never sees.
    /// </summary>
    static async Task<GuiDialogStorageBrowser> OpenBrowser()
    {
        await OnClient();
        bool handled = await Input.Hotkey("packrat.openall");
        Assert.True(handled, "the packrat.openall hotkey is registered and handled");

        var dialog = await Gui.WaitFor<GuiDialogStorageBrowser>(200);
        await Frames.Wait(20);

        // What went in, in the order the window lists it. A scene that comes back looking thin is
        // usually a container that never answered, and that is invisible in the frame itself.
        var held = typeof(GuiDialogStorageBrowser).GetField("_containers",
            BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dialog) as List<BlockEntityContainer>;
        Log("    window holds " + (held == null ? "?" : held.Count + ": " +
            string.Join(", ", held.Select(c => (c.Block?.Code?.ToShortString() ?? "?") + "@" + c.Pos.X + "," + c.Pos.Z))));

        await OnServer();
        return dialog;
    }

    /// <summary>
    /// Types into the browser's search box the way a player does - '/' to focus it, then
    /// characters - so the frame shows a real focused field with a caret in it.
    /// </summary>
    static async Task Search(string text)
    {
        await OnClient();
        // '/' focuses the box. The mod then swallows the next OnKeyPress so the slash itself does
        // not land in the field - so the slash's own KeyPress has to be sent, or the suppression
        // eats the first letter of the search instead and the box reads "opper".
        await Input.Press(GlKeys.Slash);
        await Input.Type('/');
        await Ticks(2);
        await Input.Type(text);
        await Frames.Wait(30);
        await OnServer();
    }

    /// <summary>
    /// Picks a sort mode.
    ///
    /// The dropdown's own SetSelectedValue only repaints the control; the handler that actually
    /// re-sorts is private, so it is called directly and the recompose it triggers sets the
    /// dropdown to match. Reaching for the mod's own method rather than re-sorting here keeps
    /// the frame honest about what the control does.
    /// </summary>
    static async Task Sort(GuiDialogStorageBrowser dialog, string mode)
    {
        await OnClient();
        var handler = typeof(GuiDialogStorageBrowser).GetMethod("OnSortModeSelected",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handler, "GuiDialogStorageBrowser.OnSortModeSelected still exists");
        handler.Invoke(dialog, new object[] { mode, true });
        await Frames.Wait(30);
        await OnServer();
    }

    // ---------------------------------------------------------------- scenes

    [VsTest(TimeoutMs = 300000), RequiresClient, PlotSize(48, 32)]
    public async Task Shot01_OneWindowForEveryContainer()
    {
        // The hero shot. Thirteen containers of six types along three walls, and one window
        // holding the lot of them, with the coloured outlines saying which slots came from where.
        await World.SetCalendarTo(500 * 24 + 12);
        await BuildStoreroom();
        await StockStoreroom();

        await StandInDoorway();
        await HideHud();
        await OpenBrowser();

        Log("01 -> " + await Shot.Take(Out("01-one-window.png")));
    }

    [VsTest(TimeoutMs = 300000), RequiresClient, PlotSize(48, 32)]
    public async Task Shot02_TheStoreroomItself()
    {
        // What the window replaces: the same room, and the thirteen right-clicks it would
        // otherwise take. Worth a frame of its own - the browser shot means nothing to someone
        // who has not seen how much is behind it.
        await World.SetCalendarTo(500 * 24 + 12);
        await BuildStoreroom();
        await StockStoreroom();

        await StandInDoorway();
        await HideHud();

        Log("02 -> " + await Shot.Take(Out("02-the-storeroom.png")));
    }

    [VsTest(TimeoutMs = 300000), RequiresClient, PlotSize(48, 32)]
    public async Task Shot03_SearchAcrossAllOfThem()
    {
        // "copper" reaches across a chest of ingots, a chest of tools and a trunk of fletching
        // at once - which is the point, and is invisible in a shot of a single chest.
        await World.SetCalendarTo(500 * 24 + 12);
        await BuildStoreroom();
        await StockStoreroom();

        await StandInDoorway();
        await HideHud();
        await OpenBrowser();
        await Search("copper");

        Log("03 -> " + await Shot.Take(Out("03-search.png")));
    }

    [VsTest(TimeoutMs = 300000), RequiresClient, PlotSize(48, 32)]
    public async Task Shot04_SortedByMaterial()
    {
        await World.SetCalendarTo(500 * 24 + 12);
        await BuildStoreroom();
        await StockStoreroom();

        await StandInDoorway();
        await HideHud();
        var dialog = await OpenBrowser();
        await Sort(dialog, "Material");

        Log("04 -> " + await Shot.Take(Out("04-sorted.png")));

        // The mod persists the sort mode, so leaving it on Material would silently change every
        // scene that runs after this one - including a later re-run of the hero shot.
        await Sort(dialog, "None");
    }

    [VsTest(TimeoutMs = 300000), RequiresClient, PlotSize(48, 32)]
    public async Task Shot05_PriorityTypes()
    {
        // '.packrat priority types' in the chat log. The command exists because the tokens are
        // not guessable - a Basket is 'stationarybasket' and a Trunk is not a kind of chest - so
        // a shot of its output is worth more than a shot of the syntax.
        await World.SetCalendarTo(500 * 24 + 12);
        await BuildStoreroom();
        await StockStoreroom();

        await StandInDoorway();
        await HideHud("Chat");

        await OnClient();
        // Two things this scene needs that are easy to get wrong.
        //
        // The chat panel is not up in a testkit client - HudDialogChat is not even in OpenedGuis
        // until something opens it - and it only holds what was said *after* it came up, so the
        // hotkey has to be fired before the commands, not after.
        //
        // And the commands go through TriggerChatMessage, not SendChatMessage. SendChatMessage
        // sends to the server; '.packrat' is a client command, and sent that way it is swallowed
        // with no output and no error - a frame of an empty chat panel that looks like a timing
        // problem and is not one.
        var chat = Capi.Input.HotKeys["chatdialog"];
        chat.Handler(chat.CurrentMapping);
        await Frames.Wait(10);

        Capi.TriggerChatMessage(".packrat priority set trunk,chest,crate");
        await Ticks(20);
        Capi.TriggerChatMessage(".packrat priority types");
        await Frames.Wait(40);
        await OnServer();

        Log("05 -> " + await Shot.Take(Out("05-priority-types.png")));

        // Put the chat away again. HideHud deliberately leaves it alone, so an open panel would
        // otherwise sit in the corner of every scene that runs after this one.
        await OnClient();
        chat.Handler(chat.CurrentMapping);
        await Frames.Wait(5);
        await OnServer();
    }

    [VsTest(TimeoutMs = 300000), RequiresClient, PlotSize(48, 32)]
    public async Task Shot06_IconSource()
    {
        // Cropped square to 480x480 as a candidate mod icon, so this wants containers filling the
        // frame at close range rather than the length of the room. It is not wired into the build -
        // see docs/screenshots/README.md.
        //
        // Two things have to be kept out of an icon that are fine in a gallery shot, and the
        // browser being open is what does it. The crosshair is drawn by the client dead centre,
        // is not a dialog, and cannot be closed - but it is not drawn at all while a GUI has the
        // mouse ungrabbed. The block selection outline is the other, and HideHud has already taken
        // the player's reach away. The window itself is centred, so the aim is turned to the right
        // to throw the containers into the left third of the frame, clear of it, and the crop in
        // make-shots.sh takes that third.
        await World.SetCalendarTo(500 * 24 + 12);
        await BuildStoreroom();
        await StockStoreroom();

        await Aim(V(24.8, 1, MinZ + 3.6), V(MinX + 0.3, 1.40, MinZ + 0.8));
        await HideHud();

        Log("06 -> " + await Shot.Take(Out("06-icon-source.png")));
    }
}
