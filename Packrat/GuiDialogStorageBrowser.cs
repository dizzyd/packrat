using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Packrat;

/// <summary>
/// A unified browser dialog for viewing and searching multiple container inventories at once.
/// </summary>
public class GuiDialogStorageBrowser : GuiDialog
{
    public override string ToggleKeyCombinationCode => null;
    public override double DrawOrder => 0.2;
    public override bool PrefersUngrabbedMouse => false;

    private readonly SortedInventoryView _sortedInventory;
    private readonly List<BlockEntityContainer> _containers;
    private readonly ICoreClientAPI _capi;
    private readonly Action<SortMode> _onSortModeChanged;

    private const int Cols = 10;
    private const int MaxVisibleRows = 8;
    private const string DialogName = "packrat-storage-browser";

    // Store inset bounds for scissor clipping in OnRenderGUI
    private ElementBounds _insetBounds;

    // Search filter
    private string _searchFilter = "";
    private List<EnumBlockMaterial> _matchedMaterials = new();
    private List<EnumFoodCategory> _matchedFoodCategories = new();

    // Dimming texture for non-matching slots
    private LoadedTexture _dimTexture;

    // Flag to suppress key press after focusing search
    private bool _suppressNextKeyPress;

    // Track slot count for dynamic recomposition
    private int _lastSlotCount;

    // Colors for container outlines (cycle through these) - RGB values
    private static readonly double[][] ContainerColors = new double[][]
    {
        new[] { 0.3, 0.5, 1.0 },   // Blue
        new[] { 0.3, 0.9, 0.4 },   // Green
        new[] { 1.0, 0.7, 0.2 },   // Orange
        new[] { 1.0, 0.3, 0.5 },   // Pink
        new[] { 0.7, 0.4, 1.0 },   // Purple
        new[] { 0.2, 0.9, 0.9 },   // Cyan
        new[] { 0.9, 0.9, 0.2 },   // Yellow
        new[] { 1.0, 0.5, 0.3 },   // Coral
    };

    // Sort mode dropdown options
    private static readonly string[] SortModeNames = { "None", "A-Z", "Category", "Material" };

    public GuiDialogStorageBrowser(
        ICoreClientAPI capi,
        SortedInventoryView sortedInventory,
        List<BlockEntityContainer> containers,
        Action<SortMode> onSortModeChanged = null)
        : base(capi)
    {
        _capi = capi;
        _sortedInventory = sortedInventory;
        _containers = containers;
        _onSortModeChanged = onSortModeChanged;

        // Create a 1x1 semi-transparent black texture for dimming
        int dimColor = (220 << 24) | (0 << 16) | (0 << 8) | 0; // ARGB: 86% opacity black
        var dimBitmap = new BakedBitmap { TexturePixels = new[] { dimColor }, Width = 1, Height = 1 };
        _dimTexture = new LoadedTexture(capi);
        _capi.Render.LoadTexture(dimBitmap, ref _dimTexture, false, 0, false);

        // Make dialog movable by default (set initial position if none stored)
        if (_capi.Gui.GetDialogPosition(DialogName) == null)
        {
            // Calculate approximate dialog size for centering
            int totalSlots = _sortedInventory.Count;
            int totalRows = Math.Max(1, (int)Math.Ceiling(totalSlots / (float)Cols));
            int visibleRows = Math.Max(1, Math.Min(totalRows, MaxVisibleRows));
            bool needsScrollbar = totalRows > visibleRows;

            double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
            double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
            double elemToDlgPad = GuiStyle.ElementToDialogPadding;
            double titleBarHeight = 25;
            double searchBoxHeight = 30;

            double gridWidth = (slotSize + pad) * Cols;
            double gridHeight = (slotSize + pad) * visibleRows;
            double scrollbarWidth = needsScrollbar ? 20 : 0;

            double dialogWidth = gridWidth + 12 + elemToDlgPad * 2 + scrollbarWidth;
            double dialogHeight = titleBarHeight + searchBoxHeight + 8 + gridHeight + 12 + elemToDlgPad * 2;

            int x = (int)((_capi.Render.FrameWidth - dialogWidth * RuntimeEnv.GUIScale) / 2 / RuntimeEnv.GUIScale);
            int y = (int)((_capi.Render.FrameHeight - dialogHeight * RuntimeEnv.GUIScale) / 2 / RuntimeEnv.GUIScale);
            _capi.Gui.SetDialogPosition(DialogName, new Vec2i(x, y));
        }

        ComposeDialog();
    }

