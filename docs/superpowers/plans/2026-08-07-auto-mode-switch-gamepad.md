# 上游合并 + 全自动/手动切换 + 手柄开关 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 合入上游正式版适配(20 种新弹种),实现全自动/手动模式开关(关闭 = 雷达完全休眠,可随时手动接管),手柄 Select 键切换,并安装到正式版游戏目录。

**Architecture:** Phase 0 手工合入上游 8-06/8-07 正式版适配(3 个文件直接替换,3 个手工合并);Phase 1 把 `FcsModule.sweepActive` 升级为 `autoMode` 单一开关并接线 UI 状态;Phase 2 手柄 Select 映射;Phase 3 复制 Demo 版 MelonLoader 并部署四个 dll 到正式版 `F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator`。

**Tech Stack:** .NET 6 / MelonLoader (IL2CPP) / UnityEngine InputSystem。无测试框架(IL2CPP 运行时行为),验证 = 编译 + 运行时清单(见 spec)。

**Spec:** `docs/superpowers/specs/2026-08-07-auto-mode-switch-gamepad-design.md`

---

### Task 1: BulletType 枚举扩为 20 种(上游正式版弹种)

**Files:**
- Modify: `IronNestFCS.Logic/FCS/GunSystem.cs:10-16`

- [ ] **Step 1: 替换枚举**

将 `IronNestFCS.Logic/FCS/GunSystem.cs` 第 10-16 行的枚举替换为:

```csharp
public enum BulletType {
    AP = 1,
    APHE = 2,
    ATMC = 3,
    CLMN = 4,
    CYAN = 5,
    DRIL = 6,
    EQKE = 7,
    FLCH = 8,
    HCHE = 9,
    HE = 10,
    INCN = 11,
    LE = 12,
    PLCM = 13,
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}
```

注意:AP=1 不变,HE 3→10,HCHE 2→9,SMK 5→16,STAR 4→17。代码全部按名字引用,重编号无影响。

- [ ] **Step 2: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FCS/GunSystem.cs
git commit -m "feat: expand BulletType to 20 types (upstream release adaptation)"
```

---

### Task 2: GunSystem null 防护(上游)

**Files:**
- Modify: `IronNestFCS.Logic/FCS/GunSystem.cs`

- [ ] **Step 1: 应用上游 null 防护(4 处)**

1. `CanFire()`(第 79-81 行)改为:

```csharp
    public bool CanFire() {
        return gunController != null && gunController.CanFire;
    }
```

2. `SetElevation` 开头(第 83-85 行之间)插入:

```csharp
    public IEnumerator SetElevation(float elevation) {
        if (elevationLever == null || gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever or gun controller unbound");
            yield break;
        }
        elevationLever.SetSliderValue(elevation);
        yield return new WaitForSeconds(0.1f);
        while (!Mathf.Approximately(gunController.CurrentElevation, elevation)) {
            elevationLever.SetSliderValue(elevation);
            yield return new WaitForSeconds(1f);
        }
    }
