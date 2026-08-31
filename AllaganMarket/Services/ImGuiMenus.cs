using System.Collections.Generic;
using System.Linq;

using AllaganMarket.Mediator;
using AllaganMarket.Models;
using AllaganMarket.Settings;

using DalaMock.Host.Mediator;

using Dalamud.Plugin.Services;

using Dalamud.Bindings.ImGui;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AllaganMarket.Services;

public class ImGuiMenus
{
    private readonly UndercutService undercutService;
    private readonly Configuration configuration;
    private readonly LocalizationService localization;
    private readonly IGameInterfaceService gameInterfaceService;
    private readonly ExcelSheet<Recipe> recipeSheet;
    private readonly HashSet<uint> recipeItemIds;

    public ImGuiMenus(UndercutService undercutService, Configuration configuration, LocalizationService localization, IDataManager dataManager, IGameInterfaceService gameInterfaceService)
    {
        this.undercutService = undercutService;
        this.configuration = configuration;
        this.localization = localization;
        this.gameInterfaceService = gameInterfaceService;
        this.recipeSheet = dataManager.GetExcelSheet<Recipe>();
        this.recipeItemIds = this.recipeSheet.Select(c => c.ItemResult.RowId).Distinct().ToHashSet();
    }

    public MessageBase? DrawSoldItemMenu(SoldItem soldItem)
    {
        if (ImGui.Selectable(this.localization.Get("Menu.MoreInformation")))
        {
            return new OpenMoreInformation(soldItem.ItemId);
        }

        if (this.recipeItemIds.Contains(soldItem.ItemId) && ImGui.Selectable(this.localization.Get("Menu.OpenCraftingLog")))
        {
            this.gameInterfaceService.OpenCraftingLog(soldItem.ItemId);
        }

        if (ImGui.Selectable(this.localization.Get("Menu.DeleteSoldItem")))
        {
            return new DeleteSoldItem(soldItem);
        }

        return null;
    }

    public MessageBase? DrawSaleItemMenu(SaleItem saleItem)
    {
        if (ImGui.Selectable(this.localization.Get("Menu.MoreInformation")))
        {
            return new OpenMoreInformation(saleItem.ItemId);
        }

        if (this.recipeItemIds.Contains(saleItem.ItemId) && ImGui.Selectable(this.localization.Get("Menu.OpenCraftingLog")))
        {
            this.gameInterfaceService.OpenCraftingLog(saleItem.ItemId);
        }

        if (ImGui.Selectable(this.localization.Get("Menu.MarkAsUpdated")))
        {
            this.undercutService.InsertFakeMarketPriceCache(saleItem);
        }

        ImGui.NewLine();
        ImGui.Text(this.localization.Get("Menu.UndercutSettings"));
        ImGui.Separator();


        if (ImGui.Selectable(this.localization.Get("Menu.UseDefault"), this.configuration.GetUndercutComparison(saleItem.ItemId) == null))
        {
            this.configuration.RemoveUndercutComparison(saleItem.ItemId);
        }

        if (ImGui.Selectable(
                this.localization.Get("Setting.UndercutComparison.Choice.Any"),
                this.configuration.GetUndercutComparison(saleItem.ItemId) == UndercutComparison.Any))
        {
            this.configuration.SetUndercutComparison(saleItem.ItemId, UndercutComparison.Any);
        }

        if (ImGui.Selectable(
                this.localization.Get("Setting.UndercutComparison.Choice.NqOnly"),
                this.configuration.GetUndercutComparison(saleItem.ItemId) == UndercutComparison.NqOnly))
        {
            this.configuration.SetUndercutComparison(saleItem.ItemId, UndercutComparison.NqOnly);
        }

        if (ImGui.Selectable(
                this.localization.Get("Setting.UndercutComparison.Choice.HqOnly"),
                this.configuration.GetUndercutComparison(saleItem.ItemId) == UndercutComparison.HqOnly))
        {
            this.configuration.SetUndercutComparison(saleItem.ItemId, UndercutComparison.HqOnly);
        }

        if (ImGui.Selectable(
                this.localization.Get("Setting.UndercutComparison.Choice.MatchingQuality"),
                this.configuration.GetUndercutComparison(saleItem.ItemId) == UndercutComparison.MatchingQuality))
        {
            this.configuration.SetUndercutComparison(saleItem.ItemId, UndercutComparison.MatchingQuality);
        }

        return null;
    }
}
