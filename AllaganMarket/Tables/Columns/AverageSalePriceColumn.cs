using System.Globalization;

using AllaganLib.Interface.Grid;
using AllaganLib.Interface.Grid.ColumnFilters;
using AllaganLib.Interface.Services;

using LocalizationService = AllaganMarket.Services.LocalizationService;

using DalaMock.Host.Mediator;

using Dalamud.Bindings.ImGui;

namespace AllaganMarket.Tables.Columns;

public class AverageSalePriceColumn(NumberFormatInfo gilFormat, ImGuiService imGuiService, StringColumnFilter stringColumnFilter, LocalizationService localization)
    : GilColumn(gilFormat, imGuiService, stringColumnFilter)
{
    public override string DefaultValue { get; set; } = string.Empty;

    public override string Key { get; set; } = "AverageSalePrice";

    public override string Name
    {
        get => localization.GetOrDefault("Column.AverageSalePrice", "Avg. Sale Price");
        set { }
    }

    public override string HelpText { get; set; } = "The average sale price of this item.";

    public override string Version { get; set; } = "1.0.0";

    public override string? RenderName { get; set; } = null;

    public override int Width { get; set; } = 80;

    public override bool HideFilter { get; set; } = false;

    public override ImGuiTableColumnFlags ColumnFlags { get; set; } = ImGuiTableColumnFlags.None;

    public override string EmptyText { get; set; } = string.Empty;

    public override string? CurrentValue(SearchResult item)
    {
        if (item.SaleSummaryItem != null)
        {
            return ((int)(item.SaleSummaryItem.Earned / item.SaleSummaryItem.Quantity)).ToString();
        }

        return null;
    }
}
