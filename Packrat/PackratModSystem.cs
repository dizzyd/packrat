using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [ProtoMember(2)]
    public List<BlockPos> SkippedPositions { get; set; }
}

/// <summary>
/// Client's container priority list, pushed to the server.
///
/// This is not optional. The destination of a shift-click is never transmitted -
/// Packet_ActivateInventorySlot carries only the clicked source slot - so the server
/// re-derives the destination by running the same weighting itself. If the two sides
/// disagree about priority they pick different containers, and the next inventory sync
/// snaps the items back to wherever the server put them.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ContainerPriorityMessage
{
    [ProtoMember(1)]
    public List<string> Types { get; set; }
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
    private static long _browseTimeoutCallbackId; // Timeout callback to handle unresponsive containers

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
            .RegisterMessageType(typeof(OpenManyConfirmMessage))
            .RegisterMessageType(typeof(ContainerPriorityMessage));
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
        // Normalise on load so a hand-edited config resolves identically here and on the server
        _config.ContainerPriority = NormalizePriority(_config.ContainerPriority);

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

        // The server re-derives shift-click destinations itself, so it needs this player's
        // priority list before they touch any container
        api.Event.PlayerJoin += joiningPlayer =>
        {
            if (joiningPlayer == api.World?.Player) SendContainerPriority();
        };

        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create("packrat")
            .WithDescription("PackRat storage browser settings")
            .BeginSubCommand("priority")
                .WithDescription("Container types to prefer when shift-clicking items into storage")
                .HandleWith(_ => ShowContainerPriority())
                .BeginSubCommand("set")
                    .WithDescription("Set the priority order, highest priority first")
                    .WithArgs(parsers.All("types"))
                    .HandleWith(SetContainerPriority)
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithDescription("Clear the priority list and go back to defaults")
                    .HandleWith(_ => ResetContainerPriority())
                .EndSubCommand()
                .BeginSubCommand("types")
                    .WithDescription("List the container types within reach, with the token to use")
                    .HandleWith(_ => ShowContainerTypes())
                .EndSubCommand()
            .EndSubCommand();

        // Register debug toggle command
        api.ChatCommands.Create("packratdebug")
            .WithDescription("Toggle PackRat debug logging")
            .HandleWith(_ =>
            {
                _debugLogging = !_debugLogging;
                return TextCommandResult.Success($"PackRat debug logging: {(_debugLogging ? "ON" : "OFF")}");
            });
    }

    // Every block code first part known to this client, used to catch typos in
    // .packrat priority set. Built once - the block registry does not change at runtime.
    private static HashSet<string> _knownBlockTokens;

    private static bool IsKnownToken(string token)
    {
        if (_knownBlockTokens == null)
        {
            _knownBlockTokens = new HashSet<string>();
            foreach (var block in _clientApi?.World?.Blocks ?? new List<Block>())
            {
                var part = block?.Code?.FirstCodePart();
                if (part != null) _knownBlockTokens.Add(part);
            }
        }

        return _knownBlockTokens.Contains(token);
    }

    private static TextCommandResult ShowContainerPriority()
    {
        var priority = _config?.ContainerPriority;
        if (priority == null || priority.Count == 0)
        {
            return TextCommandResult.Success(
                "No container priority set - all container types rank equally.\n" +
                "Use '.packrat priority types' to see what is nearby, then " +
                "'.packrat priority set trunk,chest,crate'.");
        }

        var lines = new List<string> { "Container priority, highest first:" };
        for (int i = 0; i < priority.Count; i++)
        {
            lines.Add($"  {i + 1}. {priority[i]}");
        }
        lines.Add("  (all other types rank below these)");
        lines.Add("Merging into a container that already holds the item always wins, whatever the order.");

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    private static TextCommandResult SetContainerPriority(TextCommandCallingArgs args)
    {
        var raw = args[0] as string;
        if (string.IsNullOrWhiteSpace(raw))
            return TextCommandResult.Error("Give a list of container types, e.g. '.packrat priority set trunk,chest,crate'.");

        var requested = NormalizePriority(
            new List<string>(raw.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)));

        if (requested.Count == 0)
            return TextCommandResult.Error("No usable container types in that list.");

        OnContainerPriorityChanged(requested);

        var message = new List<string> { "Container priority set, highest first:" };
        for (int i = 0; i < requested.Count; i++)
        {
            message.Add($"  {i + 1}. {requested[i]}");
        }

        // Warn rather than reject: a token can be perfectly valid and simply come from a mod
        // that is not loaded right now, and rejecting it would make the list uneditable
        var unknown = requested.FindAll(token => !IsKnownToken(token));
        if (unknown.Count > 0)
        {
            message.Add($"Warning: no block called {string.Join(", ", unknown)} is loaded. " +
                        "These entries will do nothing unless a mod providing them is active - " +
                        "check spelling with '.packrat priority types'.");
        }

        return TextCommandResult.Success(string.Join("\n", message));
    }

    private static TextCommandResult ResetContainerPriority()
    {
        OnContainerPriorityChanged(new List<string>());
        return TextCommandResult.Success("Container priority cleared - all container types rank equally again.");
    }

    private static TextCommandResult ShowContainerTypes()
    {
        var modSystem = _clientApi?.ModLoader?.GetModSystem<PackratModSystem>();
        var player = _clientApi?.World?.Player;
        if (modSystem == null || player == null)
            return TextCommandResult.Error("Not in a world.");

        var containers = modSystem.ScanAccessibleContainers(player);
        if (containers.Count == 0)
            return TextCommandResult.Success("No storage containers in reach. Stand where you would press the open-all key.");

        // Group by the same token the priority list matches on
        var counts = new Dictionary<string, int>();
        var names = new Dictionary<string, string>();
        foreach (var container in containers)
        {
            var token = container.Block?.Code?.FirstCodePart();
            if (token == null) continue;

            counts.TryGetValue(token, out int seen);
            counts[token] = seen + 1;
            if (!names.ContainsKey(token))
                names[token] = container.Block.GetPlacedBlockName(_clientApi.World, container.Pos);
        }

        var priority = _config?.ContainerPriority ?? new List<string>();
        var tokens = new List<string>(counts.Keys);
        tokens.Sort(StringComparer.Ordinal);   // dictionary order is unspecified; keep output stable

        var lines = new List<string> { "Container types in reach:" };
        foreach (var token in tokens)
        {
            // Chat renders in a proportional font, so column padding would look ragged
            int rank = priority.IndexOf(token);
            var rankNote = rank >= 0 ? $" [priority {rank + 1}]" : "";
            lines.Add($"  {token} - \"{names[token]}\" x{counts[token]}{rankNote}");
        }
        lines.Add("Use the token on the left with '.packrat priority set'.");

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// Push this client's container priority list to the server, which needs it to derive the
    /// same shift-click destination the client does.
    /// </summary>
    private static void SendContainerPriority()
    {
        if (_clientApi == null) return;

        _clientApi.Network.GetChannel(ModId).SendPacket(new ContainerPriorityMessage
        {
            Types = new List<string>(_config?.ContainerPriority ?? new List<string>())
        });
    }

    /// <summary>
    /// Persist the container priority list and push it to the server.
    /// </summary>
    private static void OnContainerPriorityChanged(List<string> newPriority)
    {
        _config.ContainerPriority = NormalizePriority(newPriority);
        _clientApi?.StoreModConfig(_config, $"{ModId}-client.json");
        SendContainerPriority();
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

        _reinforcementSystem = api.ModLoader.GetModSystem<ModSystemBlockReinforcement>();

        // Static state survives a world reload in singleplayer
        _serverPriorities.Clear();

        api.Network
            .GetChannel(Mod.Info.ModID)
            .SetMessageHandler<OpenManyMessage>(HandleOpenManyRequest)
            .SetMessageHandler<ContainerPriorityMessage>(HandleContainerPriority);

        api.Event.PlayerDisconnect += leavingPlayer => _serverPriorities.Remove(leavingPlayer.PlayerUID);
    }

    /// <summary>
    /// Store a client's container priority list for use when re-deriving shift-click
    /// destinations on this side.
    /// </summary>
    private void HandleContainerPriority(IServerPlayer sender, ContainerPriorityMessage msg)
    {
        var types = msg?.Types;
        if (types == null || types.Count == 0)
        {
            _serverPriorities.Remove(sender.PlayerUID);
            return;
        }

        // Untrusted input, and it gets walked on every suitability check
        var cleaned = NormalizePriority(types);

        if (cleaned.Count == 0)
            _serverPriorities.Remove(sender.PlayerUID);
        else
            _serverPriorities[sender.PlayerUID] = cleaned;

        if (_debugLogging)
            _api?.Logger.Debug($"[PackRat] [Server] container priority for {sender.PlayerName}: {string.Join(", ", cleaned)}");
    }

    private void HandleOpenManyRequest(IServerPlayer sender, OpenManyMessage msg)
    {
        int crateCount = 0;
        List<BlockPos> skippedPositions = null;

        foreach (var pos in msg.Positions)
        {
            var be = _serverApi.World.BlockAccessor.GetBlockEntity(pos);
            if (be is not BlockEntityContainer container) continue;

            // Check if player has permission to access this container
            if (_reinforcementSystem?.IsLockedForInteract(pos, sender) == true)
            {
                skippedPositions ??= new List<BlockPos>();
                skippedPositions.Add(pos);
                continue;
            }

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

        // Always send confirmation back to client (includes skipped positions so client doesn't wait forever)
        _serverApi.Network.GetChannel(Mod.Info.ModID).SendPacket(
            new OpenManyConfirmMessage { CrateCount = crateCount, SkippedPositions = skippedPositions },
            sender
        );
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
                if (_reinforcementSystem?.IsLockedForInteract(pos, player) != true)
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

        // Cancel any pending timeout
        if (_browseTimeoutCallbackId != 0)
        {
            _clientApi?.Event.UnregisterCallback(_browseTimeoutCallbackId);
            _browseTimeoutCallbackId = 0;
        }
    }

    /// <summary>
    /// Called when the browse timeout expires. Shows the browser with whatever containers
    /// have responded, giving up on any that haven't (version mismatch, incompatible mods, etc.)
    /// </summary>
    private static void OnBrowseTimeout(float dt)
    {
        // Check browse mode first to avoid race with ResetBrowseMode
        if (!_browseMode)
        {
            _browseTimeoutCallbackId = 0;
            return; // Already finished
        }

        _browseTimeoutCallbackId = 0; // Callback has fired, clear the ID

        if (_debugLogging)
        {
            _clientApi?.Logger.Debug($"[PackRat] Browse timeout - {_pendingPositions.Count} positions still pending, {_pendingCrateConfirmation} crates unconfirmed");
        }

        // Give up waiting and show browser with whatever containers we have
        _pendingPositions.Clear();
        _pendingCrateConfirmation = 0;
        ShowBrowser();
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

    /// <summary>
    /// Find every storage container the player can currently reach and is allowed to open,
    /// using the same room/line-of-sight/reinforcement rules the browser itself uses.
    ///
    /// Shared by the open-all hotkey and by .packrat priority types, so the types command can
    /// never report containers the browser would not actually open.
    /// </summary>
    private List<BlockEntityContainer> ScanAccessibleContainers(IClientPlayer player)
    {
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
        var playerBlockPos = player.Entity.Pos.AsBlockPos;
        var playerFeetY = playerBlockPos.Y - 1;
        startPos.Y = Math.Max(startPos.Y, playerFeetY);

        // Timing instrumentation
        var scanTimer = Stopwatch.StartNew();
        long losTimeMs = 0;
        int blocksWalked = 0;
        int containersFound = 0;
        int losChecks = 0;
        int roomRejects = 0;

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
            if (strictCheck && player.Entity.Pos.DistanceTo(blockCenter) > 5.1) return;

            // A container sealed inside a room belongs to that room, not to whoever can see
            // into it. Without this, standing outside an enclosed cellar and looking through
            // the doorway pulls its containers in, while standing inside the cellar correctly
            // excludes everything outside - an asymmetry with no justification.
            //
            // This also tightens the in-room branch: room.Location is only a bounding box (the
            // Room class says so explicitly), so a sealed closet inside a larger room's box
            // would otherwise leak its containers in. Room.Contains consults the room's
            // PosInRoom mask, so it answers real membership rather than box containment.
            var containerRoom = _roomSystem?.GetRoomForPosition(blockPos);
            if (containerRoom is { ExitCount: 0 } && !containerRoom.Contains(playerBlockPos))
            {
                roomRejects++;
                return;
            }

            if (strictCheck)
            {
                losChecks++;
                var losTimer = Stopwatch.StartNew();
                bool hasLos = HasLineOfSightTo(player, blockCenter);
                losTimeMs += losTimer.ElapsedMilliseconds;
                if (!hasLos) return;
            }

            // Check reinforcement system permits access
            bool isLocked = _reinforcementSystem?.IsLockedForInteract(blockPos, player) == true;
            if (!isLocked)
            {
                // Skip empty retrieveOnly containers (e.g., looted ruin chests)
                // These won't send inventory packets when OnPlayerRightClick is called
                if (container is BlockEntityGenericTypedContainer typed &&
                    typed.retrieveOnly &&
                    typed.Inventory.Empty)
                {
                    if (_debugLogging)
                        _api.Logger.Debug($"[PackRat] Skipping empty retrieveOnly container at {blockPos}");
                    return; // Skip this container
                }

                chests.Add(container);
            }
        });

        scanTimer.Stop();
        if (_debugLogging)
        {
            _api.Logger.Debug($"[PackRat] Scan: {scanTimer.ElapsedMilliseconds}ms total, " +
                              $"{blocksWalked} blocks walked, {containersFound} containers found, " +
                              $"{losChecks} LOS checks ({losTimeMs}ms), {roomRejects} rejected as another room's, " +
                              $"{chests.Count} accessible, " +
                              $"strictCheck={strictCheck}");
        }

        // Expand Storage Controllers - add their linked containers
        ExpandStorageControllers(chests, accessor, player);

        return chests;
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

        var chests = ScanAccessibleContainers(player);

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

            // Register a timeout in case some containers don't respond (version mismatch, incompatible mods, etc.)
            _browseTimeoutCallbackId = _clientApi.Event.RegisterCallback(OnBrowseTimeout, 3000);

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

        // Remove any positions that the server skipped (e.g., due to permission denial)
        if (msg.SkippedPositions != null)
        {
            foreach (var pos in msg.SkippedPositions)
            {
                _pendingPositions.Remove(pos);
                _openedContainers.RemoveAll(c => c.Pos.Equals(pos));
            }
        }

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
    ///
    /// Both ends are identified by Pos, not by inventory ID. An inventory with a block
    /// position is a block container; the player's own inventories have none. The ID is
    /// the wrong handle for this: it comes from the block's inventoryClassName, so
    /// chest.json, chest-trunk.json, chest-labeled.json and storagevessel.json all read
    /// "chest" while the stationary basket reads "basket" - and a fixed list of ID
    /// prefixes silently misses every container not on it. A basket left both ends of the
    /// test unrecognised, so shift-clicking out of the browser routed items into a basket
    /// rather than into the player, and out of a basket into any other container.
    ///
    /// Pos is also exactly what GetBestSuitedSlot_ContainerPreference keys its lift on, so
    /// the set of inventories lifted above the player is now the same set blocked from
    /// receiving from another container. That pairing is the point: the lift is what makes
    /// an unblocked container beat the player's own pack.
    ///
    /// Scoped to a shift-click, because container-to-container is exactly what the game's
    /// own automation does and it must keep working. BlockEntityItemFlow.TryPullFrom asks a
    /// chute for the best slot with an adjacent chest's slot as the source - both
    /// positioned - and BECrate, BETrough and BehaviorContainer do the same shape of thing.
    /// Every one of them calls GetBestSuitedSlot with a null op, and the chute builds its
    /// own transfer op with no modifier keys, so ShiftDown separates a player moving items
    /// by hand from a machine moving them on its own.
    ///
    /// ShiftDown rather than ActingPlayer: the shift-click path sets both, but only one of
    /// them exists on a headless server with nobody connected, which is where this is
    /// tested.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.GetBestSuitedSlot),
        new Type[] {typeof(ItemSlot), typeof(ItemStackMoveOperation), typeof(List<ItemSlot>)})]
    public static bool GetBestSuitedSlot_BlockContainerToContainer(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots,
        InventoryBase __instance, ref WeightedSlot __result)
    {
        // Hoppers, chutes and troughs move items between containers on their own - leave
        // them alone. Only a shift-click is redirected.
        if (op == null || !op.ShiftDown) return true;

        // Source is a block container and so is the destination - refuse, so the item
        // falls through to the player's own inventory
        if (sourceSlot?.Inventory is InventoryBase source && source.Pos != null && __instance.Pos != null)
        {
            __result = new WeightedSlot();
            return false;
        }

        return true;
    }

    // Weight modifiers layered on top of the vanilla suitability score.
    //
    // Vanilla containers (BEGenericTypedContainer, BECrate, BEBarrel) all use
    // BaseWeight 1 and add +1 when the source is player inventory, so they score
    // 5 for merging into a matching stack and 3 for claiming a new slot. That
    // 2.0 gap is the budget every modifier here has to fit inside: as long as
    // the modifiers sum to less than 2.0, a merge always beats a new slot and
    // these adjustments only order otherwise-equivalent targets.
    //
    // Weights are never assigned outright - only added to - so the vanilla
    // merge/new distinction survives.
    // Resolved container type code per inventory, e.g. "trunk", "chest", "labeledchest", "crate".
    //
    // Keyed on the inventory instance rather than the position: a block entity gets a fresh
    // inventory when the block is broken and replaced, so a stale entry can never outlive the
    // block it described.
    private static readonly ConditionalWeakTable<InventoryBase, string> _containerTypeCache = new();

    /// <summary>
    /// Resolve the container type code used by the priority list - the first part of the
    /// block code, e.g. "trunk", "chest", "labeledchest", "crate", "basket", "barrel".
    ///
    /// This has to come from the block, not the inventory: chest.json, chest-trunk.json and
    /// chest-labeled.json all declare inventoryClassName "chest", so every one of them has an
    /// inventory ID of "chest-x/y/z" and they are indistinguishable at the inventory layer -
    /// which is exactly the distinction the priority list needs to express.
    ///
    /// Returns null for inventories that are not block containers (the player's own
    /// inventories have no Pos).
    /// </summary>
    private static string GetContainerTypeCode(InventoryBase inv)
    {
        var pos = inv?.Pos;
        if (pos == null) return null;

        if (_containerTypeCache.TryGetValue(inv, out var cached)) return cached;

        var block = inv.Api?.World?.BlockAccessor?.GetBlock(pos);
        var code = block?.Code?.FirstCodePart();

        // An unloaded or freshly-broken block reads as air. Don't cache that - the real block
        // may resolve on a later call, and a cached "air" would never be corrected.
        if (code == null || block.Id == 0) return null;

        _containerTypeCache.Add(inv, code);
        return code;
    }

    // Per-player container priority, server side, keyed by player UID. Populated by
    // ContainerPriorityMessage and dropped on disconnect. The client keeps its own list in
    // _config; in singleplayer both sides exist in one process, so lookups are resolved by
    // EnumAppSide rather than by which API happens to be non-null.
    private static readonly Dictionary<string, List<string>> _serverPriorities = new();

    // Largest priority list accepted from a client, to bound what an untrusted packet can
    // make the server hold and walk on every suitability check.
    private const int MaxPriorityEntries = 32;

    /// <summary>
    /// Normalise a priority list: trim, lowercase, drop blanks and duplicates, and bound the
    /// length.
    ///
    /// Both sides must run this over the same input, or they compute different bonuses and
    /// pick different containers - a hand-edited config containing "Trunk" would otherwise
    /// match on the server, which normalises, and not on the client, which would not.
    /// </summary>
    private static List<string> NormalizePriority(List<string> types)
    {
        var cleaned = new List<string>();
        if (types == null) return cleaned;

        foreach (var type in types)
        {
            var token = type?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(token) || cleaned.Contains(token)) continue;

            cleaned.Add(token);
            if (cleaned.Count >= MaxPriorityEntries) break;
        }

        return cleaned;
    }

    /// <summary>
    /// The container priority list in effect for the player performing a move, or null if
    /// they have no preference.
    /// </summary>
    private static List<string> GetPriorityFor(IPlayer player, EnumAppSide? side)
    {
        // An inventory with no Api gives no reliable way to tell which side's list applies,
        // and guessing wrong is exactly the divergence this lookup exists to avoid
        if (player?.PlayerUID == null || side == null) return null;

        if (side == EnumAppSide.Server)
            return _serverPriorities.TryGetValue(player.PlayerUID, out var stored) ? stored : null;

        // On the client the acting player is always the local player
        return _config?.ContainerPriority;
    }

    /// <summary>
    /// Rank bonus for a container type: highest for the first list entry, tapering to a
    /// positive minimum for the last, and zero for anything unlisted - so every listed type
    /// outranks every unlisted one.
    ///
    /// The maximum is deliberately below the 2.0 gap between a vanilla merge (5) and a
    /// vanilla new slot (3), so priority can never promote claiming a fresh slot over topping
    /// up a stack that already exists. Priority orders equally-good targets; it does not
    /// override merging.
    /// </summary>
    private static float GetPriorityBonus(string typeCode, List<string> priority)
    {
        if (typeCode == null || priority == null || priority.Count == 0) return 0f;

        int index = priority.IndexOf(typeCode);
        if (index < 0) return 0f;

        return RankBonusMax * (priority.Count - index) / priority.Count;
    }

    private const float RankBonusMax = 1.5f;              // first entry in the priority list
    private const float ContainerPreferenceBonus = 1.5f;  // any block container, over the player's own inventory
    private const float CrateMatchBonus = 0.5f;           // crate already holding this item type
    private const float EmptyCratePenalty = 1.0f;         // claiming a wholly-empty single-type container
    private const float PositionEpsilon = 0.01f;          // deterministic tiebreak between containers

    /// <summary>
    /// Harmony postfix to handle crate shift-click targeting:
    /// - Crates with matching items: small bonus, so they win over an equivalent chest merge
    /// - Wholly-empty crates: penalised below any container that already has room,
    ///   because claiming an empty crate monopolises it for a single item type
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
            // Item matches - nudge ahead of an equivalent merge into a chest
            __result.weight += CrateMatchBonus;
            if (_debugLogging)
                _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - MATCH ({srcItem} matches {existingItem}), weight {originalWeight} -> {__result.weight}");
        }
        else
        {
            // Crate is wholly empty - claiming it locks it to one item type, so rank it
            // below any container that already has room for this stack
            __result.weight -= EmptyCratePenalty;
            if (_debugLogging)
                _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - EMPTY crate, weight {originalWeight} -> {__result.weight}");
        }
    }

    // Bounds that InWorldContainer.GetPerishRate() clamps its own result to. The perish
    // bonus is normalised against them rather than against 1.0, so it stays meaningful when
    // every candidate is above 1.0.
    private const float MinPerishRate = 0.1f;
    private const float MaxPerishRate = 2.4f;
    private const float PerishBonusMax = 10f;             // dominates type priority by design

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

        // Only block containers are compared on how well they preserve food. Player
        // inventories have no position and report a placeholder rate of exactly 1.0, which
        // the normalised curve would turn into a large bonus - enough for a backpack to beat
        // a chest. The old clamped formula happened to map 1.0 to zero and hid this.
        if (__instance.Pos == null)
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

        // Lower perish rate = higher weight, normalised across the whole range of rates the
        // game can produce.
        //
        // This used to be Math.Max(0f, (1f - perishRate) * 10f), which gave every rate at or
        // above 1.0 the same zero bonus. In a warm climate that is every container in the
        // world - a chest and a storage vessel in the same room report 2.4 and 1.8, both
        // clamped to zero - so the preference silently did nothing precisely where good
        // storage matters most. Normalising instead of clamping keeps it monotonic, so the
        // vessel still wins even though both containers are "bad" in absolute terms.
        float rate = GameMath.Clamp(perishRate, MinPerishRate, MaxPerishRate);
        float bonus = PerishBonusMax * (MaxPerishRate - rate) / (MaxPerishRate - MinPerishRate);
        __result.weight += bonus;

        if (_debugLogging)
        {
            var side = __instance.Api?.Side.ToString() ?? "unknown";
            _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - PERISH item, rate={perishRate:F2}, bonus={bonus:F1}, newWeight={__result.weight:F1}");
        }
    }

    /// <summary>
    /// Harmony postfix lifting every block container above the player's own inventory, and
    /// adding a tiny deterministic offset derived from the container's position so that no
    /// two containers can ever score exactly equal.
    ///
    /// The lift is needed because the player's backpack competes in the same ranking -
    /// TryTransferAway is called with onlyPlayerInventory false - and a backpack merge scores
    /// 3, exactly what a container scores for a new slot. Without the lift, EmptyCratePenalty
    /// would push an empty crate below the player's own inventory, so shift-clicking an item
    /// you already carry would keep it in your pack rather than putting it in the crate.
    ///
    /// It is applied uniformly to every positioned inventory, so the relative order among
    /// containers is unchanged - a firepit still outranks a chest by exactly as much as before.
    /// Only the container-versus-player-inventory boundary moves.
    ///
    /// The tiebreak matters because the destination of a shift-click is never transmitted -
    /// Packet_ActivateInventorySlot carries only the clicked source slot, and the server
    /// re-derives the destination by running the same weighting itself. PlayerInventoryManager
    /// picks the best slot with a strictly-greater comparison over an insertion-ordered
    /// dictionary, so a tie resolves to whichever inventory was opened last - an order the
    /// client and server do not reliably share, since openable containers are opened via an
    /// async OnPlayerRightClick round-trip. A tie would therefore let the two sides choose
    /// different containers.
    ///
    /// The offset is derived from block position alone, so both sides compute the same value,
    /// and is far smaller than any real weight difference.
    ///
    /// This matters because the destination of a shift-click is never transmitted -
    /// Packet_ActivateInventorySlot carries only the clicked source slot, and the server
    /// re-derives the destination by running the same weighting itself. PlayerInventoryManager
    /// picks the best slot with a strictly-greater comparison over an insertion-ordered
    /// dictionary, so a tie resolves to whichever inventory was opened last - an order the
    /// client and server do not reliably share, since openable containers are opened via an
    /// async OnPlayerRightClick round-trip. A tie would therefore let the two sides choose
    /// different containers.
    ///
    /// The offset is derived from block position alone, so both sides compute the same value,
    /// and is far smaller than any real weight difference.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.GetBestSuitedSlot),
        new Type[] {typeof(ItemSlot), typeof(ItemStackMoveOperation), typeof(List<ItemSlot>)})]
    public static void GetBestSuitedSlot_ContainerPreference(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots,
        InventoryBase __instance, ref WeightedSlot __result)
    {
        if (__result.slot == null) return;

        // Player inventories have no position - they get neither the lift nor a tiebreak
        var pos = __instance.Pos;
        if (pos == null) return;

        var typeCode = GetContainerTypeCode(__instance);
        var priority = GetPriorityFor(op?.ActingPlayer, __instance.Api?.Side);
        float rankBonus = GetPriorityBonus(typeCode, priority);

        int hash = ((pos.X * 73856093) ^ (pos.Y * 19349663) ^ (pos.Z * 83492791)) & 0x7fffffff;
        __result.weight += ContainerPreferenceBonus + rankBonus + hash % 1000 / 1000f * PositionEpsilon;

        if (_debugLogging)
        {
            var side = __instance.Api?.Side.ToString() ?? "unknown";
            _api?.Logger.Debug($"[PackRat] [{side}] GetBestSuitedSlot: {__instance.InventoryID} - type '{typeCode ?? "unresolved"}', rank bonus {rankBonus:F2}, weight {__result.weight:F3}");
        }
    }
}
