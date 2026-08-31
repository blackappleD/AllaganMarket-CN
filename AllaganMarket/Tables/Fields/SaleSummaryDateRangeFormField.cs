using System;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Services;
using AllaganLib.Interface.Widgets;

using AllaganMarket.Models;
using LocalizationService = AllaganMarket.Services.LocalizationService;

namespace AllaganMarket.Tables.Fields;

public class SaleSummaryDateRangeFormField(ImGuiService imGuiService, LocalizationService localization)
    : DateRangeFormField<SaleSummary>(new DateRangePickerWidget(), imGuiService)
{
    public override (DateTime, DateTime)? DefaultValue { get; set; } = null;

    public override string Key { get; set; } = "SaleSummaryDateRange";

    public override string Name
    {
        get => localization.Get("Main.SaleSummaryDateRange");
        set { }
    }

    public override string HelpText
    {
        get => localization.Get("Main.SaleSummaryDateRangeHelp");
        set { }
    }

    public override string Version { get; set; } = "1.0.0";
}
