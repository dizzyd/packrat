using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// The Perishable sort mode (PR #7), driven through the browser the way a player
    /// would: the real hotkey, then a mouse click on the dropdown's entry.
    ///
    /// The groups it has to produce, in order: food already spoiling, most spoiled
    /// first; fresh food, fewest real hours to go first; food that is not aging
    /// where it sits; and things that do not perish at all.
    /// </summary>
    public class PackratPerishableSort
    {
        const string Chest  = "game:chest-east";
        const string Vessel = "game:storagevessel-red-fired";

        static BlockPos Stand    => P(12, 1, 12);
        static BlockPos ChestAt  => P(10, 1, 12);
        static BlockPos VesselAt => P(14, 1, 12);

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task PerishableSortOrdersByWhatSpoilsFirst()
        {
            World.SetBlock(Chest, ChestAt);
            World.SetBlock(Vessel, VesselAt);
            var vessel = World.BE<BlockEntityGenericTypedContainer>(VesselAt);
            await Ticks(10);

            // A vessel keeps vegetables longer than a chest does. Both rates are read
            // from the client, because that is where the browser sorts.
            float chestRate = await ClientRate(ChestAt);
            float vesselRate = await ClientRate(VesselAt);
            Log($"perish rate: chest {chestRate}, vessel {vesselRate}");
            Assert.True(vesselRate < chestRate * 0.8f, "a vessel slows vegetables enough to tell the containers apart");

            // The cabbage has fewer hours on its own clock than the carrot but more in
            // real time, once the vessel's slower rate is applied. Sorting on the raw
            // number would put it first.
            float carrotHours = 100;
            float cabbageHours = carrotHours * (vesselRate / chestRate) * 1.2f;

            var chest = World.BE<BlockEntityGenericTypedContainer>(ChestAt).Inventory;
            chest[0].Itemstack = World.Stack("game:stick");
            chest[1].Itemstack = Vegetable("turnip", freshHoursLeft: 0, spoiled: 0.2f);
            chest[2].Itemstack = Vegetable("carrot", freshHoursLeft: carrotHours, spoiled: 0);
            chest[3].Itemstack = Hot(Vegetable("parsnip", freshHoursLeft: 50, spoiled: 0));
            chest[4].Itemstack = World.Stack("game:flint");
            chest[5].Itemstack = Vegetable("onion", freshHoursLeft: 0, spoiled: 0.6f, quantity: 64);

            // Spoiled by almost the same amount, and the smaller stack - so if the two
            // levels ever compare as equal, the larger-stack tiebreak puts this one second.
            chest[6].Itemstack = Vegetable("onion", freshHoursLeft: 0, spoiled: 0.60004f, quantity: 1);
            for (int i = 0; i <= 6; i++) chest[i].MarkDirty();
            vessel.Inventory[0].Itemstack = Vegetable("cabbage", freshHoursLeft: cabbageHours, spoiled: 0);
            vessel.Inventory[0].MarkDirty();

            await Player.Teleport(Stand);
            await Ticks(10);

            var config = (PackratConfig)typeof(PackratModSystem)
                .GetField("_config", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var was = config.SortMode;
            config.SortMode = SortMode.None;

            try
            {
                await Input.Hotkey(PackratModSystem.ModId + ".openall");
                var dialog = await Gui.WaitFor<GuiDialogStorageBrowser>();
                await Frames.Wait(5);

                var view = (SortedInventoryView)typeof(GuiDialogStorageBrowser)
                    .GetField("_sortedInventory", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
                Assert.Equal(SortMode.None, view.SortMode, "the browser opens unsorted");

                await PickSortMode(dialog, "Spoilage");
                Assert.Equal(SortMode.ByPerishable, view.SortMode, "the dropdown entry selects ByPerishable");
                Assert.Equal(SortMode.ByPerishable, config.SortMode, "the in-memory config reflects the selection");

                var shown = await OnClientRun(() => Enumerable.Range(0, view.Count)
                    .Select(i => view[i].Itemstack)
                    .Where(stack => stack != null)
                    .Select(stack => stack.Collectible.Code.Path + " x" + stack.StackSize)
                    .ToList());
                Log("shown: " + string.Join(", ", shown));

                var expected = new[]
                {
                    "vegetable-onion x1",    // 60.004% spoiled
                    "vegetable-onion x64",   // 60% spoiled
                    "vegetable-turnip x1",   // 20% spoiled
                    "vegetable-carrot x1",   // 100h in a chest
                    "vegetable-cabbage x1",  // fewer raw hours, but in a vessel
                    "vegetable-parsnip x1",  // too hot to age
                    "flint x1",              // does not perish, A-Z
                    "stick x1",
                };
                Assert.Equal(string.Join(", ", expected), string.Join(", ", shown), "the Perishable order");

                await Input.Hotkey(PackratModSystem.ModId + ".openall");
                await Ticks(10);
            }
            finally
            {
                await OnClient();
                typeof(PackratModSystem).GetMethod("OnSortModeChanged", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { was });
                await OnServer();
            }
        }

        // ---------- helpers ----------

        /// <summary>
        /// A vegetable with its perish clock set directly: so many fresh hours left,
        /// or so far into spoiling. Vegetables carry a single transition, Perish.
        /// </summary>
        static ItemStack Vegetable(string type, float freshHoursLeft, float spoiled, int quantity = 1)
        {
            var stack = World.Stack("game:vegetable-" + type, quantity);
            stack.Collectible.UpdateAndGetTransitionStates(Sapi.World, new DummySlot(stack));

            var attr = (ITreeAttribute)stack.Attributes["transitionstate"];
            var fresh = (FloatArrayAttribute)attr["freshHours"];
            var transition = (FloatArrayAttribute)attr["transitionHours"];
            var transitioned = (FloatArrayAttribute)attr["transitionedHours"];
            Assert.Equal(1, fresh.value.Length, type + " has one transition");

            fresh.value[0] = 200;
            transition.value[0] = 48;
            transitioned.value[0] = spoiled > 0 ? 200 + spoiled * 48 : 200 - freshHoursLeft;
            attr.SetDouble("lastUpdatedTotalHours", Sapi.World.Calendar.TotalHours);
            return stack;
        }

        /// <summary>Above 75 degrees food stops aging, so its rate is 0.</summary>
        static ItemStack Hot(ItemStack stack)
        {
            stack.Collectible.SetTemperature(Sapi.World, stack, 900);
            return stack;
        }

        static async Task<float> ClientRate(BlockPos pos)
        {
            return await OnClientRun(() =>
            {
                var be = Capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityContainer;
                Assert.NotNull(be, "the client has the container at " + pos);
                var stack = new ItemStack(Capi.World.GetItem(new AssetLocation("game:vegetable-carrot")));
                return be.Inventory.GetTransitionSpeedMul(EnumTransitionType.Perish, stack);
            });
        }

        /// <summary>
        /// Opens the sort dropdown and clicks the named entry. The list reads the
        /// platform's cursor, not the event's, so both have to be moved.
        /// </summary>
        static async Task PickSortMode(GuiDialogStorageBrowser dialog, string name)
        {
            var (dropdown, x, y, line, index) = await OnClientRun(() =>
            {
                var dd = dialog.SingleComposer.GetDropDown("sortdropdown");
                Assert.NotNull(dd, "the sort dropdown");
                var b = dd.Bounds;
                double lineHeight = 30 * dd.listMenu.Scale * Vintagestory.API.Config.RuntimeEnv.GUIScale;
                return (dd, (int)(b.renderX + 10), b.renderY + b.InnerHeight, lineHeight, Array.IndexOf(dd.listMenu.Names, name));
            });
            Assert.True(index >= 0, "the dropdown offers " + name);

            // The dropdown's own box, then the entry in the list that opens beneath it
            await ClickAt(x, (int)(y - line / 2));
            Assert.True(await OnClientRun(() => dropdown.listMenu.IsOpened), "the dropdown opened");
            await ClickAt(x, (int)(y + (index + 0.5) * line));
        }

        static async Task ClickAt(int x, int y)
        {
            await OnClient();
            var game = (ClientMain)Capi.World;
            game.Platform.GetType()
                .GetMethod("SetMousePosition", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(game.Platform, new object[] { (float)x, (float)y });
            game.OnMouseMove(new MouseEvent(x, y));
            await OnServer();
            await Frames.Wait(3);
            await Input.Click();
            await Frames.Wait(5);
        }

        static async Task<T> OnClientRun<T>(Func<T> body)
        {
            await OnClient();
            try { return body(); }
            finally { await OnServer(); }
        }
    }
}
