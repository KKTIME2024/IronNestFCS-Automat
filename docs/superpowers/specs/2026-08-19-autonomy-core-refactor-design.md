# 自主交战内核渐进重构设计

- 日期：2026-08-19
- 基线：`master@87405a4`
- 分支：`codex/autonomy-core-refactor`
- 状态：**Milestone 1 代码切换已完成（2026-08-20）**。BaselinePlanner 为唯一自动决策源；旧 picker（`PickTarget`/`CreateAutoTask`）、`LegacyDecisionRecorder`、影子比较与 legacy runtime fallback 已全部删除；Ctrl+Numpad7 改为 Baseline ↔ PlanningDisabled 紧急停用开关。**最终游戏内回归仍待用户部署后完成**（见 §10.3 验收清单）。

## 1. 背景与目标

IronNestFCS 上游是以玩家标记为输入的 FIFO 自动操作 Mod：任务进入队列后，系统依次完成选弹、装弹、装药、瞄准、击发和退膛。Fork 的长期目标是智能化自主交战火炮：系统持续观察战场、整体规划双管火力、协调共享硬件、评估结果并重新规划，同时保留玩家通过地图桌 T1–T4 下达高优先级任务的能力。

已有 CBT 实验暴露了结构性问题：策略、目标登记、双管协调、硬件锁、失败恢复和游戏状态都集中在 FSC 协程中。继续向 FIFO 执行器添加条件会扩大时序组合，难以验证。重构目标是建立清晰、可测试的自主控制 seam，而不是重写已经工作的所有游戏交互。

## 2. 决策与约束

1. `master` 是唯一代码起点和黄金行为基线。
2. `dev`、历史 CBT 和未提交的 `refactor/planner` 只作为参考，不整体合并或 cherry-pick。
3. 第一里程碑严格保持 `master` 的外部行为；只允许单独修复卡死、误射、锁泄漏或热重载残留等安全缺陷。
4. 第一里程碑不恢复 CBT，不新增多轮任务，不改变集群策略，不拆 FSC 的机械执行流程。
5. 所有自动构建默认输出到仓库内隔离目录；只有用户确认进入运行时验收后才部署到游戏目录。
6. 所有 Unity/IL2CPP 访问继续运行在 MelonCoroutine 主线程；禁止 `async`/`Task.Delay`。
7. 每个小阶段都需要纯 C# 自动测试和游戏内运行时验收。运行时日志位于 `F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator\MelonLoader\Logs`。

## 3. 物理模型

- 两管共用一个炮塔方位，仰角分别独立。
- 左右臂杆分别是各炮管的击发保险/约束阀。
- `TriggerConsole.Fire()` 是全局能量输入；调用时所有解除保险的炮管都会击发。
- 普通单发只允许目标炮管解除保险；另一管可以并行装填和调仰角，但必须保持保险。
- `SameTrigger` 组可以授权两管同时解除保险，并由一次全局击发带出两发。
- 不同方位或无关任务不得同时解除保险。
- 保险解除至全局击发或重新上保险完成期间，由所属 FireGroup 独占 `FireGateLease`。

## 4. 目标架构

```text
游戏对象
  -> ObservationAdapter
  -> 不可变 BattleSnapshot
  -> Planner.Plan(snapshot)
  -> EngagementPlan
  -> Scheduler
  -> ExecutionLease / ShotOrder
  -> Weapon Executor
  -> 结构化执行事件
  -> 下一份可信快照
```

### 4.1 ObservationAdapter

唯一读取 Unity/IL2CPP 对象的模块。一次观测产生带版本号和有效性标志的不可变快照。反射失败、场景未就绪或实体字典不可用时返回 `ObservationInvalid`，不得把空列表解释为全体死亡。

### 4.2 BattleSnapshot

不持有 Unity 对象，不读取全局时间，创建后不可修改。至少包含：

- 观测版本、游戏时间和有效性；
- 当前炮塔方位；
- 左右炮管快照；
- 存活敌目标的身份、位置、方位、距离、优先级、星级、装甲/地下/移动属性、速度可信度和免疫弹种；
- 存活友军位置；
- 当前目标、落点和集群成员预约；
- 当前人工 FIFO；
- 第一里程碑固定为 CBT disabled。

### 4.3 Planner

Planner 是纯计算模块。外部 interface 是对整套火力单元进行一次规划：

```text
EngagementPlan Plan(BattleSnapshot snapshot)
```

Planner 不持有 FSC、TacticalRadar、Unity 时间或 IL2CPP 引用，不订阅运行时事件，也不直接启动移动、采购或击发操作。

### 4.4 EngagementPlan

计划面向整个双管火力单元，而不是按 Left、Right 逐管选目标。计划由以下元素组合：

