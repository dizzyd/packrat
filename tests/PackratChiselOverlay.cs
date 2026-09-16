using System;
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
    /// Containers carrying a Chisel it Everywhere overlay.
    ///
    /// That mod never replaces the host block - a chest keeps its block and its block
    /// entity - so nothing in Packrat's type registry is involved and the container is
    /// found by the scan exactly as before. What it does do, since 0.7.0, is make the
    /// host position *seal*: it postfixes every Block.GetRetention implementation and
    /// returns a non-zero heat retention for any position carrying an overlay
    /// (ComputeRetentionHeat returns 1 or -1 and never 0).
    ///
    /// RoomRegistry's flood fill treats a non-zero retention as a wall, so an overlaid
    /// chest stops being part of the room it stands in and instead resolves to a "room"
    /// that is nothing but the chest's own block - enclosed, with the player not in it.
    /// Packrat's rule that a container sealed in a room belongs to that room then threw
    /// the chest away, and the browser skipped it.
    ///
    /// Run with the mod loaded for any of this to mean anything:
    ///
    ///     cairn-cli sync packratcompat
    ///     run.sh ../Packrat/tests --mod ../Packrat/Packrat \
    ///         --mods ~/.cairn/packs/packratcompat/Mods --client
    ///
    /// Without it each test logs and returns.
    /// </summary>
    public class PackratChiselOverlay
    {
        const string Wall  = "game:rock-granite";
        const string Chest = "game:chest-north";

        const string ModId      = "chiseleverywhere";
        const string SystemType = "ChiselEverywhere.ChiselEverywhereModSystem";
        const string OverlayType = "ChiselEverywhere.ChiselOverlay";

        // Sealed shell with a 4x3x4 interior.
        static BlockPos ShellMin => P(1, 0, 1);
        static BlockPos ShellMax => P(6, 4, 6);
        static BlockPos InsideChest => P(2, 1, 2);
        static BlockPos InsideStand => P(5, 1, 5);
        static BlockPos WallChest   => P(1, 1, 3);   // a hole in the shell, filled by the chest itself

        // A big sealed room with a sealed closet in the corner of it.
        static BlockPos BigMin       => P(1, 0, 1);
        static BlockPos BigMax       => P(11, 4, 11);
        static BlockPos ClosetMin    => P(7, 0, 7);
        static BlockPos ClosetMax    => P(9, 2, 9);
        static BlockPos ClosetChest  => P(8, 1, 8);
        static BlockPos BigRoomStand => P(4, 1, 5);

        static BlockPos OutsideChest => P(12, 1, 10);
        static BlockPos OutsideStand => P(12, 1, 12);

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AnOverlaidChestInTheRoomIsStillFound()
        {
            if (!Loaded()) return;

            await BuildShell(ShellMin, ShellMax, gap: null);
            World.SetBlock(Chest, InsideChest);
            await Ticks(10);

            Carve(InsideChest);
            await Ticks(10);

            AssertSeals(InsideChest);

            await Player.Teleport(InsideStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (chest={Show(InsideChest)})");
            Assert.True(found.Contains(InsideChest),
                "a chest in the room the player is standing in, carrying a chisel overlay");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AnOverlaidChestSetIntoTheRoomWallIsStillFound()
        {
            if (!Loaded()) return;

            // The overlay's own headline feature: a chest set into the wall leaks the
            // room open, and carving over it seals it again. That puts the chest in the
            // shell rather than the interior, and RoomRegistry's Location is the
            // bounding box of the *interior* only - so the chest sits one block outside
            // the box the in-room branch of the scan walks.
            await BuildShell(ShellMin, ShellMax, gap: WallChest);
            World.SetBlock(Chest, WallChest);
            await Ticks(10);

            Carve(WallChest);
            await Ticks(10);

            AssertSeals(WallChest);

            await Player.Teleport(InsideStand);
            await Ticks(10);

            var room = Room(InsideStand);
            Assert.NotNull(room, "the player's room");
            Assert.Equal(0, room.ExitCount, "the overlay seals the room the chest is set into");
            Assert.False(room.Contains(WallChest), "the overlaid chest is not part of the room volume");

            var found = await Scan();
            Log(Describe(found) + $"  (wallChest={Show(WallChest)} room={room.Location})");
            Assert.True(found.Contains(WallChest), "a chest set into the wall of the room the player is in");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AnOverlaidChestOutInTheOpenIsStillFound()
        {
            if (!Loaded()) return;

            World.SetBlock(Chest, OutsideChest);
            await Ticks(10);

            Carve(OutsideChest);
            await Ticks(10);

            AssertSeals(OutsideChest);

            await Player.Teleport(OutsideStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (chest={Show(OutsideChest)})");
            Assert.True(found.Contains(OutsideChest), "a chest two blocks away carrying a chisel overlay");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AnOverlaidChestSealedInAClosetIsStillLeftAlone()
        {
            if (!Loaded()) return;

            // The exemption must be for a container that seals *itself*, not a blanket
            // pass on the room rule. A chest shut in a closet is still the closet's,
            // overlay or no overlay.
            await BuildShell(BigMin, BigMax, gap: null);
            await BuildShell(ClosetMin, ClosetMax, gap: null);

            World.SetBlock(Chest, ClosetChest);
            await Ticks(10);

            Carve(ClosetChest);
            await Ticks(10);

            AssertSeals(ClosetChest);

            await Player.Teleport(BigRoomStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (closetChest={Show(ClosetChest)})");
            Assert.False(found.Contains(ClosetChest),
                "an overlaid chest sealed in a closet the player is not in");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AChestBrickedIntoSolidRockIsStillLeftAlone()
        {
            if (!Loaded()) return;

            // The same self-sealing shape the overlay produces, reached without it: a
            // chest encased in stone resolves to a room that is only itself. It has to
            // stay shut, which is what keeps the new exemption honest - it turns on the
            // space next to the container, not on the degenerate room alone.
            await BuildShell(BigMin, BigMax, gap: null);

            var encased = P(3, 1, 3);
            World.SetBlock(Chest, encased);
            foreach (var facing in BlockFacing.ALLFACES)
                World.SetBlock(Wall, encased.AddCopy(facing));
            await Ticks(10);

            await Player.Teleport(BigRoomStand);
            await Ticks(10);

            var found = await Scan();
            Log(Describe(found) + $"  (encased={Show(encased)})");
            Assert.False(found.Contains(encased), "a chest bricked into solid rock");
        }

        // ---------- helpers ----------

        static bool Loaded()
        {
            if (Sapi.ModLoader.IsModEnabled(ModId)) return true;

            Log($"{ModId} is not loaded - rerun with --mods ~/.cairn/packs/packratcompat/Mods. " +
                "This test proved nothing.");
            return false;
        }

        /// <summary>
        /// Put an overlay on a block the way the mod itself does, through its own public
        /// SetOverlay. Going through the mod rather than writing the chunk moddata by
        /// hand is the point: if SetOverlay moves or its shape changes, this fails rather
        /// than quietly testing a format nothing reads any more.
        /// </summary>
        static void Carve(BlockPos pos)
        {
            var system = Sapi.ModLoader.GetModSystem(SystemType);
            Assert.NotNull(system, $"{SystemType} is loaded");

            var overlayType = system.GetType().Assembly.GetType(OverlayType);
            Assert.NotNull(overlayType, $"{OverlayType} still exists");

            var overlay = Activator.CreateInstance(overlayType);
            // A 2-voxel slab across the whole block, in the material of the wall.
            overlayType.GetField("Cuboids").SetValue(overlay,
                new[] { BlockEntityMicroBlock.ToUint(0, 0, 0, 16, 2, 16, 0) });
            overlayType.GetField("MaterialCodes").SetValue(overlay, new[] { Wall });

            var setOverlay = system.GetType().GetMethod("SetOverlay",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(setOverlay, "ChiselEverywhereModSystem.SetOverlay still exists");
            setOverlay.Invoke(system, new object[] { pos, overlay });

            // SetOverlay invalidates the *server's* room cache only. The scan under test
            // runs on the client against the client's RoomRegistry, so clear that one too
            // rather than waiting on whatever happens to mark the chunk dirty next.
            InvalidateRooms();
        }

        /// <summary>
        /// The mechanism this whole suite rests on. If the mod stops sealing, every test
        /// here would pass for the wrong reason.
        /// </summary>
        static void AssertSeals(BlockPos pos)
        {
            var block = Sapi.World.BlockAccessor.GetBlock(pos);
            var retention = block.GetRetention(pos, BlockFacing.UP, EnumRetentionType.Heat);
            Assert.NotEqual(0, retention, $"the overlay makes {Show(pos)} retain heat (seals)");
        }

        static Room Room(BlockPos pos) =>
            Capi.ModLoader.GetModSystem<RoomRegistry>().GetRoomForPosition(pos);

        static void InvalidateRooms()
        {
            foreach (var api in new ICoreAPI[] { Sapi, Capi })
            {
                var registry = api?.ModLoader.GetModSystem<RoomRegistry>();
                if (registry == null) continue;

                var field = typeof(RoomRegistry).GetField("roomsByChunkIndex",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field, "RoomRegistry.roomsByChunkIndex still exists");
                ((System.Collections.IDictionary)field.GetValue(registry)).Clear();
            }
        }

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

        /// <summary>Solid on every face of the box, air inside, optionally one block left empty.</summary>
        static async Task BuildShell(BlockPos min, BlockPos max, BlockPos gap)
        {
            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        var pos = new BlockPos(x, y, z, 0);
                        if (gap != null && pos.Equals(gap)) continue;

                        bool shell = x == min.X || x == max.X ||
                                     y == min.Y || y == max.Y ||
                                     z == min.Z || z == max.Z;
                        World.SetBlock(shell ? Wall : "game:air", pos);
                    }

            await Ticks(10);
        }

        static string Describe(HashSet<BlockPos> found) =>
            $"scan found {found.Count}: " + string.Join(", ", found.Select(Show));

        static string Show(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
    }
}
