using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Services;

namespace AllaganMarket.Settings;

public class RaisePriceWhenLowestSetting(ImGuiService imGuiService)
    : BooleanFormField<Configuration>(imGuiService), ISetting
{
    public override bool DefaultValue { get; set; }

    public override string Key { get; set; } = "RaisePriceWhenLowest";

    public override string Name { get; set; } = "Raise price when already lowest";

    public override string HelpText { get; set; } =
        "When your listing is already the lowest, raise it to the lowest competing price minus the undercut amount.";

    public override string Version { get; set; } = "1.0.0.1";

    public SettingType Type { get; set; } = SettingType.Undercutting;

    public bool ShowInSettings => true;
}
