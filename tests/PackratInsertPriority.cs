using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Where a shift-clicked item actually lands.
    ///
    /// Packrat expresses this as four Harmony postfixes that nudge the weight
    /// InventoryBase.GetBestSuitedSlot returns, so the rules the README states in
    /// words only exist as arithmetic on that weight. These call the patched method
    /// directly and compare the weights, which is exact and does not depend on the
    /// order containers happened to open in.
    ///
    /// All server-side, so they run headless.
    /// </summary>
    public class PackratInsertPriority
    {
        const string Chest = "game:chest-north";
        const string Crate = "game:crate";
        const string Item  = "game:stick";
        const string Other = "game:drygrass";

        [VsTest]
        public async Task AContainerAlreadyHoldingTheItemBeatsAnEmptyOne()
        {
            // Rule 1: your stack gets topped up rather than scattered.
            var stocked = await Place(Chest, P(4, 1, 4), Item, 8);
            var empty = await Place(Chest, P(6, 1, 4));

            var source = Source(Item, 4);
            Assert.Greater(Weight(stocked, source), Weight(empty, source),
                "a chest already holding sticks outranks an empty chest");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AnEmptyCrateIsTheLastResort()
        {
            // Rule 3, and the behaviour change in 1.2.0: claiming an empty crate locks
            // the whole crate to one item type, so it must lose to anything with space.
            var crate = await Place(Crate, P(4, 1, 4));
            var chest = await Place(Chest, P(6, 1, 4));

            var source = Source(Item, 4);
            var crateWeight = Weight(crate, source);
            var chestWeight = Weight(chest, source);
            Log($"empty crate {crateWeight:F3} vs empty chest {chestWeight:F3}");

            Assert.Less(crateWeight, chestWeight, "an empty crate ranks below a chest with space");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ACrateHoldingSomethingElseIsRefusedOutright()
        {
            // A crate holds one item type, so a mismatch is not merely deprioritised -
            // the patch clears the result so nothing can be routed there at all.
            var crate = await Place(Crate, P(4, 1, 4), Other, 4);

            var inv = Inventory(crate);
            var best = inv.GetBestSuitedSlot(Source(Item, 4), Op(4), new List<ItemSlot>());

            Assert.Null(best.slot, "a crate full of dry grass offers no slot for sticks");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ACrateAlreadyHoldingTheItemBeatsAChestWithSpace()
        {
            // The match bonus exists to nudge a crate ahead of an equivalent merge into
            // a chest, so a crate dedicated to an item keeps collecting it.
            var crate = await Place(Crate, P(4, 1, 4), Item, 8);
            var chest = await Place(Chest, P(6, 1, 4), Item, 8);

            var source = Source(Item, 4);
            Assert.Greater(Weight(crate, source), Weight(chest, source),
                "a crate already holding sticks outranks a chest also holding sticks");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ABlockContainerOutranksAnInventoryWithNoPosition()
        {
            // The player's backpack competes in the same ranking and has no Pos, so
            // every positioned container is lifted above it. Without that lift the
            // empty-crate penalty would push a crate below your own pack, and
            // shift-clicking an item you already carry would just keep it.
            var chest = await Place(Chest, P(4, 1, 4));

            var backpackLike = new DummyInventory(Sapi, 4);
            var source = Source(Item, 4);

            var chestWeight = Weight(chest, source);
            var dummyWeight = backpackLike.GetBestSuitedSlot(source, Op(4), new List<ItemSlot>()).weight;
            Log($"chest {chestWeight:F3} vs positionless inventory {dummyWeight:F3}");

            Assert.Greater(chestWeight, dummyWeight, "a chest outranks an inventory with no position");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task NoTwoContainersEverScoreExactlyEqual()
        {
            // This one is not cosmetic. The destination of a shift-click is never
            // transmitted - the packet carries only the source slot - so the server
            // re-derives it by running the same weighting. A tie resolves to whichever
            // inventory was opened last, an order the two sides do not reliably share,
            // so a tie lets client and server pick different containers and the items
            // snap back on the next sync.
            var a = await Place(Chest, P(4, 1, 4));
            var b = await Place(Chest, P(6, 1, 4));
            var c = await Place(Chest, P(4, 1, 6));

            var source = Source(Item, 4);
            var wa = Weight(a, source);
            var wb = Weight(b, source);
            var wc = Weight(c, source);
            Log($"three identical empty chests: {wa:F6}, {wb:F6}, {wc:F6}");

            Assert.NotEqual(wa, wb, "two identical chests at different positions");
            Assert.NotEqual(wb, wc, "two identical chests at different positions");
            Assert.NotEqual(wa, wc, "two identical chests at different positions");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheTiebreakIsDerivedFromPositionAlone()
        {
            // Both sides have to compute the same offset, so it must depend on nothing
            // but the block position - not on open order, instance identity or time.
            var chest = await Place(Chest, P(4, 1, 4));
            var source = Source(Item, 4);

            var first = Weight(chest, source);
            await Ticks(5);
            var second = Weight(chest, source);

            Assert.Equal(first, second, "the same container weighs the same twice");
            await Task.CompletedTask;
        }

        // ---------- helpers ----------

        static async Task<BlockPos> Place(string code, BlockPos pos, string contents = null, int quantity = 0)
        {
            World.SetBlock(code, pos);
            await Ticks(4);

            if (contents != null)
            {
                var inv = Inventory(pos);
                inv[0].Itemstack = World.Stack(contents, quantity);
                inv[0].MarkDirty();
                await Ticks(2);
            }

            return pos;
        }

        static InventoryBase Inventory(BlockPos pos) =>
            (InventoryBase)World.BE<BlockEntityContainer>(pos).Inventory;

        static ItemSlot Source(string code, int quantity)
        {
            var holder = new DummyInventory(Sapi, 1);
            holder[0].Itemstack = World.Stack(code, quantity);
            return holder[0];
        }

        static ItemStackMoveOperation Op(int quantity) =>
            new(Sapi.World, EnumMouseButton.Left, EnumModifierKey.SHIFT,
                EnumMergePriority.AutoMerge, quantity);

        /// <summary>The weight Packrat's patches settle on, or -inf when no slot is offered.</summary>
        static float Weight(BlockPos pos, ItemSlot source)
        {
            var best = Inventory(pos).GetBestSuitedSlot(source, Op(source.StackSize), new List<ItemSlot>());
            return best.slot == null ? float.NegativeInfinity : best.weight;
        }
    }
}
