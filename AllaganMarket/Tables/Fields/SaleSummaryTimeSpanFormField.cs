using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Services;
using AllaganLib.Interface.Widgets;

using AllaganMarket.Models;
using LocalizationService = AllaganMarket.Services.LocalizationService;

namespace AllaganMarket.Tables.Fields;

public class SaleSummaryTimeSpanFormField(ImGuiService imGuiService, LocalizationService localization)
    : TimeSpanFormField<SaleSummary>(new TimeSpanPickerWidget(), imGuiService)
{
    public override (TimeUnit, int)? DefaultValue { get; set; } = null;

    public override string Key { get; set; } = "SameSummaryTimeSpan";

    public override string Name
    {
        get => localization.Get("Main.SaleSummaryTimeSpan");
        set { }
    }

    public override string HelpText { get; set; } = string.Empty;

    public override string Version { get; set; } = "1.0.0";
}
