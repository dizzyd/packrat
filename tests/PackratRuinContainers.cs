using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Issue #1: distinguish inventory containers in ruins from break-to-loot vessels.
    /// Stocked collapsed containers can be opened by vanilla in 1.21.6 and 1.22.7.
    /// retrieveOnly prevents insertion; it is not a break-before-opening flag.
    /// These tests preserve that behavior, without claiming to cover an unidentified
    /// historical/modded chest or making a rule about an entire ruin's location.
    /// </summary>
    public class PackratRuinContainers
    {
        static readonly string[] Families =
            { "game:chest-north", "game:trunk-north", "game:stationarybasket-north" };

        [VsTest]
        public async Task StockedRetrieveOnlyContainersMatchVanillaOpening()
        {
            int tested = 0;
            foreach (var code in Families)
            {
                var block = Sapi.World.GetBlock(new AssetLocation(code));
                Assert.NotNull(block, code);
                foreach (var type in block.Attributes["types"].AsArray<string>())
                {
                    if (!block.Attributes["retrieveOnly"][type].AsBool()) continue;
                    using (var fixture = new OpeningFixture(block, type))
                    {
                        fixture.Stock(Sapi.World.GetItem(new AssetLocation("game:stick")));
                        Assert.True(fixture.Container.retrieveOnly, code + ":" + type);
                        Assert.True(fixture.Container.Inventory.PutLocked, "insertion is restricted");
                        fixture.AssertSameOpening();
                        Assert.True(fixture.Container.Inventory.PutLocked, "opening did not allow insertion");
                    }
                    tested++;
                }
            }
            Assert.GreaterOrEqual(tested, 8, "collapsed chests, trunks and aged baskets exercised");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task OrdinaryStorageStillMatchesVanillaOpening()
        {
            foreach (var code in Families)
            {
                var block = Sapi.World.GetBlock(new AssetLocation(code));
                using (var fixture = new OpeningFixture(block, block.Attributes["defaultType"].AsString()))
                {
                    Assert.False(fixture.Container.retrieveOnly, "ordinary storage permits insertion");
                    fixture.AssertSameOpening();
                }
            }
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task LootVesselsHaveNoContainerInventory()
        {
            var vessel = Sapi.World.Blocks.First(b => b is BlockLootVessel);
            Assert.True(string.IsNullOrEmpty(vessel.EntityClass), "break-to-loot vessel has no block entity class");
            var pos = P(12, 1, 12);
            World.SetBlock(vessel.Code.ToString(), pos);
            await Ticks(4);
            Assert.True(Sapi.World.BlockAccessor.GetBlockEntity(pos) == null,
                "Packrat's BlockEntityContainer scan cannot select a loot vessel");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task StockedRetrieveOnlyContainersAreDiscovered()
        {
            var positions = await PlaceDiscoveryFixtures(stocked: true);
            var found = await Scan();
            foreach (var pos in positions) Assert.True(found.Contains(pos), "stocked ruin inventory remains eligible: " + pos);
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task EmptyRetrieveOnlyContainersAreSkippedButOrdinaryStorageIsKept()
        {
            var positions = await PlaceDiscoveryFixtures(stocked: false);
            var ordinary = P(12, 1, 10);
            World.SetBlock("game:chest-north", ordinary);
            await Ticks(10);
            var found = await Scan();
            foreach (var pos in positions) Assert.False(found.Contains(pos), "empty retrieve-only inventory is skipped: " + pos);
            Assert.True(found.Contains(ordinary), "an ordinary empty chest remains eligible");
        }

        static async Task<List<BlockPos>> PlaceDiscoveryFixtures(bool stocked)
        {
            // Leave room for the trunk's two-block footprint.
            var positions = new List<BlockPos> { P(10, 1, 11), P(12, 1, 14), P(14, 1, 11) };
            var types = new List<string>();
            for (int i = 0; i < Families.Length; i++)
            {
                var pos = positions[i];
                World.SetBlock(Families[i], pos);
                var be = World.BE<BlockEntityGenericTypedContainer>(pos);
                Assert.NotNull(be, Families[i]);
                string type = be.Block.Attributes["types"].AsArray<string>()
                    .First(t => be.Block.Attributes["retrieveOnly"][t].AsBool());
                types.Add(type);
                var placed = new ItemStack(be.Block);
                placed.Attributes.SetString("type", type);
                be.OnBlockPlaced(placed);
                if (stocked) be.Inventory[0].Itemstack = new ItemStack(Sapi.World.GetItem(new AssetLocation("game:stick")));
                be.MarkDirty(true);
            }
            await Player.Teleport(P(12, 1, 12));
            await Ticks(10);
            // A missing or unsynchronized client BE must not make a scan test pass.
            await OnClient();
            for (int i = 0; i < positions.Count; i++)
            {
                var be = Capi.World.BlockAccessor.GetBlockEntity(positions[i]) as BlockEntityGenericTypedContainer;
                Assert.NotNull(be, "client received the fixture: " + Families[i]);
                Assert.Equal(types[i], be.type, "client received the damaged subtype");
                Assert.True(be.retrieveOnly, "client fixture is retrieve-only");
                Assert.Equal(!stocked, be.Inventory.Empty, "client received the expected inventory state");
            }
            await OnServer();
            return positions;
        }

        static async Task<HashSet<BlockPos>> Scan()
        {
            await OnClient();
            var system = Capi.ModLoader.GetModSystem<PackratModSystem>();
            var scan = typeof(PackratModSystem).GetMethod("ScanAccessibleContainers", BindingFlags.Instance | BindingFlags.NonPublic);
            var containers = (List<BlockEntityContainer>)scan.Invoke(system, new object[] { Capi.World.Player });
            var found = new HashSet<BlockPos>(containers.Select(c => c.Pos));
            await OnServer();
            return found;
        }

        /// <summary>
        /// Controlled ordinary Survival interaction: real vanilla behavior, BE,
        /// inventory manager and Packrat request handler; fake world/network plumbing.
        /// No real player connection, screen, claim or reinforcement scenario is used.
        /// </summary>
        sealed class OpeningFixture : IDisposable
        {
            readonly FieldInfo serverApiField = typeof(PackratModSystem).GetField("_serverApi", BindingFlags.Static | BindingFlags.NonPublic);
            readonly object previousApi;
            readonly PackratModSystem system = new PackratModSystem();
            readonly ServerPlayer player;
            readonly ServerPlayerInventoryManager manager;
            readonly IServerWorldAccessor world;
            readonly List<int> packets = new List<int>();
            int opens;
            public readonly BlockEntityGenericTypedContainer Container;

            public OpeningFixture(Block block, string type)
            {
                previousApi = serverApiField.GetValue(null);
                var accessor = RuinFixtureProxy.Create<IBlockAccessor>((m, a) => m.Name == "GetBlockEntity" ? Container : RuinFixtureProxy.Default(m));
                world = RuinFixtureProxy.Create<IServerWorldAccessor>((m, a) => m.Name == "get_BlockAccessor" ? accessor : RuinFixtureProxy.Default(m));
                var channel = RuinFixtureProxy.Create<IServerNetworkChannel>((m, a) => RuinFixtureProxy.Default(m));
                var network = RuinFixtureProxy.Create<IServerNetworkAPI>((m, a) =>
                {
                    if (m.Name == "SendBlockEntityPacket") packets.Add((int)a[2]);
                    return m.Name == "GetChannel" ? channel : RuinFixtureProxy.Default(m);
                });
                var api = RuinFixtureProxy.Create<ICoreServerAPI>((m, a) => m.Name switch
                {
                    "get_Side" => EnumAppSide.Server, "get_World" => world, "get_Network" => network,
                    _ => RuinFixtureProxy.Default(m)
                });
                player = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
                var data = (ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));
                Set(data, typeof(ServerWorldPlayerData), "PlayerUID", "packrat-ruin-fixture");
                Set(data, typeof(ServerWorldPlayerData), "connected", true);
                Set(data, typeof(ServerWorldPlayerData), "Entityplayer", new EntityPlayer { EntityId = 42 });
                data.GameMode = EnumGameMode.Survival;
                Set(player, typeof(ServerPlayer), "worlddata", data);
                manager = new ServerPlayerInventoryManager(new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>(), player, null);
                Set(player, typeof(ServerPlayer), "inventoryMgr", manager);
                Container = new BlockEntityGenericTypedContainer
                {
                    Api = api, Block = block, Pos = new BlockPos(0, 0, 0), type = type,
                    LidOpenEntityId = new HashSet<long>()
                };
                // Use vanilla initialization, including its retrieve-only flag and
                // OnInvOpened/OnInvClosed callbacks, rather than copying those rules.
                typeof(BlockEntityGenericTypedContainer).GetMethod("InitInventory", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Container, new object[] { block });
                var inventory = Container.Inventory;
                inventory.InvNetworkUtil = new InventoryNetworkUtil(inventory, api);
                inventory.LateInitialize("chest-ruinfixture", api);
                inventory.AuditLogAccess = false;
                inventory.OnInventoryOpened += _ => opens++;
                var mod = new FixtureMod();
                typeof(Mod).GetProperty("Info").SetValue(mod, new ModInfo { ModID = PackratModSystem.ModId });
                typeof(ModSystem).GetProperty("Mod").SetValue(system, mod);
                serverApiField.SetValue(null, api);
            }

            public void Stock(Item item) => Container.Inventory[0].Itemstack = new ItemStack(item);

            public void AssertSameOpening()
            {
                var handling = EnumHandling.PassThrough;
                var selection = new BlockSelection(Container.Pos, BlockFacing.UP, Container.Block);
                bool opened = new BlockBehaviorContainer(Container.Block).OnBlockInteractStart(world, player, selection, ref handling);
                Assert.True(opened, "vanilla permits opening " + Container.Block.Code + ":" + Container.type);
                Assert.Equal(1, opens, "vanilla opens the real inventory");
                Assert.True(packets.SequenceEqual(new[] { 5000 }), "vanilla sends the inventory-open packet");
                manager.CloseInventory(Container.Inventory);
                packets.Clear();
                opens = 0;
                typeof(PackratModSystem).GetMethod("HandleOpenManyRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(system, new object[] { player, new OpenManyMessage { Positions = new List<BlockPos> { Container.Pos } } });
                Assert.Equal(1, opens, "Packrat opens the same real inventory");
                Assert.True(packets.SequenceEqual(new[] { 5000 }), "Packrat sends the same inventory-open packet");
            }

            public void Dispose()
            {
                serverApiField.SetValue(null, previousApi);
                manager.CloseInventory(Container.Inventory);
            }

            static void Set(object target, Type type, string name, object value) =>
                type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
            sealed class FixtureMod : Mod { }
        }
    }

    // DispatchProxy must derive from a public, non-sealed type.
    public class RuinFixtureProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object[], object> Handler;
        protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        public static T Create<T>(System.Func<MethodInfo, object[], object> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, RuinFixtureProxy>();
            ((RuinFixtureProxy)(object)proxy).Handler = handler;
            return proxy;
        }
        public static object Default(MethodInfo method) => method.ReturnType == typeof(void) || !method.ReturnType.IsValueType
            ? null : Activator.CreateInstance(method.ReturnType);
    }
}
