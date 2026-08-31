using System;
using System.Collections.Generic;

using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Services;

namespace AllaganMarket.Settings;

public class ChatNotifyUndercutGroupingSetting : EnumFormField<ChatNotifyUndercutGrouping, Configuration>, ISetting
{
    private readonly Dictionary<Enum, string> choices = new()
    {
        { ChatNotifyUndercutGrouping.Individual, "Individually" },
        { ChatNotifyUndercutGrouping.Together, "All Together" },
        { ChatNotifyUndercutGrouping.GroupByItem, "By Item" },
        { ChatNotifyUndercutGrouping.GroupByRetainer, "By Retainer" },
    };

    public ChatNotifyUndercutGroupingSetting(ImGuiService imGuiService)
        : base(imGuiService)
    {
    }

    public override Enum DefaultValue { get; set; } = ChatNotifyUndercutGrouping.GroupByItem;

    public override string Key { get; set; } = "UndercutGrouping";

    public override string Name { get; set; } = "Group undercut messages by?";

    public override string HelpText { get; set; } =
        "When multiple undercuts occur, how should these be grouped together?";

    public override string Version { get; set; } = "1.0.0.1";

    public override bool Equal(Enum item1, Enum item2)
    {
        return Equals(item1, item2);
    }

    public override Dictionary<Enum, string> Choices => this.choices;

    public SettingType Type { get; set; } = SettingType.Chat;

    public bool ShowInSettings => true;
}

public enum ChatNotifyUndercutGrouping
{
    Individual,
    Together,
    GroupByItem,
    GroupByRetainer,
}
