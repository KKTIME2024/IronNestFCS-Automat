# IronNest Brain progress

Updated 2026-09-02. 本阶段继续遵守：Brain 默认关闭；PlayerParity 不读取隐藏实体真值；不启动 PPO/PyTorch/Stable Baselines；不自动操作主菜单。第一次真实 bring-up 仍以用户手动进入固定任务为前提。

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
- `BrainResetHarness`：纯逻辑 reset watchdog，严格排序 `RESET_START → OBJECT_TEARDOWN → NEW_MISSION_DETECTED → FSC_REBOUND → KNOWLEDGE_CLEARED → FIRST_OBSERVATION → RESET_SUCCESS`，失败会产生 `RESET_FAILED`，并记录 wall-clock latency。
- `MissionManagerResetAdapter`：Logic 侧主线程适配器，仅使用生成 API 暴露的 `MissionManager.Instance`、`CurrentPhase`、`CurrentMission`、`CurrentMissionSceneName` 和 `ReloadCurrentMission()`；不把 `MissionGraph` 或实体数据带入 Brain。
- Named Pipe transport：单请求连接、JSONL、协议版本、malformed/version 错误、断线 Hold fallback；附带 stdlib-only fake Python random client。
- `BrainRuntimeCoordinator`：已接入主线程事件循环。它把 mission detection、decision point、纯 DTO observation、pipe action、mask/episode/decision generation guard、reward transition、terminal 和 reset 串成一个事件驱动状态机；不在每帧发 observation。
- `BrainRuntimeCoordinator` bring-up hardening：pipe 请求只进线程安全 DTO 队列，Unity/IL2CPP 访问只在 `FcsModule.Update()`；动作同时校验 `episode_id`、`decision_id`、`runtime_generation`；reset 中允许同一旧 episode 的延迟 impact 归因，成功切代后旧事件丢弃；重复 terminal 不重复结算/重置；policy 超时统一 Hold。
- In-process bring-up policy：`InProcessScriptedPolicy` 只选择当前 mask 中排序后的首个 Engage，否则 Hold；通过 `IRONNEST_BRAIN_INPROCESS=1` 或 `UserData/IronNestFCS/brain-inprocess.enabled` 单独启用，默认不启用，仍可用真实 pipe 驱动。
- `FcsBrainRuntimeHost`：Logic 侧只把可见电报/公开 track、FSC 执行结果和 `MissionStatsRuntimeProbe` 计数转换成 Brain DTO/game event；Unity/IL2CPP 访问留在主线程。
- `FcsModule.Update` 接线：runtime flag 开启时启动 Named Pipe server、host、reward probe，并在 `Update` 中 Tick coordinator；普通 FCS 未开启 runtime 时继续走原有 planner/FCS 路径。
- `FcsModule.Update` bring-up diagnostics：删除 runtime marker 会在主线程安全停掉 Brain runtime/pipe 并恢复普通 FCS；Numpad6 输出 runtime、episode/generation、pending decision、pipe、MissionManager、known/actionable tracks、FSC slots/in-flight、reward decision count、last audit/error；一次性阶段日志覆盖 runtime start、mission bind、intel/actionable track、decision/action/FSC mission、gun committed、shot/impact、terminal/reset、第二 episode。
- `MissionManagerResetAdapter` + runtime：terminal 后执行 `RESET_START → OLD_MISSION_GONE → NEW_MISSION_FOUND → FSC_REBOUND → KNOWLEDGE_CLEARED → FIRST_OBSERVATION`，旧 episode event/action 被 generation guard 丢弃；reset 超时进入安全 `Failed`，不循环重置。
- `tools/brain_pipe_random_client.py`：现在是真实 Named Pipe scripted client（首个合法 Engage，否则 Hold），会记录 episode/decision、observation、mask、action、transition、terminal，并在 terminal 后重新握手。
- Pipe wire format：请求/响应统一支持 snake_case（`episode_id`、`decision_id`、`action_kind` 等），并在协议边界测试了 Python 客户端实际使用的 JSON 形状。

## Tested

