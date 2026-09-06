using System.Collections.Generic;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Wizard;

using AllaganMarket.Settings;
using AllaganMarket.Services;

namespace AllaganMarket.Features;

public class UndercutsFeature(IEnumerable<IFormField<Configuration>> settings, LocalizationService localization) : Feature<Configuration>(
    [
        typeof(UndercutBySetting),
        typeof(MaximumAutoUndercutPercentageSetting),
        typeof(UndercutComparisonSetting),
    ],
    settings)
{
    public override string Name => localization.Get("Wizard.Feature.Undercuts.Name");

    public override string Description => localization.Get("Wizard.Feature.Undercuts.Description");
}
