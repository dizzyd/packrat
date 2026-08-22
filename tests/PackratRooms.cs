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
    /// Rooms, which Packrat leans on for two separate things: deciding which
    /// containers a player can reach, and (through vanilla) how well a container
    /// preserves food.
    ///
    /// Every test here builds a sealed room out of blocks and asks the game what it
    /// made of it, rather than assuming. A room is only a room if RoomRegistry says
    /// ExitCount is 0, and that depends on real block placement.
    /// </summary>
    public class PackratRooms
    {
        const string Wall  = "game:rock-granite";
        const string Chest = "game:chest-north";
        const string Food  = "game:vegetable-carrot";

        // A sealed shell with a 3x2x3 interior, clear of the plot edge.
        static BlockPos ShellMin => P(1, 0, 1);
        static BlockPos ShellMax => P(5, 3, 5);
        static BlockPos Inside   => P(3, 1, 3);
        static BlockPos Outside  => P(12, 1, 12);

        [VsTest]
        public async Task ASealedShellIsSeenAsARoomWithNoExits()
        {
            // The whole in-room branch of container discovery keys on ExitCount == 0.
            // If a shell this simple does not register, nothing downstream means
            // anything - so this runs first and says so plainly.
            await BuildSealedRoom();

            var room = Rooms().GetRoomForPosition(Inside);
            Assert.NotNull(room, "the sealed shell registers as a room");
            Log($"ExitCount={room.ExitCount} smallRoom={room.IsSmallRoom} " +
                $"cellar={room.IsSmallRoom && room.ExitCount == 0}");

            Assert.Equal(0, room.ExitCount, "a fully sealed shell has no exits");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ARoomOutsideIsNotTheRoomYouAreStandingIn()
        {
            // The rule Packrat added in 1.2.0: a container sealed inside a room belongs
            // to that room, not to whoever can see into it. Room.Contains consults the
            // room's own mask rather than its bounding box, which is what makes that
            // answerable at all.
            await BuildSealedRoom();

            var room = Rooms().GetRoomForPosition(Inside);
            Assert.True(room.Contains(Inside), "the room contains a block inside it");
            Assert.False(room.Contains(Outside), "the room does not contain a block outside it");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AContainerInASealedRoomPreservesFoodBetter()
        {
            // What the README promises as "a cellar". Packrat does not compute this
            // itself - it reads GetTransitionSpeedMul - but the whole perish preference
            // is pointless if a sealed room does not actually register as cooler.
            await BuildSealedRoom();

            World.SetBlock(Chest, Inside);
            World.SetBlock(Chest, Outside);
            await Ticks(10);

            var food = World.Stack(Food, 1);
            var inside = Inventory(Inside).GetTransitionSpeedMul(EnumTransitionType.Perish, food);
            var outside = Inventory(Outside).GetTransitionSpeedMul(EnumTransitionType.Perish, food);
            Log($"perish rate inside {inside:F3}, outside {outside:F3}");

            Assert.Less(inside, outside, "a sealed room preserves food better than open ground");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task PerishableFoodIsRoutedToTheBetterPreservingContainer()
        {
            // The rule that overrides all the others.
            await BuildSealedRoom();

            World.SetBlock(Chest, Inside);
            World.SetBlock(Chest, Outside);
            await Ticks(10);

            var food = Source(Food, 1);
            var cellar = Weight(Inside, food);
            var openGround = Weight(Outside, food);
            Log($"perishable: sealed room {cellar:F3} vs open ground {openGround:F3}");

            Assert.Greater(cellar, openGround, "a perishable prefers the container in the sealed room");

            // ...and an inert item is not swayed by it, so the bonus is not simply a
            // blanket preference for indoor containers.
            var stick = Source("game:stick", 1);
            var inertGap = Weight(Inside, stick) - Weight(Outside, stick);
            var foodGap = cellar - openGround;
            Log($"gap for a perishable {foodGap:F3}, for a stick {inertGap:F3}");
            Assert.Greater(foodGap, inertGap, "the preference comes from spoilage, not from being indoors");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ThePerishBonusMatchesTheNormalisedCurve()
        {
            // Pins the formula rather than just its direction, which is what tells the
            // two implementations apart.
            //
            // The bonus used to be Math.Max(0f, (1f - rate) * 10f). That hands every
            // rate at or above 1.0 the same zero, so wherever the whole world is warm -
            // which is most of it - the preference silently did nothing. The current
            // form normalises across the range GetPerishRate actually clamps to, so it
            // stays monotonic even when every candidate is bad in absolute terms.
            //
            // Measuring both rates and both weights inside one test keeps this honest:
            // plots sit in different climates, so numbers cannot be carried between
            // tests.
            const float minRate = 0.1f, maxRate = 2.4f, bonusMax = 10f;

            await BuildSealedRoom();
            World.SetBlock(Chest, Inside);
            World.SetBlock(Chest, Outside);
            await Ticks(10);

            var food = World.Stack(Food, 1);
            float rateIn = Inventory(Inside).GetTransitionSpeedMul(EnumTransitionType.Perish, food);
            float rateOut = Inventory(Outside).GetTransitionSpeedMul(EnumTransitionType.Perish, food);

            float Bonus(float rate) =>
                bonusMax * (maxRate - GameMath.Clamp(rate, minRate, maxRate)) / (maxRate - minRate);

            float expected = Bonus(rateIn) - Bonus(rateOut);

            // Differencing against an inert item cancels the base weight, the container
            // lift and the position tiebreak, leaving only the perish term.
            var foodSlot = Source(Food, 1);
            var stick = Source("game:stick", 1);
            float actual = (Weight(Inside, foodSlot) - Weight(Outside, foodSlot))
                         - (Weight(Inside, stick) - Weight(Outside, stick));

            Log($"rates in {rateIn:F3} / out {rateOut:F3} -> expected gap {expected:F3}, measured {actual:F3}");
            Assert.Close(actual, expected, 0.02, "perish bonus gap matches the normalised curve");

            await Task.CompletedTask;
        }

        // ---------- helpers ----------

        static RoomRegistry Rooms() => Sapi.ModLoader.GetModSystem<RoomRegistry>();

        /// <summary>
        /// Build a hollow stone box: solid on every face, air inside. Nothing here can
        /// be assumed - the plot floor is already solid, but the walls and ceiling have
        /// to exist or RoomRegistry counts the gaps as exits.
        /// </summary>
        static async Task BuildSealedRoom()
        {
            var min = ShellMin;
            var max = ShellMax;

            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        bool shell = x == min.X || x == max.X ||
                                     y == min.Y || y == max.Y ||
                                     z == min.Z || z == max.Z;
                        World.SetBlock(shell ? Wall : "game:air", new BlockPos(x, y, z, 0));
                    }
                }
            }

            await Ticks(10);
        }

        static InventoryBase Inventory(BlockPos pos) =>
            (InventoryBase)World.BE<BlockEntityContainer>(pos).Inventory;

        static ItemSlot Source(string code, int quantity)
        {
            var holder = new DummyInventory(Sapi, 1);
            holder[0].Itemstack = World.Stack(code, quantity);
            return holder[0];
        }

        static float Weight(BlockPos pos, ItemSlot source)
        {
            var op = new ItemStackMoveOperation(Sapi.World, EnumMouseButton.Left,
                EnumModifierKey.SHIFT, EnumMergePriority.AutoMerge, source.StackSize);
            var best = Inventory(pos).GetBestSuitedSlot(source, op, new List<ItemSlot>());
            return best.slot == null ? float.NegativeInfinity : best.weight;
        }
    }
}
