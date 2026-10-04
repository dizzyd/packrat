using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Issue #6: a GUI disposed during disconnect must never close against its old
    /// world after a new world starts. These headless tests call the actual mod and
    /// engine lifecycle methods; only external client services are controlled.
    /// Uninitialized dialogs deliberately bypass composition/OpenGL, not disposal
    /// or close logic. This is not an end-to-end disconnect/rejoin client test.
    /// </summary>
    public class PackratBrowserLifecycle
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;

        [VsTest]
        public void DisposedOpenBrowserCannotReachTheOldWorldRandom()
        {
            // The same real engine objects as the issue's controlled reproduction.
            // Its old ClientMain.Random throws even while the new world's is healthy.
            var player = Uninitialized<ClientPlayer>();
            var worldData = Uninitialized<ClientWorldPlayerData>();
            Set(worldData, "entityplayer", new EntityPlayer());
            Set(player, "worlddata", worldData);
            var oldWorld = Uninitialized<ClientMain>();
            oldWorld.player = player;
            oldWorld.rand = new ThreadLocal<Random>(() => new Random(1));
            oldWorld.rand.Dispose();
            using var healthyRandom = new ThreadLocal<Random>(() => new Random(2));
            var oldApi = Uninitialized<ClientCoreAPI>();
            Set(oldApi, "game", oldWorld);
            using var dialog = Dialog(oldApi);
            int closeEvents = 0;
            dialog.OnClosed += () => closeEvents++;

            dialog.Dispose();
            dialog.Dispose();
            Assert.False(dialog.IsOpened(), "Dispose invalidates the engine's opened flag");
            Assert.False(dialog.Focused, "Dispose invalidates the engine's focused flag");
            Assert.False(dialog.TryClose(), "a disposed dialog cannot close against its old API");
            Assert.False(dialog.TryOpen(), "a disposed dialog cannot reopen");
            Assert.False(dialog.TryOpen(false), "neither open overload can resurrect it");
            Assert.Equal(0, closeEvents, "world teardown is not a normal GUI close");
            Assert.NotNull(healthyRandom.Value, "the replacement world's RNG stays healthy");
            Assert.Throws<ObjectDisposedException>(() => { var ignored = oldWorld.rand.Value; },
                "negative-control fixture really has the reported disposed ThreadLocal");
        }

        [VsTest]
        public Task LeavingTheOwnerCancelsPendingWorkAndSilentlyDisposesTheDialog() => Isolated(() =>
        {
            var services = new ClientServices();
            var owner = new PackratModSystem();
            Begin(owner, services.Api);
            Assert.Equal(1, services.ListenerCount("LeaveWorld"), "one leave listener");
            Assert.Equal(1, services.ListenerCount("PlayerJoin"), "one join listener");
            using var dialog = Dialog(services.Api);
            SeedBrowse(dialog, 42);

            services.Raise("LeaveWorld");
            owner.Dispose(); // Engine disposal after LeaveWorld is intentionally redundant.
            Assert.Null(Get("_clientSessionOwner"), "no owner retained after leaving");
            Assert.Null(Get("_clientApi"), "no old API retained");
            Assert.Null(Get("_browserDialog"), "no old dialog retained");
            Assert.False(dialog.IsOpened(), "old dialog disposed silently");
            Assert.False(dialog.TryClose(), "old dialog cannot later run close work");
            Assert.False((bool)Get("_browseMode"), "browse mode reset");
            Assert.Equal(0, Pending.Count, "pending positions cleared");
            Assert.Equal(0, Opened.Count, "opened container references cleared");
            Assert.Equal(0, Get("_pendingCrateConfirmation"), "crate confirmation cleared");
            Assert.Equal(0L, Get("_browseTimeoutCallbackId"), "timeout ID cleared");
            Assert.Null(Get("_browseRequest"), "request identity invalidated");
            Assert.Equal(1, services.Cancelled.Count, "timeout cancelled exactly once");
            Assert.Equal(42L, services.Cancelled[0], "the owner's timeout was cancelled");
            Assert.Equal(0, services.ListenerCount("LeaveWorld"), "leave listener removed");
            Assert.Equal(0, services.ListenerCount("PlayerJoin"), "join listener removed");
            Assert.Equal(0, services.AudioCalls, "teardown made no sound calls");
            Assert.Equal(0, services.Packets, "teardown sent no inventory packets");
            Assert.Equal(0, services.LidPackets, "teardown sent no lid packets");
        });

        [VsTest]
        public Task ReplacingTheSessionProtectsTheNewOwnerFromOldAndServerDisposal() => Isolated(() =>
        {
            var oldServices = new ClientServices();
            var oldOwner = new PackratModSystem();
            Begin(oldOwner, oldServices.Api);
            var oldDialog = Dialog(oldServices.Api);
            SeedBrowse(oldDialog, 41);
            var lateLeave = oldServices.Listener("LeaveWorld");

            var newServices = new ClientServices();
            var newOwner = new PackratModSystem();
            Begin(newOwner, newServices.Api);
            Assert.False(oldDialog.IsOpened(), "starting another session disposes the old GUI");
            Assert.Equal(0, oldServices.ListenerCount("LeaveWorld"), "old event source detached");
            Assert.Equal(0, oldServices.ListenerCount("PlayerJoin"), "old join listener detached");
            Assert.Equal(41L, oldServices.Cancelled[0], "old API cancels its own callback");
            var newDialog = Dialog(newServices.Api);
            var request = SeedBrowse(newDialog, 84);

            oldOwner.Dispose();
            lateLeave.DynamicInvoke(); // Queued invocation captured before detaching.
            new PackratModSystem().Dispose(); // Server-side instance never owned the client.
            AssertCurrent(newOwner, newServices, newDialog, request, 84);
            Assert.Equal(0, newServices.Cancelled.Count, "obsolete teardown cannot cancel new work");
            Assert.Equal(1, newServices.ListenerCount("LeaveWorld"), "new leave listener survives");
            Assert.Equal(1, newServices.ListenerCount("PlayerJoin"), "new join listener survives");
        });

        [VsTest]
        public Task LateTimeoutAndConfirmationCannotMutateTheReplacementSession() => Isolated(() =>
        {
            var oldServices = new ClientServices();
            var oldOwner = new PackratModSystem();
            Begin(oldOwner, oldServices.Api);
            var oldRequest = SeedBrowse(Dialog(oldServices.Api), 41);
            var newServices = new ClientServices();
            var newOwner = new PackratModSystem();
            Begin(newOwner, newServices.Api);
            var newDialog = Dialog(newServices.Api);
            var newRequest = SeedBrowse(newDialog, 84);

            Invoke(null, "OnBrowseTimeout", oldServices.Api, oldRequest, 3f);
            Invoke(oldOwner, "HandleOpenManyConfirm", new OpenManyConfirmMessage
            {
                SkippedPositions = new List<BlockPos> { new BlockPos(1, 2, 3, 0) }
            });
            oldOwner.OpenAll(null); // A retained old hotkey delegate must also be inert.
            AssertCurrent(newOwner, newServices, newDialog, newRequest, 84);
            Assert.Equal(0, newServices.AudioCalls, "late old work cannot close the new GUI");
        });

        [VsTest]
        public Task ResetInvalidatesTimeoutsEvenWithinTheSameClientSession() => Isolated(() =>
        {
            var services = new ClientServices();
            var owner = new PackratModSystem();
            Begin(owner, services.Api);
            var oldRequest = SeedBrowse(Dialog(services.Api), 41);
            Invoke(null, "ResetBrowseMode");
            var newDialog = Dialog(services.Api);
            var newRequest = SeedBrowse(newDialog, 84);

            Invoke(null, "OnBrowseTimeout", services.Api, oldRequest, 3f);
            AssertCurrent(owner, services, newDialog, newRequest, 84);
            Assert.Equal(1, services.Cancelled.Count, "only the first request was cancelled");
        });

        [VsTest]
        public void NormalCloseStillClosesInventoryAndLidWhenAudioIsDisposed()
        {
            var services = new ClientServices { AudioFailure = new ObjectDisposedException("old audio RNG") };
            var inventory = new CloseCountingInventory();
            var container = new CloseCountingContainer(inventory);
            using var dialog = Dialog(services.Api, container);
            int closed = 0;
            dialog.OnClosed += () => closed++;
            Assert.True(dialog.TryClose(), "ordinary close succeeds despite a disposed sound service");
            Assert.Equal(1, inventory.Closes, "inventory Close is still called");
            Assert.Equal(1, services.InventoryCloses, "player inventory manager still closes it");
            Assert.Equal(1, services.Packets, "inventory close packet still sent");
            Assert.Equal(1, services.LidPackets, "lid close packet still sent");
            Assert.Equal(1, services.AudioCalls, "sound was attempted, then failed harmlessly");
            Assert.Equal(1, closed, "normal GUI close event is preserved");
            Assert.Equal(1, services.GuiCloses, "engine GUI close notification is preserved");
            dialog.TryClose();
            dialog.Dispose();
            Assert.Equal(1, inventory.Closes, "repeated close/dispose does not close inventories twice");
            Assert.Equal(1, services.AudioCalls, "repeated close/dispose does not retry audio");
        }

        [VsTest]
        public void OpenSoundAlsoToleratesDisposedAudioWithoutHidingOtherFailures()
        {
            var services = new ClientServices { AudioFailure = new ObjectDisposedException("audio") };
            using var dialog = Dialog(services.Api);
            dialog.OnGuiOpened();
            Assert.Equal(1, services.AudioCalls, "open sound exercised");
            services.AudioFailure = new InvalidOperationException("not a disposal failure");
            var error = Assert.Throws<InvalidOperationException>(() => dialog.OnGuiOpened());
            Assert.Equal("not a disposal failure", error.Message, "unrelated audio errors remain visible");
            dialog.Dispose();
        }

        [VsTest]
        public void InventoryFailuresAreNotSwallowedByTheAudioGuard()
        {
            var services = new ClientServices();
            var inventory = new CloseCountingInventory { Failure = new ObjectDisposedException("inventory") };
            using var dialog = Dialog(services.Api, new CloseCountingContainer(inventory));
            var error = Assert.Throws<ObjectDisposedException>(() => dialog.TryClose());
            Assert.Equal("inventory", error.ObjectName, "inventory exception escapes unchanged");
            Assert.Equal(0, services.AudioCalls, "failed inventory close never reached audio");
            dialog.Dispose();
        }

        [VsTest]
        public void InvalidSessionCallbacksAndShutdownAreSilent()
        {
            var services = new ClientServices();
            var inventory = new CloseCountingInventory();
            using var staleDialog = Dialog(services.Api, new CloseCountingContainer(inventory));
            SetSessionCheck(staleDialog, () => false);
            staleDialog.TryClose();
            staleDialog.OnGuiClosed(); // A close callback already queued by the old GUI.
            staleDialog.OnGuiOpened();
            Assert.False(staleDialog.TryOpen(), "an obsolete dialog cannot reopen");
            Assert.Equal(0, inventory.Closes, "stale callbacks never close old inventories");
            Assert.Equal(0, services.AudioCalls, "stale callbacks never use old sound services");
            Assert.Equal(0, services.Packets, "stale callbacks never send packets");
            staleDialog.Dispose();

            services.ShuttingDown = true;
            using var shuttingDownDialog = Dialog(services.Api, new CloseCountingContainer(inventory));
            shuttingDownDialog.OnGuiOpened();
            shuttingDownDialog.TryClose();
            Assert.Equal(0, services.AudioCalls, "shutdown does not attempt optional audio");
            Assert.Equal(0, inventory.Closes, "shutdown does not close through disposed world services");
            shuttingDownDialog.Dispose();
        }

        [VsTest]
        public void DisposedDialogIgnoresQueuedSlotPackets()
        {
            var services = new ClientServices();
            using var dialog = Dialog(services.Api);
            var send = typeof(GuiDialogStorageBrowser).GetMethod("DoSendPacket", Fields);
            Assert.NotNull(send, "slot-grid send callback exists");
            send.Invoke(dialog, new[] { new object() });
            Assert.Equal(1, services.Packets, "active slot-grid callback sends normally");
            dialog.Dispose();
            send.Invoke(dialog, new[] { new object() });
            Assert.Equal(1, services.Packets, "queued slot-grid callback sends nothing after disposal");
        }

        [VsTest]
        public void DialogDisposalUnsubscribesTheSortedViewFromSourceInventories()
        {
            var services = new ClientServices();
            var source = new InventoryGeneric(1, "packrat-lifecycle", "slots", null);
            var composite = new CompositeInventoryView(null);
            composite.AddInventory(source);
            var sorted = new SortedInventoryView(composite);
            int otherSubscriberCalls = 0;
            source.SlotModified += slot => otherSubscriberCalls++;
            var dirty = Field(typeof(SortedInventoryView), "_isDirty");
            source.DidModifyItemSlot(source[0]);
            Assert.True((bool)dirty.GetValue(sorted), "source change reached the active sorted view");
            sorted.FilterPredicate = (index, slot) => true;
            sorted.RebuildDisplayOrder();
            Assert.False((bool)dirty.GetValue(sorted), "control starts clean");
            using var dialog = Dialog(services.Api);
            Set(dialog, "_sortedInventory", sorted);
            dialog.Dispose();
            dialog.Dispose();
            source.DidModifyItemSlot(source[0]);
            Assert.False((bool)dirty.GetValue(sorted), "disposed view no longer receives source changes");
            Assert.False(sorted.IsFiltering, "disposed view releases its captured filter callback");
            Assert.Equal(2, otherSubscriberCalls, "unrelated slot subscribers remain registered");
        }

        [VsTest]
        public Task CurrentSessionShutdownRejectsHotkeysAndConfirmations() => Isolated(() =>
        {
            var services = new ClientServices();
            var owner = new PackratModSystem();
            Begin(owner, services.Api);
            using var dialog = Dialog(services.Api);
            var request = SeedBrowse(dialog, 84);
            services.ShuttingDown = true;
            owner.OpenAll(null);
            Invoke(owner, "HandleOpenManyConfirm", new OpenManyConfirmMessage
            {
                SkippedPositions = new List<BlockPos> { new BlockPos(1, 2, 3, 0) }
            });
            AssertCurrent(owner, services, dialog, request, 84);
            Assert.Equal(0, services.AudioCalls, "shutdown hotkey did not close through old world");
        });

        // The fake GUI deliberately excludes rendering. This additional tier checks
        // actual composition, close and reopen with a real client and real inventory.
        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task LiveClientBrowserCloseAndReopenClosesTheRealInventory()
        {
            World.SetBlock("game:chest-north", P(4, 1, 4));
            await Ticks(10);
            await Player.StandNear(P(4, 1, 4));
            await OnClient();
            var chest = Capi.World.BlockAccessor.GetBlockEntity(P(4, 1, 4)) as BlockEntityContainer;
            Assert.NotNull(chest, "client received the chest block entity");
            var player = Capi.World.Player;
            GuiDialogStorageBrowser dialog = null;
            try
            {
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    player.InventoryManager.OpenInventory(chest.Inventory);
                    Assert.True(chest.Inventory.HasOpened(player), "real inventory is open before browser");
                    var composite = new CompositeInventoryView(Capi);
                    composite.AddInventory(chest.Inventory, false);
                    dialog = new GuiDialogStorageBrowser(Capi, new SortedInventoryView(composite),
                        new List<BlockEntityContainer> { chest });
                    Assert.True(dialog.TryOpen(), "real rendered browser opens");
                    Assert.True(dialog.TryClose(), "real rendered browser closes");
                    Assert.False(chest.Inventory.HasOpened(player), "normal close closes the real inventory");
                    dialog.Dispose();
                    Assert.False(dialog.TryOpen(), "disposed real browser cannot reopen");
                    dialog = null;
                }
            }
            finally
            {
                if (dialog != null)
                {
                    dialog.TryClose();
                    dialog.Dispose();
                }
                player.InventoryManager.CloseInventory(chest.Inventory);
                if (Sapi != null) await OnServer();
            }
        }

        static GuiDialogStorageBrowser Dialog(ICoreClientAPI api, params BlockEntityContainer[] containers)
        {
            var dialog = Uninitialized<GuiDialogStorageBrowser>();
            Set(dialog, "capi", api);
            Set(dialog, "_capi", api);
            Set(dialog, "_containers", new List<BlockEntityContainer>(containers));
            Set(dialog, "opened", true);
            Set(dialog, "focused", true);
            dialog.Composers = new GuiDialog.DlgComposers(dialog);
            // Old revisions lack the predicate. Keeping this optional lets their
            // disposal regression fail on behavior rather than a missing field.
            var predicate = SessionCheckField();
            if (predicate != null) predicate.SetValue(dialog, (Func<bool>)(() => true));
            return dialog;
        }

        static FieldInfo SessionCheckField()
        {
            foreach (var field in typeof(GuiDialogStorageBrowser).GetFields(Fields))
                if (field.FieldType == typeof(Func<bool>)) return field;
            return null;
        }

        static void SetSessionCheck(GuiDialogStorageBrowser dialog, Func<bool> predicate)
        {
            var field = SessionCheckField();
            Assert.NotNull(field, "dialog has a session-validity predicate");
            field.SetValue(dialog, predicate);
        }

        static void Begin(PackratModSystem system, ICoreClientAPI api) => Invoke(system, "BeginClientSession", api);
        static HashSet<BlockPos> Pending => (HashSet<BlockPos>)Get("_pendingPositions");
        static List<BlockEntityContainer> Opened => (List<BlockEntityContainer>)Get("_openedContainers");

        static object SeedBrowse(GuiDialogStorageBrowser dialog, long timeout)
        {
            var request = new object();
            Put("_browserDialog", dialog);
            Put("_browseMode", true);
            Put("_browseRequest", request);
            Put("_browseTimeoutCallbackId", timeout);
            Put("_pendingCrateConfirmation", 2);
            Pending.Clear();
            Pending.Add(new BlockPos(1, 2, 3, 0));
            Opened.Clear();
            Opened.Add(new CloseCountingContainer(new CloseCountingInventory()));
            return request;
        }

        static void AssertCurrent(PackratModSystem owner, ClientServices services,
            GuiDialogStorageBrowser dialog, object request, long timeout)
        {
            Assert.True(ReferenceEquals(owner, Get("_clientSessionOwner")), "new owner retained");
            Assert.True(ReferenceEquals(services.Api, Get("_clientApi")), "new API retained");
            Assert.True(ReferenceEquals(dialog, Get("_browserDialog")), "new dialog retained");
            Assert.True(dialog.IsOpened(), "new GUI remains open");
            Assert.True((bool)Get("_browseMode"), "new browse request remains active");
            Assert.True(ReferenceEquals(request, Get("_browseRequest")), "new request token retained");
            Assert.Equal(timeout, Get("_browseTimeoutCallbackId"), "new callback ID unchanged");
            Assert.Equal(2, Get("_pendingCrateConfirmation"), "new crate responses still pending");
            Assert.Equal(1, Pending.Count, "new pending positions unchanged");
            Assert.Equal(1, Opened.Count, "new container set unchanged");
        }

        static async Task Isolated(Action test)
        {
            // A single synchronous critical section on the client thread when one
            // exists. Never yield while Packrat's live static state is replaced.
            if (Capi != null) await OnClient();
            var saved = new Dictionary<FieldInfo, object>();
            foreach (var name in new[] { "_clientSessionOwner", "_clientApi", "_browserDialog",
                "_browseMode", "_browseRequest", "_browseTimeoutCallbackId", "_pendingCrateConfirmation",
                "_pendingPositions", "_openedContainers", "_knownBlockTokens" })
            {
                var field = typeof(PackratModSystem).GetField(name, Fields);
                if (field != null) saved.Add(field, field.GetValue(null));
            }
            try
            {
                foreach (var entry in saved)
                    entry.Key.SetValue(null, entry.Key.FieldType.IsValueType
                        ? Activator.CreateInstance(entry.Key.FieldType) : null);
                Put("_pendingPositions", new HashSet<BlockPos>());
                Put("_openedContainers", new List<BlockEntityContainer>());
                test();
            }
            finally
            {
                try
                {
                    var ownerField = typeof(PackratModSystem).GetField("_clientSessionOwner", Fields);
                    if (ownerField?.GetValue(null) is PackratModSystem owner)
                        Invoke(owner, "EndClientSession");
                }
                finally
                {
                    foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
                }
                if (Capi != null && Sapi != null) await OnServer();
            }
        }

        static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        static object Get(string name) => Field(typeof(PackratModSystem), name).GetValue(null);
        static void Put(string name, object value) => Field(typeof(PackratModSystem), name).SetValue(null, value);
        static void Set(object instance, string name, object value) => Field(instance.GetType(), name).SetValue(instance, value);
        static FieldInfo Field(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, Fields | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new InvalidOperationException("Missing lifecycle field " + type.Name + "." + name);
        }

        static void Invoke(object instance, string name, params object[] args)
        {
            var method = typeof(PackratModSystem).GetMethod(name, Fields);
            Assert.NotNull(method, "lifecycle seam " + name);
            try { method.Invoke(instance, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        // DispatchProxy must be public and non-sealed to generate its subclass.
        // Unexpected service accesses fail: a test cannot silently access an old
        // world, network or renderer through a default-returning mock.
        public class ServiceProxy : DispatchProxy
        {
            public System.Func<MethodInfo, object[], object> Handler;
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
            public static T Create<T>(System.Func<MethodInfo, object[], object> handler) where T : class
            {
                var proxy = DispatchProxy.Create<T, ServiceProxy>();
                ((ServiceProxy)(object)proxy).Handler = handler;
                return proxy;
            }
        }

        sealed class ClientServices
        {
            public readonly ICoreClientAPI Api;
            public readonly List<long> Cancelled = new List<long>();
            public bool ShuttingDown;
            public Exception AudioFailure;
            public int AudioCalls, Packets, LidPackets, InventoryCloses, GuiCloses;
            readonly Dictionary<string, Delegate> listeners = new Dictionary<string, Delegate>();

            public ClientServices()
            {
                var events = ServiceProxy.Create<IClientEventAPI>((method, args) =>
                {
                    if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_"))
                    {
                        var name = method.Name.Substring(method.Name.IndexOf('_') + 1);
                        listeners.TryGetValue(name, out var existing);
                        listeners[name] = method.Name.StartsWith("add_")
                            ? Delegate.Combine(existing, (Delegate)args[0])
                            : Delegate.Remove(existing, (Delegate)args[0]);
                        return null;
                    }
                    if (method.Name == "UnregisterCallback") { Cancelled.Add((long)args[0]); return null; }
                    return Unexpected(method);
                });
                var manager = ServiceProxy.Create<IPlayerInventoryManager>((method, args) =>
                {
                    if (method.Name == "CloseInventory") { InventoryCloses++; return null; }
                    return Unexpected(method);
                });
                var player = Uninitialized<FixturePlayer>();
                var data = Uninitialized<ClientWorldPlayerData>();
                data.PlayerUID = "packrat-lifecycle-test";
                Set(data, "entityplayer", new EntityPlayer());
                Set(player, "worlddata", data);
                player.Manager = manager;
                var world = ServiceProxy.Create<IClientWorldAccessor>((method, args) =>
                {
                    if (method.Name == "get_Player") return player;
                    if (method.Name == "PlaySoundAt")
                    {
                        AudioCalls++;
                        if (AudioFailure != null) throw AudioFailure;
                        return null;
                    }
                    return Unexpected(method);
                });
                var network = ServiceProxy.Create<IClientNetworkAPI>((method, args) =>
                {
                    if (method.Name == "SendPacketClient") { Packets++; return null; }
                    if (method.Name == "SendBlockEntityPacket")
                    {
                        Assert.Equal((int)EnumBlockEntityPacketId.Close, args[1], "lid CLOSE packet ID");
                        LidPackets++;
                        return null;
                    }
                    return Unexpected(method);
                });
                var gui = ServiceProxy.Create<IGuiAPI>((method, args) =>
                {
                    if (method.Name == "TriggerDialogClosed") { GuiCloses++; return null; }
                    return Unexpected(method);
                });
                Api = ServiceProxy.Create<ICoreClientAPI>((method, args) =>
                {
                    switch (method.Name)
                    {
                        case "get_Event": return events;
                        case "get_World": return world;
                        case "get_Network": return network;
                        case "get_Gui": return gui;
                        case "get_IsShuttingDown": return ShuttingDown;
                        default: return Unexpected(method);
                    }
                });
            }

            public Delegate Listener(string name) => listeners.TryGetValue(name, out var result) ? result : null;
            public int ListenerCount(string name) => Listener(name)?.GetInvocationList().Length ?? 0;
            public void Raise(string name) => Listener(name)?.DynamicInvoke();
            static object Unexpected(MethodInfo method) => throw new InvalidOperationException(
                "Unexpected client service access: " + method.DeclaringType.Name + "." + method.Name);
        }

        // IClientPlayer has an internal default interface method in the game SDK,
        // which DispatchProxy cannot implement. Keep the real engine player and
        // override only its public inventory-service interface for these tests.
        sealed class FixturePlayer : ClientPlayer, IClientPlayer
        {
            public IPlayerInventoryManager Manager;
            public FixturePlayer() : base(null) { } // Construction is bypassed above.
            IPlayerInventoryManager IPlayer.InventoryManager => Manager;
        }

        sealed class CloseCountingInventory : InventoryGeneric
        {
            public int Closes;
            public Exception Failure;
            public CloseCountingInventory() : base(1, "packrat-lifecycle", "fixture", null) { }
            public override bool HasOpened(IPlayer player) => Closes == 0;
            public override object Close(IPlayer player)
            {
                if (Failure != null) throw Failure;
                Closes++;
                return new object();
            }
        }

        sealed class CloseCountingContainer : BlockEntityOpenableContainer
        {
            readonly InventoryBase inventory;
            public CloseCountingContainer(InventoryBase inventory)
            {
                this.inventory = inventory;
                Pos = new BlockPos(1, 2, 3, 0);
            }
            public override InventoryBase Inventory => inventory;
            public override string InventoryClassName => "packrat-lifecycle";
            public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel) => false;
        }
    }
}
