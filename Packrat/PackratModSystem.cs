using System;
using System.Collections.Generic;
using System.Linq;
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

    public static OpenManyMessage FromContainers(List<BlockEntityContainer> containers)
    {
        var msg = new OpenManyMessage();
        msg.Positions = new List<BlockPos>();
        foreach (var c in containers)
        {
            msg.Positions.Add(c.Pos);
        }

        return msg;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OpenManyConfirmMessage
{
    [ProtoMember(1)]
    public int CrateCount { get; set; }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class CloseBrowserMessage
{
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


    public static string ModId => "packrat";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        _api = api;

        if (!Harmony.HasAnyPatches(Mod.Info.ModID))
        {
            _harmony = new Harmony(Mod.Info.ModID);
            _harmony.PatchAll();
        }

        // Register network channel and also register our messages
        api.Network
            .RegisterChannel(Mod.Info.ModID)
            .RegisterMessageType(typeof(OpenManyMessage))
            .RegisterMessageType(typeof(OpenManyConfirmMessage))
            .RegisterMessageType(typeof(CloseBrowserMessage));
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
    }


    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        _serverApi = api;

        api.Network
            .GetChannel(Mod.Info.ModID)
            .SetMessageHandler<OpenManyMessage>(HandleOpenManyRequest)
            .SetMessageHandler<CloseBrowserMessage>(HandleCloseBrowserRequest);
    }

    private void HandleCloseBrowserRequest(IServerPlayer sender, CloseBrowserMessage msg)
    {
        // Currently unused - kept for future server-side cleanup if needed
    }

    private void HandleOpenManyRequest(IServerPlayer sender, OpenManyMessage msg)
    {
        int crateCount = 0;

        foreach (var pos in msg.Positions)
        {
            var be = _serverApi.World.BlockAccessor.GetBlockEntity(pos);
            if (be is BlockEntityGenericTypedContainer typedContainer)
            {
                // Directly send inventory data - bypasses the retrieveOnly/empty check that crates have
                var data = BlockEntityContainerOpen.ToBytes(
                    "BlockEntityInventory",
                    Lang.Get(typedContainer.dialogTitleLangCode),
                    (byte)typedContainer.quantityColumns,
                    typedContainer.Inventory
                );
                _serverApi.Network.SendBlockEntityPacket(sender, pos, (int)EnumBlockContainerPacketId.OpenInventory, data);
                sender.InventoryManager.OpenInventory(typedContainer.Inventory);
            }
            else if (be is BlockEntityCrate crate)
            {
                // Crates - just open the inventory on the server, client accesses directly
                // (BlockEntityCrate doesn't extend BlockEntityOpenableContainer so no packet handling)
                sender.InventoryManager.OpenInventory(crate.Inventory);
                crateCount++;
            }
            else if (be is BlockEntityOpenableContainer container)
            {
                // Fall back to OnPlayerRightClick for other container types
                container.OnPlayerRightClick(sender, new BlockSelection(pos, BlockFacing.UP, container.Block));
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
            if (block is BlockGenericTypedContainer)
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
            var totalVolume = block.CollisionBoxes.Sum(box =>
                (box.X2 - box.X1) * (box.Y2 - box.Y1) * (box.Z2 - box.Z1));
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
        if (player.WorldData.CurrentGameMode == EnumGameMode.Creative) return false;

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

        var eyePos = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos - 0.5f);

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
            var range = player.WorldData.PickingRange + 1;
            startPos = (eyePos - range).AsBlockPos;
            endPos = (eyePos + range + 1.0f).AsBlockPos;
        }

        // Now that we have our area to scan, do the scan - taking into account anything that
        // might be blocking the player's ability to interact with the storage
        accessor.WalkBlocks(startPos, endPos, (block, x, y, z) =>
        {
            // Don't bother with any blocks that aren't a container
            if (block is not BlockGenericTypedContainer && block is not BlockContainer && block is not BlockCrate) return;

            var blockPos = new BlockPos(x, y, z);

            // Don't bother with any containers that are out of reach
            var blockCenter = new Vec3d(x + 0.5, y + 0.5, z + 0.5);
            if (player.Entity.Pos.DistanceTo(blockCenter) > 5.1) return;

            // Try multiple points on the container to check visibility
            if (strictCheck && !HasLineOfSightTo(player, blockCenter)) return;

            // Check for block entity - support GenericTypedContainer, GenericContainer, and Crate
            var be = accessor.GetBlockEntity(blockPos);
            if (be is not BlockEntityGenericTypedContainer && be is not BlockEntityGenericContainer && be is not BlockEntityCrate) return;

            // Check reinforcement system permits access
            bool isLocked = _reinforcementSystem.IsLockedForInteract(blockPos, player);
            if (!isLocked && be is BlockEntityContainer container)
            {
                chests.Add(container);
            }
        });

        if (chests.Count > 0)
        {
            // Enter browse mode - Harmony patch will intercept OpenInventory packets
            _browseMode = true;
            _pendingPositions.Clear();
            _openedContainers.Clear();
            _pendingCrateConfirmation = 0;

            // Separate containers by type - only BlockEntityOpenableContainer uses packets
            int crateCount = 0;

            foreach (var chest in chests)
            {
                if (chest is BlockEntityOpenableContainer)
                {
                    _pendingPositions.Add(chest.Pos.Copy());
                }
                else
                {
                    // Crates don't use the packet system - access inventory directly
                    crateCount++;
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
            _browseMode = false;
            _pendingPositions.Clear();
            _pendingCrateConfirmation = 0;
            return;
        }

        // Create composite inventory
        var composite = new CompositeInventoryView(_clientApi);
        var player = _clientApi.World.Player;
        foreach (var container in _openedContainers)
        {
            if (container?.Inventory != null)
            {
                bool isCrate = container is BlockEntityCrate;
                composite.AddInventory(container.Inventory, isCrate);

                // Make sure crate inventories are opened on the client
                // (Chests are opened via the Harmony patch, but crates bypass that)
                if (isCrate && !container.Inventory.HasOpened(player))
                {
                    player.InventoryManager.OpenInventory(container.Inventory);
                }
            }
        }

        // Safety check - don't open empty browser
        if (composite.Count == 0)
        {
            _api?.Logger.Warning("PackRat: No slots found in containers, not opening browser");
            _browseMode = false;
            _pendingPositions.Clear();
            _pendingCrateConfirmation = 0;
            return;
        }

        // Create and show the browser dialog
        _browserDialog = new GuiDialogStorageBrowser(_clientApi, composite, _openedContainers);
        _browserDialog.TryOpen();

        // Exit browse mode
        _browseMode = false;
        _pendingPositions.Clear();
        _pendingCrateConfirmation = 0;
    }

    /// <summary>
    /// Harmony patch to intercept server packets and suppress individual container dialogs
    /// when in browse mode
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockEntityOpenableContainer),
        nameof(BlockEntityOpenableContainer.OnReceivedServerPacket))]
    public static bool OnReceivedServerPacket(int packetid, byte[] data, BlockEntityOpenableContainer __instance)
    {
        // Only suppress OpenInventory packets when in browse mode
        if (_browseMode && packetid == (int)EnumBlockContainerPacketId.OpenInventory)
        {
            // Process the inventory data
            var blockContainer = BlockEntityContainerOpen.FromBytes(data);
            __instance.Inventory.FromTreeAttributes(blockContainer.Tree);
            __instance.Inventory.ResolveBlocksOrItems();

            // Open the inventory client-side
            var player = _clientApi?.World?.Player;
            if (player != null)
            {
                player.InventoryManager.OpenInventory(__instance.Inventory);
            }

            // Remove from pending
            _pendingPositions.Remove(__instance.Pos);

            // If all inventories received AND crate confirmation received, show the browser
            if (_pendingPositions.Count == 0 && _pendingCrateConfirmation == 0)
            {
                ShowBrowser();
            }

            // Return false to skip original method (which would create individual dialog)
            return false;
        }

        return true;
    }

    /// <summary>
    /// Harmony postfix to handle crate shift-click targeting:
    /// - Crates with matching items: high priority (keep original weight)
    /// - Empty crates: low priority (fallback only)
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
