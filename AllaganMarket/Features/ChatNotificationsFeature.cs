using System.Collections.Generic;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Wizard;

using AllaganMarket.Settings;
using AllaganMarket.Services;

namespace AllaganMarket.Features;

public class ChatNotificationsFeature(IEnumerable<IFormField<Configuration>> settings, LocalizationService localization) : Feature<Configuration>(
    [
        typeof(ChatNotifyUndercutSetting),
        typeof(ChatNotifyUndercutLoginSetting),
        typeof(ChatNotifySoldItemSetting),
        typeof(ChatNotifyUndercutCharacterSetting),
        typeof(ChatNotifyUndercutGroupingSetting),
    ],
    settings)
{
    public override string Name => localization.Get("Wizard.Feature.ChatNotifications.Name");

    public override string Description => localization.Get("Wizard.Feature.ChatNotifications.Description");
}
