using System;
using System.Collections.Generic;
using System.Linq;

using AllaganMarket.Extensions;
using AllaganMarket.Mediator;
using AllaganMarket.Services;
using AllaganMarket.Services.Interfaces;
using AllaganMarket.Settings;

using DalaMock.Host.Mediator;
using DalaMock.Shared.Interfaces;

using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Math;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AllaganMarket.Windows;

public class RetainerSellListOverlayWindow : OverlayWindow
{
    private readonly ICharacterMonitorService characterMonitorService;
    private readonly SaleTrackerService saleTrackerService;
    private readonly IClientState clientState;
    private readonly Configuration configuration;
    private readonly ItemUpdatePeriodSetting updatePeriodSetting;
    private readonly IFont font;
    private readonly ExcelSheet<Item> itemSheet;
    private readonly RetainerOverlayCollapsedSetting overlayCollapsedSetting;
    private readonly ShowRetainerOverlaySetting retainerOverlaySetting;
    private readonly IRetainerMarketService retainerMarketService;
    private readonly UndercutService undercutService;
    private readonly HighlightingRetainerSellListSetting retainerSellListSetting;
    private readonly LocalizationService localization;
    private readonly IInventoryService inventoryService;
    private readonly AutoListingService autoListingService;
    private readonly AutoUndercutService autoUndercutService;
    private bool showAllItems;
    private uint batchListItemId;
    private bool batchListIsHq;
    private int batchListStackCount = 1;
    private bool batchListRefreshPrice = true;
    private string batchListSearchText = string.Empty;

    public RetainerSellListOverlayWindow(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog logger,
        MediatorService mediator,
        ImGuiService imGuiService,
        ICharacterMonitorService characterMonitorService,
        SaleTrackerService saleTrackerService,
        IClientState clientState,
        Configuration configuration,
        ItemUpdatePeriodSetting updatePeriodSetting,
        IFont font,
        ExcelSheet<Item> itemSheet,
        RetainerOverlayCollapsedSetting overlayCollapsedSetting,
        ShowRetainerOverlaySetting retainerOverlaySetting,
        IRetainerMarketService retainerMarketService,
        UndercutService undercutService,
        HighlightingRetainerSellListSetting retainerSellListSetting,
        LocalizationService localization,
        IInventoryService inventoryService,
        AutoListingService autoListingService,
        AutoUndercutService autoUndercutService)
        : base(addonLifecycle, gameGui, logger, mediator, imGuiService, "Retainer Sell List Overlay")
    {
        this.characterMonitorService = characterMonitorService;
        this.saleTrackerService = saleTrackerService;
        this.clientState = clientState;
        this.configuration = configuration;
        this.updatePeriodSetting = updatePeriodSetting;
        this.font = font;
        this.itemSheet = itemSheet;
        this.overlayCollapsedSetting = overlayCollapsedSetting;
        this.retainerOverlaySetting = retainerOverlaySetting;
        this.retainerMarketService = retainerMarketService;
        this.undercutService = undercutService;
        this.retainerSellListSetting = retainerSellListSetting;
        this.localization = localization;
        this.inventoryService = inventoryService;
        this.autoListingService = autoListingService;
        this.autoUndercutService = autoUndercutService;
        this.AttachAddon("RetainerSellList", AttachPosition.Right);
        this.Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;
        this.RespectCloseHotkey = false;
    }

    public bool IsCollapsed
    {
        get => this.overlayCollapsedSetting.CurrentValue(this.configuration);

        set => this.overlayCollapsedSetting.UpdateFilterConfiguration(this.configuration, value);
    }

