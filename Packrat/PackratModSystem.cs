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

    // Client config (persisted)
    private static PackratConfig _config;

    // Debug logging (toggle with .packratdebug command)
    private static bool _debugLogging;

    // Registry of storage container types to include in scanning
    private static readonly HashSet<Type> _storageContainerTypes = new();

    // Types that need OnReceivedServerPacket patched (where the method is defined/overridden)
    private static readonly HashSet<Type> _typesToPatch = new();

    // Mod container types to discover at runtime: (TypeName, NeedsPatch)
    // NeedsPatch = true if the type has its own OnReceivedServerPacket (not inherited)
    private static readonly (string TypeName, bool NeedsPatch)[] _modContainerTypes =
    {
        // SortableStorage - has its own OnReceivedServerPacket implementation
        ("SortableStorage.ModSystem.BESortableOpenableContainer", true),
        // ContainersBundle - extends BlockEntityOpenableContainer, inherits patched method
        ("ContainersBundle.BlockEntityCBContainer", false),
        // BetterCrates - extends BlockEntityContainer, uses direct inventory access like vanilla crates
        ("BetterCratesNamespace.BetterCrateBlockEntity", false),
        // StorageController - extends BlockEntityGenericTypedContainer, links to other containers
        ("storagecontroller.BlockEntityStorageController", false),
        // Primitive Survival - placed tree hollows (extends BlockEntityOpenableContainer)
        ("PrimitiveSurvival.ModSystem.BETreeHollowPlaced", false),
        // Primitive Survival - grown tree hollows (extends BlockEntityDisplayCase, direct access like crates)
        ("PrimitiveSurvival.ModSystem.BETreeHollowGrown", false),
    };

    // Cache for Storage Controller's ContainerList property (accessed via reflection)
    private static PropertyInfo _storageControllerContainerListProp;

    public static string ModId => "packrat";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        _api = api;

        // Initialize container type registry
        InitializeContainerTypeRegistry();

        if (!Harmony.HasAnyPatches(Mod.Info.ModID))
        {
            _harmony = new Harmony(Mod.Info.ModID);
            _harmony.PatchAll();

            // Apply dynamic patches for container types that need OnReceivedServerPacket interception
            ApplyContainerPatches();
        }

        // Register network channel and also register our messages
        api.Network
            .RegisterChannel(Mod.Info.ModID)
            .RegisterMessageType(typeof(OpenManyMessage))
            .RegisterMessageType(typeof(OpenManyConfirmMessage));
    }

    /// <summary>
    /// Initialize the registry of storage container types and types to patch.
    /// </summary>
    private void InitializeContainerTypeRegistry()
    {
        // Only initialize once
        if (_storageContainerTypes.Count > 0) return;

        // Vanilla storage container types to scan for
        // Note: BlockEntityOpenableContainer is NOT included (too broad - includes firepits)
        _storageContainerTypes.Add(typeof(BlockEntityCrate));
        _storageContainerTypes.Add(typeof(BlockEntityGenericTypedContainer));

        // Vanilla types that need OnReceivedServerPacket patched
        // BlockEntityOpenableContainer is the base where the method is defined
        _typesToPatch.Add(typeof(BlockEntityOpenableContainer));

        _api.Logger.Debug("[PackRat] Registered vanilla container types");

        // Discover and add mod container types
        foreach (var (typeName, needsPatch) in _modContainerTypes)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type != null)
            {
                _storageContainerTypes.Add(type);
                if (needsPatch)
                {
                    _typesToPatch.Add(type);
                }
                _api.Logger.Notification($"[PackRat] Discovered mod container type: {typeName}");
            }
        }

        _api.Logger.Debug($"[PackRat] Total storage types: {_storageContainerTypes.Count}, types to patch: {_typesToPatch.Count}");
    }

    /// <summary>
    /// Check if a BlockEntity is a known storage container type.
    /// Checks the type hierarchy against registered types.
    /// </summary>
    private static bool IsStorageContainer(BlockEntity be)
    {
        // Check if this type or any of its base types is in our registry
        var checkType = be.GetType();
        while (checkType != null && checkType != typeof(object))
        {
            if (_storageContainerTypes.Contains(checkType))
                return true;
            checkType = checkType.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Apply Harmony patches for container types that need OnReceivedServerPacket interception.
    /// </summary>
    private void ApplyContainerPatches()
    {
        var prefixMethod = typeof(PackratModSystem).GetMethod(nameof(OnReceivedServerPacket_Prefix),
            BindingFlags.Public | BindingFlags.Static);
        if (prefixMethod == null) return;

        foreach (var containerType in _typesToPatch)
        {
            var targetMethod = containerType.GetMethod("OnReceivedServerPacket",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(byte[]) },
                null);

            if (targetMethod == null) continue;

            _harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefixMethod));
            _api.Logger.Debug($"[PackRat] Patched {containerType.Name}.OnReceivedServerPacket");
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        _clientApi = api;

        // Load client config
        _config = api.LoadModConfig<PackratConfig>($"{ModId}-client.json") ?? new PackratConfig();

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

    /// <summary>
    /// Save client config when sort mode changes
    /// </summary>
    private static void OnSortModeChanged(SortMode newMode)
    {
        _config.SortMode = newMode;
        _clientApi?.StoreModConfig(_config, $"{ModId}-client.json");
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

            if (IsDirectAccessContainer(container))
            {
                // Crates and display cases (like tree hollows) - open inventory directly
                sender.InventoryManager.OpenInventory(container.Inventory);
                crateCount++;
            }
            // Use OnPlayerRightClick for openable containers (vanilla chests, mod containers, etc.)
            else if (be is BlockEntityOpenableContainer openable)
            {
                openable.OnPlayerRightClick(sender, new BlockSelection(pos, BlockFacing.UP, openable.Block));
            }
            // Fallback: try to invoke OnPlayerRightClick via reflection for mod containers
            else if (IsStorageContainer(be))
            {
                TryInvokeOnPlayerRightClick(be, sender, pos);
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
    /// Check if a container uses direct inventory access (no OpenInventory packet flow).
    /// This includes crates and display cases (like Primitive Survival's tree hollows).
    /// These containers don't send inventory packets - we open them directly and wait for server confirmation.
    /// </summary>
    private static bool IsDirectAccessContainer(BlockEntityContainer container)
    {
        // Check by inventory ID prefix
        var invId = container.Inventory?.InventoryID;
        if (invId?.StartsWith("crate-") == true || invId?.StartsWith("bettercrate-") == true)
            return true;

        // Check by type hierarchy - BlockEntityDisplayCase and its subclasses use direct access
        // (includes Primitive Survival's BETreeHollowGrown which extends BlockEntityDisplayCase)
        var checkType = container.GetType();
        while (checkType != null && checkType != typeof(object))
        {
            if (checkType.Name == "BlockEntityDisplayCase" || checkType.Name == "BETreeHollowGrown")
                return true;
            checkType = checkType.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Check if a container is a crate (by inventory ID prefix) - used for shift-click handling
    /// </summary>
    private static bool IsCrate(BlockEntityContainer container)
    {
        var invId = container.Inventory?.InventoryID;
        return invId?.StartsWith("crate-") == true || invId?.StartsWith("bettercrate-") == true;
    }

    /// <summary>
    /// Expand Storage Controllers by adding their linked containers to the list.
    /// Storage Controller mod maintains a list of linked container positions.
    /// Note: Storage Controllers themselves are REMOVED from the list after expansion,
    /// because they have custom OnPlayerRightClick that doesn't send inventory packets.
    /// </summary>
    private void ExpandStorageControllers(List<BlockEntityContainer> containers, IBlockAccessor accessor, IPlayer player)
    {
        // Collect positions from all storage controllers first to avoid modifying list while iterating
        var linkedPositions = new HashSet<BlockPos>();
        var storageControllers = new List<BlockEntityContainer>();
        var existingPositions = new HashSet<BlockPos>();

        foreach (var container in containers)
        {
            existingPositions.Add(container.Pos);

            var containerList = GetStorageControllerLinkedContainers(container);
            if (containerList != null)
            {
                // This is a Storage Controller - mark it for removal and collect its linked containers
                storageControllers.Add(container);

                foreach (var pos in containerList)
                {
                    if (pos != null && !existingPositions.Contains(pos))
                    {
                        linkedPositions.Add(pos);
                    }
                }
            }
        }

        // Remove Storage Controllers from the list - they don't work with Packrat's packet flow
        // (their OnPlayerRightClick only opens a dialog client-side, doesn't send inventory packets)
        foreach (var sc in storageControllers)
        {
            containers.Remove(sc);
            if (_debugLogging)
            {
                _api.Logger.Debug($"[PackRat] Removed Storage Controller at {sc.Pos} (incompatible packet flow)");
            }
        }

        if (linkedPositions.Count == 0) return;

        // Add linked containers that are valid and accessible
        int added = 0;
        foreach (var pos in linkedPositions)
        {
            // Skip if we already have this container
            if (existingPositions.Contains(pos)) continue;

            var be = accessor.GetBlockEntity(pos);
            if (be is BlockEntityContainer linkedContainer && IsStorageContainer(be))
            {
                // Skip if this is also a Storage Controller (nested controllers)
                if (GetStorageControllerLinkedContainers(linkedContainer) != null)
                    continue;

                // Check reinforcement
                if (!_reinforcementSystem.IsLockedForInteract(pos, player))
                {
                    containers.Add(linkedContainer);
                    existingPositions.Add(pos);
                    added++;
                }
            }
        }

        if (_debugLogging && added > 0)
        {
            _api.Logger.Debug($"[PackRat] Expanded {added} containers from Storage Controllers");
        }
    }

    /// <summary>
    /// Get linked containers from a Storage Controller via reflection.
    /// Returns null if the container is not a Storage Controller.
    /// </summary>
    private static List<BlockPos> GetStorageControllerLinkedContainers(BlockEntityContainer container)
    {
        var type = container.GetType();

        // Check if this is a Storage Controller (by type name to avoid hard dependency)
        if (type.FullName == null || !type.FullName.Contains("StorageController"))
            return null;

        // Cache the property accessor
        if (_storageControllerContainerListProp == null)
        {
            _storageControllerContainerListProp = type.GetProperty("ContainerList",
                BindingFlags.Public | BindingFlags.Instance);
        }

        if (_storageControllerContainerListProp == null)
            return null;

        return _storageControllerContainerListProp.GetValue(container) as List<BlockPos>;
    }

    /// <summary>
    /// Try to invoke OnPlayerRightClick on a block entity via reflection.
    /// Used for mod containers that don't extend BlockEntityOpenableContainer.
    /// </summary>
    private void TryInvokeOnPlayerRightClick(BlockEntity be, IServerPlayer player, BlockPos pos)
    {
        var method = be.GetType().GetMethod("OnPlayerRightClick",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(IPlayer), typeof(BlockSelection) },
            null);

        if (method != null)
        {
            var blockSel = new BlockSelection(pos, BlockFacing.UP, be.Block);
            method.Invoke(be, new object[] { player, blockSel });
        }
        else
        {
            _api.Logger.Warning($"[PackRat] Container at {pos} has no OnPlayerRightClick method");
        }
    }

    /// <summary>
    /// Reset browse mode state
    /// </summary>
    private static void ResetBrowseMode()
    {
        _browseMode = false;
        _pendingPositions.Clear();
        _pendingCrateConfirmation = 0;
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

            // Filter to storage containers using type registry
            if (!IsStorageContainer(be)) return;

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
            _api.Logger.Debug($"[PackRat] Scan: {scanTimer.ElapsedMilliseconds}ms total, " +
                              $"{blocksWalked} blocks walked, {containersFound} containers found, " +
                              $"{losChecks} LOS checks ({losTimeMs}ms), {chests.Count} accessible, " +
                              $"strictCheck={strictCheck}");
        }

        // Expand Storage Controllers - add their linked containers
        ExpandStorageControllers(chests, accessor, player);

        if (chests.Count > 0)
        {
            // Enter browse mode - Harmony patch will intercept OpenInventory packets
            _browseMode = true;
            _pendingPositions.Clear();
            _openedContainers.Clear();
            _pendingCrateConfirmation = 0;

            // Separate containers: direct access (crates, display cases) vs packet-based (chests)
            int directAccessCount = 0;
            foreach (var chest in chests)
            {
                if (IsDirectAccessContainer(chest))
                    directAccessCount++;
                else
                    _pendingPositions.Add(chest.Pos.Copy());
            }

            // Debug logging: show all candidates expected to send inventory
            if (_debugLogging)
            {
                _api.Logger.Debug($"[PackRat] Found {chests.Count} containers total:");
                _api.Logger.Debug($"[PackRat]   Direct access (crates/display cases): {directAccessCount}");
                _api.Logger.Debug($"[PackRat]   Chests (expecting inventory packets): {_pendingPositions.Count}");
                _api.Logger.Debug($"[PackRat] Candidates expecting inventory packets:");
                foreach (var chest in chests)
                {
                    bool isDirect = IsDirectAccessContainer(chest);
                    var invId = chest.Inventory?.InventoryID ?? "null";
                    var blockName = chest.Block?.Code?.ToString() ?? "unknown";
                    _api.Logger.Debug($"[PackRat]   {chest.Pos} - {blockName} (inv: {invId}) - {(isDirect ? "DIRECT" : "CHEST/packet pending")}");
                }
            }

            // Store all containers for the browser
            _openedContainers.AddRange(chests);

            // Track direct access confirmation - we need to wait for server to confirm they are open
            _pendingCrateConfirmation = directAccessCount;

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
            bool isDirect = IsDirectAccessContainer(container);
            composite.AddInventory(container.Inventory, isCrate);

            // Make sure direct access inventories are opened on the client
            // (Chests are opened via the Harmony patch, but direct access containers bypass that)
            if (isDirect && !container.Inventory.HasOpened(player))
            {
                player.InventoryManager.OpenInventory(container.Inventory);
            }
        }

        // Safety check - don't open empty browser
        if (composite.Count == 0)
        {
            _api?.Logger.Warning("[PackRat] No slots found in containers, not opening browser");
            ResetBrowseMode();
            return;
        }

        // Create sorted view with persisted sort mode
        var sortedView = new SortedInventoryView(composite);
        sortedView.SortMode = _config?.SortMode ?? SortMode.None;

        // Create and show the browser dialog
        _browserDialog = new GuiDialogStorageBrowser(_clientApi, sortedView, _openedContainers, OnSortModeChanged);
        _browserDialog.TryOpen();
        ResetBrowseMode();
    }

    /// <summary>
    /// Harmony prefix to intercept server packets and suppress individual container dialogs
    /// when in browse mode. Applied dynamically to container types that need it.
    /// </summary>
    public static bool OnReceivedServerPacket_Prefix(int packetid, byte[] data, BlockEntityContainer __instance)
    {
        return HandleServerPacket(packetid, data, __instance.Inventory, __instance.Pos);
    }

    /// <summary>
    /// Common handler for intercepting OpenInventory packets in browse mode
    /// </summary>
    private static bool HandleServerPacket(int packetid, byte[] data, InventoryBase inventory, BlockPos pos)
    {
        // Only suppress OpenInventory packets when in browse mode
        // EnumBlockContainerPacketId.OpenInventory = 5000, used by vanilla, SortableStorage, and ContainersBundle
        if (!_browseMode || packetid != (int)EnumBlockContainerPacketId.OpenInventory)
            return true;

        // Process the inventory data (format is compatible between vanilla and SortableStorage)
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
        if (sourceInvId != null && (sourceInvId.StartsWith("chest-") || sourceInvId.StartsWith("crate-") || sourceInvId.StartsWith("bettercrate-")))
        {
            // Source is from a container - block all other containers as destinations
            // This forces items to go to player inventory
            if (__instance.InventoryID != null &&
                (__instance.InventoryID.StartsWith("chest-") || __instance.InventoryID.StartsWith("crate-") || __instance.InventoryID.StartsWith("bettercrate-")))
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
        if (__instance.InventoryID == null ||
            (!__instance.InventoryID.StartsWith("crate-") && !__instance.InventoryID.StartsWith("bettercrate-")))
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

        var side = __instance.Api?.Side.ToString() ?? "unknown";
        var srcItem = sourceSlot?.Itemstack?.GetName() ?? "null";
        var existingItem = existingSlot?.Itemstack?.GetName() ?? "null";
        var originalWeight = __result.weight;

        if (existingSlot != null && sourceSlot?.Itemstack != null)
        {
            // Crate has items - check if source matches
            if (!sourceSlot.Itemstack.Equals(__instance.Api.World, existingSlot.Itemstack, GlobalConstants.IgnoredStackAttributes))
            {
                // Item type doesn't match - block this crate entirely
                if (_debugLogging)
                    _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - BLOCKED (mismatch: {srcItem} vs {existingItem})");
                __result = new WeightedSlot();
                return;
            }
            // Item matches - boost weight to beat empty chests (~3) and chests with matching items (~5)
            __result.weight = 6f;
            if (_debugLogging)
                _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - MATCH ({srcItem} matches {existingItem}), weight {originalWeight} -> {__result.weight}");
        }
        else
        {
            // Crate is empty - boost priority above chests (which typically return 1-4)
            __result.weight = 5f;
            if (_debugLogging)
                _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - EMPTY crate, weight {originalWeight} -> {__result.weight}");
        }
    }

    /// <summary>
    /// Harmony postfix to prefer containers with lower perish rates for perishable items.
    /// Cellars, ice boxes, storage vessels, etc. will be preferred over normal storage.
    /// Applies to ALL inventories - any container that reduces perish rate will be prioritized.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.GetBestSuitedSlot),
        new Type[] {typeof(ItemSlot), typeof(ItemStackMoveOperation), typeof(List<ItemSlot>)})]
    public static void GetBestSuitedSlot_PerishRateHandling(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots,
        InventoryBase __instance, ref WeightedSlot __result)
    {
        // If no valid slot was found, nothing to do
        if (__result.slot == null)
            return;

        // Check if source item is perishable
        var stack = sourceSlot?.Itemstack;
        if (stack == null) return;

        var transProps = stack.Collectible?.TransitionableProps;
        if (transProps == null) return;

        bool isPerishable = false;
        foreach (var prop in transProps)
        {
            if (prop.Type == EnumTransitionType.Perish)
            {
                isPerishable = true;
                break;
            }
        }

        if (!isPerishable) return;

        // Get the perish rate for this inventory
        float perishRate = __instance.GetTransitionSpeedMul(EnumTransitionType.Perish, stack);

        // Adjust weight: lower perish rate = higher weight
        // perishRate 0 -> +10 bonus (ice box / zero perish)
        // perishRate 0.5 -> +5 bonus (cellar)
        // perishRate 1.0 -> +0 bonus (normal storage)
        // perishRate > 1 -> no bonus (bad storage)
        float bonus = Math.Max(0f, (1f - perishRate) * 10f);
        __result.weight += bonus;

        if (_debugLogging)
        {
            var side = __instance.Api?.Side.ToString() ?? "unknown";
            _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - PERISH item, rate={perishRate:F2}, bonus={bonus:F1}, newWeight={__result.weight:F1}");
        }
    }
}
