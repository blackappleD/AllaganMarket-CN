using System;
using System.Collections.Generic;

using AllaganLib.Interface.FormFields;

using AllaganMarket.Localization;
using AllaganMarket.Services;

namespace AllaganMarket.Settings;

public sealed class LanguageSetting : EnumFormField<PluginLanguage, Configuration>, ISetting
{
    private readonly LocalizationService localization;

    public LanguageSetting(ImGuiService imGuiService, LocalizationService localization)
        : base(imGuiService)
    {
        this.localization = localization;
    }

    public override Enum DefaultValue { get; set; } = PluginLanguage.Auto;

    public override string Key { get; set; } = "Language";

    public override string Name
    {
        get => this.localization.Get("Setting.Language.Name");
        set { }
    }

    public override string HelpText
    {
        get => this.localization.Get("Setting.Language.Help");
        set { }
    }

    public override string Version { get; set; } = "1.0.0";

    public override bool Equal(Enum item1, Enum item2)
    {
        return Equals(item1, item2);
    }

    public override Dictionary<Enum, string> Choices => new()
    {
        [PluginLanguage.Auto] = this.localization.Get("Language.Auto"),
        [PluginLanguage.English] = this.localization.Get("Language.English"),
        [PluginLanguage.ChineseSimplified] = this.localization.Get("Language.ChineseSimplified"),
    };

    public SettingType Type { get; set; } = SettingType.General;

    public bool ShowInSettings => true;
}
