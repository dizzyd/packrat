using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

    private readonly CompositeInventoryView _compositeInventory;
    private readonly List<BlockEntityContainer> _containers;
    private readonly ICoreClientAPI _capi;

    private const int Cols = 10;
    private const int MaxVisibleRows = 8;

    // Store inset bounds for scissor clipping in OnRenderGUI
    private ElementBounds _insetBounds;

    // Search filter
    private string _searchFilter = "";

    // Dimming texture for non-matching slots
    private LoadedTexture _dimTexture;

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

    public GuiDialogStorageBrowser(
        ICoreClientAPI capi,
        CompositeInventoryView compositeInventory,
        List<BlockEntityContainer> containers)
        : base(capi)
    {
        _capi = capi;
        _compositeInventory = compositeInventory;
        _containers = containers;

        // Create a 1x1 semi-transparent black texture for dimming
        int dimColor = (220 << 24) | (0 << 16) | (0 << 8) | 0; // ARGB: 86% opacity black
        var dimBitmap = new BakedBitmap { TexturePixels = new[] { dimColor }, Width = 1, Height = 1 };
        _dimTexture = new LoadedTexture(capi);
        _capi.Render.LoadTexture(dimBitmap, ref _dimTexture, false, 0, false);

        ComposeDialog();
    }

    private void ComposeDialog()
    {
        int totalSlots = _compositeInventory.Count;
        int totalRows = Math.Max(1, (int)Math.Ceiling(totalSlots / (float)Cols));
        int visibleRows = Math.Max(1, Math.Min(totalRows, MaxVisibleRows));
        bool needsScrollbar = totalRows > visibleRows;

        double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double elemToDlgPad = GuiStyle.ElementToDialogPadding;

        // Search box dimensions
        double searchBoxHeight = 30;
        double gridWidth = (slotSize + pad) * Cols;
        double searchBoxWidth = gridWidth + 12; // Match inset width (grid + 6px border each side)

        // Background bounds - child elements will be positioned relative to this
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(elemToDlgPad);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        // Title bar height
        double titleBarHeight = 25;

        // Search box bounds - below title bar
        ElementBounds searchBounds = ElementBounds.Fixed(0, titleBarHeight, searchBoxWidth, searchBoxHeight);

        // Slot grid bounds - positioned below search box
        ElementBounds slotGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, pad, titleBarHeight + searchBoxHeight + 8 + pad, Cols, visibleRows);

        // Full grid bounds - for scrolling (total height)
        ElementBounds fullGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 0, Cols, totalRows);

        // Inset bounds around slot grid
        ElementBounds insetBounds = slotGridBounds.ForkBoundingParent(6, 6, 6, 6);
        _insetBounds = insetBounds; // Store for scissor clipping

        string title = Lang.Get($"{PackratModSystem.ModId}:browser-title");
        string searchPlaceholder = Lang.Get($"{PackratModSystem.ModId}:search-placeholder");

        if (needsScrollbar)
        {
            // Clipping bounds for scrollable area
            ElementBounds clippingBounds = slotGridBounds.CopyOffsetedSibling();
            clippingBounds.fixedHeight -= 3;

            // Dialog bounds - auto-sized with extra width for scrollbar
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            // Scrollbar bounds to the right of inset
            ElementBounds scrollbarBounds = ElementStdBounds.VerticalScrollbar(insetBounds);

            // Custom draw bounds for container outlines (same as full grid)
            ElementBounds outlineBounds = fullGridBounds.CopyOffsetedSibling();

            SingleComposer = _capi.Gui
                .CreateCompo("packrat-storage-browser", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(title, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddTextInput(searchBounds, OnSearchTextChanged, CairoFont.WhiteSmallText(), "searchbox")
                    .AddInset(insetBounds)
                    .AddVerticalScrollbar(OnScrollbarNewValue, scrollbarBounds, "scrollbar")
                    .BeginClip(clippingBounds)
                        .AddItemSlotGrid(_compositeInventory, DoSendPacket, Cols, fullGridBounds, "slotgrid")
                        .AddDynamicCustomDraw(outlineBounds, DrawContainerOutlines, "outlines")
                    .EndClip()
                .EndChildElements()
                .Compose();

            SingleComposer.GetScrollbar("scrollbar").SetHeights(
                (float)slotGridBounds.fixedHeight,
                (float)(fullGridBounds.fixedHeight + pad)
            );

            SingleComposer.GetTextInput("searchbox").SetPlaceHolderText(searchPlaceholder);
        }
        else
        {
            // Dialog bounds - auto-sized (no scrollbar needed)
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            // Custom draw bounds for container outlines
            ElementBounds outlineBounds = slotGridBounds.CopyOffsetedSibling();

            SingleComposer = _capi.Gui
                .CreateCompo("packrat-storage-browser", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(title, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddTextInput(searchBounds, OnSearchTextChanged, CairoFont.WhiteSmallText(), "searchbox")
                    .AddInset(insetBounds)
                    .AddItemSlotGrid(_compositeInventory, DoSendPacket, Cols, slotGridBounds, "slotgrid")
                    .AddDynamicCustomDraw(outlineBounds, DrawContainerOutlines, "outlines")
                .EndChildElements()
                .Compose();

            SingleComposer.GetTextInput("searchbox").SetPlaceHolderText(searchPlaceholder);
        }
    }

    private void DrawContainerOutlines(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double cellSize = slotSize + pad;

        // Scale for current GUI scale
        double scale = RuntimeEnv.GUIScale;
        cellSize *= scale;

        int colorIndex = 0;
        foreach (var (startIndex, count) in _compositeInventory.ContainerBoundaries)
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
    }

    /// <summary>
    /// Check if a slot matches the current search filter.
    /// Empty filter matches everything. Empty slots never match a non-empty filter.
    /// </summary>
    private bool SlotMatchesFilter(int slotIndex)
    {
        if (string.IsNullOrEmpty(_searchFilter)) return true;

        var slot = _compositeInventory[slotIndex];
        if (slot?.Itemstack == null)
        {
            // For empty crate slots, check the template item
            if (_compositeInventory.IsSlotInCrate(slotIndex))
            {
                var templateItem = _compositeInventory.GetCrateTemplateItem(slotIndex);
                if (templateItem != null)
                {
                    string templateName = templateItem.GetName().ToLowerInvariant();
                    return templateName.Contains(_searchFilter);
                }
            }
            return false;
        }

        string itemName = slot.Itemstack.GetName().ToLowerInvariant();
        return itemName.Contains(_searchFilter);
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

    public override void OnRenderGUI(float deltaTime)
    {
        base.OnRenderGUI(deltaTime);

        // Render ghost items for empty crate slots
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

        // Light ghost effect - ~15% opacity white
        int ghostColor = (40 << 24) | (255 << 16) | (255 << 8) | 255;

        for (int i = 0; i < _compositeInventory.Count; i++)
        {
            var slot = _compositeInventory[i];

            // Only render ghost for empty crate slots
            if (slot?.Itemstack != null) continue;
            if (!_compositeInventory.IsSlotInCrate(i)) continue;

            var templateItem = _compositeInventory.GetCrateTemplateItem(i);
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

        // Render dimming overlay on non-matching slots when search is active
        if (!string.IsNullOrEmpty(_searchFilter) && _dimTexture?.TextureId > 0)
        {
            // Apply scissor clipping using the inset bounds (visible scroll area)
            if (_insetBounds != null)
            {
                _capi.Render.PushScissor(_insetBounds, true);
            }

            for (int i = 0; i < _compositeInventory.Count; i++)
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
