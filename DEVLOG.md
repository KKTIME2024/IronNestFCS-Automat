# Dev Log — 2025-07-25

## 架构
在原版 svr2kos2 FCS 基础上渐进式添加战术智能，安全边界：不改 GunSystem / BallisticCalc / TriggerConsole / CoroutineLock。

## 今日改动

### 新增
- `TacticalDecider` — 纯数据决策模块：弹种选择、装药决策、优先级+转角排序
- `TacticalRadar` — FireMissionRoot 扫描 + Entity反射读取 Role/Icon/Stars
  - 敌我识别：Role bitmask (Enemy/Ally/Target/Reference/Artillery/Fortification/Tank)
  - 地下检测：名字关键词 (bunker/underground/shelter/pillbox/dugout/bompproof)
  - 优先级体系：5(火炮/FDC) > 4(弹药库/高价值/3星) > 3(装甲/工事/1星) > 2(普通) > 1(其他)

### 弹种 & 装药
- 当前全部 AP（弹种选择留到后续调优）
- 优先级 >=4 自动满装药 (6包 MAX)

### 队列模型
- `FSC.OnGunIdle` 事件 — 每炮退膛完成时触发
- 扫描 → 取 Top 2 未打目标 → 清空队列(fp) → 填入新任务
- 跳过另一门炮正在打的目标 (避免重复)
- `FSC.ClearPendingTasks()` — 只清队列不碰槽位
- `FSC.EnqueueTaskFront()` — 高优先级插队

### Numpad 0
- 开关扫荡循环，默认关（不会进游戏就自动打）

## 待调查：游戏可读信息
  - 电报消息 (Telegram/MissionBriefing) — 关卡目标、敌军位置描述
  - 侦查报告 (LocationReport card) — 已购买的报告类型
  - 观测机/侦察机状态 (ScoutPlane card)
  - 天气/风向 (WindController?) — 影响弹道修正
  - FireMission 的 Phase/TimeRemaining — 关卡阶段/倒计时
  - 地图上的敌军类型 Icon 字符串 — 完整枚举
  - Spotter / MoveZone / MoveDirection cards — 已激活的战术支援

## 坑
- `ElevationErrorDeg` 在 Demo 版不存在 → 用回 `CurrentElevation`
- `CoroutineLock` 带超时会导致锁被强制抢走 → 取消超时
- `OnGunIdle` 双炮同帧退膛 → 加 0.5s 窗口防重入
- `ClearAllTasks` 清槽位导致一炮正在执行的任务丢失 → 改为只清队列