- Unit tested：**51/51 Brain tests passed**，包括 coordinator 正常 episode/reset、policy timeout Hold、stale action/observation generation、pending decision supersede、旧 episode event 污染、延迟 impact、重复 terminal、reset timeout safe state、WaitingForMission、pipe 断线/重连和幂等 shutdown。
- Corpus output：`scanned=14; tactical=14; fully-parsed=3; partial=4; unsupported=7`。
- Build tested：`IronNestFCS.Logic` 构建成功，0 errors，21 个既有 warning；Brain test harness 构建成功，0 errors。
- Game load tested：之前的 marker smoke run 成功加载最新 Logic/Abstractions，并记录 telegram/actuator/reward 三个 opt-in；无 Brain exception。
- Code integration tested：`IronNestFCS.Logic` 构建成功，0 errors，21 个既有 warning；Brain test harness 构建成功，0 errors；**51/51 Brain tests passed**，新增 reset single-flight、延迟 impact、generation guard、pending decision supersede、in-process policy、dispose/transport shutdown 覆盖。当前机器没有 `python`/`py`，因此 Python client 未做本机语法执行（脚本仅使用 stdlib）。
- Real mission tested：**UNVERIFIED IN REAL MISSION**。尚未人工进入固定任务，因此没有声称真实 pipe、terminal 或 MissionManager reload 成功。

## Fairness invariants

- 正式 PlayerParity 数据流没有 `FireMission.Entities`、`TacticalRadar`、`MapEntity.Position/Health/State`、Enemy GameObject 或实时 hidden velocity 输入。
- 隐藏实体 fixture 的位置、health、velocity、state 改变时，纯 observation fingerprint 不变。
- moving track 只能从最后一次合法报告外推；没有新电报/地图揭示时，游戏内部实体移动不会刷新 Track。
- `PlayerParityActionBridge` 仍为显式 opt-in；Engage 使用合法地图点和空 `EntityId`，由现有 FSC 负责底层火控。
- Position-only audit：Brain Engage 的 `TargetInfo.EntityId` 明确为空，mission/round 只携带合法地图位置、快照速度和预测点；FSC 的 `TargetRegistry` 以位置注册，移动目标的二次采纳仅在存在合法 track id 时发生；不会从空 id 反查隐藏 `MapEntity`。这条路径已完成静态源码检查，真实命中/回位仍未实测。

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
Observation: yes (pure DTO + guarded Logic adapters + main-thread runtime)
Action: yes (high-level action mask + opt-in FSC bridge + real pipe queue)
Reward: partial (public counter probe mapped to typed events + attribution ledger; exact damage/round attribution not real-mission verified)
Terminal: partial (runtime terminal path wired from public mission state; real mission callback not verified)
Reset: partial (coordinator + MissionManager public-API adapter wired; real reload unverified)
IPC: yes in code (real runtime server and scripted client wired; in-game transport unverified)
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

## Working-tree boundary

`8fe7065 feat(brain): harden runtime bring-up` contains the isolated Brain
contracts/coordinator, reset/generation tests, in-process policy, host,
transport client, and this progress/plan documentation. `FcsModule.cs` still
contains the runtime wiring in the same dirty hunks as earlier user work, so it
was deliberately not force-split or committed. The other dirty planner/FSC,
documentation, and archive paths were not reset, restored, or overwritten.

## Blockers

- 无人值守流程仍不能从主菜单通过公开 API 进入固定 mission；本轮明确把“手动进入一次任务”作为启动前提。
- 真实任务中的 pipe 握手、可见电报、FSC action、terminal callback 和 MissionManager reload：**UNVERIFIED IN REAL MISSION**。
- `MissionStatsRuntimeProbe` 目前只能读公开 counters；精确 damage amount、round attribution 和 native 事件时序仍需真实任务验证。
- 当前机器没有 Python 解释器，所以未执行 fake client 的本地 syntax smoke；用户环境需有 Python 3。

## Next highest-value step

### Test A — isolate game path (no Python)

1. 创建 `UserData/IronNestFCS/brain-runtime.enabled` 和 `brain-inprocess.enabled`（或设置 `IRONNEST_BRAIN_RUNTIME=1`、`IRONNEST_BRAIN_INPROCESS=1`）。
2. 启动游戏，手动进入固定任务并等待；成功标准是 `TRACK_ACTIONABLE → ACTION_ACCEPTED → FSC_MISSION_CREATED → SHOT_FIRED`。

### Test B — isolate IPC

1. 删除 `brain-inprocess.enabled`，保留 `brain-runtime.enabled`。
2. 运行 `py -3 tools\\brain_pipe_random_client.py ironnest-brain 100`；成功标准是 `DECISION_REQUEST → ACTION_ACCEPTED → SHOT_FIRED`。

### Test C/D — terminal and reset

等待 `EPISODE_TERMINAL` 后观察 `RESET_START → RESET_SUCCESS → EPISODE_2_STARTED`。首次异常按 Numpad6 输出状态快照；不要第一次同时打开 8x。真实 pipe、开火、terminal、MissionManager reload、多 episode 和 4x/8x 结果仍必须标记 **UNVERIFIED IN REAL MISSION**，不能由本地测试替代。
