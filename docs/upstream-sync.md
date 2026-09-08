# 上游同步与发布

本仓库是 `Critical-Impact/AllaganMarket` 的独立 fork。`origin` 只指向
`blackappleD/AllaganMarket-CN`，`upstream` 只用于读取原仓库更新。

首次配置：

```powershell
git remote add upstream https://github.com/Critical-Impact/AllaganMarket.git
```

同步上游时，在确认工作区已提交或已保存当前修改后执行：

```powershell
git fetch upstream
git checkout main
git merge upstream/main
git push origin main
```

不要执行 `git push upstream`。这样本地修改只会进入自己的 fork，不会反向写入原仓库。

## 发布

项目版本号位于 `AllaganMarket/AllaganMarket.csproj`。版本号沿用 Dalamud 清单的四段格式：

- 上游 `1.4.0.2` 对应你的基础版本 `1.4.0.2000`
- 在该上游版本上的后续修改依次使用 `1.4.0.2001`、`1.4.0.2002`……
- 上游进入 `1.4.0.3` 后，你的下一组版本从 `1.4.0.3000` 开始

创建四段版本 tag 后，GitHub Actions 会构建 Dalamud 插件并在自己的 fork 创建 Release：

```powershell
git tag v1.4.0.2000
git push origin v1.4.0.2000
```

Release 资产固定命名为 `AllaganMarket-bld.zip`。发布后，将该版本号和下载地址同步到
`blackappleD/DalamudPlugins` 的 `repo.json`。
