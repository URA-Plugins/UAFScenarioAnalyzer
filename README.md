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
