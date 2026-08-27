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
    /// Shift-clicking OUT of the browser.
    ///
    /// The browser keeps several containers open at once, so every one of them competes
    /// to receive an item shift-clicked out of any other - and containers are lifted above
    /// the player's own inventory by GetBestSuitedSlot_ContainerPreference. Without a
    /// guard, items never leave the dialog: they hop from one container to the next.
    ///
    /// PackratInsertPriority covers the other direction and builds its sources from a
    /// DummyInventory, which has no Pos, so nothing there exercises this guard at all.
    /// These use real container slots as the source, which is what the guard reads.
    ///
    /// All server-side, so they run headless.
    /// </summary>
    public class PackratShiftOut
    {
        const string Chest  = "game:chest-north";
        const string Basket = "game:stationarybasket-north";
        const string Crate  = "game:crate";
        const string Chute  = "game:chute-elbow-down-east";
        const string Item   = "game:stick";

        [VsTest]
        public async Task AChestRefusesWhatWasShiftClickedOutOfAnotherChest()
        {
            var from = await Place(Chest, P(4, 1, 4), Item, 8);
            var to   = await Place(Chest, P(6, 1, 4));

            Assert.Null(Offered(to, Inventory(from)[0]),
                "a chest must not accept items shift-clicked out of another chest");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ABasketRefusesWhatWasShiftClickedOutOfAChest()
        {
            // The bug this file was written for. Containers used to be recognised by
            // inventory ID prefix - chest-, crate-, bettercrate- - and a stationary basket
            // declares inventoryClassName "basket", so it matched none of them and quietly
            // took everything shift-clicked out of the browser.
            var from = await Place(Chest, P(4, 1, 4), Item, 8);
            var to   = await Place(Basket, P(6, 1, 4));

            Log($"basket inventory id = {Inventory(to).InventoryID}");
            Assert.Null(Offered(to, Inventory(from)[0]),
                "a basket must not accept items shift-clicked out of a chest");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AChestRefusesWhatWasShiftClickedOutOfABasket()
        {
            // The same gap in the other direction: an unrecognised source meant the guard
            // never ran, so a basket's contents could be routed into any other container.
            var from = await Place(Basket, P(4, 1, 4), Item, 8);
            var to   = await Place(Chest, P(6, 1, 4));

            Log($"source inventory id = {Inventory(from)[0].Inventory?.InventoryID}");
            Assert.Null(Offered(to, Inventory(from)[0]),
                "a chest must not accept items shift-clicked out of a basket");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ACrateRefusesWhatWasShiftClickedOutOfAChest()
        {
            var from = await Place(Chest, P(4, 1, 4), Item, 8);
            var to   = await Place(Crate, P(6, 1, 4), Item, 8);

            Assert.Null(Offered(to, Inventory(from)[0]),
                "a crate must not accept items shift-clicked out of a chest, even a matching one");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task YourOwnPackOutranksAnEmptyContainer()
        {
            // The +1.5 container lift applies whatever the source is, so an empty container
            // slot (2 + 1.5) outweighs merging into a stack you already carry (3) unless the
            // guard clears it first. That is the arithmetic that made items stay in the
            // dialog rather than come to you.
            var from   = await Place(Chest, P(4, 1, 4), Item, 8);
            var basket = await Place(Basket, P(6, 1, 4));

            var src = Inventory(from)[0];

            var pack = new DummyInventory(Sapi, 4);
            pack[0].Itemstack = World.Stack(Item, 8);

            var basketBest = Inventory(basket).GetBestSuitedSlot(src, Op(4), new List<ItemSlot>());
            var packBest   = pack.GetBestSuitedSlot(src, Op(4), new List<ItemSlot>());
            var basketWeight = basketBest.slot == null ? float.NegativeInfinity : basketBest.weight;
            Log($"empty basket {basketWeight:F3} vs pack merge {packBest.weight:F3}");

            Assert.Greater(packBest.weight, basketWeight,
                "the player's own pack wins when shift-clicking out of storage");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AChuteCanStillPullOutOfAChest()
        {
            // The guard is scoped to a shift-click for this reason. Container to container
            // is exactly what the game's own automation does -
            // BlockEntityItemFlow.TryPullFrom asks the chute for a slot with the adjacent
            // chest's slot as the source, both of them positioned - and it calls
            // GetBestSuitedSlot with a null op, which is what separates it from a player.
            var chest = await Place(Chest, P(4, 1, 4), Item, 8);
            var chute = await Place(Chute, P(5, 1, 4));

            var best = Inventory(chute).GetBestSuitedSlot(Inventory(chest)[0], null, new List<ItemSlot>());
            Log($"chute <- chest: slot={(best.slot == null ? "null" : "offered")} weight={best.weight:F3}");

            Assert.NotNull(best.slot, "a chute must still pull items out of a chest");
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

        /// <summary>The slot a container offers for a shift-click, or null when it refuses.</summary>
        static ItemSlot Offered(BlockPos pos, ItemSlot source) =>
            Inventory(pos).GetBestSuitedSlot(source, Op(source.StackSize), new List<ItemSlot>()).slot;

        /// <summary>
        /// A shift-click. SHIFT is what marks it as one - the guard reads that rather than
        /// ActingPlayer precisely so it can be exercised headless, where nobody is connected
        /// and Player.Me is null.
        /// </summary>
        static ItemStackMoveOperation Op(int quantity) =>
            new(Sapi.World, EnumMouseButton.Left, EnumModifierKey.SHIFT,
                EnumMergePriority.AutoMerge, quantity);
    }
}
