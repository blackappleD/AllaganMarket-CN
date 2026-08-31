using System.Globalization;

using AllaganLib.Interface.Grid;
using AllaganLib.Interface.Grid.ColumnFilters;
using AllaganLib.Interface.Services;

using LocalizationService = AllaganMarket.Services.LocalizationService;

using DalaMock.Host.Mediator;

using Dalamud.Bindings.ImGui;

namespace AllaganMarket.Tables.Columns;

public class UnitPriceColumn(NumberFormatInfo gilFormat, ImGuiService imGuiService, StringColumnFilter stringColumnFilter, LocalizationService localization)
    : GilColumn(gilFormat, imGuiService, stringColumnFilter)
{
    public override string DefaultValue { get; set; } = string.Empty;

    public override string Key { get; set; } = "UnitPrice";

    public override string Name
    {
        get => localization.GetOrDefault("Column.UnitPrice", "Unit Price");
        set { }
    }

    public override string? RenderName { get; set; }

    public override int Width { get; set; } = 100;

    public override bool HideFilter { get; set; } = false;

    public override ImGuiTableColumnFlags ColumnFlags { get; set; } = ImGuiTableColumnFlags.None;

    public override string EmptyText { get; set; } = string.Empty;

    public override string HelpText { get; set; } = "The unit price of the item being sold.";

    public override string Version { get; set; } = "1.0.0";

    public override string? CurrentValue(SearchResult item)
    {
        return item.SoldItem?.UnitPrice.ToString() ?? item.SaleItem?.UnitPrice.ToString() ?? null;
    }
}
