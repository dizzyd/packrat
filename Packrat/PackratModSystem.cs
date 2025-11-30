using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace Packrat;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OpenManyMessage
{
    [ProtoMember(1)]
    public List<BlockPos> Positions { get; set; }

    public static OpenManyMessage FromContainers(List<BlockEntityContainer> containers) =>
        new() { Positions = containers.Select(c => c.Pos).ToList() };
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OpenManyConfirmMessage
{
    [ProtoMember(1)]
    public int CrateCount { get; set; }
}

[HarmonyPatch]
public class PackratModSystem : ModSystem
{
    private Harmony _harmony;
    private static ICoreAPI _api;
    private static ICoreClientAPI _clientApi;
    private static ICoreServerAPI _serverApi;

    private RoomRegistry _roomSystem;
    private ModSystemBlockReinforcement _reinforcementSystem;

    // Browse mode state (client-side)
    private static bool _browseMode;
    private static HashSet<BlockPos> _pendingPositions = new();
    private static List<BlockEntityContainer> _openedContainers = new();
    private static GuiDialogStorageBrowser _browserDialog;
    private static int _pendingCrateConfirmation; // Number of crates waiting for server confirmation

    // Debug logging (toggle with .packratdebug command)
    private static bool _debugLogging;

    // Reflection cache for typed container info
    private static readonly Dictionary<Type, (FieldInfo title, FieldInfo columns)?> _typedContainerCache = new();


    public static string ModId => "packrat";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        _api = api;

        if (!Harmony.HasAnyPatches(Mod.Info.ModID))
        {
            _harmony = new Harmony(Mod.Info.ModID);
            _harmony.PatchAll();

            // Conditionally patch SortableStorage if it's loaded
            PatchSortableStorageIfLoaded();
        }

        // Register network channel and also register our messages
        api.Network
            .RegisterChannel(Mod.Info.ModID)
            .RegisterMessageType(typeof(OpenManyMessage))
            .RegisterMessageType(typeof(OpenManyConfirmMessage));
    }

    /// <summary>
    /// Conditionally patches SortableStorage's container class if the mod is loaded
    /// </summary>
    private void PatchSortableStorageIfLoaded()
    {
        // Try to find SortableStorage's base container class
        var sortableType = AccessTools.TypeByName("SortableStorage.ModSystem.BESortableOpenableContainer");
        if (sortableType == null) return;

        // Find the OnReceivedServerPacket method
        var targetMethod = sortableType.GetMethod("OnReceivedServerPacket",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(int), typeof(byte[]) },
            null);

        if (targetMethod == null) return;

        // Get our prefix method
        var prefixMethod = typeof(PackratModSystem).GetMethod(nameof(OnReceivedServerPacket_Generic),
            BindingFlags.Public | BindingFlags.Static);

        if (prefixMethod == null) return;

        _harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefixMethod));
        _api.Logger.Notification("PackRat: SortableStorage detected, patched for compatibility");
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        _clientApi = api;

        _roomSystem = api.ModLoader.GetModSystem<RoomRegistry>();
        _reinforcementSystem = api.ModLoader.GetModSystem<ModSystemBlockReinforcement>();

        var hotkey = Mod.Info.ModID + ".openall";
        api.Input.RegisterHotKey(
            hotkey,
            Lang.Get($"{Mod.Info.ModID}:openall"),
            GlKeys.R,
            HotkeyType.CharacterControls);
        api.Input.SetHotKeyHandler(hotkey, OpenAll);

        // Handle server confirmation for crate inventories
        api.Network
            .GetChannel(Mod.Info.ModID)
            .SetMessageHandler<OpenManyConfirmMessage>(HandleOpenManyConfirm);

        // Register debug toggle command
        api.ChatCommands.Create("packratdebug")
            .WithDescription("Toggle PackRat debug logging")
            .HandleWith(_ =>
            {
                _debugLogging = !_debugLogging;
                return TextCommandResult.Success($"PackRat debug logging: {(_debugLogging ? "ON" : "OFF")}");
            });
    }


    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        _serverApi = api;

        api.Network
            .GetChannel(Mod.Info.ModID)
            .SetMessageHandler<OpenManyMessage>(HandleOpenManyRequest);
    }

    private void HandleOpenManyRequest(IServerPlayer sender, OpenManyMessage msg)
    {
        int crateCount = 0;

        foreach (var pos in msg.Positions)
        {
            var be = _serverApi.World.BlockAccessor.GetBlockEntity(pos);
            if (be is not BlockEntityContainer container) continue;

            if (IsCrate(container))
            {
                // Crates - just open the inventory on the server, client accesses directly
                sender.InventoryManager.OpenInventory(container.Inventory);
                crateCount++;
            }
            // Check if this container has typed container properties (dialogTitleLangCode, quantityColumns)
            else if (TryGetTypedContainerInfo(be, out var titleLangCode, out var columns))
            {
                // Send inventory packet - works for vanilla, SortableStorage, ContainersBundle, etc.
                var data = BlockEntityContainerOpen.ToBytes(
                    "BlockEntityInventory",
                    Lang.Get(titleLangCode),
                    (byte)columns,
                    container.Inventory
                );
                _serverApi.Network.SendBlockEntityPacket(sender, pos, (int)EnumBlockContainerPacketId.OpenInventory, data);
                sender.InventoryManager.OpenInventory(container.Inventory);
            }
            // Fall back to OnPlayerRightClick if available
            else if (be is BlockEntityOpenableContainer openable)
            {
                openable.OnPlayerRightClick(sender, new BlockSelection(pos, BlockFacing.UP, openable.Block));
            }
        }

        // Send confirmation back to client that all crate inventories are now open
        if (crateCount > 0)
        {
            _serverApi.Network.GetChannel(Mod.Info.ModID).SendPacket(
                new OpenManyConfirmMessage { CrateCount = crateCount },
                sender
            );
        }
    }

    /// <summary>
    /// Check if a container is a crate (by inventory ID prefix)
    /// </summary>
    private static bool IsCrate(BlockEntityContainer container) =>
        container.Inventory?.InventoryID?.StartsWith("crate-") == true;

    /// <summary>
    /// Reset browse mode state
    /// </summary>
    private static void ResetBrowseMode()
    {
        _browseMode = false;
        _pendingPositions.Clear();
        _pendingCrateConfirmation = 0;
    }

    /// <summary>
    /// Attempts to get typed container info (dialogTitleLangCode, quantityColumns) via reflection.
    /// Works for vanilla BlockEntityGenericTypedContainer, SortableStorage, ContainersBundle, etc.
    /// </summary>
    private static bool TryGetTypedContainerInfo(BlockEntity be, out string titleLangCode, out int columns)
    {
        titleLangCode = null;
        columns = 4;

        var type = be.GetType();

        // Check cache first
        if (!_typedContainerCache.TryGetValue(type, out var cached))
        {
            // Look for dialogTitleLangCode and quantityColumns fields
            var titleField = type.GetField("dialogTitleLangCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var columnsField = type.GetField("quantityColumns", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            cached = (titleField != null && columnsField != null)
                ? (titleField, columnsField)
                : null;
            _typedContainerCache[type] = cached;
        }

        if (cached == null) return false;

        titleLangCode = cached.Value.title.GetValue(be) as string;
        if (string.IsNullOrEmpty(titleLangCode)) return false;

        var columnsValue = cached.Value.columns.GetValue(be);
        if (columnsValue is int c)
            columns = c;

        return true;
    }

    private bool HasLineOfSightTo(IPlayer player, Vec3d targetPoint)
    {
        Vec3d playerEyePos = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos);

        // Define a more sophisticated block filter for line of sight
        BlockFilter blockFilter = (pos, block) =>
        {
            // Blocks that are air don't need to be considered
            if (block == null || block.Id == 0)
                return false;

            // Target position is always visible
            if (pos.X == (int)targetPoint.X && pos.Y == (int)targetPoint.Y && pos.Z == (int)targetPoint.Z)
                return false;

            // Other containers don't block
            if (block is BlockContainer or BlockCrate)
                return false;

            // Allow seeing through transparent blocks
            if (block.RenderPass == EnumChunkRenderPass.Transparent ||
                block.RenderPass == EnumChunkRenderPass.BlendNoCull ||
                block.Replaceable >= 6000)
            {
                return false;
            }

            // If no collision boxes, allow seeing through
            if (block.CollisionBoxes == null || block.CollisionBoxes.Length == 0)
                return false;

            // Check collision box volume; if the volume is small (50% or less), allow seeing
            // through it. This handles chiseled blocks, furniture, fences, etc.
            float totalVolume = 0;
            foreach (var box in block.CollisionBoxes)
                totalVolume += (box.X2 - box.X1) * (box.Y2 - box.Y1) * (box.Z2 - box.Z1);
            if (totalVolume < 0.5f) // 50% threshold
                return false;

            // Block sight if it's a solid block with substantial collision
            return true;
        };

        // Perform the actual raycast
        var selection = player.Entity.World.InteresectionTester.GetSelectedBlock(
            playerEyePos,
            targetPoint,
            blockFilter
        );

        // If nothing blocks the ray or it's the target block itself
        return selection == null ||
               (selection.Position.X == (int)targetPoint.X &&
                selection.Position.Y == (int)targetPoint.Y &&
                selection.Position.Z == (int)targetPoint.Z);
    }

    public bool OpenAll(KeyCombination _)
    {
        var player = _clientApi.World.Player;

        // If browser is already open, close it
        if (_browserDialog != null && _browserDialog.IsOpened())
        {
            _browserDialog.TryClose();
            _browserDialog = null;
            return true;
        }

        List<BlockEntityContainer> chests = new();
        var accessor = _api.World.BlockAccessor;

        BlockPos startPos;
        BlockPos endPos;

        var eyePos = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos);

        // If the player is in a room, use the room to bound scanning and skip the heavy
        // line of sight checking. If there is a column, wall, etc. in that room that
        // obscures the storage, we'll still be able to access it.
        //
        // If the player is NOT in a room, we use a range scan and line of sight checking
        // to determine what can be opened. This is both more costly and more unpredictable
        // when in crowded spaces - we are checking visibility to the center of the block,
        // so if the center of the block is just slightly out of view, it will not open it.
        var strictCheck = true;
        var room = _roomSystem.GetRoomForPosition(player.Entity.Pos.AsBlockPos);
        if (room is { ExitCount: 0 })
        {
            startPos = room.Location.Start.AsBlockPos;
            endPos = room.Location.End.AsBlockPos;
            strictCheck = false;
        }
        else
        {
            // Not in an enclosed room; use a ranged scan
            // Cap to 6 blocks since we reject anything > 5.1 blocks away anyway
            startPos = (eyePos - 6).AsBlockPos;
            endPos = (eyePos + 6).AsBlockPos;
        }

        // Only scan from player's feet level and up (allow one block below for chests player stands on)
        var playerFeetY = player.Entity.Pos.AsBlockPos.Y - 1;
        startPos.Y = Math.Max(startPos.Y, playerFeetY);

        // Timing instrumentation
        var scanTimer = Stopwatch.StartNew();
        long losTimeMs = 0;
        int blocksWalked = 0;
        int containersFound = 0;
        int losChecks = 0;

        // Now that we have our area to scan, do the scan - taking into account anything that
        // might be blocking the player's ability to interact with the storage
        var blockPos = new BlockPos(0, 0, 0); // Reuse to avoid allocations
        accessor.WalkBlocks(startPos, endPos, (block, x, y, z) =>
        {
            blocksWalked++;

            // Check for block entity that is a storage container
            blockPos.Set(x, y, z);
            var be = accessor.GetBlockEntity(blockPos);
            if (be is not BlockEntityContainer container) return;

            // Filter to storage containers using type checks:
            // - BlockEntityCrate: vanilla crates
            // - BlockEntityOpenableContainer: vanilla chests, ContainersBundle
            // - TryGetTypedContainerInfo: SortableStorage and other typed container mods
            bool isStorageContainer = be is BlockEntityCrate
                || be is BlockEntityOpenableContainer
                || TryGetTypedContainerInfo(be, out string _, out int _);
            if (!isStorageContainer) return;

            containersFound++;

            // When using ranged scan, don't bother with any containers that are out of reach or that the player
            // can't see directly
            var blockCenter = new Vec3d(x + 0.5, y + 0.5, z + 0.5);
            if (strictCheck)
            {
                if (player.Entity.Pos.DistanceTo(blockCenter) > 5.1) return;

                losChecks++;
                var losTimer = Stopwatch.StartNew();
                bool hasLos = HasLineOfSightTo(player, blockCenter);
                losTimeMs += losTimer.ElapsedMilliseconds;
                if (!hasLos) return;
            }

            // Check reinforcement system permits access
            bool isLocked = _reinforcementSystem.IsLockedForInteract(blockPos, player);
            if (!isLocked)
            {
                chests.Add(container);
            }
        });

        scanTimer.Stop();
        if (_debugLogging)
        {
            _api.Logger.Debug($"[PackRat Debug] Scan: {scanTimer.ElapsedMilliseconds}ms total, " +
                              $"{blocksWalked} blocks walked, {containersFound} containers found, " +
                              $"{losChecks} LOS checks ({losTimeMs}ms), {chests.Count} accessible, " +
                              $"strictCheck={strictCheck}");
        }

        if (chests.Count > 0)
        {
            // Enter browse mode - Harmony patch will intercept OpenInventory packets
            _browseMode = true;
            _pendingPositions.Clear();
            _openedContainers.Clear();
            _pendingCrateConfirmation = 0;

            // Separate containers: crates use direct access, everything else uses packets
            int crateCount = 0;
            foreach (var chest in chests)
            {
                if (IsCrate(chest))
                    crateCount++;
                else
                    _pendingPositions.Add(chest.Pos.Copy());
            }

            // Debug logging: show all candidates expected to send inventory
            if (_debugLogging)
            {
                _api.Logger.Debug($"[PackRat Debug] Found {chests.Count} containers total:");
                _api.Logger.Debug($"[PackRat Debug]   Crates (direct access): {crateCount}");
                _api.Logger.Debug($"[PackRat Debug]   Chests (expecting inventory packets): {_pendingPositions.Count}");
                _api.Logger.Debug($"[PackRat Debug] Candidates expecting inventory packets:");
                foreach (var chest in chests)
                {
                    bool isCrate = IsCrate(chest);
                    var invId = chest.Inventory?.InventoryID ?? "null";
                    var blockName = chest.Block?.Code?.ToString() ?? "unknown";
                    _api.Logger.Debug($"[PackRat Debug]   {chest.Pos} - {blockName} (inv: {invId}) - {(isCrate ? "CRATE" : "CHEST/packet pending")}");
                }
            }

            // Store all containers for the browser
            _openedContainers.AddRange(chests);

            // Track crate confirmation - we need to wait for server to confirm crates are open
            _pendingCrateConfirmation = crateCount;

            // Send request to server to open ALL container inventories
            var msg = OpenManyMessage.FromContainers(chests);
            _clientApi.Network.GetChannel(Mod.Info.ModID).SendPacket(msg);

            // If we have no chests (only crates) and no crates, show browser immediately (shouldn't happen)
            // Otherwise, browser will be shown when:
            // - All chest inventory packets are received (via Harmony patch), AND
            // - Crate confirmation is received (via HandleOpenManyConfirm)
            if (_pendingPositions.Count == 0 && _pendingCrateConfirmation == 0)
            {
                ShowBrowser();
            }
        }

        return true;
    }

    private void HandleOpenManyConfirm(OpenManyConfirmMessage msg)
    {
        _pendingCrateConfirmation = 0;

        // If we're still in browse mode and no more pending chest packets, show browser
        if (_browseMode && _pendingPositions.Count == 0)
        {
            ShowBrowser();
        }
    }

    private static void ShowBrowser()
    {
        if (_clientApi == null || _openedContainers.Count == 0)
        {
            ResetBrowseMode();
            return;
        }

        // Create composite inventory
        var composite = new CompositeInventoryView(_clientApi);
        var player = _clientApi.World.Player;
        foreach (var container in _openedContainers)
        {
            if (container?.Inventory == null) continue;

            bool isCrate = IsCrate(container);
            composite.AddInventory(container.Inventory, isCrate);

            // Make sure crate inventories are opened on the client
            // (Chests are opened via the Harmony patch, but crates bypass that)
            if (isCrate && !container.Inventory.HasOpened(player))
            {
                player.InventoryManager.OpenInventory(container.Inventory);
            }
        }

        // Safety check - don't open empty browser
        if (composite.Count == 0)
        {
            _api?.Logger.Warning("PackRat: No slots found in containers, not opening browser");
            ResetBrowseMode();
            return;
        }

        // Create and show the browser dialog
        _browserDialog = new GuiDialogStorageBrowser(_clientApi, composite, _openedContainers);
        _browserDialog.TryOpen();
        ResetBrowseMode();
    }

    /// <summary>
    /// Harmony patch to intercept server packets and suppress individual container dialogs
    /// when in browse mode. This handles vanilla BlockEntityOpenableContainer and mods that extend it
    /// (like ContainersBundle).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockEntityOpenableContainer),
        nameof(BlockEntityOpenableContainer.OnReceivedServerPacket))]
    public static bool OnReceivedServerPacket(int packetid, byte[] data, BlockEntityOpenableContainer __instance)
    {
        return HandleServerPacket(packetid, data, __instance.Inventory, __instance.Pos);
    }

    /// <summary>
    /// Generic Harmony patch for mods that don't extend BlockEntityOpenableContainer
    /// (like SortableStorage). Applied dynamically at runtime if the mod is loaded.
    /// </summary>
    public static bool OnReceivedServerPacket_Generic(int packetid, byte[] data, BlockEntityContainer __instance)
    {
        return HandleServerPacket(packetid, data, __instance.Inventory, __instance.Pos);
    }

    /// <summary>
    /// Common handler for intercepting OpenInventory packets in browse mode
    /// </summary>
    private static bool HandleServerPacket(int packetid, byte[] data, InventoryBase inventory, BlockPos pos)
    {
        // Only suppress OpenInventory packets when in browse mode
        if (!_browseMode || packetid != (int)EnumBlockContainerPacketId.OpenInventory)
            return true;

        // Process the inventory data
        var blockContainer = BlockEntityContainerOpen.FromBytes(data);
        inventory.FromTreeAttributes(blockContainer.Tree);
        inventory.ResolveBlocksOrItems();

        // Open the inventory client-side
        _clientApi?.World?.Player?.InventoryManager.OpenInventory(inventory);

        // Remove from pending and show browser if all received
        _pendingPositions.Remove(pos);
        if (_pendingPositions.Count == 0 && _pendingCrateConfirmation == 0)
        {
            ShowBrowser();
        }

        // Return false to skip original method (which would create individual dialog)
        return false;
    }

    /// <summary>
    /// Harmony prefix to block container-to-container transfers.
    /// When shift-clicking FROM a container, items should go to player inventory, not other containers.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.GetBestSuitedSlot),
        new Type[] {typeof(ItemSlot), typeof(ItemStackMoveOperation), typeof(List<ItemSlot>)})]
    public static bool GetBestSuitedSlot_BlockContainerToContainer(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots,
        InventoryBase __instance, ref WeightedSlot __result)
    {
        // Check if source is from a container (chest or crate)
        var sourceInvId = sourceSlot?.Inventory?.InventoryID;
        if (sourceInvId != null && (sourceInvId.StartsWith("chest-") || sourceInvId.StartsWith("crate-")))
        {
            // Source is from a container - block all other containers as destinations
            // This forces items to go to player inventory
            if (__instance.InventoryID != null &&
                (__instance.InventoryID.StartsWith("chest-") || __instance.InventoryID.StartsWith("crate-")))
            {
                __result = new WeightedSlot();
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Harmony postfix to handle crate shift-click targeting:
    /// - Crates with matching items: high priority (keep original weight)
    /// - Empty crates: boosted priority above chests
    /// - Crates with mismatched items: blocked
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.GetBestSuitedSlot),
        new Type[] {typeof(ItemSlot), typeof(ItemStackMoveOperation), typeof(List<ItemSlot>)})]
    public static void GetBestSuitedSlot_CrateHandling(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots,
        InventoryBase __instance, ref WeightedSlot __result)
    {
        // Only process crate inventories
        if (__instance.InventoryID == null || !__instance.InventoryID.StartsWith("crate-"))
            return;

        // If no valid slot was found, nothing to do
        if (__result.slot == null)
            return;

        // Find if crate has any existing items
        ItemSlot existingSlot = null;
        for (int i = 0; i < __instance.Count; i++)
        {
            if (__instance[i]?.Itemstack != null)
            {
                existingSlot = __instance[i];
                break;
            }
        }

        if (existingSlot != null && sourceSlot?.Itemstack != null)
        {
            // Crate has items - check if source matches
            if (!sourceSlot.Itemstack.Equals(__instance.Api.World, existingSlot.Itemstack, GlobalConstants.IgnoredStackAttributes))
            {
                // Item type doesn't match - block this crate entirely
                __result = new WeightedSlot();
                return;
            }
            // Item matches - keep original weight (crate with matching items is highest priority)
        }
        else
        {
            // Crate is empty - boost priority above chests (which typically return 1-4)
            __result.weight = 5f;
        }
    }
}
