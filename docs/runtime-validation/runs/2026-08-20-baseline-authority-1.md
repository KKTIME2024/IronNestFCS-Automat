# Autonomy Core Runtime Validation — Baseline Authority Run 1
- Commit: c64e289 (same build as shadow acceptance run 1)
- DLL SHA-256: identical to 2026-08-20-shadow-acceptance-1.md (no redeploy between runs)
- Scenario / target composition: 1 auto-engagement session under Baseline
  authority (Numpad7 toggle at 16:39:23.543), non-CBT. Hostiles: enemyfield
  (armored field guns, ARM), enemymech (moving mechanized), enemyinfantry;
  14 hostiles per scan; moving targets with known velocity (vk, v~0.025
  world units/s); HE cluster on moving leader with infantry member; AP
  single shots and two-barrel AP salvo on field guns; 4 manual tasks
  (T1-T4, incl. STAR illumination) interleaved.
- Start and end local time: 2026-08-20 16:38:11 → 16:46:55 (game exit)
- Inputs: Numpad7 authority toggle to Baseline (16:39:23); manual tasks
  T1-T4 issued 16:39:51-16:40:01 (manual=1→4); no F9 during session
  (scan counter 1..676, zero resets).
- Complete automatic engagement cycle observed: yes — first plan 16:39:26
  (two-barrel AP salvo, free=2, group-shot- IDs = new planner); load
  (16:39:29 AP in both cylinders) → charge decision (16:39:34/37
  "装药定格 dist=10.29→3号 / dist=5.35→2号") → fire → next round
  (16:40:21/23, 16:40:42/43, 16:41:04/05). Moving-target HE cluster
  dispatched 16:41:25 (group-cluster-enemymech#4, v=0.0245,0.0021,-0.0037,
  member enemyinfantry#7).
- PLAN-SHADOW: 0 (expected — shadow comparison only runs under legacy
  authority)
- PLAN-FALLBACK: 0 (acceptance criterion met)
- PLAN-GOLDEN: 682 — 30 scans with real plans (DRIL x27, AP x3, HE x1 shot
  entries counting both halves), 496 NoFreeBarrel, 84 NoEligibleTarget.
  All plan lines carry new-planner group IDs (group-shot-/group-cluster-).
- Manual coexistence: planner yielded while manual queue non-empty
  (free=0, manual=1..4, legacy=-); manual FIFO consumed in order incl.
  STAR round (16:40:20/22); planner resumed after queue drained.
  AutoReplenish bought powder (16:40:24).
- Lock leak / stuck task / unintended fire: none. 0 ERROR, 0 Exception.
  Game exit 16:46:50-55: FireMissionMissing x18 — radar kept last data,
  no reconcile, no dispatch (P0 guard at scene teardown); clean exit
  ("Preferences Saved!").
- Relevant log filename and line timestamps:
  MelonLoader\Logs\26-8-20_16-38-11.log
  - 16:39:23.543 planner authority: Baseline
  - 16:39:26.383 scan=1 two-barrel AP salvo (first new-planner plan)
  - 16:39:51.454-16:40:01.460 manual=1→2→4 (planner yielded)
  - 16:40:20.504/16:40:22.642 STAR manual rounds loaded
  - 16:41:25.932 scan=142 moving HE cluster (vk)
  - 16:46:50.660-16:46:55.206 FireMissionMissing window at exit (no reconcile)
  - 16:46:55.805 Preferences Saved! (game exit)
- Result: PASS