    private void ComposeDialog()
    {
        int totalSlots = _sortedInventory.Count;
        int totalRows = Math.Max(1, (int)Math.Ceiling(totalSlots / (float)Cols));
        int visibleRows = Math.Max(1, Math.Min(totalRows, MaxVisibleRows));
        bool needsScrollbar = totalRows > visibleRows;

        double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double elemToDlgPad = GuiStyle.ElementToDialogPadding;

        // Search box and sort dropdown dimensions
        double controlRowHeight = 30;
        double gridWidth = (slotSize + pad) * Cols;
        double dropdownWidth = 90;
        double searchBoxWidth = gridWidth + 12 - dropdownWidth - 8; // Leave room for dropdown

        // Background bounds - child elements will be positioned relative to this
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(elemToDlgPad);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        // Title bar height
        double titleBarHeight = 25;

        // Search box bounds - below title bar
        ElementBounds searchBounds = ElementBounds.Fixed(0, titleBarHeight, searchBoxWidth, controlRowHeight);

        // Sort dropdown bounds - to the right of search box
        ElementBounds sortDropdownBounds = ElementBounds.Fixed(searchBoxWidth + 8, titleBarHeight, dropdownWidth, controlRowHeight);

        // Slot grid bounds - positioned below search box
        ElementBounds slotGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, pad, titleBarHeight + controlRowHeight + 8 + pad, Cols, visibleRows);

