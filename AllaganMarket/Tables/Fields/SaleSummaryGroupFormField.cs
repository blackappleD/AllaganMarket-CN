using System;
using System.Collections.Generic;
using System.Linq;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Services;

using AllaganMarket.Extensions;
using AllaganMarket.Models;
using LocalizationService = AllaganMarket.Services.LocalizationService;

namespace AllaganMarket.Tables.Fields;

public class SaleSummaryGroupFormField(ImGuiService imGuiService, LocalizationService localization)
    : FlagsEnumFormField<SaleSummaryGroup, SaleSummary>(imGuiService)
{
    public override SaleSummaryGroup DefaultValue { get; set; } = SaleSummaryGroup.Item;

    public override string Key { get; set; } = "SaleSummaryGroup";

    public override string Name
    {
        get => localization.Get("Main.SaleSummaryGroup");
        set { }
    }

    public override string HelpText
    {
        get => localization.Get("Main.SaleSummaryGroupHelp");
        set { }
    }

    public override string Version { get; set; } = "1.0.0";

    public override bool HideAlreadyPicked { get; set; }

    public override SaleSummaryGroup AddFlag(SaleSummaryGroup existingFlags, SaleSummaryGroup newFlag)
    {
        return existingFlags | newFlag;
    }

    public override SaleSummaryGroup RemoveFlag(SaleSummaryGroup existingFlags, SaleSummaryGroup existingFlag)
    {
        return existingFlags & ~existingFlag;
    }

    public override bool FlagEmpty(SaleSummaryGroup flag)
    {
        return flag == SaleSummaryGroup.None;
    }

    public override string GetComboLabel(SaleSummary configuration)
    {
        var currentValue = this.CurrentValue(configuration);
        var choices = this.GetChoices(configuration);
        return localization.Format("Main.GroupBy", string.Join(
                   ", ",
                   choices.Where(
                              c => (c.Key != SaleSummaryGroup.None && currentValue.HasFlag(c.Key)) ||
                                   (currentValue == SaleSummaryGroup.None && c.Key == SaleSummaryGroup.None))
                          .Select(c => localization.GetOrDefault($"SaleSummaryGroup.{c.Key}", c.Value))));
    }

    public override Dictionary<SaleSummaryGroup, string> GetChoices(SaleSummary configuration)
    {
        var values = Enum.GetValues<SaleSummaryGroup>();
        return values.ToDictionary(c => c, c => localization.GetOrDefault($"SaleSummaryGroup.{c}", c.FormattedName()));
    }
}
