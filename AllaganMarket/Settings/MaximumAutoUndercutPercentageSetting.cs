using AllaganLib.Interface.FormFields;
using AllaganLib.Interface.Services;

namespace AllaganMarket.Settings;

public class MaximumAutoUndercutPercentageSetting(ImGuiService imGuiService)
    : IntegerFormField<Configuration>(imGuiService), ISetting
{
    public override string? Affix { get; } = "%";

    public override int DefaultValue { get; set; } = 10;

    public override string Key { get; set; } = "MaximumAutoUndercutPercentage";

    public override string Name { get; set; } = "Maximum automatic price reduction";

    public override string HelpText { get; set; } =
        "Skip an automatic undercut when the recommended price would reduce the current listing price by more than this percentage.";

    public override string Version { get; set; } = "1.0.0.1";

    public SettingType Type { get; set; } = SettingType.Undercutting;

    public bool ShowInSettings => true;
}
