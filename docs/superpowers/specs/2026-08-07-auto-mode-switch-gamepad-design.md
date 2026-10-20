# 设计:全自动/手动模式开关 + 手柄开关

日期:2026-08-07
来源:粉丝反馈两条——① 自动功能不能关闭,需要在全自动和手动标点 T1-T4 之间自如切换;② 需要纯手柄支持(后收敛为:手柄只需一个全自动开关)。

## 问题

1. 即使关闭扫荡(`sweepActive = false`),被动雷达仍每 5 秒扫描并**无条件覆盖地图标记 1-4**(`TacticalRadar.AutoPlaceMarkers` 恒 true),玩家手动拖放的标记会被冲掉 → "自动功能不能关闭"。
2. 全自动模式"开启了就停不下来":关闭后已入队的任务继续执行,手动无法及时接管。
3. mod 全部输入为键盘/鼠标(`Keyboard.current` / `Mouse.current`),无手柄支持。手柄玩家无法切换全自动模式。

## 背景:为什么手动模式更要紧

游戏已发正式版,新增大量内容,全自动模式(雷达扫描 + Top 2 填充)可能涵盖不了新目标类型。手动标点 T1-T4 是保底玩法,模式切换必须**可靠、即时可感、状态可见**。

## 方案(已确认)

- 单一开关 `autoMode`(由 `sweepActive` 语义升级而来)管全部自动行为。
- 手柄仅映射一个键:全自动开关。不做 T1-T4/弹种/装药的手柄映射。
- 不采用:双开关拆分、TacticalRadar 改造(不需要——FcsModule 挡住 Scan 调用即可)、摇杆模拟鼠标。
- **前置(Phase 0)**:先合入上游 svr2kos2 的正式版适配(新弹种),再实现切换逻辑。上游只做手动模式,全自动是本分支独有特性。

## Phase 0:上游正式版适配合并(前置)

上游 8-06/8-07 提交(我们 fork 点 9543162 之后 9 commits),只合与本项目 Logic 相关的正式版适配:

| 文件 | 上游改动 | 本地状态 | 合并方式 |
|---|---|---|---|
| `GunSystem.cs` | BulletType 枚举扩为 20 种(AP=1 不变,HE 3→10);null 防护 | 本地未改 | 直接替换 |
| `PurchaseDeck.cs` | 硬编码 5 卡 → `Dictionary<BulletType, Transform>` + `Enum.TryParse`(卡 ID 去 "Shell" 后缀自动匹配新弹种) | 本地未改 | 直接替换 |
| `TriggerConsole.cs` | null 防护 | 本地未改 | 直接替换 |
| `MapTable.cs` | 字段私有化(`turret`→private) | 本地加了 SetMarkerWorldPos/ResetMarker | 手工合:私有化 + 新增 `Turret` 属性供 TacticalRadar |
| `TacticalRadar.cs` | 上游无此文件 | 本地独有 | 2 处 `fcs.MapTable.turret` → `fcs.MapTable.Turret` |
| `FSC.cs` | +ReplenishPowderLoop(驻留协程:装药<6 每 5s 补一包,deskLock 保护)、ExposeAllEntities 改 VisualRoot | 本地大改(Entities 坐标重构) | 手工合:加 using/常量/TryBind 启动/方法 |
| `FcsSceneInteractor.cs` | 20 弹种按钮布局重排(x=0.8/y=-0.65 起 + y 阶梯),AutoFire/MaxCharge 挪入 TargetButtons | 本地有 3D 按钮 + URP 修复 | 手工合:保留本地增强,应用上游布局 |

不采纳:`csproj` GameDir(本地路径不同)、CustomRecords 改动(独立模块,与弹种无关,后续需要再合)。

兼容性确认(已核对):
- BulletType 按名字引用(AP/HE/HCHE),枚举重编号无编译影响;免疫字符串 `ToString()` 匹配不变
- `BuyPowders()`/`BuyShell(type, side)` 签名不变,FSC 调用兼容
- TacticalDecider 自动选弹目前只覆盖 AP/HE/HCHE——新弹种策略扩展不在本次范围(ponytail: 不做,后续有需要再说)

## 设计

### 1. 模式开关(FcsModule.cs)

`sweepActive` 改名/升级为 `autoMode`,语义:

| autoMode | 行为 |
|---|---|
| true(全自动) | 现状全部行为:炮退膛扫描填 Top 2、被动 5s 扫描、CBT 轮询、覆盖标记 1-4 |
| false(手动) | **雷达完全休眠**:不调 `radar.Scan()`、不轮询 CBT、不覆盖标记、不自动入队。手动 T1-T4(Numpad 1-4 / 3D 按钮)照常 |

- 切入手动时:`fcs.ClearPendingTasks()` 清掉自动入队的队列;正在执行的任务**不打断,打完自然停**(明确不做立即打断——协程断在装填/击发中间态会导致炮卡壳,之前 shell-loading 时序 bug 即此类)。停止后不再有新任务,手动立即接管。
- 切换入口:
  - 键盘:Numpad 0 / Ctrl+0(保留现有)
  - 手柄:`Gamepad.current` 的 Select(⧉)键,`wasPressedThisFrame` 读取
- `FcsWindow.AutoSweepEnabled` 接线(当前为死代码),IMGUI 窗口显示 `[AUTO]` / `[MANUAL]`。
- Update 中被动扫描块与 CBT 轮询块加 `autoMode &&` 条件。

### 2. 手柄映射(FcsModule.cs)

```
Select (⧉)  = 全自动开关(等价 Numpad 0)
```

- 只读 `Gamepad.current`(新 Input System,与 Keyboard 同源,热重载安全,无死区问题)。
- `Gamepad.current == null`(无手柄)时自然跳过,不影响键鼠玩家。

### 3. 文档(README)

按键表更新:手柄 Select = 全自动开关。

## 改动文件

| 文件 | 改动 |
|---|---|
| `IronNestFCS.Logic/FcsModule.cs` | autoMode 语义、手柄块、扫描/CBT 条件、清队列(核心,~20 行) |
| `IronNestFCS.Logic/FcsWindow.cs` | AutoSweepEnabled 接线 + `[AUTO]/[MANUAL]` 显示(约 5 行) |
| `README.md` | 按键表 |

TacticalRadar.cs / FSC.cs 零改动。

## 测试

不引入测试框架(IL2CPP 游戏内 mod,协程/队列/标记均为运行时行为,单测成本高收益低)。采用运行时验证清单,按步骤操作并逐条核对:

1. 编译通过。
2. 进游戏手动验证(热重载 F9):

**场景 A:手动模式(改动后基线)**
   - 开机默认手动 → 拖标记 → Numpad 1-4 / T1-T4 按钮开火
   - 等 >5s:敌情栏不刷新、标记不被覆盖(改动前此处会冲)

**场景 B:手动 → 全自动**
   - Select / Numpad 0 → IMGUI 显示 `[AUTO]`
   - 雷达接管:自动扫描、覆盖标记、退膛后清队列填 Top 2

**场景 C:全自动 → 手动(本次核心,含"停不下来"验证)**
   - 全自动运行中(队列有任务、炮正在打)按 Select / Numpad 0 → 显示 `[MANUAL]`
   - ① 队列立即清空(Queue 显示 0)② 正在打的任务继续执行完,不打断 ③ 之后无新任务入队 ④ 等 >5s 标记不再被覆盖、敌情栏不刷新
   - 手动立即接管:Numpad 1-4 开火生效

**场景 D:无手柄机器回归**
   - 不插手柄,确认 Numpad 0 行为与改动前一致(键鼠玩家不受影响)
