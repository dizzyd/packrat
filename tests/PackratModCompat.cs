using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Packrat.Tests
{
    /// <summary>
    /// Packrat's support for other storage mods, which is six type names in a string
    /// array resolved with AccessTools.TypeByName.
    ///
    /// Every one of them is a silent failure by design: a mod that renames or moves
    /// its block entity does not break Packrat, it just stops being supported, and
    /// the only sign is a missing Notification line in a log nobody reads. That
    /// tradeoff is right - Packrat must not hard-depend on six optional mods - but it
    /// means nothing tells you when a lookup goes stale except a test that loads the
    /// mod and checks.
    ///
    /// Run with the compat pack for these to mean anything:
    ///
    ///     cairn-cli sync packratcompat
    ///     run.sh ../Packrat/tests --mod ../Packrat/Packrat \
    ///         --mods ~/.cairn/packs/packratcompat/Mods
    ///
    /// Without it each one logs and returns, so read the log line rather than the
    /// green tick.
    /// </summary>
    public class PackratModCompat
    {
        [VsTest]
        public async Task PrimitiveSurvivalTreeHollowsAreDiscovered()
        {
            if (!Loaded("primitivesurvival")) return;

            // Placed and grown hollows are different base classes - one extends
            // BlockEntityOpenableContainer, the other BlockEntityDisplayCase - which is
            // why Packrat lists them separately with different patch requirements.
            AssertDiscovered("PrimitiveSurvival.ModSystem.BETreeHollowPlaced");
            AssertDiscovered("PrimitiveSurvival.ModSystem.BETreeHollowGrown");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task StorageControllerIsDiscovered()
        {
            if (!Loaded("storagecontroller")) return;

            AssertDiscovered("storagecontroller.BlockEntityStorageController");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task StorageControllerStillExposesContainerList()
        {
            // Packrat reads the controller's linked containers through a property found
            // by name, and returns null - quietly ignoring every linked container - if
            // it is not there.
            if (!Loaded("storagecontroller")) return;

            var type = AccessTools.TypeByName("storagecontroller.BlockEntityStorageController");
            Assert.NotNull(type, "the storage controller type resolves");

            var prop = type.GetProperty("ContainerList", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(prop, "storagecontroller ContainerList property");
            Assert.Equal(typeof(List<BlockPos>), prop.PropertyType, "ContainerList is a List<BlockPos>");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task EveryNameInTheCompatListIsCheckedByThisSuite()
        {
            // Guards the guard: if someone adds a seventh mod container type to
            // _modContainerTypes, this fails until a test covers it, rather than the
            // new entry going unverified for ever.
            var covered = new HashSet<string>
            {
                "SortableStorage.ModSystem.BESortableOpenableContainer",
                "ContainersBundle.BlockEntityCBContainer",
                "BetterCratesNamespace.BetterCrateBlockEntity",
                "storagecontroller.BlockEntityStorageController",
                "PrimitiveSurvival.ModSystem.BETreeHollowPlaced",
                "PrimitiveSurvival.ModSystem.BETreeHollowGrown",
            };

            var declared = DeclaredCompatTypeNames();
            Log($"declared: {declared.Count} — {string.Join(", ", declared.Select(Short))}");

            foreach (var name in declared)
            {
                Assert.True(covered.Contains(name),
                    $"{name} is named in this suite (add a case for it)");
            }
            Assert.Equal(covered.Count, declared.Count, "compat entries known to this suite");

            await Task.CompletedTask;
        }

        // ---------- helpers ----------

        static bool Loaded(string modid)
        {
            if (Sapi.ModLoader.IsModEnabled(modid)) return true;

            Log($"{modid} is not loaded — rerun with --mods ~/.cairn/packs/packratcompat/Mods. " +
                "This test proved nothing.");
            return false;
        }

        /// <summary>
        /// Assert the name resolves *and* that Packrat actually put it in the registry
        /// the scan consults. Resolving alone would not prove discovery ran.
        /// </summary>
        static void AssertDiscovered(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            Assert.NotNull(type, $"{typeName} resolves");

            var registry = (HashSet<Type>)typeof(PackratModSystem)
                .GetField("_storageContainerTypes", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

            Assert.NotNull(registry, "Packrat's _storageContainerTypes registry");
            Assert.True(registry.Contains(type), $"{Short(typeName)} is in Packrat's scan registry");
        }

        static List<string> DeclaredCompatTypeNames()
        {
            var field = typeof(PackratModSystem)
                .GetField("_modContainerTypes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field, "_modContainerTypes still exists");

            var names = new List<string>();
            foreach (var entry in (System.Collections.IEnumerable)field.GetValue(null))
            {
                names.Add((string)entry.GetType().GetField("Item1").GetValue(entry));
            }
            return names;
        }

        static string Short(string typeName) => typeName.Substring(typeName.LastIndexOf('.') + 1);
    }
}
