using System.Collections.Generic;

using Autofac;

using AllaganMarket.Services;

namespace AllaganMarket.Settings.Layout;

public class ChatSettingLayout : SettingPage
{
    private readonly LocalizationService localization;

    public ChatSettingLayout(IComponentContext componentContext, LocalizationService localization) : base(componentContext)
    {
        this.localization = localization;
    }

    public override SettingType SettingType => SettingType.Chat;

    public override List<ISettingLayoutItem> GenerateLayoutItems()
    {
        return
        [
            new TextLayoutItem("Chat.Undercuts.General", this.localization),
            new SeparatorLayoutItem(),
            new SettingLayoutItem(typeof(ChatNotifyUndercutSetting), this.localization),
            new SettingLayoutItem(typeof(ChatNotifyUndercutCharacterSetting), this.localization),
            new SettingLayoutItem(typeof(ChatNotifyUndercutGroupingSetting), this.localization),
            new SettingLayoutItem(typeof(ChatNotifyUndercutChatTypeSetting), this.localization),
            new SpacerLayoutItem(),
            new TextLayoutItem("Chat.Undercuts.Login", this.localization),
            new SeparatorLayoutItem(),
            new SettingLayoutItem(typeof(ChatNotifyUndercutLoginSetting), this.localization),
            new SettingLayoutItem(typeof(ChatNotifyUndercutLoginChatTypeSetting), this.localization),
            new SpacerLayoutItem(),
            new TextLayoutItem("Chat.ItemSold", this.localization),
            new SeparatorLayoutItem(),
            new SettingLayoutItem(typeof(ChatNotifySoldItemSetting), this.localization),
            new SettingLayoutItem(typeof(ChatNotifySoldItemChatTypeSetting), this.localization)
        ];
    }
}
