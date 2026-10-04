using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;
using VsTestkit.Testing;

namespace Packrat.Tests
{
    // The vanilla half of #5: inventory close and block-entity close are different
    // protocols. Real engine containers, inventory managers and animation state;
    // only network/world/render boundaries are isolated, so this runs headless.
    public class PackratLidBroadcast
    {
        [VsTest]
        public Task OrdinaryInventoryCloseLeavesTheObserverLidOpen()
        {
            using var f = new Fixture();
            f.Open(f.Alice);
            f.AssertViewers(f.Alice);
            f.OrdinaryClose(f.Alice);
            f.AssertServerViewers();
            Assert.Equal(0, f.Packets.Count, "ordinary inventory close sends no observer lid packet");
            f.AssertViewers(f.Alice);
            return Task.CompletedTask;
        }

        [VsTest]
        public Task BlockEntityCloseBroadcastsTheActorAndRepeatedCloseIsSafe()
        {
            using var f = new Fixture();
            f.Open(f.Alice);
            f.OrdinaryClose(f.Alice);
            f.CloseAndDeliver(f.Alice);
            f.AssertServerViewers();
            f.AssertViewers();
            // Both close routes may run again after the inventory was already removed.
            f.OrdinaryClose(f.Alice);
            f.CloseAndDeliver(f.Alice);
            f.AssertServerViewers();
            f.AssertViewers();
            return Task.CompletedTask;
        }

        [VsTest]
        public Task TheFirstViewerLeavesTheLidOpenAndTheLastViewerClosesIt()
        {
            using var f = new Fixture();
            f.Open(f.Alice);
            f.Open(f.Bob);
            f.AssertServerViewers(f.Alice, f.Bob);
            f.AssertViewers(f.Alice, f.Bob);
            f.OrdinaryClose(f.Alice);
            f.CloseAndDeliver(f.Alice);
            f.AssertServerViewers(f.Bob);
            f.AssertViewers(f.Bob);
            f.CloseAndDeliver(f.Alice);
            f.AssertServerViewers(f.Bob);
            f.AssertViewers(f.Bob);
            f.OrdinaryClose(f.Bob);
            f.CloseAndDeliver(f.Bob);
            f.AssertServerViewers();
            f.AssertViewers();
            return Task.CompletedTask;
        }

        [VsTest]
        public Task BlockEntityCloseBeforeOrdinaryCloseIsAlsoSafe()
        {
            using var f = new Fixture();
            f.Open(f.Alice);
            f.CloseAndDeliver(f.Alice);
            f.AssertServerViewers();
            f.AssertViewers();
            f.OrdinaryClose(f.Alice);
            Assert.Equal(0, f.Packets.Count, "late ordinary close sends no extra lid packet");
            f.AssertServerViewers();
            f.AssertViewers();
            return Task.CompletedTask;
        }

        sealed class Fixture : IDisposable
        {
            public readonly IServerPlayer Alice = Player(101, "lid-alice");
            public readonly IServerPlayer Bob = Player(202, "lid-bob");
            public readonly List<(BlockPos Pos, int Id, byte[] Data, IServerPlayer[] Except)> Packets = new();
            readonly BlockEntityGenericTypedContainer server, observer;
            readonly HashSet<IRenderer> renderers = new();
            readonly List<Action> unsubscribe = new();

            public Fixture()
            {
                var events = Proxy.Create<IClientEventAPI>((m, a) =>
                {
                    if (m.Name == "RegisterRenderer") renderers.Add((IRenderer)a[0]);
                    else if (m.Name == "UnregisterRenderer") renderers.Remove((IRenderer)a[0]);
                    else throw Unexpected(m);
                    return null;
                });
                var world = Proxy.Create<IClientWorldAccessor>((m, a) => throw Unexpected(m));
                var clientApi = Proxy.Create<ICoreClientAPI>((m, a) => m.Name switch
                {
                    "get_Side" => EnumAppSide.Client, "get_World" => world, "get_Event" => events,
                    _ => throw Unexpected(m)
                });
                var network = Proxy.Create<IServerNetworkAPI>((m, a) =>
                {
                    if (m.Name != "BroadcastBlockEntityPacket") throw Unexpected(m);
                    Packets.Add(((BlockPos)a[0], (int)a[1], (byte[])a[2], (IServerPlayer[])a[3]));
                    return null;
                });
                var serverApi = Proxy.Create<ICoreServerAPI>((m, a) => m.Name switch
                {
                    "get_Side" => EnumAppSide.Server, "get_Network" => network,
                    _ => throw Unexpected(m)
                });
                server = Container(serverApi);
                observer = Container(clientApi);
                observer.Behaviors.Add(new BEBehaviorAnimatable(observer)
                    { animUtil = new BlockEntityAnimationUtil(clientApi, observer) });
            }

            // Seed already-valid ordinary viewers, deliberately outside access/opening
            // flows. Their real manager invokes the same vanilla inventory callbacks.
            public void Open(IServerPlayer player)
            {
                player.InventoryManager.OpenInventory(server.Inventory);
                observer.OnReceivedServerPacket(5001,
                    SerializerUtil.Serialize(new OpenContainerLidPacket(player.Entity.EntityId, true)));
            }

