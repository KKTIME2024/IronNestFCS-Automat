# IronNest Brain progress

Updated 2026-09-02. 本阶段继续遵守：Brain 默认关闭；PlayerParity 不读取隐藏实体真值；不启动 PPO/PyTorch/Stable Baselines；不要求用户手动进入任务。

## Completed

- `PlayerParityKnowledge`：只接受 Telegram / MapReveal / ScoutRecon，拒绝 `OmniscientDebug`；支持合法位置、航向、速度、距离、路线和未来时刻匀速外推。
- `PlayerParityObservationProjection`：对排序后的纯 Brain DTO 做 deterministic SHA-256 审计投影；API 没有 Unity、GameObject、MapEntity、radar 或 entity collection 参数。
- `TelegramSemanticParser`：消费最终可见文本；支持 grid、clock、course、knots、bearing、distance、route waypoint、arrival/future time；无法安全解释的目标角色、弹药建议、命中/摧毁、侦察、反炮兵、优先级和任务命令标记为 `UnsupportedSemantic`，保留原始 evidence。
- checked-in telegram corpus：14 条来自游戏 `sharedassets0.assets` 的任务电报/模板实例，覆盖 High Tide、Iron Road、移动目标、路线、装甲阵地、AP 建议、侦察、反炮兵、命中/摧毁、优先级和失败语义。
- Track 生命周期：`Unknown → Reported → Located → Tracked → Stale → Lost / DestroyedConfirmed`；支持公开 track ID 合并、合法冲突审计、显式摧毁确认和新合法情报重新发现；不通过内部 entity ID 关联。
- 纯 Brain contract：`BrainObservation`、`ActionMask`、`BrainFacade`、`DrainEvents`、`EpisodeState`、episode/decision IDs。
- `RandomLegalPolicy`：只从当前 action mask 采样。
- `ScriptedBaselinePolicy`：继续当前任务；否则选择最新的、非 Lost/DestroyedConfirmed 且可 Engage 的 track；否则 Hold。
- `BrainRewardAttributionLedger`：通过 `decision_id → mission_id → round_id` 归因 delayed damage/kill/mission/miss/ammo/IronNest damage reward，并拒绝旧 episode callback。
- `BrainEpisode`：纯逻辑 `Starting → Running → Terminal/Failed → Resetting`，terminal 只接受一次，reset 清理计数、reward 和原因。

## Tested

- Unit tested：**35/35 Brain tests passed**。
- Corpus output：`scanned=14; tactical=14; fully-parsed=3; partial=4; unsupported=7`。
- Build tested：`IronNestFCS.Logic` 构建成功，0 errors，21 个既有 warning；Brain test harness 构建成功，0 errors。
- Game load tested：之前的 marker smoke run 成功加载最新 Logic/Abstractions，并记录 telegram/actuator/reward 三个 opt-in；无 Brain exception。
- Real mission tested：**no**。之前 smoke run 停在主菜单/scene-bind（`Player Turret Piece` / `Fire Mission Root` 尚未就绪），没有人工输入，也没有把它误报为完整战斗。

## Fairness invariants

- 正式 PlayerParity 数据流没有 `FireMission.Entities`、`TacticalRadar`、`MapEntity.Position/Health/State`、Enemy GameObject 或实时 hidden velocity 输入。
- 隐藏实体 fixture 的位置、health、velocity、state 改变时，纯 observation fingerprint 不变。
- moving track 只能从最后一次合法报告外推；没有新电报/地图揭示时，游戏内部实体移动不会刷新 Track。
- `PlayerParityActionBridge` 仍为显式 opt-in；Engage 使用合法地图点和空 `EntityId`，由现有 FSC 负责底层火控。

## Telegram coverage

```text
scanned: 14 checked-in visible fixtures
fully parsed: 3
partial: 4
unsupported: 7
```

这不是“所有 bundle 字符串”的最终比例：安装目录中已确认更多 mission key（例如 `HIGH_TIDE_1..13`、`IRON_ROAD_1..22`），但其中很多是未解析 localization 模板或状态/命令文本，仍需逐条导出最终 PrintJob 后才能纳入严格 corpus。

## Environment status

```text
Observation: yes (pure DTO + guarded Logic adapters)
Action: yes (high-level action mask + opt-in FSC bridge)
Reward: partial (native counter probe + pure attribution ledger; not yet full runtime linkage)
Terminal: partial (pure episode state; no real mission terminal callback wired)
Reset: no (pure reset state exists; MissionManager reload harness not wired)
IPC: no
```

## Real-game status

```text
episodes: 0 complete
successful episodes: 0
reset success: 0
4x: not measured
8x: not measured
transitions/hour: not measured
```

## Blockers

- 无人值守流程还不能可靠地从主菜单通过公开 API 进入固定 mission；当前没有脆弱的屏幕坐标点击实现。
- Teleprinter hook 已加载但没有在无人值守 smoke 中捕获 completed mission PrintJob。
- `MissionStatsRuntimeProbe` 只能读公开 counters，尚未沿 FSC 的 mission/round 链路发布完整 runtime reward attribution。
- 没有自动 reset harness，因此不能诚实地进行 4x/8x 长时间 episode benchmark。
- Named Pipe/TCP IPC 尚未实现。

## Next highest-value step

1. 在不依赖 UI 点击的前提下，继续检查/验证 `MissionManager` 的公开 mission-load/reload API；若不存在，保留 blocker。
2. 将 `BrainRewardAttributionLedger` 的 IDs 接入现有 `Mission`/`MissionRound` 运行时事件，而不读取敌方实体状态。
3. 在此基础上实现单一 Named Pipe JSON 协议和 fake Python client；仍先跑 scripted/random policy，不启动 PPO。
