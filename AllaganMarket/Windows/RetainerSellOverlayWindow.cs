using System;
using System.Globalization;

using AllaganMarket.Mediator;
using AllaganMarket.Models;
using AllaganMarket.Services;
using AllaganMarket.Services.Interfaces;
using AllaganMarket.Settings;

using DalaMock.Host.Mediator;
using DalaMock.Shared.Interfaces;

using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AllaganMarket.Windows;

public class RetainerSellOverlayWindow : OverlayWindow
{
    private readonly ICharacterMonitorService characterMonitorService;
    private readonly SaleTrackerService saleTrackerService;
    private readonly IClientState clientState;
    private readonly Configuration configuration;
    private readonly IFont font;
    private readonly ExcelSheet<Item> itemSheet;
    private readonly RetainerOverlayCollapsedSetting overlayCollapsedSetting;
    private readonly IInventoryService inventoryService;
    private readonly ShowRetainerOverlaySetting retainerOverlaySetting;
    private readonly IRetainerMarketService retainerMarketService;
    private readonly UndercutService undercutService;
    private readonly LocalizationService localization;

    public RetainerSellOverlayWindow(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog logger,
        MediatorService mediator,
        ImGuiService imGuiService,
        ICharacterMonitorService characterMonitorService,
        SaleTrackerService saleTrackerService,
        IClientState clientState,
        Configuration configuration,
        IFont font,
        ExcelSheet<Item> itemSheet,
        RetainerOverlayCollapsedSetting overlayCollapsedSetting,
        IInventoryService inventoryService,
        ShowRetainerOverlaySetting retainerOverlaySetting,
        IRetainerMarketService retainerMarketService,
        UndercutService undercutService,
        LocalizationService localization)
        : base(addonLifecycle, gameGui, logger, mediator, imGuiService, "Retainer Sell Overlay")
    {
        this.characterMonitorService = characterMonitorService;
        this.saleTrackerService = saleTrackerService;
        this.clientState = clientState;
        this.configuration = configuration;
        this.font = font;
        this.itemSheet = itemSheet;
        this.overlayCollapsedSetting = overlayCollapsedSetting;
        this.inventoryService = inventoryService;
        this.retainerOverlaySetting = retainerOverlaySetting;
        this.retainerMarketService = retainerMarketService;
        this.undercutService = undercutService;
        this.localization = localization;
        this.AttachAddon("RetainerSell", AttachPosition.Right);
        this.Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;
        this.RespectCloseHotkey = false;
    }

    public bool IsCollapsed
    {
        get => this.overlayCollapsedSetting.CurrentValue(this.configuration);

        set => this.overlayCollapsedSetting.UpdateFilterConfiguration(this.configuration, value);
    }

    public unsafe uint CurrentItemId
    {
        get
        {
            var selectedItem = this.inventoryService.GetInventorySlot(InventoryType.BlockedItems, 0);
            return selectedItem == null ? 0 : selectedItem->ItemId;
        }
    }

    public unsafe InventoryItem.ItemFlags? CurrentItemFlags
    {
        get
        {
            var selectedItem = this.inventoryService.GetInventorySlot(InventoryType.BlockedItems, 0);
            return selectedItem == null ? null : selectedItem->Flags;
        }
    }

    public Item? CurrentItem => this.itemSheet.GetRow(this.CurrentItemId);

    public SaleItem? CurrentSaleItem => this.saleTrackerService.GetSaleItem(
        this.CurrentItemId,
        this.characterMonitorService.ActiveRetainer?.WorldId ?? null);

    public override bool DrawConditions()
    {
        return this.retainerOverlaySetting.CurrentValue(this.configuration) && base.DrawConditions();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        if (this.IsOpen && this.characterMonitorService.ActiveRetainerId == 0 && this.CurrentItemId != 0)
        {
            this.IsOpen = false;
        }
    }

