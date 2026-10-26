# Changelog

## [1.0.0] - 2026-10-15
### Added
- **Diagonal Strut Efficiency (Stage 1A):** Angled structural members ($20^\circ\text{–}65^\circ$, targeting $26^\circ$ and $45^\circ$ beams and logs) receive a configurable horizontal support loss discount (`StrutLossMultiplier = 0.45`).
- **Closed-Loop Truss Triangle Solver (Stage 1B):** Detects both 3-piece triangles ($A \leftrightarrow B \leftrightarrow C$) and 4-piece Warren truss triangles where a $4\text{m}$ chord is formed by two collinear $2\text{m}$ beams ($A \leftrightarrow B \leftrightarrow C \leftrightarrow D \leftrightarrow A$), granting a `0.50x` support loss reduction across all members in the triangle.
- **Dynamic Live Loads, Two-Stage Fracture & "Troll-Trap" Mechanics (Stage 3):**
  - Tracks live entity weight (Players + inventory, Carts + cargo, and heavy creatures like Trolls, Lox, Stone Golems, Abominations, and Berserkers), transferring deck loads into supporting beams.
  - **Two-Stage Player Fracture (`PlayerLoadDamagesBeforeDestroy = true`):** Overloading a span with a player, cart, or tamed creature fractures healthy pieces into Valheim's damaged (`20%` HP, `m_broken`) state with a `25-second` crossing grace window (`PostFractureGraceSeconds = 25.0`) so you can finish hauling your load across and repair with a Hammer; overloading an already-damaged piece after the grace window destroys it.
  - **Hostile Instant-Collapse (`HostileLoadDestroysImmediately = true`):** Untamed enemies (Trolls, Stone Golems, Abominations, Berserkers) bypass the fracture stage and immediately collapse overloaded spans.
- **Real-Time Acoustic & Visual Stress Feedback:**
  - **75%–89% Capacity (Creak Zone):** Emits Valheim's built-in 3D spatial timber/material creak audio and highlights stressed pieces in warm yellow-orange under load (even without the Hammer equipped).
  - **90%–99% Capacity (Snap / Crack Zone):** Emits sharp Valheim material fracture cracks, spawns splinter/sawdust particles, and highlights critically stressed pieces in red (`StressHighlightIntensity = 0.45`).
  - **Motion-Aware Acoustic Throttling:** Automatically quiets and spaces out settling creaks when standing still on a stressed span.
- **Fast Post-Load Structural Convergence:** Added a 6-frame Gauss-Seidel warmup coroutine on world/zone load so long-span trusses converge to their true support values in `< 0.6s` after player spawn.
- **Multiplayer Master Switch:** Added `[0. General] EnableMod` toggle in `com.kinematiclogic.vikingtruss.cfg`.
