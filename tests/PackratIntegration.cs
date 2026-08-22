using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// The seams where Packrat reaches into the game by name.
    ///
    /// Nearly all of this mod's risk lives here rather than in its own logic. It
    /// patches an overload of InventoryBase.GetBestSuitedSlot selected by argument
    /// types, intercepts OnReceivedServerPacket found by name and signature, calls
    /// OnPlayerRightClick reflectively, and resolves six mod container types from
    /// string type names. Every one of those compiles perfectly when the target has
    /// moved or been renamed, and then quietly does nothing.
    ///
    /// A green build says none of that was checked. These tests are the check.
    /// </summary>
    public class PackratIntegration
    {
        [VsTest]
        public async Task ModIsLoaded()
        {
            Assert.True(Sapi.ModLoader.IsModEnabled("packrat"), "packrat mod enabled");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheGetBestSuitedSlotOverloadStillExists()
        {
            // Selected by exact argument types. An added or reordered parameter in a
            // game update leaves the attribute matching nothing at all.
            Assert.NotNull(GetBestSuitedSlot(), "InventoryBase.GetBestSuitedSlot(ItemSlot, ItemStackMoveOperation, List<ItemSlot>)");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AllFourGetBestSuitedSlotPatchesAreAttached()
        {
            // One prefix that redirects container-to-container moves, and three
            // postfixes: crate handling, perish rate, and container preference.
            var info = Harmony.GetPatchInfo(GetBestSuitedSlot());
            Assert.NotNull(info, "GetBestSuitedSlot is patched at all");

            var ours = Ours(info.Prefixes).Concat(Ours(info.Postfixes))
                .Select(p => p.PatchMethod.Name).OrderBy(n => n).ToList();

            Log("attached: " + string.Join(", ", ours));

            foreach (var expected in new[]
            {
                "GetBestSuitedSlot_BlockContainerToContainer",
                "GetBestSuitedSlot_CrateHandling",
                "GetBestSuitedSlot_PerishRateHandling",
                "GetBestSuitedSlot_ContainerPreference",
            })
            {
                Assert.Contains(string.Join(",", ours), expected, $"{expected} is attached");
            }

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheOnReceivedServerPacketTargetStillExists()
        {
            // Packrat intercepts this to swallow the "open dialog" packet for
            // containers it opened itself. Found by name plus an (int, byte[])
            // signature, on the base class where vanilla defines it.
            var target = typeof(BlockEntityOpenableContainer).GetMethod(
                "OnReceivedServerPacket",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int), typeof(byte[]) }, null);

            Assert.NotNull(target, "BlockEntityOpenableContainer.OnReceivedServerPacket(int, byte[])");

            var info = Harmony.GetPatchInfo(target);
            Assert.NotNull(info, "...and Packrat patched it");
            Assert.GreaterOrEqual(Ours(info.Prefixes).Count, 1, "Packrat prefixes on OnReceivedServerPacket");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ContainersStillExposeOnPlayerRightClick()
        {
            // TryInvokeOnPlayerRightClick looks this up on the instance type for mod
            // containers that do not extend BlockEntityOpenableContainer. If it is
            // missing the mod logs a warning and the container silently never opens.
            var method = typeof(BlockEntityOpenableContainer).GetMethod(
                "OnPlayerRightClick",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(IPlayer), typeof(Vintagestory.API.Common.BlockSelection) }, null);

            Assert.NotNull(method, "OnPlayerRightClick(IPlayer, BlockSelection)");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ChestsAndCratesAreStillTheTypesTheRegistryScansFor()
        {
            // The registry is seeded with BlockEntityCrate and
            // BlockEntityGenericTypedContainer, and IsStorageContainer walks the base
            // chain looking for them. Those two names are compile-time safe; what is
            // not is the assumption that a chest and a crate still *are* them. If
            // vanilla re-parents either, the scan stops seeing that container with no
            // error anywhere.
            World.SetBlock("game:chest-north", P(4, 1, 4));
            World.SetBlock("game:crate", P(6, 1, 4));
            await Ticks(4);

            Assert.NotNull(World.BE<BlockEntityGenericTypedContainer>(P(4, 1, 4)),
                "a chest is a BlockEntityGenericTypedContainer");
            Assert.NotNull(World.BE<BlockEntityCrate>(P(6, 1, 4)),
                "a crate is a BlockEntityCrate");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheRoomRegistryIsAvailable()
        {
            // Container discovery is entirely built on this: no RoomRegistry means the
            // in-room branch never runs and every scan silently degrades to the
            // line-of-sight fallback.
            Assert.NotNull(Sapi.ModLoader.GetModSystem<RoomRegistry>(), "RoomRegistry mod system");
            await Task.CompletedTask;
        }

        [VsTest, RequiresClient]
        public async Task PatchesAreRegisteredExactlyOnce()
        {
            // ModSystem.Start() runs once per side and in singleplayer both sides
            // resolve the same assembly, so a bare PatchAll() there registers every
            // patch twice. Packrat guards it with Harmony.HasAnyPatches; this pins
            // that guard.
            //
            // Headless there is only one side, so the count is 1 either way - this is
            // [RequiresClient] to be skipped rather than to pass without meaning.
            var info = Harmony.GetPatchInfo(GetBestSuitedSlot());
            var byName = Ours(info.Prefixes).Concat(Ours(info.Postfixes))
                .GroupBy(p => p.PatchMethod.Name);

            foreach (var group in byName)
            {
                Assert.Equal(1, group.Count(), $"registrations of {group.Key}");
            }

            await Task.CompletedTask;
        }

        // ---------- helpers ----------

        static MethodInfo GetBestSuitedSlot() =>
            typeof(InventoryBase).GetMethod(
                nameof(InventoryBase.GetBestSuitedSlot),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(ItemSlot), typeof(ItemStackMoveOperation), typeof(List<ItemSlot>) },
                null);

        static List<Patch> Ours(IEnumerable<Patch> patches) =>
            patches.Where(p => p.owner == PackratModSystem.ModId).ToList();
    }
}
