using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Where a browser goes when it is done with - issue #6.
    ///
    /// A browser is built fresh on every open, so each one closed has to be released,
    /// and one still open when the world goes away has to be dropped without being
    /// closed: the engine disposes dialogs on leaving a world but leaves them reporting
    /// IsOpened, and before the fix the next world's R found that dialog in a static,
    /// closed it, and crashed playing the close sound through the old world.
    ///
    /// Leaving a world for real ends the session the tests run in, so the last test
    /// calls the teardown the LeaveWorld event calls, and stands in for the engine
    /// around it.
    /// </summary>
    public class PackratBrowserLifecycle
    {
        const string Chest = "game:chest-north";

        static BlockPos Stand   => P(8, 1, 8);
        static BlockPos ChestAt => P(8, 1, 10);

        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        [VsTest]
        public async Task DisposingTheSortedViewUnsubscribesItFromItsSources()
        {
            // The sources belong to block entities and outlive the browser, so a view
            // left subscribed is kept alive by every chest it ever showed.
            var inv = new InventoryGeneric(4, "packrat-lifecycle-test", Sapi);
            var composite = new CompositeInventoryView(Sapi);
            composite.AddInventory(inv);
            var view = new SortedInventoryView(composite);
            Assert.Equal(1, Subscribers(inv), "the view listens to its source");

            view.Dispose();
            Assert.Equal(0, Subscribers(inv), "a disposed view does not");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AClosedBrowserIsReleasedAndTheNextOpenBuildsANewOne()
        {
            await PlaceChest();

            var first = await OpenBrowser();
            await CloseBrowser();

            await OnClient();
            Assert.False(first.IsOpened(), "the browser closed");
            Assert.False(Capi.Gui.LoadedGuis.Contains(first), "the closed browser is no longer loaded");
            Assert.False(ClientChest().HasOpened(Capi.World.Player), "closing it closed the chest");
            Assert.Equal(0, Subscribers(ClientChest()), "its view stopped listening to the chest");
            await OnServer();

            var second = await OpenBrowser();
            Assert.False(ReferenceEquals(first, second), "the next open built a new browser");
            await CloseBrowser();
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task LeavingTheWorldDropsAnOpenBrowserWithoutClosingIt()
        {
            await PlaceChest();
            var old = await OpenBrowser();

            await OnClient();
            var system = Capi.ModLoader.GetModSystem<PackratModSystem>();
            var chest = ClientChest();
            Assert.True(Capi.Event.GetType().GetField("LeaveWorld", Private)?.GetValue(Capi.Event)
                is Action leave && leave.GetInvocationList().Any(d => d.Target == null &&
                    d.Method.DeclaringType == typeof(PackratModSystem)), "the teardown is hooked to LeaveWorld");

            int closes = 0;
            old.OnClosed += () => closes++;
            typeof(PackratModSystem).GetMethod("EndClientSession", Private).Invoke(null, null);

            Assert.False(old.IsOpened(), "the dropped browser no longer reports itself open");
            Assert.False(old.TryClose(), "and cannot be closed afterwards");
            Assert.Equal(0, closes, "it was dropped, not closed");
            Assert.True(chest.HasOpened(Capi.World.Player), "so it sent nothing to close the chest");
            Assert.Null(StaticField("_browserDialog"), "nothing keeps hold of it");
            Assert.Null(StaticField("_clientApi"), "nor of the client it belonged to");

            // Stand in for the engine: in a real leave its lists and the chest go with
            // the world. Then give Packrat its client back, as the next world would.
            Capi.Gui.OpenedGuis.Remove(old);
            Capi.Gui.LoadedGuis.Remove(old);
            Capi.Network.SendPacketClient(chest.Close(Capi.World.Player));
            Capi.World.Player.InventoryManager.CloseInventory(chest);
            typeof(PackratModSystem).GetField("_clientApi", Private).SetValue(null, Capi);
            await OnServer();
            await Ticks(4);

            // The crash was here: R reached the old browser and closed it.
            var fresh = await OpenBrowser();
            Assert.False(ReferenceEquals(old, fresh), "R opened a new browser rather than touching the old one");
            await CloseBrowser();
        }

        // ---------- helpers ----------

        static async Task PlaceChest()
        {
            World.SetBlock(Chest, ChestAt);
            var be = World.BE<BlockEntityGenericTypedContainer>(ChestAt);
            be.Inventory[0].Itemstack = World.Stack("game:stick");
            be.MarkDirty(true);
            await Ticks(10);

            await Player.Teleport(Stand);
            await Ticks(10);
        }

        static async Task<GuiDialogStorageBrowser> OpenBrowser()
        {
            await Input.Hotkey(PackratModSystem.ModId + ".openall");
            var dialog = await Gui.WaitFor<GuiDialogStorageBrowser>();
            await Frames.Wait(2);
            return dialog;
        }

        static async Task CloseBrowser()
        {
            await Input.Hotkey(PackratModSystem.ModId + ".openall");
            await Ticks(10);
        }

        static InventoryBase ClientChest()
        {
            var be = Capi.World.BlockAccessor.GetBlockEntity(ChestAt) as BlockEntityGenericTypedContainer;
            Assert.NotNull(be, "the client has the chest");
            return (InventoryBase)be.Inventory;
        }

        static object StaticField(string name) =>
            typeof(PackratModSystem).GetField(name, Private).GetValue(null);

        static int Subscribers(InventoryBase inv) =>
            (typeof(InventoryBase).GetField("SlotModified", Private).GetValue(inv) as Delegate)?
                .GetInvocationList().Count(d => d.Target is SortedInventoryView) ?? 0;
    }
}