```

(原 `while (gunController.CurrentElevation != elevation)` 改为 `Mathf.Approximately` 比较)

3. `WaitBackToIdle()`(第 171 行)改为:

```csharp
        while (gunController != null && gunController.elevationChangeVelocity != 0) {
```

4. `WaitFire()`(第 178 行)改为:

```csharp
        while (gunController != null && !gunController.pendingReload) {
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FCS/GunSystem.cs
git commit -m "fix: null guards in GunSystem (upstream release adaptation)"
```

---

### Task 3: PurchaseDeck 重构为 Dictionary 卡表(上游)

**Files:**
- Modify: `IronNestFCS.Logic/FCS/PurchaseDeck.cs`

- [ ] **Step 1: 替换字段与 TryBind**

`_heCard/_apCard/_starCard/_smkCard/_hcheCard` 五个字段(第 9-13 行)替换为:

```csharp
    private Dictionary<BulletType, Transform> bulletCards = new();
```

`TryBind` 的 switch(第 21-45 行)替换为:

```csharp
        foreach (var card in cards) {
            MelonLogger.Msg($"[FCS] PurchaseDeck: Found card {card.CurrentDefinition.ID}");
            if (Enum.TryParse(
                    card.CurrentDefinition.ID.Replace("SMOKE", "SMK").Replace("Shell", ""),
                    out BulletType type
                )) {
                bulletCards[type] = card.transform;
            }
            else if (card.CurrentDefinition.ID == "PowderCharges") {
                _powderCard = card.transform;
            }
        }
```

文件头部 `using System.Collections;` 后加 `using static System.Enum;`。

- [ ] **Step 2: 替换 BuyShell 卡查找**

`BuyShell` 的 switch 表达式(第 58-65 行)替换为:

```csharp
        var card = bulletCards.GetValueOrDefault(type);
```

其余(卡位移动、拨盘、买按钮)保持原样。

- [ ] **Step 3: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add IronNestFCS.Logic/FCS/PurchaseDeck.cs
git commit -m "feat: PurchaseDeck dictionary-based card lookup (upstream release adaptation)"
```

---

### Task 4: TriggerConsole null 防护(上游)

**Files:**
- Modify: `IronNestFCS.Logic/FCS/TriggerConsole.cs`

- [ ] **Step 1: 应用 3 处 null 防护**

1. `Fire()`(第 44-46 行):

```csharp
    public void Fire() {
        _fire?.AddEnergy(255);
    }
```

2. `Arm()`(第 48-54 行):

```csharp
    public IEnumerator Arm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        arm?.OnClickDown();
        yield return new WaitForSeconds(0.2f);
        arm?.OnClickUp();
        yield return new WaitForSeconds(1f);
    }
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FCS/TriggerConsole.cs
git commit -m "fix: null guards in TriggerConsole (upstream release adaptation)"
```

---

### Task 5: MapTable 字段私有化 + Turret 属性 + TacticalRadar 适配

**Files:**
- Modify: `IronNestFCS.Logic/FCS/MapTable.cs`
- Modify: `IronNestFCS.Logic/TacticalRadar.cs:290,302`

- [ ] **Step 1: MapTable 字段私有化**

`MapTable.cs` 第 9-12 行替换为(注意 `FireMission` → `fireMission`,`turret` → 私有 + 新属性):

```csharp
    private Transform? turret;
    private Dictionary<int, Transform> artilleries;
    private Transform? fireMissionRoot;
    private FireMission? fireMission;

    /// <summary>炮塔 Transform 只读访问(上游私有化后供 TacticalRadar 等使用)</summary>
    public Transform? Turret => turret;
```

`TryBind` 末尾(第 47-49 行)改为:

```csharp
        fireMissionRoot = fireMissionObject.transform;
        fireMission = fireMissionRoot.GetComponent<FireMission>();
        return fireMission != null;
```

其余(GetMarkTarget/SetMarkerWorldPos/ResetMarker/GetAllFireMissionEntities)保持不变。

- [ ] **Step 2: TacticalRadar 改用 Turret 属性**

`TacticalRadar.cs` 第 290、302 行的 `fcs.MapTable.turret` 都改为 `fcs.MapTable.Turret`。

- [ ] **Step 3: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED(若报 `fireMissionRoot`/`fireMission` 未用警告可忽略)

- [ ] **Step 4: Commit**

```bash
git add IronNestFCS.Logic/FCS/MapTable.cs IronNestFCS.Logic/TacticalRadar.cs
git commit -m "refactor: MapTable field privatization + Turret accessor (upstream release adaptation)"
```

---

### Task 6: FSC.cs 合入驻留装药补给协程(上游)

**Files:**
- Modify: `IronNestFCS.Logic/FSC.cs`

- [ ] **Step 1: 加 using 与常量**

文件头(第 5 行 `using UnityEngine;` 后)加:

```csharp
using UnityEngine.InputSystem;
```

类内字段区(第 30 行 `private const string HarmonyId...` 后)加:

```csharp
    // 驻留装药补给:装药低于 6 包时每 5s 补一包(deskLock 保护),与任务内购买互补。
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;
```

- [ ] **Step 2: TryBind 启动补给协程**

`TryBind` 中(第 96 行 `MelonLogger.Msg("[FCS] Initialize: ...")` 之后、`if (IsBound) RunEnemyProbe();` 注释块内)加:

```csharp
        if (IsBound) {
            // 驻留装药补给协程:保证装药充足,减少任务内等待购买。
            _runningCoroutines.Add(MelonCoroutines.Start(ReplenishPowderLoop()));
        }
```

- [ ] **Step 3: 新增 ReplenishPowderLoop 方法**

在 `ExposeAllEntities()` 方法前插入:

```csharp
    /// <summary>
    /// 驻留协程:每 5s 检查两管炮装药,低于阈值补一包。用 _deskLock 保护(采购台是共享硬件,
    /// 与任务内采购互斥)。TryBind 成功后登记进 _runningCoroutines,Dispose 时随协程一起 Stop。
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            // 取两管炮装药的最小值:任一管低于阈值就补
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add IronNestFCS.Logic/FSC.cs
git commit -m "feat: resident powder replenish loop (upstream release adaptation)"
```

---

### Task 7: FcsSceneInteractor 按钮布局重排(上游,20 弹种)

**Files:**
- Modify: `IronNestFCS.Logic/FcsSceneInteractor.cs`

- [ ] **Step 1: InitializeBulletTypeButtons 布局参数**

`InitializeBulletTypeButtons()`(第 37-85 行)中:

1. 第 38-39 行 `const float z = -18.4181f; float x = 0.3488f;` 改为:

```csharp
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
```

2. 循环内第 50 行 `button.transform.position = new Vector3(x, -0.6916f, z);` 改为:

```csharp
            button.transform.position = new Vector3(x, y, z);
```

3. 循环尾第 57 行 `x -= 0.05f;` 后加:

```csharp
            y -= 0.0045f;
```

4. 删除原方法内 AutoFire 与 MaxCharge 按钮两段(第 60-84 行)——它们移到 `InitializeTargetButtons()` 开头(见 Step 2)。

- [ ] **Step 2: InitializeTargetButtons 加 AutoFire/MaxCharge 并重排**

`InitializeTargetButtons()`(第 91-121 行)替换为:

```csharp
    /// <summary>
    /// Auto Fire / Max Charge 开关 + 4 个目标按钮(对应地图上 1~4 号炮兵标记)。
    /// 点击目标即用当前选中弹种为该目标入队一个任务,调度器自动派给空闲炮管。
    /// </summary>
    private void InitializeTargetButtons() {
        const float z = -18.5881f;
        var x = 0.8f;
        var y = -0.65f;

        GameObject? autoFireButton = null;
        autoFireButton = AddButton(() => {
            AutoFire = !AutoFire;
            SetColor(autoFireButton, AutoFire ? Color.red : Color.white);
        }, AutoFire ? Color.red : Color.white);
        autoFireButton.transform.position = new Vector3(x, y, z);
        autoFireButton.transform.localScale = Vector3.one * 0.02f;
        var autoFiretext = AddText("Auto Fire", 14f);
        autoFiretext.transform.SetParent(autoFireButton.transform, false);
        autoFiretext.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFiretext.transform.localScale = Vector3.one * 1.0f;

        x -= 0.05f;
        y -= 0.0045f;

        GameObject maxChargeButton = null;
        maxChargeButton = AddButton(() => {
            maxCharge = !maxCharge;
            SetColor(maxChargeButton, maxCharge ? Color.red : Color.white);
        }, maxCharge ? Color.red : Color.white);
        maxChargeButton.transform.position = new Vector3(x, y, z);
        maxChargeButton.transform.localScale = Vector3.one * 0.02f;
        var maxChargeText = AddText("Max Charge", 14f);
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one * 1.0f;

        x -= 0.05f;
        y -= 0.0045f;

        for (var i = 1; i <= 4; i++) {
            var targetId = i;
            GameObject button = null;
            button = AddButton(() => {
                var task = fcs.MapTable.GetMarkTarget(targetId);
                if (task == null) {
                    return; // 地图上没有这个编号的目标
                }
                task.targetId = targetId;
                task.bulletType = selectedBulletType;
                fcs.EnqueueTask(task);
                SetColor(button, Color.gray);
                button.GetComponent<Collider>().enabled = false;
                MelonCoroutines.Start(InvokeDelay(() => {
                    SetColor(button, Color.red);
                    button.GetComponent<Collider>().enabled = true;
                }, 1f));
            }, Color.red);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            targetButtons[targetId] = button;
            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }
```

保留本地 `AddButton`/`SetColor`(URP)/`AddText`/`WaitAndClick`/`InvokeDelay` 实现不动。

- [ ] **Step 3: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add IronNestFCS.Logic/FcsSceneInteractor.cs
git commit -m "feat: reflow console buttons for 20 bullet types (upstream release adaptation)"
```

---

### Task 8: GameDir 切换到正式版路径(两个 csproj)

**Files:**
- Modify: `IronNestFCS/IronNestFCS.csproj:11`
- Modify: `IronNestFCS.Logic/IronNestFCS.Logic.csproj:9`

- [ ] **Step 1: 替换 GameDir(两个文件)**

`IronNestFCS.csproj` 第 11 行和 `IronNestFCS.Logic.csproj` 第 9 行:

```xml
<GameDir>F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator</GameDir>
```

- [ ] **Step 2: 确认 MelonLoader 已就位后编译**

先执行 Task 12 Step 1(复制 MelonLoader),再:

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED,`IronNestFCS.Logic.dll` 输出到 `F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator\UserData\IronNestFCS\`

- [ ] **Step 3: Commit**

```bash
git add IronNestFCS/IronNestFCS.csproj IronNestFCS.Logic/IronNestFCS.Logic.csproj
git commit -m "chore: point GameDir to full-release install path"
```

---

### Task 9: 全自动/手动模式开关(autoMode)

**Files:**
- Modify: `IronNestFCS.Logic/FcsModule.cs`

- [ ] **Step 1: sweepActive 升级为 autoMode**

`FcsModule.cs` 全部 `sweepActive` 替换为 `autoMode`(字段、OnGunIdle 检查、Update 切换块),并加切换方法:

字段(第 21 行附近):

```csharp
    private bool autoMode;      // 全自动模式:true=雷达接管;false=手动(雷达完全休眠)
```

`OnGunIdle`(第 36 行)检查改为 `if (!autoMode) return;`。

`Update` 中 Numpad 0 切换块(第 111-117 行)改为:

```csharp
        // Numpad 0 / Ctrl+0: 切换全自动/手动模式
        if (kb.numpad0Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit0Key.wasPressedThisFrame))
        {
            ToggleAutoMode();
            return;
        }
```

新增方法(放在 `OnGui` 前):

```csharp
    /// <summary>切换全自动/手动模式。切手动时清空自动队列(正在打的不打断,打完自然停)。</summary>
    private void ToggleAutoMode()
    {
        autoMode = !autoMode;
        nextSweepTime = 0;  // 重设时间窗,防止立即触发的首轮被防重入窗口跳过
        if (!autoMode)
        {
            fcs.ClearPendingTasks();   // 手动接管:清掉自动入队的队列
            MelonLogger.Msg("[FCS] 手动模式:雷达休眠,手动标点 T1-T4 接管");
        }
        else
        {
            OnGunIdle();
            MelonLogger.Msg("[FCS] 全自动模式:雷达接管");
        }
        if (window != null) window.AutoSweepEnabled = autoMode;
    }
```

- [ ] **Step 2: 被动扫描与 CBT 轮询加 autoMode 条件**

`Update` 中被动扫描块(第 83-87 行):

```csharp
        // 被动扫描(只显示敌情)——仅全自动模式
        if (radar != null && fcs.IsBound && autoMode && Time.time - lastScanTime > 5f)
```

CBT 轮询块(第 90-91 行):

```csharp
        if (fcs.IsBound && autoMode && Time.time - lastCbtPollTime > 5f)
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add IronNestFCS.Logic/FcsModule.cs
git commit -m "feat: auto/manual mode switch — manual mode fully halts radar, clears queue"
```

---

### Task 10: IMGUI 面板显示 [AUTO]/[MANUAL]

**Files:**
- Modify: `IronNestFCS.Logic/FcsWindow.cs`

- [ ] **Step 1: 接线状态显示**

`FcsWindow.OnGui` 的 `[Sweep ON]` 块(第 57-63 行)替换为:

```csharp
        GUI.color = AutoSweepEnabled ? ClrSweep : ClrLabel;
        GUI.Label(new Rect(x, y, w, h), AutoSweepEnabled ? "[AUTO]" : "[MANUAL]");
        GUI.color = oldColor;
        y += lineH;
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FcsWindow.cs
git commit -m "feat: show [AUTO]/[MANUAL] mode in status panel"
```

---

### Task 11: 手柄 Select 键切换模式

**Files:**
- Modify: `IronNestFCS.Logic/FcsModule.cs`

- [ ] **Step 1: Gamepad 块**

`Update` 中,在键盘块之前(第 106-108 行 `var kb = Keyboard.current;` 之前)插入:

```csharp
        // 手柄:Select 键切换全自动/手动模式(等价 Numpad 0)
        var gp = Gamepad.current;
        if (gp != null && gp.selectButton.wasPressedThisFrame && fcs.IsBound)
        {
            ToggleAutoMode();
            return;
        }
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FcsModule.cs
git commit -m "feat: gamepad Select toggles auto/manual mode"
```

---

### Task 12: 安装到正式版(复制 MelonLoader + 部署 dll)

**Files:**
- Create(复制): `F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator\` 下的 `version.dll`、`MelonLoader/`、`Mods/`、`UserLibs/`

- [ ] **Step 1: 从 Demo 复制 MelonLoader 运行时**

Run(PowerShell):

```powershell
$demo = 'F:\SteamLibrary\steamapps\common\IRON NEST Heavy Turret Simulator Demo'
$game = 'F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator'
Copy-Item "$demo\version.dll" $game -Force
Copy-Item "$demo\MelonLoader" $game -Recurse -Force
```

Expected: 正式版根目录出现 `version.dll` 与 `MelonLoader/`(含 net6/Il2CppAssemblies)

- [ ] **Step 2: 全量构建**

Run: `dotnet build IronNestFCS.sln -c Release`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: 部署 Host / Abstractions / CustomRecords**

Run(PowerShell):

```powershell
$game = 'F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator'
New-Item "$game\Mods", "$game\UserLibs" -ItemType Directory -Force
Copy-Item "IronNestFCS\bin\Release\net6.0\IronNestFCS.dll" "$game\Mods\" -Force
Copy-Item "IronNestFCS.Abstractions\bin\Release\net6.0\IronNestFCS.Abstractions.dll" "$game\UserLibs\" -Force
Copy-Item "IronNestFCS.CustomRecords\bin\Release\net6.0\IronNestFCS.CustomRecords.dll" "$game\Mods\" -Force
Get-ChildItem "$game\Mods", "$game\UserLibs", "$game\UserData\IronNestFCS" | Select-Object FullName, Length
```

Expected(四 dll 就位):
- `Mods\IronNestFCS.dll`、`Mods\IronNestFCS.CustomRecords.dll`
- `UserLibs\IronNestFCS.Abstractions.dll`
- `UserData\IronNestFCS\IronNestFCS.Logic.dll`(构建时由 OutputPath 直接输出)

- [ ] **Step 4: 启动验证**

启动正式版游戏,确认 MelonLoader 控制台显示 IronNestFCS 加载成功、无绑定错误。若场景内面板显示 `Waiting for scene...`,按 F9 重绑定。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: deploy mod to full-release install path"
```

---

### Task 13: 运行时验证清单(进游戏)

**Files:** 无(手工验证,来自 spec 场景 A-D)

- [ ] **Step 1: 场景 A — 手动模式基线**

拖标记 → Numpad 1-4 / T1-T4 按钮开火;等 >5s:敌情栏不刷新、标记不被覆盖。

- [ ] **Step 2: 场景 B — 手动 → 全自动**

Select(或 Numpad 0)→ 面板显示 `[AUTO]`;雷达接管:自动扫描、覆盖标记、退膛后清队列填 Top 2。

- [ ] **Step 3: 场景 C — 全自动 → 手动(核心)**

全自动运行中按 Select → `[MANUAL]`;① 队列清空(Queue 0)② 在打的任务继续执行完 ③ 之后无新任务 ④ >5s 后标记不被覆盖、敌情栏不刷新;Numpad 1-4 手动开火立即生效。

- [ ] **Step 4: 场景 D — 无手柄回归**

拔手柄,确认 Numpad 0 行为与改动前一致。

- [ ] **Step 5: 新弹种抽查**

全自动模式下确认新弹种枚举生效(日志 `[Radar]` 行弹种选择无异常);手动模式下 20 个弹种按钮可见、可点选(排布在控制台旁)。
