# UAFScenarioAnalyzer

解析 UAF 剧本回合训练信息。

## History

成功输出按 `(single_mode_chara_id, turn)` 保存在当前插件实例的内存中；同一键的后续输出原位更新。记录不会跨插件重载或进程重启保留。

焦点位于训练分析面板时，方向键用于浏览记录：`↑` 前一条、`↓` 后一条、`←` 最旧、`→` 最新。正文仍可使用 `PageUp`、`PageDown`、`Home`、`End` 和鼠标滚轮滚动。

配置文件为 `PluginData/UAFScenarioAnalyzer/settings.json`：

```json
{
  "historyLimit": 100
}
```

`historyLimit` 的有效范围为 `0` 到 `1000`，默认值为 `100`。设为 `0` 时禁用 history，但仍显示最近一次成功输出。调低上限会立即删除最旧记录；调高上限不会恢复已经删除的记录。

## 构建

```powershell
git -c core.longpaths=true submodule update --init --recursive
dotnet build .\UAFScenarioAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；版本 tag 触发 GitHub Release 发布。环境要求、共用 workflow 本地映射和发布规则见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。
