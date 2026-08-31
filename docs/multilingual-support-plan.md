# Allagan Market 多语言支持方案

## 现状分析

项目是基于 .NET/Dalamud 的 ImGui 插件。用户可见文本目前直接散落在主窗口、配置窗口、Overlay、设置项、表格列、向导、右键菜单、聊天通知和 DTR 状态栏中；配置 key、CSV 字段和内部日志则属于稳定数据或诊断信息。

物品、世界和职业名称已经通过 Lumina 的 `ExtractText()` 获取，原则上应继续使用游戏数据语言。插件自己的菜单、设置、提示和通知应由独立的插件语言控制。当前 `CultureInfo.CurrentCulture` 和 Humanizer 的缓存还没有与语言选择绑定。

## 目标与边界

- 首期支持 `Auto`、英文、简体中文，后续可增加日语、德语和法语。
- 支持运行时切换，不改变现有配置 key、枚举值、CSV 格式和历史数据。
- 缺少翻译时回退到英文，并记录一次诊断日志。
- 插件文本和游戏实体名称分离；不手工翻译 Lumina 物品/世界名称。
- 日志与调试数据默认保留英文，便于问题检索和维护者支持。

## 技术方案

### 资源与服务

使用 .NET `.resx` 资源文件：`Resources/Strings.resx` 为英文基线，`Strings.zh-CN.resx` 为简体中文。由 `LocalizationService` 封装 `ResourceManager`，提供 `Get`、`Format`、当前 `CultureInfo` 和 `LanguageChanged` 事件。

语言回退链为：`zh-CN -> zh -> en-US`；未知语言直接回退到英文。不要修改进程级 `CurrentCulture`，数字、日期和 Humanizer 通过服务显式使用选定 culture。

### 配置

在 `Configuration` 增加 `PluginLanguage Language = Auto`，通过 General 分类中的 `LanguageSetting` 暴露。语言设置只保存枚举值，不保存翻译文本或 `CultureInfo`。语言变化后清理相对时间缓存，并刷新窗口、菜单、设置元数据、通知和 DTR 文本。

### 文本 key 约定

使用稳定的语义 key，例如：

```text
Window.Main.Title
Menu.File
Setting.UndercutBy.Name
Setting.UndercutBy.Help
Enum.UndercutComparison.Any
Column.UnitPrice
Message.Notification.UndercutBy
Tooltip.SearchOperators.Numeric
```

禁止在 UI 中新增英文原文作为隐式 key。设置项、Feature、表格列和字段的 `Name`/`HelpText`/`Description` 应通过 DI 获取本地化服务；领域模型不要反向依赖本地化服务。

### 格式化与外部集成

- 聊天通知使用命名占位符和本地化模板，按语言处理复数。
- DTR 标题、数量单复数和 Tooltip 在语言切换后重新设置。
- Humanizer 缓存 key 必须包含语言，或在语言变化时清空。
- CSV 读写继续使用 `InvariantCulture`，保持兼容；导出表头是否翻译应作为后续独立选项。
- 命令名称保持不变，命令帮助文本可本地化。
- 窗口标题使用稳定 ImGui ID（例如 `本地化标题##MainWindow`），避免切换语言后丢失窗口位置。

### 字体

首期必须验证 Dalamud 默认字体是否包含中文 glyph。若不包含，使用 Dalamud 字体图集机制加载具有明确许可的 CJK 字体，并在字体构建阶段配置中文 glyph range。

## 分阶段实施

1. 建立资源文件、本地化服务、语言枚举、配置字段和回退测试。
2. 迁移主窗口、配置窗口、设置元数据、Feature、表格列和枚举显示文本。
3. 迁移 Overlay、右键菜单、聊天通知、DTR 和命令帮助；接入语言变化事件。
4. 增加资源 key 完整性检查、CJK 字体检查和中英文 UI 回归测试。

## 验收标准

- 英文和简体中文可实时切换，缺少 key 时不显示资源 key。
- 设置、向导、表格、Tooltip、聊天通知和 DTR 均使用当前语言。
- 切换语言不影响窗口布局、配置、CSV 和历史数据。
- 1280x720 与 1920x1080 下中文长文本不截断、不重叠。
- 构建产物包含所有 satellite resource，CI 能报告资源 key 差异。
