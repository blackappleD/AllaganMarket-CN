using System.Numerics;

using AllaganLib.Interface.Grid;
using AllaganLib.Interface.Services;

using AllaganMarket.Extensions;
using LocalizationService = AllaganMarket.Services.LocalizationService;

using DalaMock.Host.Mediator;

using Dalamud.Plugin.Services;

using Dalamud.Bindings.ImGui;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AllaganMarket.Tables.Columns;

public class ItemIconColumn : IconColumn<SearchResultConfiguration, SearchResult, MessageBase>
{
    private readonly ExcelSheet<Item> itemSheet;
    private readonly LocalizationService localization;

    public ItemIconColumn(ExcelSheet<Item> itemSheet, ITextureProvider textureProvider, ImGuiService imGuiService, LocalizationService localization) : base(textureProvider, imGuiService)
    {
        this.itemSheet = itemSheet;
        this.localization = localization;
    }

    public override int DefaultValue { get; set; } = 0;

    public override string Key { get; set; } = "Icon";

    public override string Name
    {
        get => localization.GetOrDefault("Column.Icon", "Icon");
        set { }
    }

    public override string? RenderName { get; set; } = null;

    public override int Width { get; set; } = 32;

    public override bool HideFilter { get; set; } = true;

    public override ImGuiTableColumnFlags ColumnFlags { get; set; } = ImGuiTableColumnFlags.None;

    public override string EmptyText { get; set; } = string.Empty;

    public override Vector2 IconSize { get; set; } = new(32, 32);

    public override int? CurrentValue(SearchResult item)
    {
        if (item.SaleSummaryItem != null)
        {
            if (item.SaleSummaryItem.SaleSummaryKey.ItemId != null)
            {
                var itemRow = this.itemSheet.GetRowOrDefault((uint)item.SaleSummaryItem.SaleSummaryKey.ItemId);
                if (itemRow != null)
                {
                    return itemRow.Value.Icon;
                }
            }
            if (item.SaleSummaryItem.SaleSummaryKey.IsHq != null)
            {
                return item.SaleSummaryItem.SaleSummaryKey.IsHq == true ? 61391 : 61394;
            }
            if (item.SaleSummaryItem.SaleSummaryKey.WorldId != null)
            {
                return 65;
            }
            if (item.SaleSummaryItem.SaleSummaryKey.OwnerId != null)
            {
                return 62045;
            }
            if (item.SaleSummaryItem.SaleSummaryKey.RetainerId != null)
            {
                return 60560;
            }
        }
        return item.SaleItem?.GetItem(this.itemSheet)!.Value.Icon ?? item.SoldItem?.GetItem(this.itemSheet)!.Value.Icon ?? null;
    }

    public override string HelpText { get; set; } = "The icon of the item";

    public override string Version { get; set; } = "1.0.0.2";
}
