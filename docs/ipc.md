# IPC 接口

其他 Dalamud 插件可以通过 IPC 调用 Allagan Market 的自动压价。所有名称都以 `AllaganMarket.` 开头。

| 名称 | 类型 | 说明 |
| --- | --- | --- |
| `AllaganMarket.ApiVersion` | `Func<int>` | 接口版本，当前为 `1` |
| `AllaganMarket.AutoUndercut.Start` | `Func<bool>` | 对勾选了"自动压价"的雇员执行压价，等同于点击悬浮窗里的 ▶ 按钮 |
| `AllaganMarket.AutoUndercut.StartForRetainers` | `Func<ulong[], bool>` | 只对给定雇员 ID（当前角色名下）执行压价，不看勾选框 |
| `AllaganMarket.AutoUndercut.Cancel` | `Action` | 取消正在进行的压价 |
| `AllaganMarket.AutoUndercut.IsRunning` | `Func<bool>` | 自动压价是否在运行 |
| `AllaganMarket.AutoUndercut.EnabledRetainerCount` | `Func<int>` | 当前角色名下勾选了"自动压价"的雇员数量，未登录时为 `0` |
| `AllaganMarket.AutoUndercut.IsBusy` | `Func<bool>` | 本插件是否有任何自动化（压价、批量上架/撤回、模特补货）在运行 |
| `AllaganMarket.AutoUndercut.Started` | 事件，无参数 | 一次压价开始 |
| `AllaganMarket.AutoUndercut.Finished` | 事件，`bool` | 一次压价结束；`true` 表示所有选中雇员都处理完，`false` 表示被取消或出错 |

注意：

- 如果雇员列表（`RetainerList` 窗口）没开，插件会自动交互可触及范围内的传唤铃（城内/旅馆约 4.6 米，房屋内约 6.5 米）把它打开，最多尝试 2 次；附近没有传唤铃、角色正忙（战斗、过图、咏唱、其他交互中）或始终打不开时，放弃并触发 `Finished(false)`。
- `Start` / `StartForRetainers` 返回 `false` 表示没有启动：有其他自动化在运行、未登录，或没有匹配的雇员。
- 事件在游戏主线程上发出。

## 调用示例

```csharp
var start = pluginInterface.GetIpcSubscriber<bool>("AllaganMarket.AutoUndercut.Start");
var finished = pluginInterface.GetIpcSubscriber<bool, object>("AllaganMarket.AutoUndercut.Finished");

finished.Subscribe(completed => log.Information($"压价结束: {completed}"));

if (!start.InvokeFunc())
{
    log.Warning("Allagan Market 没有启动压价");
}
```

插件未加载时 `InvokeFunc` 会抛出 `IpcNotReadyError`，调用方需要自行捕获。