- `ShotIntent`：目标或落点、预期弹种/装药、移动目标快照、爆区和允许的 fallback；
- `FireGroup`：一组具有共同击发语义的 Shot；
- `TriggerMode`：独立击发或 `SameTrigger`；
- 组间约束：无约束、先后顺序、落地先后或落地对齐；
- 失败/降级规则。

不存在单独的 `Consecutive` 任务类型。连续射击由多个 FireGroup 和组间约束组合；如果未来确实必须使用同一炮管，再增加窄化的 `SameBarrel` 资源约束。

第一里程碑只实际启用 master 等价的普通独立组；其他协调语义只完成定义，不接入执行路径。

### 4.5 Scheduler

Scheduler 负责验证快照版本、按 FireGroup 原子登记目标、分配炮管、解释组间约束、签发 ExecutionLease、接收执行事件并决定继续、降级或结束。Scheduler 不点击游戏对象。

原子性粒度是 FireGroup：

- `SameTrigger` 组必须整体接受或整体拒绝；
- 独立单发组可以分别推进；
- 有前置约束的组必须等待约束成立；
- Scheduler 不能把失败的协调组静默拆成 FIFO，除非计划明确允许该降级。

### 4.6 Weapon Executor

Executor 只执行被授权的机械动作：选弹、装弹、装药、瞄准、解除保险、击发和退膛回位。它不重新选目标、不读取 CBT 阶段，也不自行创造替代任务。状态不符或动作失败时返回结构化事件，由 Scheduler 重新观测和决策。

第一里程碑使用 `LegacySchedulerAdapter` 把普通独立组转换为 master 的 `ArtilleryTask` 并交给现有 FSC。FSC 暂时作为黑盒执行器。

## 5. 炮管状态与脏炮管策略

炮管不是空白执行槽，而是携带机械状态和已投入弹药的资源。`BarrelSnapshot` 至少表达：

- reload state 及观测可信度；
- 膛内弹种；
- 膛内装药数及可信度；
- `CanFire`、`pendingReload` 和 breech lock；
- 击发保险状态；
- 当前 ExecutionLease；
- 当前是否处于机构动画或恢复过程。

状态处理规则：

| 状态 | 正常策略 |
|---|---|
| 空膛 | 正常规划和装填 |
| 已装弹、尚未合栓且机构稳定 | 作为可调整库存；优先匹配合法目标，无合法目标时允许正常退弹 |
| 推弹、装药或合栓动画中 | 不取消、不退弹，等待稳定状态后重新观测 |
| `CanFire` / 已合栓 | 形成 `CommittedRound`，视为正常流程中的不可逆提交点 |
| 已击发、等待退膛 | 不可分配，等待机械流程结束 |
| 状态未知或异常 | 隔离并诊断；不把未知解释为空膛或装药不足，不立即强制复位 |

对 `CommittedRound`，Planner 必须优先为已存在的弹种和装药寻找合法目标，允许向下选择较低优先级目标，但不得突破弹种有效性、目标免疫、射程、友军禁区或 FireGroup 硬约束。找不到合法目标时保持炮管隔离等待，不向无效目标或空地泄弹。

游戏存在 `RegressState` 和 `EjectChamberedShell`，但精确的正常可逆状态边界尚需运行时观测确认。当前 `CleanState` 使用强制解锁、回退和复位从合栓状态退弹，只能视为异常恢复手段，不能成为正常调度路径。

Executor 接受带前置条件的 `ExecutionLease`。开始前如炮管状态版本、膛内弹药或保险状态与 lease 不符，返回 `StateChanged`；不得自行退弹、换目标或换策略。

## 6. 玩家 T1–T4 任务

玩家默认不直接操作装弹机、装药机构、炮塔、仰角或击发机构。玩家任务只能通过地图桌 T1–T4 生成。

T1–T4 是独立于自主 Planner 的高优先级 FIFO 命令通道：

- 严格按玩家提交顺序保留；
- 绕过自主目标优先级、经济纪律、弹种建议、友军禁区和自动任务去重策略；
- 使用地图桌提交时确定的目标、弹种和参数，不得被 Planner 改写；
- Scheduler 只检查物理可执行性；暂时不可执行时排队并显示原因；
- 已开始的自动任务不打断；人工 FIFO 从下一个安全空闲点接管；
- 人工 FIFO 清空前不生成新的自主计划；
- 已合栓的 `CommittedRound` 不为人工任务强制退弹。

## 7. 失败语义

- 无效观测：保持上一份可信态势，不宣告死亡，不生成新计划。
- 无合法计划：返回明确原因，不生成空任务。
- 计划版本过期：Scheduler 拒绝且不登记、不操作硬件。
- FireGroup 部分不可接受：整组拒绝。
- Executor 状态变化：返回事件并重新观测，不在 Executor 内修改战术意图。
- 读取失败：保留 `Unknown`，不得使用 `-1`、空字符串或 `false` 冒充业务状态。
- 异常取消：必须恢复本管击发保险后才能释放 FireGateLease。
- 后续 Scheduler/Executor 中的所有等待必须有超时、终态和诊断；第一里程碑不借重构名义批量修改 master FSC 的等待路径。

