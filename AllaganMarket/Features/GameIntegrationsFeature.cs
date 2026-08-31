using System.Collections.Generic;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Wizard;

using AllaganMarket.Services;

namespace AllaganMarket.Features;

public class GameIntegrationsFeature(IEnumerable<IFormField<Configuration>> settings, LocalizationService localization) : Feature<Configuration>(
    [
    ],
    settings)
{
    public override string Name => localization.Get("Wizard.Feature.GameIntegrations.Name");

    public override string Description => localization.Get("Wizard.Feature.GameIntegrations.Description");
}