    public override void Draw()
    {
        var collapsed = this.IsCollapsed;

        var currentCursorPosX = ImGui.GetCursorPosX();

        this.SizeConstraints = new WindowSizeConstraints()
        {
            MaximumSize = new Vector2(340, 800) * ImGui.GetIO().FontGlobalScale,
        };

        if (collapsed && ImGuiService.DrawIconButton(this.font, FontAwesomeIcon.ChevronRight, ref currentCursorPosX))
        {
            this.IsCollapsed = false;
        }

        if (!collapsed && ImGuiService.DrawIconButton(this.font, FontAwesomeIcon.ChevronLeft, ref currentCursorPosX))
        {
            this.IsCollapsed = true;
        }

        if (collapsed)
        {
            return;
        }

        ImGui.SameLine();
        this.localization.RefreshFromConfiguration();
        ImGui.Text(this.localization.Get("Overlay.Title"));

        ImGui.SameLine();

        currentCursorPosX = ImGui.GetWindowSize().X;

        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Bars,
                ref currentCursorPosX,
                this.localization.Get("Overlay.OpenMainWindow"),
                true))
        {
            this.MediatorService.Publish(new ToggleWindowMessage(typeof(MainWindow)));
        }

        ImGui.SameLine();

        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Cog,
                ref currentCursorPosX,
                this.localization.Get("Overlay.OpenConfigWindow"),
                true))
        {
            this.MediatorService.Publish(new ToggleWindowMessage(typeof(ConfigWindow)));
        }

        ImGui.Separator();

        if (this.retainerMarketService.InBadState)
        {
            ImGui.PushTextWrapPos();
            ImGui.Text(
                this.localization.Get("Overlay.ReloadedMessage"));
            ImGui.PopTextWrapPos();
            return;
        }

        var activeRetainer = this.characterMonitorService.ActiveRetainer;
        var currentItem = this.CurrentItem;
        var currentSaleItem = this.CurrentSaleItem;
        if (this.clientState.IsLoggedIn && activeRetainer != null && currentItem != null)
        {
            bool? isHq = this.CurrentItemFlags switch
            {
                null => null,
                var flags => flags.Value.HasFlag(InventoryItem.ItemFlags.HighQuality),
            };

            var recommendedUnitPrice = this.undercutService.GetRecommendedUnitPrice(activeRetainer.WorldId, currentItem.Value.RowId, isHq ?? false, 1, false);
            var lastUpdated = this.undercutService.GetLastUpdateTime(activeRetainer.WorldId, currentItem.Value.RowId);
            var marketCache = this.undercutService.GetMarketPriceCache(activeRetainer.WorldId, currentItem.Value.RowId, isHq);
            if (marketCache == null)
            {
                marketCache = this.undercutService.GetMarketPriceCache(activeRetainer.WorldId, currentItem.Value.RowId, null);
            }

            var recommendedPrice = recommendedUnitPrice == null ? this.localization.Get("Overlay.NoData") : recommendedUnitPrice.Value.Amount.ToString();

            using (ImRaii.Table("ItemList", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Name"), ImGuiTableColumnFlags.WidthFixed, 120 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 200 * ImGui.GetIO().FontGlobalScale);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Name") + ": ");
                ImGui.TableNextColumn();
                ImGui.Text($"{currentItem.Value.Name}");

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.RecommendedUnitPrice") + ": ");

                ImGui.TableNextColumn();
                ImGui.Text($"{recommendedPrice}");
                ImGui.SameLine();
                using (ImRaii.Disabled(recommendedUnitPrice == null))
                {
                    if (ImGui.SmallButton(this.localization.Get("Overlay.CopyToGame")))
                    {
                        if (recommendedUnitPrice != null)
                        {
                            var retainerSellPtr = this.GameGui.GetAddonByName("RetainerSell");
                            if (retainerSellPtr != IntPtr.Zero)
                            {
                                unsafe
                                {
                                    var retainerSellAddon = (AddonRetainerSell*)retainerSellPtr.Address;
                                    retainerSellAddon->AskingPrice->SetValue((int)recommendedUnitPrice.Value.Amount);
                                }
                            }
                        }
                    }
                }

                if (recommendedUnitPrice?.UsedFallback ?? false)
                {
                    ImGui.SameLine();
                    this.ImGuiService.HelpMarker(
                            this.localization.Get("Overlay.FallbackTooltip"),
                            textColor: ImGuiColors.DalamudYellow);
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.UpdatedAt") + ": ");
                ImGui.TableNextColumn();
                ImGui.Text($"{lastUpdated?.ToString(CultureInfo.CurrentCulture) ?? this.localization.Get("Overlay.NoData")}");

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.ListedAt") + ": ");
                ImGui.TableNextColumn();
                ImGui.Text($"{currentSaleItem?.ListedAt.ToString(CultureInfo.CurrentCulture) ?? this.localization.Get("Overlay.NotAvailable")}");
            }

            using (ImRaii.PushFont(this.font.IconFont))
            {
                var contentRegion = ImGui.GetWindowSize();
                var padding = ImGui.GetStyle().WindowPadding;
                var infoText = $"{FontAwesomeIcon.InfoCircle.ToIconString()}";
                var iconSize = ImGui.CalcTextSize(infoText);
                ImGui.SetCursorPos(new Vector2(contentRegion.X - iconSize.X - padding.X, contentRegion.Y - iconSize.Y - padding.X));
                ImGui.Text(infoText);
            }

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.Text(this.localization.Format("Overlay.SourcedFrom", marketCache?.GetFormattedType() ?? this.localization.Get("Overlay.NotAvailable")));
                }
            }
        }
        else
        {
            ImGui.Text(this.localization.Get("Overlay.PleaseLogin"));
        }
    }
}
