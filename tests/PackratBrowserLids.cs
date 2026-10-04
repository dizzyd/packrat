using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Issue #5: pin the released close-packet fix. An ordinary inventory-close
    /// packet updates inventory state, but only BE Close tells observers to shut
    /// the lid. These tests execute actual Packrat/engine close methods and vanilla
    /// inventory callbacks, with controlled client services and no rendered GUI.
    /// </summary>
    public class PackratBrowserLids
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;

        [VsTest]
        public void NormalCloseNotifiesEveryOpenChest() => CheckClose(d => d.TryClose());

        [VsTest]
        public void EscapeNotifiesEveryOpenChest() => CheckClose(d => d.OnEscapePressed());

        [VsTest]
        public void TitleButtonNotifiesEveryOpenChest() => CheckClose(d =>
        {
            var close = typeof(GuiDialogStorageBrowser).GetMethod("OnTitleBarClose", Fields);
            Assert.NotNull(close, "title-bar callback remains present");
            close.Invoke(d, null);
        });

        [VsTest]
        public async Task HotkeyNotifiesEveryOpenChest()
        {
            // The hotkey reads static client state. Replace and restore it in one
            // synchronous section on the client thread if one exists; never yield
            // while another session's state is replaced.
            if (Capi != null) await OnClient();
            var saved = new Dictionary<FieldInfo, object>();
            foreach (var name in new[] { "_clientApi", "_browserDialog", "_clientSessionOwner" })
            {
                var field = typeof(PackratModSystem).GetField(name, Fields);
                if (field != null) saved.Add(field, field.GetValue(null));
            }
            try
            {
                using var fixture = new ClientFixture();
                var chests = new[] { fixture.Chest(1), fixture.Chest(2) };
                var dialog = fixture.Open(chests);
                var owner = new PackratModSystem();
                Set(null, typeof(PackratModSystem), "_clientApi", fixture.Api);
                Set(null, typeof(PackratModSystem), "_browserDialog", dialog);
                // Later session-owned implementations use these additional fields.
                // Optional setup keeps this ordinary-close contract independent of
                // the lifecycle fix while still usable after that fix is merged.
                typeof(PackratModSystem).GetField("_clientSessionOwner", Fields)?.SetValue(null, owner);
                typeof(PackratModSystem).GetField("_sessionApi", Fields)?.SetValue(owner, fixture.Api);
                Assert.True(owner.OpenAll(null), "R closes an already-open browser");
                fixture.AssertClosed(dialog, chests);
                Assert.Null(typeof(PackratModSystem).GetField("_browserDialog", Fields).GetValue(null),
                    "hotkey releases the closed browser");
            }
            finally
            {
                foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
                if (Capi != null && Sapi != null) await OnServer();
            }
        }

        [VsTest]
        public void ReopeningTheSameChestsSendsFreshClosePackets()
        {
            using var fixture = new ClientFixture();
            var chests = new[] { fixture.Chest(1), fixture.Chest(2) };
            for (int repeat = 0; repeat < 3; repeat++)
            {
                fixture.ClearPackets();
                var dialog = fixture.Open(chests); // A new browser owns each opening.
                dialog.TryClose();
                fixture.AssertClosed(dialog, chests);
            }
        }

        [VsTest]
        public void SwitchingContainerSetsDoesNotCloseThePreviousSetAgain()
        {
            using var fixture = new ClientFixture();
            var shared = fixture.Chest(2);
            var first = new[] { fixture.Chest(1), shared };
            var second = new[] { shared, fixture.Chest(3) };
            var dialog = fixture.Open(first);
            dialog.TryClose();
            fixture.AssertClosed(dialog, first);
            fixture.ClearPackets();
            dialog = fixture.Open(second);
            dialog.OnEscapePressed();
            fixture.AssertClosed(dialog, second);
            Assert.False(first[0].Inventory.HasOpened(fixture.Player), "old-only chest stays closed");
        }

        [VsTest]
        public void AlreadyClosedChestsAreNotNotifiedAgain()
        {
            using var fixture = new ClientFixture();
            var closed = fixture.Chest(1);
            var open = fixture.Chest(2);
            var dialog = fixture.Open(new[] { open });
            // Include a known-but-not-open inventory, as a partial-open browser can.
            Set(dialog, typeof(GuiDialogStorageBrowser), "_containers",
                new List<BlockEntityContainer> { closed, null, open });
            dialog.TryClose();
            fixture.AssertClosed(dialog, new[] { open });
            Assert.False(closed.Inventory.HasOpened(fixture.Player), "never-opened chest remains closed");
        }

        static void CheckClose(Action<GuiDialogStorageBrowser> close)
        {
            using var fixture = new ClientFixture();
            var chests = new[] { fixture.Chest(1), fixture.Chest(2) };
            var dialog = fixture.Open(chests);
            close(dialog);
            fixture.AssertClosed(dialog, chests);
        }

        static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        static void Set(object target, Type type, string name, object value)
        {
            var field = type.GetField(name, Fields);
            Assert.NotNull(field, type.Name + "." + name);
            field.SetValue(target, value);
        }

        public class ServiceProxy : DispatchProxy
        {
            public System.Func<MethodInfo, object[], object> Handler;
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
            public static T Create<T>(System.Func<MethodInfo, object[], object> handler) where T : class
            {
                var value = DispatchProxy.Create<T, ServiceProxy>();
                ((ServiceProxy)(object)value).Handler = handler;
                return value;
            }
        }

        sealed class ClientFixture : IDisposable
        {
            public readonly ICoreClientAPI Api;
            public readonly ClientPlayer Player;
            readonly ClientPlayerInventoryManager manager;
            readonly List<GuiDialog> dialogs = new List<GuiDialog>();
            readonly List<BlockPos> lids = new List<BlockPos>();
            readonly List<string> inventories = new List<string>();

            public ClientFixture()
            {
                // IClientPlayer has an internal default interface method that a
                // DispatchProxy cannot implement. Use actual engine player data.
                Player = Uninitialized<ClientPlayer>();
                var data = Uninitialized<ClientWorldPlayerData>();
                data.PlayerUID = "packrat-lid-test";
                data.CurrentGameMode = EnumGameMode.Survival;
                Set(data, typeof(ClientWorldPlayerData), "entityplayer", new EntityPlayer { EntityId = 42 });
                Set(Player, typeof(ClientPlayer), "worlddata", data);
                manager = new ClientPlayerInventoryManager(
                    new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>(), Player, null);
                Set(Player, typeof(ClientPlayer), "inventoryMgr", manager);
                var world = ServiceProxy.Create<IClientWorldAccessor>((method, args) =>
                {
                    if (method.Name == "get_Player") return Player;
                    if (method.Name == "PlaySoundAt") return null; // Optional feedback, not a lid signal.
                    return Unexpected(method);
                });
                var gui = ServiceProxy.Create<IGuiAPI>((method, args) =>
                {
                    if (method.Name == "get_LoadedGuis") return dialogs;
                    if (method.Name == "RegisterDialog") { dialogs.AddRange((GuiDialog[])args[0]); return null; }
                    if (method.Name == "TriggerDialogOpened" || method.Name == "TriggerDialogClosed" ||
                        method.Name == "RequestFocus") return null;
                    return Unexpected(method);
                });
                var network = ServiceProxy.Create<IClientNetworkAPI>((method, args) =>
                {
                    if (method.Name == "SendPacketClient")
                    {
                        var packet = args[0] as Packet_Client;
                        Assert.NotNull(packet, "real inventory network packet");
                        Assert.Equal(30, packet.Id, "ordinary inventory open/close packet");
                        Assert.Equal(0, packet.InvOpenedClosed.Opened, "inventory packet closes");
                        inventories.Add(packet.InvOpenedClosed.InventoryId);
                        return null;
                    }
                    if (method.Name == "SendBlockEntityPacket")
                    {
                        Assert.Equal((int)EnumBlockEntityPacketId.Close, args[1], "lid CLOSE packet ID");
                        Assert.Null(args[2], "close message needs no payload");
                        lids.Add(((BlockPos)args[0]).Copy());
                        return null;
                    }
                    return Unexpected(method);
                });
                Api = ServiceProxy.Create<ICoreClientAPI>((method, args) => method.Name switch
                {
                    "get_World" => world, "get_Gui" => gui, "get_Network" => network,
                    "get_Side" => EnumAppSide.Client, "get_IsShuttingDown" => false,
                    _ => Unexpected(method)
                });
            }

            public BlockEntityGenericTypedContainer Chest(int x)
            {
                var inventory = new InventoryGeneric(1, "chest", x.ToString(), null) { Api = Api };
                inventory.InvNetworkUtil = new InventoryNetworkUtil(inventory, Api);
                var chest = new BlockEntityGenericTypedContainer
                {
                    Api = Api, Pos = new BlockPos(x, 0, 0, 0), LidOpenEntityId = new HashSet<long>(), type = "normal-generic",
                    Block = new Block { Attributes = Vintagestory.API.Datastructures.JsonObject.FromJson("{}") }
                };
                Set(chest, typeof(BlockEntityGenericTypedContainer), "inventory", inventory);
                inventory.OnInventoryOpened += (OnInventoryOpenedDelegate)Delegate.CreateDelegate(
                    typeof(OnInventoryOpenedDelegate), chest,
                    typeof(BlockEntityGenericTypedContainer).GetMethod("OnInvOpened", Fields));
                inventory.OnInventoryClosed += (OnInventoryClosedDelegate)Delegate.CreateDelegate(
                    typeof(OnInventoryClosedDelegate), chest,
                    typeof(BlockEntityGenericTypedContainer).GetMethod("OnInvClosed", Fields));
                return chest;
            }

            public GuiDialogStorageBrowser Open(BlockEntityGenericTypedContainer[] chests)
            {
                foreach (var chest in chests)
                {
                    manager.OpenInventory(chest.Inventory);
                    Assert.True(chest.Inventory.HasOpened(Player), "inventory opened before browser");
                    Assert.True(chest.LidOpenEntityId.Contains(Player.Entity.EntityId), "vanilla lid callback ran");
                }
                // Skip only composition/OpenGL. Open, close and inventory logic are real.
                var dialog = Uninitialized<GuiDialogStorageBrowser>();
                dialog.Composers = new GuiDialog.DlgComposers(dialog);
                Set(dialog, typeof(GuiDialog), "capi", Api);
                Set(dialog, typeof(GuiDialogStorageBrowser), "_capi", Api);
                Set(dialog, typeof(GuiDialogStorageBrowser), "_containers", chests.Cast<BlockEntityContainer>().ToList());
                Assert.True(dialog.TryOpen(), "browser opens");
                return dialog;
            }

            public void AssertClosed(GuiDialogStorageBrowser dialog, BlockEntityGenericTypedContainer[] chests)
            {
                Assert.False(dialog.IsOpened(), "dialog closed");
                Assert.Equal(chests.Length, lids.Count, "one distinct lid-close packet per chest");
                Assert.Equal(chests.Length, inventories.Count, "one inventory-close packet per chest");
                foreach (var chest in chests)
                {
                    Assert.Equal(1, lids.Count(pos => pos.Equals(chest.Pos)), "correct lid destination " + chest.Pos);
                    Assert.Equal(1, inventories.Count(id => id == chest.Inventory.InventoryID), "correct inventory ID");
                    Assert.False(chest.Inventory.HasOpened(Player), "local inventory closed");
                    Assert.Equal(0, chest.LidOpenEntityId.Count, "local lid viewer cleared");
                }
                int lidCount = lids.Count, inventoryCount = inventories.Count;
                dialog.TryClose();
                Assert.Equal(lidCount, lids.Count, "repeat close sends no lid packet");
                Assert.Equal(inventoryCount, inventories.Count, "repeat close sends no inventory packet");
            }

            public void ClearPackets() { lids.Clear(); inventories.Clear(); }
            public void Dispose()
            {
                foreach (var dialog in dialogs.ToArray())
                {
                    if (dialog.IsOpened()) dialog.TryClose();
                    dialog.Dispose();
                }
                dialogs.Clear();
            }
            static object Unexpected(MethodInfo method) => throw new InvalidOperationException(
                "Unexpected lid fixture service: " + method.DeclaringType.Name + "." + method.Name);
        }
    }
}