        // Full grid bounds - for scrolling (total height)
        ElementBounds fullGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 0, Cols, totalRows);

        // Inset bounds around slot grid
        ElementBounds insetBounds = slotGridBounds.ForkBoundingParent(6, 6, 6, 6);
        _insetBounds = insetBounds; // Store for scissor clipping

        string title = Lang.Get($"{PackratModSystem.ModId}:browser-title");
        string searchPlaceholder = Lang.Get($"{PackratModSystem.ModId}:search-placeholder");

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        // Build the composer - scrollbar branch uses clipping, non-scrollbar doesn't
        var composer = _capi.Gui
            .CreateCompo(DialogName, dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, OnTitleBarClose)
            .BeginChildElements(bgBounds)
                .AddTextInput(searchBounds, OnSearchTextChanged, CairoFont.WhiteSmallText(), "searchbox")
                .AddDropDown(SortModeNames, SortModeNames, (int)_sortedInventory.SortMode, OnSortModeSelected, sortDropdownBounds, "sortdropdown")
                .AddInset(insetBounds);

        if (needsScrollbar)
        {
            ElementBounds clippingBounds = slotGridBounds.CopyOffsetedSibling();
            clippingBounds.fixedHeight -= 3;
            ElementBounds scrollbarBounds = ElementStdBounds.VerticalScrollbar(insetBounds);
            ElementBounds outlineBounds = fullGridBounds.CopyOffsetedSibling();

            composer
                .AddVerticalScrollbar(OnScrollbarNewValue, scrollbarBounds, "scrollbar")
                .BeginClip(clippingBounds)
                    .AddItemSlotGrid(_sortedInventory, DoSendPacket, Cols, fullGridBounds, "slotgrid")
                    .AddDynamicCustomDraw(outlineBounds, DrawContainerOutlines, "outlines")
                .EndClip();
        }
        else
        {
            ElementBounds outlineBounds = slotGridBounds.CopyOffsetedSibling();

            composer
                .AddItemSlotGrid(_sortedInventory, DoSendPacket, Cols, slotGridBounds, "slotgrid")
                .AddDynamicCustomDraw(outlineBounds, DrawContainerOutlines, "outlines");
        }

        SingleComposer = composer.EndChildElements().Compose();

        if (needsScrollbar)
        {
            SingleComposer.GetScrollbar("scrollbar").SetHeights(
                (float)slotGridBounds.fixedHeight,
                (float)(fullGridBounds.fixedHeight + pad)
            );
        }

        SingleComposer.GetTextInput("searchbox").SetPlaceHolderText(searchPlaceholder);
        SingleComposer.UnfocusOwnElements();

        // Track slot count for dynamic recomposition
        _lastSlotCount = totalSlots;
    }

    private void OnSortModeSelected(string code, bool selected)
    {
        int index = Array.IndexOf(SortModeNames, code);
        if (index < 0) return;

        var newMode = (SortMode)index;
        if (newMode == _sortedInventory.SortMode) return;

        _sortedInventory.SortMode = newMode;
        _onSortModeChanged?.Invoke(newMode);

        // Recompose the dialog since slot count may have changed
        ComposeDialog();
    }

    private void DrawContainerOutlines(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        // Skip container outlines when sorting is active
        if (_sortedInventory.IsSorting) return;

        double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double cellSize = slotSize + pad;

        // Scale for current GUI scale
        double scale = RuntimeEnv.GUIScale;
        cellSize *= scale;

        int colorIndex = 0;
        foreach (var (startIndex, count) in _sortedInventory.Underlying.ContainerBoundaries)
        {
            var color = ContainerColors[colorIndex % ContainerColors.Length];

            // Calculate the bounding rectangle for this container's slots
            int startRow = startIndex / Cols;
            int startCol = startIndex % Cols;
            int endIndex = startIndex + count - 1;
            int endRow = endIndex / Cols;
            int endCol = endIndex % Cols;

            // Draw a colored background for each row segment of this container
            for (int row = startRow; row <= endRow; row++)
            {
                int rowStartCol = (row == startRow) ? startCol : 0;
                int rowEndCol = (row == endRow) ? endCol : Cols - 1;

                // Only draw if there are slots in this row
                int slotsInRow = rowEndCol - rowStartCol + 1;
                if (slotsInRow <= 0) continue;

                double x = rowStartCol * cellSize;
                double y = row * cellSize;
                double width = slotsInRow * cellSize;
                double height = cellSize;

                double radius = 6 * scale;

                // Draw filled background only
                ctx.SetSourceRGBA(color[0], color[1], color[2], 0.25);
                DrawRoundedRectangle(ctx, x, y, width, height, radius);
                ctx.Fill();
            }

            colorIndex++;
        }
    }

    private void DrawRoundedRectangle(Context ctx, double x, double y, double width, double height, double radius)
    {
        ctx.MoveTo(x + radius, y);
        ctx.LineTo(x + width - radius, y);
        ctx.Arc(x + width - radius, y + radius, radius, -Math.PI / 2, 0);
        ctx.LineTo(x + width, y + height - radius);
        ctx.Arc(x + width - radius, y + height - radius, radius, 0, Math.PI / 2);
        ctx.LineTo(x + radius, y + height);
        ctx.Arc(x + radius, y + height - radius, radius, Math.PI / 2, Math.PI);
        ctx.LineTo(x, y + radius);
        ctx.Arc(x + radius, y + radius, radius, Math.PI, 3 * Math.PI / 2);
        ctx.ClosePath();
    }

    private void OnTitleBarClose()
    {
        TryClose();
    }

    private void OnScrollbarNewValue(float value)
    {
        var slotGrid = SingleComposer.GetSlotGrid("slotgrid");
        if (slotGrid != null)
        {
            slotGrid.Bounds.fixedY = 3 - value;
            slotGrid.Bounds.CalcWorldBounds();
        }

        // Also move the outlines with the grid
        var outlines = SingleComposer.GetCustomDraw("outlines");
        if (outlines != null)
        {
            outlines.Bounds.fixedY = 3 - value;
            outlines.Bounds.CalcWorldBounds();
            outlines.Redraw();
        }
    }

    private void OnSearchTextChanged(string text)
    {
        _searchFilter = text?.Trim().ToLowerInvariant() ?? "";

        _matchedMaterials.Clear();
        _matchedFoodCategories.Clear();

        if (!string.IsNullOrEmpty(_searchFilter))
        {
            // Cache which EnumBlockMaterial values match the filter
            foreach (EnumBlockMaterial material in Enum.GetValues(typeof(EnumBlockMaterial)))
            {
                if (material.ToString().ToLowerInvariant().Contains(_searchFilter))
                    _matchedMaterials.Add(material);
            }

            // Cache which EnumFoodCategory values match the filter
            foreach (EnumFoodCategory category in Enum.GetValues(typeof(EnumFoodCategory)))
            {
                if (category.ToString().ToLowerInvariant().Contains(_searchFilter))
                    _matchedFoodCategories.Add(category);
            }
        }
    }

    /// <summary>
    /// Check if a slot matches the current search filter.
    /// Empty filter matches everything. Empty slots never match a non-empty filter.
    /// Matches against item name, material variants, and food category.
    /// </summary>
    private bool SlotMatchesFilter(int slotIndex)
    {
        if (string.IsNullOrEmpty(_searchFilter)) return true;

        var slot = _sortedInventory[slotIndex];
        if (slot?.Itemstack == null)
        {
            // When sorting is active, empty slots are filtered out, so no need to check crates
            if (_sortedInventory.IsSorting) return false;

            // For empty crate slots (only when not sorting), check the template item
            if (_sortedInventory.Underlying.IsSlotInCrate(slotIndex))
            {
                var templateItem = _sortedInventory.Underlying.GetCrateTemplateItem(slotIndex);
                if (templateItem != null)
                {
                    return ItemMatchesFilter(templateItem.Collectible, templateItem.GetName());
                }
            }
            return false;
        }

        return ItemMatchesFilter(slot.Itemstack.Collectible, slot.Itemstack.GetName());
    }

    /// <summary>
    /// Check if a collectible matches the search filter by name, variants, code parts, or food category.
    /// </summary>
    private bool ItemMatchesFilter(CollectibleObject collectible, string itemName)
    {
        // Check item name
        if (itemName.ToLowerInvariant().Contains(_searchFilter))
            return true;

        // Check block material (for blocks)
        if (collectible is Block block && _matchedMaterials.Count > 0)
        {
            if (_matchedMaterials.Contains(block.BlockMaterial))
                return true;
        }

        // Check food category
        if (collectible.NutritionProps != null && _matchedFoodCategories.Count > 0)
        {
            if (_matchedFoodCategories.Contains(collectible.NutritionProps.FoodCategory))
                return true;
        }

        return false;
    }

    private void DoSendPacket(object packet)
    {
        _capi.Network.SendPacketClient(packet);
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        _capi.World.PlaySoundAt(new AssetLocation("sounds/block/chestopen"), _capi.World.Player.Entity);
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();

        var player = _capi.World.Player;

        foreach (var container in _containers)
        {
            if (container?.Inventory != null && container.Inventory.HasOpened(player))
            {
                _capi.Network.SendPacketClient(container.Inventory.Close(player));
                player.InventoryManager.CloseInventory(container.Inventory);
            }
        }

        // Dispose the dimming texture
        _dimTexture?.Dispose();
        _dimTexture = null;

        _capi.World.PlaySoundAt(new AssetLocation("sounds/block/chestclose"), player.Entity);
    }

    public override void OnKeyDown(KeyEvent args)
    {
        // Focus search box on /
        if (args.KeyCode == (int)GlKeys.Slash)
        {
            var searchBox = SingleComposer?.GetTextInput("searchbox");
            if (searchBox != null && !searchBox.HasFocus)
            {
                SingleComposer.UnfocusOwnElements();
                searchBox.OnFocusGained();
                _suppressNextKeyPress = true;
                args.Handled = true;
                return;
            }
        }

        base.OnKeyDown(args);
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (_suppressNextKeyPress)
        {
            _suppressNextKeyPress = false;
            args.Handled = true;
            return;
        }

        base.OnKeyPress(args);
    }

    public override void OnRenderGUI(float deltaTime)
    {
        base.OnRenderGUI(deltaTime);

        // Check if slot count changed and recompose if needed
        int currentCount = _sortedInventory.Count;
        if (currentCount != _lastSlotCount)
        {
            ComposeDialog();
            return; // Skip rest of rendering this frame, will render properly next frame
        }

        var slotGrid = SingleComposer?.GetSlotGrid("slotgrid");
        if (slotGrid == null) return;

        double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double cellSize = slotSize + pad;
        double scale = RuntimeEnv.GUIScale;
        cellSize *= scale;
        slotSize *= scale;

        // The slot grid's absX/absY already include scroll offset (via fixedY modification)
        double gridX = slotGrid.Bounds.absX;
        double gridY = slotGrid.Bounds.absY;

        // Use inset bounds for visibility check
        double visibleTop = _insetBounds?.absY ?? double.MinValue;
        double visibleBottom = _insetBounds != null ? _insetBounds.absY + _insetBounds.OuterHeight : double.MaxValue;

        // Render ghost items for empty crate slots (only when NOT sorting)
        if (!_sortedInventory.IsSorting)
        {
            // Light ghost effect - ~15% opacity white
            int ghostColor = (40 << 24) | (255 << 16) | (255 << 8) | 255;

            for (int i = 0; i < _sortedInventory.Count; i++)
            {
                var slot = _sortedInventory[i];

                // Only render ghost for empty crate slots
                if (slot?.Itemstack != null) continue;
                if (!_sortedInventory.Underlying.IsSlotInCrate(i)) continue;

                var templateItem = _sortedInventory.Underlying.GetCrateTemplateItem(i);
                if (templateItem == null) continue;

                // Calculate slot position (gridY already has scroll applied)
                int row = i / Cols;
                int col = i % Cols;
                double slotX = gridX + col * cellSize + pad * scale / 2;
                double slotY = gridY + row * cellSize + pad * scale / 2;

                // Skip if outside visible clip area
                if (slotY + slotSize < visibleTop || slotY > visibleBottom) continue;

                // Render the ghost item using the standard item size
                double itemSize = GuiElementPassiveItemSlot.unscaledItemSize * scale;
                _capi.Render.RenderItemstackToGui(
                    new DummySlot(templateItem),
                    slotX + slotSize / 2,
                    slotY + slotSize / 2,
                    100, // z-depth
                    (float)itemSize,
                    ghostColor,
                    shading: true,
                    rotate: false,
                    showStackSize: false
                );
            }
        }

        // Render dimming overlay on non-matching slots when search is active
        if (!string.IsNullOrEmpty(_searchFilter) && _dimTexture?.TextureId > 0)
        {
            // Apply scissor clipping using the inset bounds (visible scroll area)
            if (_insetBounds != null)
            {
                _capi.Render.PushScissor(_insetBounds, true);
            }

            for (int i = 0; i < _sortedInventory.Count; i++)
            {
                if (SlotMatchesFilter(i)) continue;

                int row = i / Cols;
                int col = i % Cols;
                double slotX = gridX + col * cellSize;
                double slotY = gridY + row * cellSize;

                // Skip if fully outside visible clip area (optimization)
                if (slotY + cellSize < visibleTop || slotY > visibleBottom) continue;

                _capi.Render.Render2DTexturePremultipliedAlpha(
                    _dimTexture.TextureId,
                    (float)slotX, (float)slotY,
                    (float)cellSize, (float)cellSize,
                    500  // High z-depth to render in front of items
                );
            }

            if (_insetBounds != null)
            {
                _capi.Render.PopScissor();
            }
        }
    }
}
