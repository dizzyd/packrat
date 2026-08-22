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
    /// Which containers the hotkey actually opens.
    ///
    /// ScanAccessibleContainers takes an IClientPlayer, so all of this needs a real
    /// client - there is no headless version of the question. It is also the piece
    /// with the most behaviour per line: room bounds, line of sight, reinforcement,
    /// and the rule that a container sealed in a room belongs to that room.
    /// </summary>
    public class PackratDiscovery
    {
        const string Wall  = "game:rock-granite";
        const string Chest = "game:chest-north";

        // Sealed shell with a 3x2x3 interior, clear of the plot edge.
        static BlockPos ShellMin => P(1, 0, 1);
        static BlockPos ShellMax => P(5, 3, 5);
        static BlockPos InsideChest  => P(2, 1, 2);
        static BlockPos InsideStand  => P(4, 1, 4);
        static BlockPos OutsideChest => P(10, 1, 10);
        static BlockPos OutsideStand => P(12, 1, 12);

        // A big sealed room with a sealed closet in the far corner of it.
        static BlockPos BigMin      => P(1, 0, 1);
        static BlockPos BigMax      => P(11, 4, 11);
        static BlockPos ClosetMin   => P(7, 0, 7);
        static BlockPos ClosetMax   => P(9, 2, 9);
        static BlockPos ClosetChest => P(8, 1, 8);
        static BlockPos RoomChest   => P(3, 1, 3);
        static BlockPos BigRoomStand => P(4, 1, 5);

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task StandingInARoomFindsTheContainersInIt()
        {
            await BuildSealedRoom();
            World.SetBlock(Chest, InsideChest);
            await Ticks(10);

            await Player.Teleport(InsideStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found));
            Assert.True(found.Contains(InsideChest), "the chest in the room the player is standing in");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AContainerSealedInAClosetIsLeftAlone()
        {
            // The 1.2.0 rule, tested where nothing else can account for the result.
            //
            // A big sealed room with a sealed closet inside it. The player stands in
            // the big room, so strictCheck is false - which means the 5.1 block cutoff
            // and the line of sight check are both skipped, and room.Location is only a
            // bounding box, so the closet's chest sits squarely inside the area being
            // walked. The only thing that can exclude it is Room.Contains consulting
            // the closet's own mask and finding the player is not in it.
            //
            // Standing outside a sealed shell would have proved nothing here: at that
            // distance the range cutoff excludes the chest on its own.
            await BuildBigRoom();
            await BuildCloset();

            World.SetBlock(Chest, ClosetChest);   // sealed away in the closet
            World.SetBlock(Chest, RoomChest);     // loose in the big room
            await Ticks(10);

            var closet = Sapi.ModLoader.GetModSystem<RoomRegistry>().GetRoomForPosition(ClosetChest);
            Assert.NotNull(closet, "the closet registers as a room");
            Assert.Equal(0, closet.ExitCount, "the closet is sealed");
            Assert.False(closet.Contains(BigRoomStand), "the player is not inside the closet");

            await Player.Teleport(BigRoomStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (roomChest={Show(RoomChest)} closetChest={Show(ClosetChest)})");

            Assert.True(found.Contains(RoomChest), "the chest loose in the room the player is in");
            Assert.False(found.Contains(ClosetChest), "the chest sealed in a closet the player is not in");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task OutInTheOpenOnlyNearbyContainersAreFound()
        {
            // No room, so the scan falls back to range plus line of sight. The far
            // chest is well beyond the 5.1 block cutoff.
            var near = P(12, 1, 10);
            var far = P(12, 1, 2);

            World.SetBlock(Chest, near);
            World.SetBlock(Chest, far);
            await Ticks(10);

            await Player.Teleport(OutsideStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (near={Show(near)} far={Show(far)})");

            Assert.True(found.Contains(near), "a chest two blocks away");
            Assert.False(found.Contains(far), "a chest ten blocks away");
            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task NonStorageBlockEntitiesAreNeverOpened()
        {
            // The registry deliberately excludes BlockEntityOpenableContainer as a
            // whole because it would drag in firepits and querns. A regression here
            // would be very visible - the browser filling with cooking equipment.
            World.SetBlock("game:firepit-construct1", P(11, 1, 12));
            World.SetBlock(Chest, P(13, 1, 12));
            await Ticks(10);

            await Player.Teleport(OutsideStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found));

            Assert.True(found.Contains(P(13, 1, 12)), "the chest is found");
            Assert.False(found.Contains(P(11, 1, 12)), "the firepit is not");
            await Task.CompletedTask;
        }

        // ---------- helpers ----------

        /// <summary>
        /// Call the mod's own scan, on the client thread, with the client's player.
        /// Private because nothing outside Packrat calls it - reflection here keeps the
        /// test honest rather than re-implementing the rules and proving nothing.
        /// </summary>
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

        static async Task BuildSealedRoom()
        {
            var min = ShellMin;
            var max = ShellMax;

            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        bool shell = x == min.X || x == max.X ||
                                     y == min.Y || y == max.Y ||
                                     z == min.Z || z == max.Z;
                        World.SetBlock(shell ? Wall : "game:air", new BlockPos(x, y, z, 0));
                    }

            await Ticks(10);
        }

        static Task BuildBigRoom() => Shell(BigMin, BigMax, hollow: true);
        static Task BuildCloset() => Shell(ClosetMin, ClosetMax, hollow: true);

        /// <summary>Solid on every face of the box, air inside.</summary>
        static async Task Shell(BlockPos min, BlockPos max, bool hollow)
        {
            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        bool shell = x == min.X || x == max.X ||
                                     y == min.Y || y == max.Y ||
                                     z == min.Z || z == max.Z;
                        if (!shell && !hollow) continue;
                        World.SetBlock(shell ? Wall : "game:air", new BlockPos(x, y, z, 0));
                    }

            await Ticks(10);
        }

        static string Describe(HashSet<BlockPos> found) =>
            $"scan found {found.Count}: " + string.Join(", ", found.Select(Show));

        static string Show(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
    }
}
