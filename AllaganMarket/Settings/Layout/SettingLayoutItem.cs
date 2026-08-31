using System;

using AllaganMarket.Services;

using Autofac;

namespace AllaganMarket.Settings.Layout;

public class SettingLayoutItem : ISettingLayoutItem
{
    private readonly Type settingType;
    private readonly LocalizationService localization;
    private ISetting? setting;

    public SettingLayoutItem(Type settingType, LocalizationService localization)
    {
        this.settingType = settingType;
        this.localization = localization;
    }

    public void Draw(Configuration configuration, int? labelSize = null, int? inputSize = null)
    {
        if (this.setting != null)
        {
            this.localization.Localize(this.setting);
            this.setting.Draw(configuration, labelSize, inputSize);
        }
    }

    public void Build(IComponentContext context)
    {
        this.setting = (ISetting)context.Resolve(this.settingType);
    }
}
