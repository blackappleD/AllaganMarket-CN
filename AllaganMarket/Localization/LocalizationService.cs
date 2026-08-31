using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Resources;

using AllaganMarket.Localization;
using AllaganMarket.Settings;

using Dalamud.Game.Text;

namespace AllaganMarket.Services;

/// <summary>Resolves plugin-owned text without changing the process-wide culture.</summary>
public sealed class LocalizationService
{
    private readonly Configuration configuration;
    private readonly ResourceManager resources = new(
        "AllaganMarket.Resources.Strings",
        Assembly.GetExecutingAssembly());
    private CultureInfo culture;

    public LocalizationService(Configuration configuration)
    {
        this.configuration = configuration;
        this.culture = ResolveCulture(GetConfiguredLanguage(configuration));
    }

    public CultureInfo Culture => this.culture;

    public PluginLanguage CurrentLanguage => GetConfiguredLanguage(this.configuration);

    public event Action? LanguageChanged;

    public string Get(string key)
    {
        return this.resources.GetString(key, this.culture) ??
               this.resources.GetString(key, CultureInfo.GetCultureInfo("en-US")) ??
               $"[{key}]";
    }

    public string GetOrDefault(string key, string fallback)
    {
        var value = this.Get(key);
        return value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal)
            ? fallback
            : value;
    }

    public void Localize(ISetting setting)
    {
        var key = $"Setting.{setting.Key}";
        setting.Name = this.GetOrDefault($"{key}.Name", setting.Name);
        setting.HelpText = this.GetOrDefault($"{key}.Help", setting.HelpText);

        var choicesProperty = setting.GetType().GetProperty("Choices", BindingFlags.Public | BindingFlags.Instance);
        if (choicesProperty?.GetValue(setting) is IDictionary<Enum, string> choices)
        {
            // Copy the keys before updating values so custom dictionary implementations
            // cannot invalidate enumeration while a setting is being redrawn.
            foreach (var keyChoice in new List<Enum>(choices.Keys))
            {
                choices[keyChoice] = this.GetOrDefault($"{key}.Choice.{keyChoice}", choices[keyChoice]);
            }
        }
    }

    public string Format(string key, params object[] arguments)
    {
        return string.Format(this.culture, this.Get(key), arguments);
    }

    public string GetChatTypeName(XivChatType chatType, string fallback)
    {
        return this.GetOrDefault($"ChatType.{chatType}", fallback);
    }

    public void SetLanguage(PluginLanguage language)
    {
        if (GetConfiguredLanguage(this.configuration) == language && this.culture == ResolveCulture(language))
        {
            return;
        }

        this.configuration.Language = language;
        this.configuration.EnumSettings["Language"] = language;
        this.configuration.IsDirty = true;
        this.culture = ResolveCulture(language);
        this.LanguageChanged?.Invoke();
    }

    public void RefreshFromConfiguration()
    {
        var resolved = ResolveCulture(GetConfiguredLanguage(this.configuration));
        if (resolved != this.culture)
        {
            this.culture = resolved;
            this.LanguageChanged?.Invoke();
        }
    }

    private static CultureInfo ResolveCulture(PluginLanguage language)
    {
        return language switch
        {
            PluginLanguage.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            PluginLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ => ResolveAutoCulture(),
        };
    }

    private static PluginLanguage GetConfiguredLanguage(Configuration configuration)
    {
        return configuration.EnumSettings.TryGetValue("Language", out var value) && value is PluginLanguage language
            ? language
            : configuration.Language;
    }

    private static CultureInfo ResolveAutoCulture()
    {
        var current = CultureInfo.CurrentUICulture;
        return current.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en-US");
    }
}