    public override bool DrawConditions()
    {
        return this.retainerOverlaySetting.CurrentValue(this.configuration) && base.DrawConditions();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        if (this.IsOpen && this.characterMonitorService.ActiveRetainerId == 0)
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
            MaximumSize = new Vector2(450, 800) * ImGui.GetIO().FontGlobalScale,
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

        ImGui.SameLine();

        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Eye,
                ref currentCursorPosX,
                this.localization.Get("Overlay.ShowHideItems"),
                true,
                this.showAllItems ? null : ImGuiColors.ParsedGrey))
        {
            this.showAllItems = !this.showAllItems;
        }

        ImGui.SameLine();

        var retainerHighlighting = this.retainerSellListSetting.CurrentValue(this.configuration);
        if (ImGuiService.DrawIconButton(
                this.font,
                FontAwesomeIcon.Lightbulb,
                ref currentCursorPosX,
                this.localization.Get("Overlay.ToggleRetainerSellListHighlighting"),
                true,
                retainerHighlighting ? null : ImGuiColors.ParsedGrey))
        {
            this.retainerSellListSetting.UpdateFilterConfiguration(this.configuration, !retainerHighlighting);
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
        if (this.clientState.IsLoggedIn && activeRetainer != null)
        {
            var saleItems = this.saleTrackerService.GetRetainerSales(activeRetainer.CharacterId)
                                ?.Where(c => !c.IsEmpty()).SortByRetainerMarketOrder().ToList();
            var interval = this.updatePeriodSetting.CurrentValue(this.configuration);
            var itemsToCheck = false;

            using (ImRaii.Table("ItemList", 5, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Name"), ImGuiTableColumnFlags.WidthStretch, 140 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Slot"), ImGuiTableColumnFlags.WidthFixed, 40 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.RecommendedPrice"), ImGuiTableColumnFlags.WidthFixed, 80 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.Undercut"), ImGuiTableColumnFlags.WidthFixed, 70 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableSetupColumn(this.localization.Get("Overlay.StalePricing"), ImGuiTableColumnFlags.WidthFixed, 110 * ImGui.GetIO().FontGlobalScale);
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Name"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Slot"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.RecommendedPrice"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.Undercut"));
                ImGui.TableNextColumn();
                ImGui.Text(this.localization.Get("Overlay.StalePricing"));
                if (saleItems != null)
                {
                    for (var index = 0; index < saleItems.Count; index++)
                    {
                        var saleItem = saleItems[index];
                        var isUnderCut = this.undercutService.IsItemUndercut(saleItem) ?? false;
                        var needsUpdate = this.undercutService.NeedsUpdate(saleItem, interval);
                        if (!isUnderCut && !needsUpdate && !this.showAllItems)
                        {
                            continue;
                        }

                        var recommendedUnitPrice = this.undercutService.GetRecommendedUnitPrice(saleItem);
                        var recommendedPrice = recommendedUnitPrice == null ? this.localization.Get("Overlay.NoData") : recommendedUnitPrice.Value.Amount.ToString();

                        itemsToCheck = true;
                        ImGui.TableNextRow();
                        using var green = ImRaii.PushColor(
                            ImGuiCol.Text,
                            ImGuiColors.HealerGreen,
                            !isUnderCut && !needsUpdate);
                        using var yellow = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, needsUpdate);
                        using var red = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, isUnderCut);
                        ImGui.TableNextColumn();
                        ImGui.Text(
                            saleItem.IsEmpty()
                                ? this.localization.Get("Overlay.Empty")
                                : this.itemSheet.GetRowOrDefault(saleItem.ItemId)?.Name.ExtractText() ?? this.localization.Get("Overlay.UnknownItem"));
                        green.Pop();
                        yellow.Pop();
                        red.Pop();

                        ImGui.TableNextColumn();
                        ImGui.PushTextWrapPos();
                        ImGui.Text((index + 1).ToString());
                        ImGui.PopTextWrapPos();

                        ImGui.TableNextColumn();
                        ImGui.Text(recommendedPrice.ToString());

                        ImGui.TableNextColumn();
                        ImGui.Text(saleItem.IsEmpty() ? this.localization.Get("Overlay.NotAvailable") : isUnderCut ? this.localization.Get("Overlay.Yes") : this.localization.Get("Overlay.No"));

                        ImGui.TableNextColumn();
                        var needsUpdateText = needsUpdate ? this.localization.Get("Overlay.Yes") : this.localization.Get("Overlay.No");

                        ImGui.Text(saleItem.IsEmpty() ? this.localization.Get("Overlay.NotAvailable") : needsUpdateText);
                    }
                }
            }

            if (!itemsToCheck)
            {
                ImGui.Text(this.localization.Get("Overlay.NoItemsLeft"));
            }

            ImGui.Separator();
            this.DrawBatchListingSection();
        }
        else
        {
            ImGui.Text(this.localization.Get("Overlay.PleaseLogin"));
        }
    }

    private void DrawBatchListingSection()
    {
        ImGui.Text("批量上架");

        var candidates = this.BuildInventoryCandidates();
        var freeSlots = this.CountFreeMarketSlots();
        var listedCount = this.CountListedMarketItems();
        var otherAutomationRunning = this.autoUndercutService.IsRunning;

        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("背包中没有可上架的物品。");
        }
        else
        {
            var selectedIndex = candidates.FindIndex(
                candidate => candidate.ItemId == this.batchListItemId && candidate.IsHq == this.batchListIsHq);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                this.batchListItemId = candidates[0].ItemId;
                this.batchListIsHq = candidates[0].IsHq;
                this.batchListStackCount = candidates[0].StackCount;
            }

            ImGui.SetNextItemWidth(220 * ImGui.GetIO().FontGlobalScale);
            using (var combo = ImRaii.Combo("##batch-list-item", candidates[selectedIndex].Label))
            {
                if (combo)
                {
                    if (ImGui.IsWindowAppearing())
                    {
                        this.batchListSearchText = string.Empty;
                        ImGui.SetKeyboardFocusHere();
                    }

                    // The search box stays pinned above its own scroll region;
                    // otherwise the whole popup scrolls and drags the focused
                    // input (and the IME indicator) off screen.
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##batch-list-search", "搜索物品……", ref this.batchListSearchText, 64);
                    ImGui.Separator();

                    using (ImRaii.Child("##batch-list-items", new Vector2(0, 260 * ImGui.GetIO().FontGlobalScale)))
                    {
                        for (var index = 0; index < candidates.Count; index++)
                        {
                            if (this.batchListSearchText.Length > 0 &&
                                !candidates[index].Label.Contains(this.batchListSearchText, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (ImGui.Selectable(candidates[index].Label, index == selectedIndex))
                            {
                                this.batchListItemId = candidates[index].ItemId;
                                this.batchListIsHq = candidates[index].IsHq;
                                this.batchListStackCount = candidates[index].StackCount;
                                ImGui.CloseCurrentPopup();
                            }
                        }
                    }
                }
            }

            ImGui.SetNextItemWidth(120 * ImGui.GetIO().FontGlobalScale);
            var stackCount = this.batchListStackCount;
            if (ImGui.InputInt("组数##batch-list-count", ref stackCount))
            {
                this.batchListStackCount = Math.Clamp(stackCount, 1, 20);
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"剩余槽位：{freeSlots}");

            ImGui.Checkbox("上架前查询一次市场时价##batch-list-refresh", ref this.batchListRefreshPrice);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("勾选后会在上架第一组时打开时价窗口获取推荐价格，其余各组复用该价格；不勾选则直接使用缓存的推荐价格。");
            }
        }

        if (this.autoListingService.IsRunning)
        {
            if (ImGui.Button("取消##batch-list-cancel"))
            {
                this.autoListingService.Cancel();
            }
        }
        else
        {
            using (ImRaii.Disabled(candidates.Count == 0 || freeSlots == 0 || otherAutomationRunning))
            {
                if (ImGui.Button("开始批量上架##batch-list-start"))
                {
                    this.autoListingService.Start(
                        this.batchListItemId,
                        this.batchListIsHq,
                        this.batchListStackCount,
                        this.batchListRefreshPrice);
                }
            }

            using (ImRaii.Disabled(listedCount == 0 || otherAutomationRunning))
            {
                if (ImGui.Button("批量收回给雇员##batch-retract-retainer"))
                {
                    this.autoListingService.StartRetractAll(true, this.batchListStackCount);
                }

                ImGui.SameLine();

                if (ImGui.Button("批量收回给自己##batch-retract-self"))
                {
                    this.autoListingService.StartRetractAll(false, this.batchListStackCount);
                }
            }

            if (otherAutomationRunning && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("自动压价正在运行，请等待其完成。");
            }
        }

        if (this.autoListingService.StatusMessage.Length > 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.Text(this.autoListingService.StatusMessage);
            ImGui.PopTextWrapPos();
        }
    }

    private unsafe List<(uint ItemId, bool IsHq, int StackCount, string Label)> BuildInventoryCandidates()
    {
        var stacks = new Dictionary<(uint ItemId, bool IsHq), (int StackCount, long Quantity)>();
        InventoryType[] playerInventories =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        foreach (var inventoryType in playerInventories)
        {
            var container = this.inventoryService.GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var item = container->Items[index];
                if (item.ItemId == 0)
                {
                    continue;
                }

                var row = this.itemSheet.GetRowOrDefault(item.ItemId);
                if (row == null || row.Value.ItemSearchCategory.RowId == 0)
                {
                    continue;
                }

                var key = (item.ItemId, item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                var current = stacks.TryGetValue(key, out var existing) ? existing : (StackCount: 0, Quantity: 0L);
                stacks[key] = (current.StackCount + 1, current.Quantity + item.Quantity);
            }
        }

        return stacks
            .Select(pair =>
            {
                var name = this.itemSheet.GetRowOrDefault(pair.Key.ItemId)?.Name.ExtractText() ?? this.localization.Get("Overlay.UnknownItem");
                var label = $"{name}{(pair.Key.IsHq ? " (HQ)" : string.Empty)} ({pair.Value.Quantity})";
                return (pair.Key.ItemId, pair.Key.IsHq, pair.Value.StackCount, Label: label);
            })
            .OrderBy(candidate => candidate.Label, StringComparer.Ordinal)
            .ToList();
    }

    private unsafe int CountListedMarketItems()
    {
        var container = this.inventoryService.GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded)
        {
            return 0;
        }

        var listed = 0;
        for (var index = 0; index < container->Size; index++)
        {
            if (container->Items[index].ItemId != 0)
            {
                listed++;
            }
        }

        return listed;
    }

    private unsafe int CountFreeMarketSlots()
    {
        var container = this.inventoryService.GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded)
        {
            return 0;
        }

        var freeSlots = 0;
        for (var index = 0; index < container->Size; index++)
        {
            if (container->Items[index].ItemId == 0)
            {
                freeSlots++;
            }
        }

        return freeSlots;
    }
}
