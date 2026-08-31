using System.Collections.Generic;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Wizard;

using AllaganMarket.Settings;
using AllaganMarket.Services;

namespace AllaganMarket.Features;

public class OverlaysFeature(IEnumerable<IFormField<Configuration>> settings, LocalizationService localization) : Feature<Configuration>(
    [
        typeof(ShowRetainerOverlaySetting)
    ],
    settings)
{
    public override string Name => localization.Get("Wizard.Feature.Overlays.Name");

    public override string Description => localization.Get("Wizard.Feature.Overlays.Description");
}