## 8. 第一里程碑实施顺序

1. 从 `master@87405a4` 建立隔离 worktree 和分支。
2. 在不改变派发逻辑的前提下增加结构化黄金日志、扫描有效性和只读炮管观测。
3. 添加不引用 Unity/IL2CPP 的纯领域项目和测试项目。
4. 实现不可变快照、EngagementPlan 数据模型和 master 等价 Baseline Planner。
5. 启用影子 Planner：旧路径实际开火，新 Planner 只计算并输出差异。
6. 在影子结果通过后，用 `LegacySchedulerAdapter` 将普通独立组转换为现有 ArtilleryTask；保留运行时旧路径回退开关。
7. 完成运行时验收后删除旧 `PickTarget` / `CreateAutoTask`，结束第一里程碑。

**Milestone 1 完成情况（2026-08-20）：**

- 步骤 1–6 按序完成；影子验收 886/886 match、0 mismatch、0 fallback（三轮 legacy+shadow 运行时）。
- 步骤 7 已执行：删除 `FcsModule.PickTarget`、`FcsModule.CreateAutoTask`、legacy 左/右 picker 决策循环、`LegacyDecisionRecorder`、`ShadowPlanComparer`、`PlannerAuthority.Legacy` 与全部 legacy runtime fallback；确认无调用点后删除 `FSC.TryBuildClusterTask`。
- 默认自动模式改为 Baseline（模块初始化与热重载后均保持）；Ctrl+Numpad7 切换 Baseline ↔ PlanningDisabled（紧急停用：不创建任何自动任务，T1–T4 FIFO 与在途/活动任务不受影响）。
- PLAN-GOLDEN 格式诊断保留，权威来源改为 Baseline 计划本身（字段名 `legacy=` → `baseline=`）。
- 运行时验收记录：
  - [影子验收 run 1](../runtime-validation/runs/2026-08-20-shadow-acceptance-1.md)（commit c64e289，886/886 match）
  - [Baseline 权威实弹 run 1](../runtime-validation/runs/2026-08-20-baseline-authority-1.md)（commit c64e289，0 fallback，手动 T1–T4 FIFO/F9/无效扫描通过）
- 最终游戏内回归（切换后的新构建）仍待用户部署后完成：F9 默认 Baseline、移动目标完整自动射击周期、PlanningDisabled 停用/恢复、停用态 T1–T4 FIFO。

## 9. 第一里程碑非目标

- 新 Scheduler 或 FSC 拆分；
- 新的 `SameTrigger`、落地顺序或落地对齐执行逻辑；
- CBT、紧急移动和基金纪律；
- 多轮任务；
- 新集群算法；
- 自动退弹、强制状态恢复或 CleanState 重写；
- 击杀归因和长期 BattlePicture。

## 10. 验证与验收门槛

### 10.1 自动测试

- 双管空膛；
- 单管忙、单管空闲；
- 已装兼容/不兼容弹；
- 装填动画中；
- 已合栓 `CanFire`；
- 装药读数未知；
- 无效雷达扫描；
- T1–T4 在自动任务执行中到达；
- 目标在计划生成后死亡；
- 计划版本过期；
- FireGroup 原子接受/拒绝；
- FireGateLease 只允许授权炮管解除保险。

### 10.2 影子对照

比较 master 旧路径和新 Planner 的：目标、顺序、单点/集群、集群成员、落点、弹种、装药策略和移动快照。影子模式不得改变实际任务、登记或硬件状态。

### 10.3 游戏运行时验收

至少完成三种不同关卡或目标组成的运行，每种覆盖一个完整自动交战周期，影子计划零差异；同时通过 T1–T4、热重载和无效扫描场景。每轮验收记录版本、开始时间、场景、操作步骤和日志结论。

只有以上门槛全部满足，才能让新 Planner 成为权威路径。

**切换后游戏内回归清单（待用户部署执行）：**

1. F9 热重载后默认 Baseline（UI 显示 `Planner: BASELINE`，无 fallback 字样）；
2. 完成一轮移动目标自动射击（完整周期：规划 → 装填 → 装药定格 → 击发 → 退膛回位）；
3. Ctrl+Numpad7 切 PlanningDisabled，确认无新自动任务（日志 `planner mode: PlanningDisabled`，PLAN-GOLDEN `baseline=disabled`）；
4. 停用状态提交 T1–T4，确认 FIFO 仍按序执行；
5. 切回 Baseline，确认自动规划恢复；
6. 提供日志文件名和时间段归档至 `docs/runtime-validation/runs/`。

第一里程碑完成后，下一里程碑才设计并实现真正的 Scheduler/Executor 替换。
