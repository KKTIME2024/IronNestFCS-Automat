# Autonomy Core Runtime Validation — Shadow Acceptance Run 1
- Commit: c64e289 (fix: harden scan transactionality, blast reservations, and build isolation)
- DLL SHA-256 (verified in game dir after deploy.ps1):
  - Mods\IronNestFCS.dll: 540374F3E416442E14FC6AAC41023B22B39F78352DCE100A2BFDC5B4D6FEFA9F
  - UserData\IronNestFCS\IronNestFCS.Logic.dll: 328347C2BAFE2A7BD7D8198D119E32086BFB16E59815A5D8863885E69D38BC82
  - UserLibs\IronNestFCS.Domain.dll: 94944A7621A71D7154340A8A9C98479F72F3C15F36CA9FA6B870A8DCFD5285E3
  - UserLibs\IronNestFCS.Abstractions.dll: 0DE41D3BAE004E8DFCEADC02070405E61B968333FA415FB748DAC56C044679F4
- Scenario / target composition: 3 auto-engagement sessions in one game run (non-CBT).
  Hostiles: hostileartillery (soft, static), enemyantiarmor, enemyarmor (armored),
  enemyinfantry, enemycommander; 16-19 hostiles per scan; moving targets
  (velocity ~0.1 world units/s); HCHE/HE clusters of 4-6 entities with blast
  radius ~0.37 km; single-shot DRIL/AP engagements.
- Start and end local time: 2026-08-20 16:21:06 → 16:32:31
- Inputs: auto mode engaged (Numpad 0); F9 hot reload x2 (16:24:38, 16:28:40);
  manual turret rotation (16:28:40.686, triggered cancel+barrel-wash path);
  FireMissionMissing invalid-scan window 16:28:35-16:28:40 (radar kept last
  data, no reconcile — P0 protection exercised in the field).
- Complete automatic engagement cycle observed: yes — plan → load → charge
  decision (16:29:01.560 "装药定格 dist=5.35 → 2号") → first fire
  (16:29:00.403 "全自动模式:首次开火") → task complete → barrel wash
  (16:29:02.996). Cluster shot and single-shot DRIL both dispatched.
- PLAN-SHADOW matches: 886/886 across 3 segments (262 + 282 + 342, one per
  Logic load). 41 scans dispatched real plans (DRIL x66, AP x10, HCHE x6,
  HE x4 shot entries counting both plan halves); 845 scans agreed on no-fire
  (NoFreeBarrel x664, NoEligibleTarget x181). All diff= empty.
- PLAN-SHADOW mismatches: 0
- Lock leak / stuck task / unintended fire: none observed. No exception
  stacks in log. Manual turret rotation correctly cancelled tasks and washed
  barrels. Transient BuyShell "Can't find HCHE/DRIL card" errors at
  16:28:40.732/.745 were an F9-reload race (purchase deck re-initialized
  50 ms later at 16:28:40.799-807); self-recovered, no stuck state.
- Relevant log filename and line timestamps:
  MelonLoader\Logs\26-8-20_16-21-5.log
  - 16:21:10.887 Host Started (Logic path confirmed)
  - 16:21:40.330 first PLAN-SHADOW (scan=1, match=1, 2-shot DRIL plan)
  - 16:28:35.000-16:28:40.044 FireMissionMissing window (no reconcile)
  - 16:28:40.685 scan=348 HCHE cluster 5 members, match=1
  - 16:28:40.814 F9 reload #2, Initialize: success
  - 16:29:00.403 scan=1 (post-reload) HCHE cluster 4 members, match=1, first fire
  - 16:32:24.872 last PLAN-SHADOW (scan=342, match=1)
- Result: PASS
