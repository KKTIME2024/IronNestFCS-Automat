# IronNestFCS 升级可行性报告

## 从一战火炮到数字化火控系统的上限分析

> 基于游戏 `Assembly-CSharp.dll` 二进制逆向分析，提取 ~3,000 属性名，重建游戏系统全景图。
> 调研日期：2026-07-26

---

## 目录

1. [方法](#1-方法)
2. [游戏系统全景](#2-游戏系统全景)
3. [Tier 1：立即可实现](#3-tier-1立即可实现)
4. [Tier 2：中等开发量](#4-tier-2中等开发量)
5. [Tier 3：硬边界](#5-tier-3硬边界)
6. [关键调研项](#6-关键后续调研)
7. [结论](#7-结论)

---

## 1. 方法

### 1.1 数据来源

通过 IL 属性提取（`get_*` 模式匹配）从以下文件提取类型和属性名：

```
MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll
```

由于游戏使用 IL2CPP 编译，此 DLL 是 Il2CppInterop 生成的托管互操作存根，
包含完整的类型层次、属性和方法签名——等同于读数据库 schema。

### 1.2 现有 FCS 已使用的接口

| 类型 | 用途 | 使用深度 |
|---|---|---|
| `EntityLocation` | 读取单位位置/存活状态/Role/Icon/Stars | 中等（反射读取 Role，未用所有字段） |
| `TurretController.DesiredRotation` / `rotationVelocity` | 炮塔方向转向 | 充分使用 |
| `GunController.CanFire` / `CurrentElevation` / `ChamberedShellBlueprint` / `elevationChangeVelocity` / `pendingReload` | 仰角控制与击发状态 | 充分使用 |
| `FireMission` | FireMissionRoot 绑定 | 仅绑定，未读 Mission 数据 |
| `OdometerDisplay.currentNumber` | 读取弹道计算器仰角输出 | 充分使用 |
| `DialInteractable.SetDialValue()` | 设定距离/方向/装药/弹种拨盘 | 充分使用 |
| `LookAtTarget.OnClickDown()` / `OnClickUp()` | 按钮点击模拟 | 充分使用 |
| `CylinderShellSelector.bullets` | 弹仓内容读取 | 充分使用 |
| `LinearSliderInteractable.SetSliderValue()` | 仰角杆控制 | 充分使用 |
| `PunchcardRuntime.CurrentDefinition.ID` | 采购卡牌识别 | 充分使用 |
| `DraggableItem.MoveToSlot()` | 卡牌拖拽 | 充分使用 |
| `SliderEnergyMomentumSpinner.AddEnergy()` | 击发扳机 | 充分使用 |

---

## 2. 游戏系统全景

### 2.1 Counter-Battery：反炮兵系统 ★★★★★

```
CounterBatteryTimer              CounterBatteryTimeRemaining
```

游戏内置反炮兵 AI——敌方在你开火后启动倒计时，归零时向你的位置炮击。
`CounterBatteryTimeRemaining` 是实时倒计时数据源。

**价值：** 这是整个游戏中最接近"21 世纪火控"的游戏机制——shoot-and-scoot 的完美数据输入。

### 2.2 Impact Correction Tiers：着弹修正 ★★★★★

```
ImpactCorrectionTierController    CorrectionDirectionTierConfig
CorrectionDistanceTierConfig      CorrectionEnabled
ActiveTierController              AutoLocateTierController
TierLookupRetryDelay              OnActiveTiersChanged
```

游戏不是二元命中/偏离——有多级弹道精度修正。每次偏离后精度自动提升（下一发更准）。

**价值：** 定量火力修正的基础。知道当前 Tier → 知道散布椭圆大小 → 知道需要多少发来覆盖目标。

### 2.3 Trajectory Prediction：弹道预测 ★★★★

```
PredictedImpactTime                PredictedRangeIfFiredNow
CurrentTimeToImpactSeconds         OnPredictedImpactTimeChanged
TrajectoryTargetRegistry           CalculateProjectedRangeFromElevation
```

游戏内部计算预计着弹时间、当前装填状态下的预计射程。

**价值：** MRSI（多弹同时弹着）的基础数据——知道每发飞行时间，
反算不同仰角/装药组合，使两管炮的炮弹同时落地。

### 2.4 Shell Definition：弹种属性 ★★★★

```
ShellDefinition          ShellBlueprint            ShellBlueprintPrefab
ShellId                  ShellSpeed                ShellEffectRule
ImmuneShells             ShellInstanceId           ShellInsertionMode
FireShell                ImpactShell               ShellLanded
TrackShell               EjectChamberedShell       TransferShellToChamber
HasLoadedShell           LoadedShellPrefab
```

弹种不只是 ID 字符串。有速度属性、效应规则、免疫表。

**价值：** 自动弹种匹配——读 `ImmuneShells` 避免打无效弹种，读 `ShellEffectRule` 选取最优弹种。

### 2.5 Mission System：任务系统 ★★★★

```
MissionData        MissionJson           MissionDefinition
MissionName        MissionDescription    MissionType
MissionPhase       MissionTime           CurrentMissionState
MissionGraph       MissionNode           MissionEvents
MissionStatsTracker                     MissionChanged
FinishMission      EndMission            RestartMission
LoadMission        ImportMission         GenerateMission
OperationGraph     OperationStates       CurrentOperation
```

关卡目标、阶段、时限全部数据驱动。

**价值：** 任务感知火控——根据关卡目标自动切换优先级策略。

### 2.6 Entity Attributes：实体属性 ★★★★

```
Stars    Armour    Health    MaxHealth    Role    RoleValue
IsAlive    OnEntityDestroyed    EntityFilter
TargetPower    TargetValue    EntityConditions    EntitySelection
```

远比当前 `TacticalRadar` 用到的 `Role`/`Icon`/`Stars` 字段丰富。

**价值：** 定量威胁评估——`Armour × TargetPower × 1/Distance × CounterBatteryRisk` 的动态加权。

### 2.7 Damage / Impact Pipeline：伤害管线 ★★★★

```
SpawnImpactEffectAt         SpawnExplosionNextFrame
ImpactRadius                ImpactEntities
EntitiesToDamage            OnTakeDamage
DamageChanged               FirstDamageTaken
AverageImpactDistanceFromNearestTarget
HitsOnTargets               ImpactLocation
OnImpact                    ImpactShell
```

事件驱动的伤害系统。每次命中都触发完整的事件链。

**价值：** 事件驱动 BDA——不是靠周期性扫描猜测存活状态，
而是直接监听 `OnEntityDestroyed` 和 `DamageChanged` 事件获得精确的命中反馈。

### 2.8 Recon / Scout：侦察观测 ★★★

```
ReconUsed            ReconUsedAfterFirstShot     IsRecon
ScoutingStrip        SpawnScoutPlane             ScoutPlane (类型)
MapReconClearer      MapReconClearHandle
```

**价值：** 自动侦察调度——开火后自动触发侦察获取 BDA。

### 2.9 Teleprinter：电报通讯 ★★★

```
Teleprinter          TeleprinterType
NewTeleprinterStartTrigger         OnMessage
MessageID
```

**价值：** 读取游戏内通讯（关卡简报、命令更新）作为战术情报输入。

### 2.10 Scoring / Statistics：评分统计 ★★★

```
Accuracy       ShotsFired     ShotsHit       MissedShots
ShellsFired    EnemyKills     AllyKills      DirectHits
MostKillsBySingleImpact       TargetKills    TargetsDestroyed
Score          ScoreDelta     CurrentScore   PointsDeductionDelay
```

**价值：** 射击效能仪表板 + 实时优化反馈。

### 2.11 未发现的关键系统

| 系统 | 搜索关键词 | 结果 |
|---|---|---|
| 气象/风速 | `WindController`, `Weather`, `Atmosphere`, `Barometric` | `Wind` 字符串出现 35 次但无独立控制器属性。可能是视觉动画而非弹道参数 |
| 移动目标速度 | `Velocity`, `Speed`, `MoveSpeed`, `Direction` | 有 `MovementSpeed`/`MovementType`/`MovementStartLoc`/`MovementTargetLoc`，可能通过 `Entity` 实例暴露 |
| GPS/惯导 | `GPS`, `INS`, `Inertial`, `Navigation` | `Navigate` 存在但含义不明 |
| 激光指示 | `Laser`, `Designator`, `Illuminate` | 无 |
| 热成像 | `Thermal`, `Infrared`, `IR`, `NightVision` | 无 |
| 电子战 | `ECM`, `EW`, `Jammer`, `Radar` | 无 |
| 无人机 | `Drone`, `UAV` | 无（仅有 `ScoutPlane`） |
| 数据链 | `Network`, `DataLink`, `C4I` | 无 |

---

## 3. Tier 1：立即可实现

只改 FCS Logic 层，不碰游戏。

### 3.1 Shoot-and-Scoot（急停射击）

**数据源：** `CounterBatteryTimeRemaining`

**逻辑：**
1. 每轮开火后启动 `CounterBatteryTimer`
2. 倒计时归零前 N 秒暂停火力
3. 自动转炮塔至安全方向 + 降仰角
4. 倒计时归零 → 敌方炮击落地 → 恢复火力

**难度：** 低。约 50 行新代码。

---

### 3.2 动态威胁排序

**数据源：** `Armour`, `Health`, `TargetPower`, `Distance`

**逻辑：**
```
威胁值 = Armour × TargetPower × (1 + 1/Distance) × IsArtillery × CounterBatteryRisk
```
替代当前的硬编码 1-5 优先级。

**难度：** 低。修改 `TacticalDecider.CalcPriority` 约 20 行。

---

### 3.3 弹种免疫匹配

**数据源：** `ImmuneShells`

**逻辑：** 创建任务前检查目标免疫表，跳过无效弹种。

**难度：** 低。在 `TacticalDecider` 加一层过滤。

---

### 3.4 火力修正（Walking Fire）

**数据源：** `ImpactCorrectionTier`, `AverageImpactDistanceFromNearestTarget`

**逻辑：**
1. 首轮命中偏离 → 读当前 `CorrectionTier`
2. 自动重算时在弹道计算中加入修正量
3. 连续命中 → Tier 提升 → 停止修正

**难度：** 中。需要实现偏差估算模型。约 80 行。

---

### 3.5 自动侦察调度

**数据源：** `ReconUsed`, `ReconUsedAfterFirstShot`

**逻辑：** 每轮火力结束后自动触发侦察（如果侦察通过卡片机制运作）。

**难度：** 中。需要研究侦察卡片的采购/激活机制。

---

### 3.6 事件驱动 BDA

**数据源：** `OnEntityDestroyed`, `OnTakeDamage`, `DamageChanged`

**逻辑：** 用 Harmony patch hook `OnEntityDestroyed` 事件，
替代当前 `TacticalRadar.Scan()` 的周期性轮询。

**难度：** 中。约 60 行 + 一个 Harmony patch。

---

### 3.7 任务感知策略

**数据源：** `MissionData`, `MissionJson`, `MissionType`, `CurrentMissionState`

**逻辑：** 读当前关卡类型 → 切换策略配置：
- 防空优先关卡 → 优先清除 `Artillery` 单位
- 时间限制关卡 → 优先近距离高价值目标
- 无尽波次 → 优先弹药物资效率

**难度：** 中。需要逆向 `MissionJson` 的 schema。

---

## 4. Tier 2：中等开发量

需要计算模型 + 游戏接口深度整合。

### 4.1 MRSI（多弹同时弹着）

**数据源：** `PredictedImpactTime`, `PredictedRangeIfFiredNow`

**原理：**
1. 两种装药 + 仰角组合可命中同一目标
2. 高装药高仰角 → 飞行时间长
3. 低装药低仰角 → 飞行时间短
4. 反解：知道飞行时间差值 → 两管炮错峰装填 → 同时落地

**难度：** 高。需要实现弹道时间模型并反解装药/仰角组合。约 200 行。

---

### 4.2 多目标火力规划

**输入：** 弹药库存、装药库存、目标列表、CounterBattery 时间限制

**原理：** 线性规划——在 CounterBattery 时间窗口内最大化目标摧毁数量。

**难度：** 高。约 150 行优化逻辑。

---

### 4.3 散布椭圆估算

**数据源：** `CorrectionDirectionTierConfig`, `CorrectionDistanceTierConfig`

**原理：** 每种 Tier 对应一个散布椭圆参数（方向偏差 × 距离偏差），
以此计算首轮命中概率和所需总弹药量。

**难度：** 高。需要逆向 `CorrectionTierConfig` 的字段。

---

## 5. Tier 3：硬边界

游戏引擎不建模这些物理/系统，无法实现。

| 功能 | 限制 | 原因 |
|---|---|---|
| GPS/INS 制导炮弹 | 不可行 | 游戏无制导模型、无目标追踪 API |
| 激光目标指示 | 不可行 | 无指示器/照射/反射机制 |
| 无人机视频馈送 | 不可行 | `ScoutPlane` 是卡片而非实体摄像头 |
| 相控阵雷达 | 不可行 | 游戏无雷达模型 |
| 真实风偏修正 | 不可行 | 无独立 wind controller，弹道偏差走内置 correction tier |
| 打移动目标 | 大概率不可行 | `EntityLocation` 暴露的疑似无 velocity 字段；`MovementSpeed`/`MovementType` 可能仅用于特定 AI 单位 |
| 网络化多炮协同 | N/A | 游戏只有一个炮塔 |
| 末敏弹/子母弹 | 待验证 | 检查 `ShellEffectRule` 是否支持 area effect |
| 近炸/时间引信 | 待验证 | 检查 `ShellDefinition` 是否有 fuze 字段 |
| 数据链/火力网 | 不可行 | 单人单炮塔游戏 |

---

## 6. 关键后续调研

按优先级排序：

### 6.1 `ShellDefinition` / `ShellBlueprint` 完整字段 ★★★★★

**方法：** 用 Harmony patch 在运行时 dump 所有字段值，或在 Unity Explorer 中检查。
**目的：** 确认弹种是否有速度、重量、装药率、引信类型等字段——决定弹种选择的自动化上限。

### 6.2 `Entity` 完整字段（尤其是速度）★★★★★

**方法：** 反射 dump `EntityLocation.Entity` 的所有属性名。
**目的：** 确认是否存在 `Velocity` / `Speed` / `Direction` / `MoveState`，
这决定了"打移动目标提前量"的可实现性。

### 6.3 `MissionJson` Schema ★★★★

**方法：** 在运行时截获 `MissionJson` 的字符串值。
**目的：** 理解关卡数据结构 → 任务感知火控的依据。

### 6.4 Counter-Battery 是真实机制还是 UI ★★★★

**方法：** 游戏中观察是否真的有敌方炮弹落下。
**目的：** 如果只是 UI 条而无实际炮击，shoot-and-scoot 价值大减。

### 6.5 `OnImpact` 事件能否被 Harmony Patch ★★★★

**方法：** 尝试 patch `OnImpact` 的 IL 方法。
**目的：** 事件驱动 BDA 的唯一路径——如果 patch 失败则只能继续轮询。

### 6.6 `CorrectionTierConfig` 字段结构 ★★★

**方法：** 运行时 dump。
**目的：** 散布椭圆模型的参数来源。

---

## 7. 结论

### 核心发现

IronNestFCS 的游戏数据模型远比其"一战炮手模拟器"的表象丰富。以下是按技术成熟度的能力分层：

```
已实现          Tier 1 (1-2周)          Tier 2 (2-4周)          Tier 3 (不可行)
─────           ─────────────           ─────────────           ─────────────
弹道自动解算    Shoot-and-Scoot        MRSI 时间同步齐射        GPS/INS 制导
双炮管调度      动态威胁排序           多目标火力规划            激光指示
弹种装填        Walking Fire 火力修正   散布椭圆建模             实时视频 BDA
自动采购        弹种免疫匹配                                      真实风偏
5 步确认击发    自动侦察调度                                      移动目标
地图标记        事件驱动 BDA                                      雷达/ESM
战术雷达扫描    任务感知策略                                      数据链协同
战术决策排序    Counter-Battery 规避                              末敏弹药
```

### 上限判断

**可达到的自动化等级：21 世纪数字化炮兵连的 60-70%。**

铁穹 FCS 可以实现：
- 自动发现、识别、排序、分配、打击的全流程（已完成）
- 基于反炮兵威胁的火力节奏管理（Tier 1）
- 基于实际命中反馈的迭代火力修正（Tier 1）
- 两管炮的协调火力——时间同步齐射（Tier 2）
- 基于任务目标的战术策略切换（Tier 1）

无法实现的：
- 精确制导弹药（游戏无双向数据链/末制导 API）
- 传感器融合（无人机视频、雷达、红外影像）
- 移动目标拦截（游戏大概率不暴露目标速度）
- 无人化作战（游戏内所有交互仍需要物理拨盘/按钮）

### 一句话

**一个单人单炮塔的物理模拟器，通过读取其内部数字状态流，可以模拟出现代数字化炮兵连 60% 的自动化能力——差距主要在传感器和制导，不在火控。**
