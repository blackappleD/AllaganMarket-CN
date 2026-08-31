using System.Collections.Generic;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Wizard;

using AllaganMarket.Settings;
using AllaganMarket.Services;

namespace AllaganMarket.Features;

public class MiscFeature(IEnumerable<IFormField<Configuration>> settings, LocalizationService localization) : Feature<Configuration>(
    [
        typeof(AddTitleMenuButtonSetting),
        typeof(AddDtrBarEntrySetting)
    ],
    settings)
{
    public override string Name => localization.Get("Wizard.Feature.Misc.Name");

    public override string Description => localization.Get("Wizard.Feature.Misc.Description");
}
