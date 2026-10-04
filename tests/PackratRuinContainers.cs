using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Containers found in ruins - issue #1.
    ///
    /// Collapsed chests, collapsed trunks and aged baskets are ordinary typed
    /// containers whose type is marked retrieveOnly. That flag stops items going
    /// in; it does not stop anyone opening them, and in 1.22 vanilla opens them on
    /// a right-click like any other chest. Packrat opening them too is parity, not
    /// a bypass. The break-to-loot blocks are the loot vessels, which have no
    /// inventory at all.
    ///
    /// The one difference Packrat keeps is that an *empty* ruin container is
    /// skipped: vanilla turns an emptied collapsed chest into rubble when its
    /// inventory closes, so opening every looted one in range would crumble them
    /// all the moment the browser shut.
    /// </summary>
    public class PackratRuinContainers
    {
        const string Chest  = "game:chest-north";
        const string Trunk  = "game:trunk-north";
        const string Basket = "game:stationarybasket-north";
        const string Stick  = "game:stick";

        // Out in the open, all within the 5.1 block range of Stand. The trunk is two
        // blocks long - north-facing, it also takes the position east of TrunkAt.
        static BlockPos Stand    => P(12, 1, 12);
        static BlockPos ChestAt  => P(10, 1, 11);
        static BlockPos TrunkAt  => P(12, 1, 14);
        static BlockPos BasketAt => P(14, 1, 11);
        static BlockPos PlainAt  => P(12, 1, 10);

        [VsTest]
        public async Task LootVesselsHaveNoInventory()
        {
            // The scan only ever considers a BlockEntityContainer, so a block with no
            // block entity cannot reach the browser, whatever else changes.
            var vessels = Sapi.World.Blocks.Where(b => b is BlockLootVessel).ToList();
            Assert.Greater(vessels.Count, 0, "loot vessels exist");

            foreach (var vessel in vessels)
                Assert.True(string.IsNullOrEmpty(vessel.EntityClass), vessel.Code + " has no block entity");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task StockedRuinContainersAreDiscovered()
        {
            var placed = await PlaceRuinContainers(stocked: true);
            await Player.Teleport(Stand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found));

            foreach (var pos in placed.Keys)
                Assert.True(found.Contains(pos), "a stocked ruin container: " + placed[pos]);

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task EmptyRuinContainersAreSkippedButAnEmptyChestIsNot()
        {
            // The skip has to key on retrieveOnly, not on emptiness alone - an empty
            // chest of the player's own is still somewhere to put things.
            var placed = await PlaceRuinContainers(stocked: false);
            World.SetBlock(Chest, PlainAt);
            await Ticks(10);
            await Player.Teleport(Stand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (plain={Show(PlainAt)})");

            foreach (var pos in placed.Keys)
                Assert.False(found.Contains(pos), "an empty ruin container: " + placed[pos]);
            Assert.True(found.Contains(PlainAt), "an ordinary empty chest");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task StockedRuinContainersOpenThroughTheBrowserAndStayRetrieveOnly()
        {
            // Through the real hotkey, so the server's own handling of the request is
            // what decides - if vanilla ever refuses to open these again, the browser
            // times out without them and this fails.
            var placed = await PlaceRuinContainers(stocked: true);
            await Player.Teleport(Stand);
            await Ticks(10);

            await InSurvival(async () =>
            {
                await OpenBrowser();

                foreach (var pos in placed.Keys)
                {
                    var inv = World.BE<BlockEntityGenericTypedContainer>(pos).Inventory;
                    Assert.True(Player.Me.InventoryManager.HasInventory(inv), "opened on the server: " + placed[pos]);
                    Assert.True(inv.PutLocked, "still refuses insertion: " + placed[pos]);
                }

                await CloseBrowser();
            });
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AnEmptiedCollapsedChestCrumblesWhenTheBrowserCloses()
        {
            // The reason the empty ones are skipped, and a check that closing the
            // browser reaches the server the way closing a chest does: vanilla's
            // changeIntoWhenEmpty only fires from the server-side close, in survival.
            var placed = await PlaceRuinContainers(stocked: true);
            await Player.Teleport(Stand);
            await Ticks(10);

            await InSurvival(async () =>
            {
                await OpenBrowser();

                var slot = World.BE<BlockEntityGenericTypedContainer>(ChestAt).Inventory[0];
                slot.Itemstack = null;
                slot.MarkDirty();
                await Ticks(4);

                await CloseBrowser();
                await Until(() => World.BlockCode(ChestAt)?.StartsWith("game:clutter") == true, 100,
                    "the emptied " + placed[ChestAt] + " turns to rubble");
            });
        }

        // ---------- helpers ----------

        /// <summary>
        /// One container of each ruin family, set to its first retrieveOnly type,
        /// and confirmed on the client - a scan of a block entity the client never
        /// received would pass the "skipped" test for the wrong reason.
        /// </summary>
        static async Task<Dictionary<BlockPos, string>> PlaceRuinContainers(bool stocked)
        {
            var families = new Dictionary<BlockPos, string> { [ChestAt] = Chest, [TrunkAt] = Trunk, [BasketAt] = Basket };
            var placed = new Dictionary<BlockPos, string>();

            foreach (var (pos, code) in families)
            {
                World.SetBlock(code, pos);
                var be = World.BE<BlockEntityGenericTypedContainer>(pos);
                Assert.NotNull(be, code + " has a typed container");

                var attrs = be.Block.Attributes;
                string type = attrs["types"].AsArray<string>().FirstOrDefault(t => attrs["retrieveOnly"][t].AsBool());
                Assert.NotNull(type, code + " has a retrieveOnly type");

                // OnBlockPlaced is where a placed stack's type is taken up, so give it one.
                var stack = new ItemStack(be.Block);
                stack.Attributes.SetString("type", type);
                be.OnBlockPlaced(stack);
                if (stocked) be.Inventory[0].Itemstack = World.Stack(Stick);
                be.MarkDirty(true);

                placed[pos] = code + ":" + type;
            }

            await Ticks(10);

            await OnClient();
            foreach (var (pos, name) in placed)
            {
                var be = Capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityGenericTypedContainer;
                Assert.NotNull(be, "the client has " + name);
                Assert.True(be.retrieveOnly, "the client sees " + name + " as retrieveOnly");
                Assert.Equal(!stocked, be.Inventory.Empty, "the client has " + name + (stocked ? " stocked" : " empty"));
            }
            await OnServer();

            return placed;
        }

        /// <summary>
        /// Survival, because a creative player unlocks insertion on open and never
        /// triggers changeIntoWhenEmpty - both tests would pass for the wrong reason.
        ///
        /// Not Player.SetGameMode: that runs /gamemode as the console, which looks the
        /// caller up by name and dereferences the null it gets back. Run as the player
        /// it works, and the mode is read back rather than assumed.
        /// </summary>
        static async Task InSurvival(System.Func<Task> body)
        {
            var was = Player.Me.WorldData.CurrentGameMode;
            await SetGameMode(EnumGameMode.Survival);
            try { await body(); }
            finally { await SetGameMode(was); }
        }

        static async Task SetGameMode(EnumGameMode mode)
        {
            await Cmd("/gamemode " + mode.ToString().ToLowerInvariant(), Player.Me.PlayerName);
            await Ticks(2);
            Assert.Equal(mode.ToString(), Player.Me.WorldData.CurrentGameMode.ToString(), "the player changed game mode");
        }

        static async Task OpenBrowser()
        {
            await Input.Hotkey(PackratModSystem.ModId + ".openall");
            await Gui.WaitFor<GuiDialogStorageBrowser>();
        }

        static async Task CloseBrowser()
        {
            await Input.Hotkey(PackratModSystem.ModId + ".openall");
            await Ticks(10);
        }

        static async Task<HashSet<BlockPos>> Scan()
        {
            await OnClient();

            var system = Capi.ModLoader.GetModSystem<PackratModSystem>();
            Assert.NotNull(system, "the Packrat mod system on the client");

            var method = typeof(PackratModSystem).GetMethod("ScanAccessibleContainers",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "ScanAccessibleContainers still exists");

            var containers = (List<BlockEntityContainer>)method.Invoke(system, new object[] { Capi.World.Player });
            var positions = new HashSet<BlockPos>(containers.Select(c => c.Pos));

            await OnServer();
            return positions;
        }

        static string Describe(HashSet<BlockPos> found) =>
            $"scan found {found.Count}: " + string.Join(", ", found.Select(Show));

        static string Show(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
    }
}