            public void OrdinaryClose(IServerPlayer player) => player.InventoryManager.CloseInventory(server.Inventory);

            public void CloseAndDeliver(IServerPlayer player)
            {
                Assert.Equal(0, Packets.Count, "no undelivered packets before BE1001");
                server.OnReceivedClientPacket(player, 1001, null);
                Assert.Equal(1, Packets.Count, "BE1001 emits exactly one observer packet");
                var packet = Packets[0];
                Assert.Equal(server.Pos, packet.Pos, "broadcast targets the closed chest");
                Assert.Equal(5001, packet.Id, "observer lid packet ID");
                Assert.Equal(1, packet.Except.Length, "exactly the closing player is excluded");
                Assert.True(ReferenceEquals(player, packet.Except[0]), "broadcast excludes its sender");
                var payload = SerializerUtil.Deserialize<OpenContainerLidPacket>(packet.Data);
                Assert.Equal(player.Entity.EntityId, payload.EntityId, "close names the actor");
                Assert.False(payload.Opened, "close payload has Opened=false");
                observer.OnReceivedServerPacket(packet.Id, packet.Data);
                Packets.Clear();
            }

            public void AssertViewers(params IServerPlayer[] players)
            {
                Assert.True(observer.LidOpenEntityId.SetEquals(Array.ConvertAll(players, p => p.Entity.EntityId)),
                    "observer tracks exactly the remaining viewers");
                Assert.Equal(players.Length > 0, Animation.activeAnimationsByAnimCode.ContainsKey("lidopen"),
                    "observer animates an open lid exactly while a viewer remains");
            }

            public void AssertServerViewers(params IServerPlayer[] players)
            {
                Assert.True(server.LidOpenEntityId.SetEquals(Array.ConvertAll(players, p => p.Entity.EntityId)),
                    "server tracks exactly the remaining lid viewers");
                Assert.True(server.Inventory.openedByPlayerGUIds.SetEquals(Array.ConvertAll(players, p => p.PlayerUID)),
                    "server inventory tracks exactly the remaining viewers");
            }

            BlockEntityAnimationUtil Animation => observer.GetBehavior<BEBehaviorAnimatable>().animUtil;

            BlockEntityGenericTypedContainer Container(ICoreAPI api)
            {
                var be = new BlockEntityGenericTypedContainer { Api = api, Pos = new BlockPos(4, 5, 6, 0),
                    LidOpenEntityId = new(), type = "normal-generic", Block = new Block { Attributes = JsonObject.FromJson("{}") } };
                var inv = new InventoryGeneric(16, "chest-lidregression", null) { Api = api, AuditLogAccess = false };
                inv.InvNetworkUtil = new InventoryNetworkUtil(inv, api);
                Set(be, "inventory", inv);
                var opened = (OnInventoryOpenedDelegate)Delegate.CreateDelegate(typeof(OnInventoryOpenedDelegate), be, "OnInvOpened");
                var closed = (OnInventoryClosedDelegate)Delegate.CreateDelegate(typeof(OnInventoryClosedDelegate), be, "OnInvClosed");
                inv.OnInventoryOpened += opened;
                inv.OnInventoryClosed += closed;
                unsubscribe.Add(() => { inv.OnInventoryOpened -= opened; inv.OnInventoryClosed -= closed; });
                return be;
            }

            public void Dispose()
            {
                foreach (var remove in unsubscribe) remove();
                server.Inventory.openedByPlayerGUIds.Clear();
                server.LidOpenEntityId.Clear();
                observer.LidOpenEntityId.Clear();
                Animation.activeAnimationsByAnimCode.Clear();
                Animation.Dispose();
                foreach (var player in new[] { Alice, Bob })
                    ((ServerPlayerInventoryManager)player.InventoryManager).Inventories.Clear();
                Packets.Clear();
                Assert.Equal(0, renderers.Count, "fixture unregisters its animation utility");
                // No game globals, live players, world blocks or real renderer registrations changed.
            }
        }

        static IServerPlayer Player(long id, string uid)
        {
            var player = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
            var data = (ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));
            Set(data, "PlayerUID", uid);
            Set(data, "connected", true);
            Set(data, "Entityplayer", new EntityPlayer { EntityId = id });
            data.GameMode = EnumGameMode.Survival;
            Set(player, "worlddata", data);
            Set(player, "inventoryMgr", new ServerPlayerInventoryManager(
                new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>(), player, null));
            return player;
        }

        static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
        static Exception Unexpected(MethodInfo method) => new InvalidOperationException("Unexpected fixture access: " + method);

        public class Proxy : DispatchProxy
        {
            public System.Func<MethodInfo, object[], object> Handler;
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
            public static T Create<T>(System.Func<MethodInfo, object[], object> handler) where T : class
            {
                var proxy = DispatchProxy.Create<T, Proxy>();
                ((Proxy)(object)proxy).Handler = handler;
                return proxy;
            }
        }
    }
}
