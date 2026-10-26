# VikingTruss — True Structural Engineering for Valheim

**VikingTruss** replaces Valheim's naive distance-decay building integrity calculation with a lightweight, deterministic structural topology solver and dynamic live-load engine.

Instead of punishing diagonal bracing and triangulated trusses with extra node-hop losses, **VikingTruss** rewards real structural engineering—allowing properly braced Warren, Pratt, and Howe truss bridges to span more than **$3.5\times$** further than unbraced horizontal cantilevers, while introducing dynamic weight limits (**"Troll-Trap" mechanics**) and real-time **creaking, cracking, and visual stress feedback**.

![30m Triangulated Warren Truss vs. 8m Unbraced Cantilever](https://raw.githubusercontent.com/KinematicLogic/VikingTruss/main/docs/bridge_comparison.jpg)
*Above: A 30m triangulated Warren truss cantilever built entirely out of basic wood alongside the vanilla 4-beam (8m) unbraced horizontal limit.*

![Real-Time Live Load Stress Highlight & Splinter Feedback](https://raw.githubusercontent.com/KinematicLogic/VikingTruss/main/docs/stress_highlight.jpg)
*Above: Subtle real-time stress highlighting under live load when approaching structural capacity—no Hammer required.*

---

## Key Features

### 1. Diagonal Strut Efficiency
- Angled structural members oriented between **$20^\circ$ and $65^\circ$** from horizontal (Valheim's $26^\circ$ and $45^\circ$ wood and core-wood beams) suffer **`55%` less horizontal support loss** (`StrutLossMultiplier = 0.45`).
- Flat horizontal ($0^\circ$) and vertical ($90^\circ$) pieces retain their standard vanilla support rules, so unbraced horizontal beams still break after 4 pieces (`8m`) just like vanilla.

### 2. Closed-Loop Truss Triangle Solver (3-Piece & 4-Piece Chords)
- Evaluates physical collider joints (`<= 12cm` snap contact) to verify closed, non-collinear structural triangles:
  - **3-Piece Triangles ($A \leftrightarrow B \leftrightarrow C$):** Right-angle trusses (vertical post + horizontal beam + diagonal strut) or $4\text{m}$ Core Wood triangles.
  - **4-Piece Collinear-Chord Triangles ($A \leftrightarrow B \leftrightarrow C \leftrightarrow D \leftrightarrow A$):** Recognizes Valheim's Warren truss geometry where two $45^\circ$ or $26^\circ$ diagonals span a $4\text{m}$ horizontal base made of **two collinear $2\text{m}$ horizontal beams**.
- All members in a closed structural triangle receive a **`50%` support loss reduction** (`TrussTriangleMultiplier = 0.50`).
- Strictly rejects unbraced 4-corner rectangles/boxes and multiple beams stacked at a single corner.

### 3. Dynamic Live Loads: Two-Stage Player Fracture & "Troll-Trap" Collapse
- Non-grounded horizontal spans dynamically track the weight of entities resting on or crossing them:
  - **Players:** `100 kg` base body mass + current inventory weight (`m_inventory.GetTotalWeight()`).
  - **Carts (`Vagon`):** Cart chassis mass + container cargo weight.
  - **Creatures:** Scaled to realistic structural weights (e.g., **Trolls `950 kg`**, **Lox `800 kg`**, **Stone Golems / Abominations `1,100 kg`**, **Fuling Berserkers `550 kg`**).
- **Deck-to-Beam Load Transfer:** Standing or rolling a heavy cart across `wood_floor` tiles automatically transfers load into the supporting `wood_beam` chords directly underneath.
- **Two-Stage Fracture for Player Loads (`PlayerLoadDamagesBeforeDestroy = true`):**
  - When a player-influenced overload (Player + inventory, Cart + cargo, or tamed creature such as a tamed Lox) exceeds a span's capacity (`EffectiveSupport < MinSupport`), **healthy pieces fracture into Valheim's damaged (`20%` HP) state** instead of collapsing immediately, granting a **`25-second` grace window** (`PostFractureGraceSeconds = 25`) so you can finish hauling your heavy cart across the bridge and repair the fractured timbers with a Hammer.
  - Taking another heavy load across an **already-damaged** span after the grace window destroys the fractured pieces!
- **Hostile Instant-Collapse (`HostileLoadDestroysImmediately = true`):**
  - Untamed enemies (Trolls, Stone Golems, Abominations, Fuling Berserkers) bypass the fracture stage and **immediately collapse** under-built spans (**"Troll-Trap" mechanics**).

### 4. Real-Time Audio & Visual Stress Feedback (No Hammer Required)
- **75%–89% Capacity (Creak Zone):**
  - Emits 3D spatial **timber/material creaks and groans** using Valheim's built-in studio audio clips.
  - Automatically highlights the stressed piece in **warm yellow-orange** under your feet (even without the Hammer equipped).
- **90%–99% Capacity (Snap / Crack Zone):**
  - Emits sharp **wood/material fracture cracks**, spawns a subtle burst of **sawdust/splinter particles**, and highlights the piece in **bright red** to warn of imminent fracture or collapse.

---

## Normal Wood Live-Load Reference Table

*(Note: In Valheim, your character's body mass is `100 kg`, so `Total Mass = 100 kg + Inventory Weight`.)*

| Bridge Section & Hammer Color | Unloaded `BaseSupport` | **75% Creak + Orange Glow** | **90% Crack + Red Glow** | **100% Fracture / Collapse** |
| :--- | :---: | :--- | :--- | :--- |
| **Grounded** *(Blue)* | `100` | Never *(Grounded)* | Never *(Grounded)* | Never *(Grounded)* |
| **Near Pier** *(Green)* | `68` | **1,350 kg** *(Loaded Cart)* | **1,568 kg** *(Heavy Ore Cart)* | **1,800 kg** *(Full Ore Cart)* |
| **Mid-Span** *(Yellow-Green)* | `35` | **731 kg** *(Cart / Lox)* | **825 kg** *(Lox / Troll)* | **975 kg** *(Troll / Cart)* |
| **Outer Span** *(Yellow-Orange)* | `22` | **300 kg** *(Player + 200 kg inv)* | **498 kg** *(Player + 398 kg inv)* | **650 kg** *(Player + Cart)* |
| **Far Span** *(Orange-Red)* | `17.8` | **100 kg** *(Unloaded Player)* | **430 kg** *(Player + 330 kg inv)* | **545 kg** *(Player + 445 kg inv)* |
| **Near Tip** *(Red)* | `15.0` | **100 kg** *(Unloaded Player)* | **171 kg** *(Player + 71 kg inv)* | **475 kg** *(Player + 375 kg inv)* |
| **Very Tip** *(Deep Red)* | `12.0` | **100 kg** *(Unloaded Player)* | **100 kg** *(Unloaded Player)* | **400 kg** *(Player + 300 kg inv)* |

---

## Configuration (`com.kinematiclogic.vikingtruss.cfg`)

All settings can be adjusted in `BepInEx/config/com.kinematiclogic.vikingtruss.cfg` or via Gale / r2modman's Config Editor:

- **`[0. General]`**
  - `EnableMod` *(default: `true`)* — Master switch to enable or disable all VikingTruss physics modifications.
- **`[1. Trusses & Struts]`**
  - `StrutLossMultiplier` *(default: `0.45`)* — Horizontal loss multiplier for angled beams ($20^\circ\text{–}65^\circ$).
  - `TrussTriangleMultiplier` *(default: `0.50`)* — Support loss multiplier for closed 3-piece and 4-piece structural triangles.
- **`[2. Live Loads]`**
  - `EnableLiveLoads` *(default: `true`)* — Enables dynamic entity weight fracture and collapse penalties.
  - `LiveLoadMassThreshold` *(default: `350`)* — Minimum cumulative live load mass (`kg`) before support is penalized.
  - `LiveLoadSensitivity` *(default: `0.04`)* — Support penalty per `kg` of excess live load mass.
  - `LiveLoadCheckInterval` *(default: `0.25`)* — Seconds between live-load evaluations.
  - `CreatureMassMultiplier` *(default: `1.0`)* — Multiplier for non-player creature structural mass.
  - `PlayerLoadDamagesBeforeDestroy` *(default: `true`)* — Player-influenced overloads (players, carts, tamed creatures) fracture healthy pieces into their damaged (`20%` HP) state instead of destroying them immediately. Already-damaged pieces collapse after `PostFractureGraceSeconds`.
  - `HostileLoadDestroysImmediately` *(default: `true`)* — Untamed hostile creatures (Trolls, Golems, Abominations, etc.) bypass the fracture stage and immediately collapse overloaded spans.
  - `PostFractureGraceSeconds` *(default: `25.0`)* — Grace period in seconds after a player-influenced overload fractures a piece before that newly damaged piece can collapse under continued or repeated player load.
- **`[3. Stress Feedback (Audio & Visual)]`**
  - `EnableStressSounds` *(default: `true`)* — Enables 75% creaking and 90% cracking sound effects.
  - `EnableStressVisualHighlight` *(default: `true`)* — Automatically highlights pieces under active live load at $\ge 75\%$ (orange) and $\ge 90\%$ (red) capacity without requiring the Hammer.
  - `StressHighlightIntensity` *(default: `0.45`)* — Visual intensity (`0.05–1.0`) of the automatic live-load stress highlight (`0.45` = ~45% of the vanilla Hammer highlight glow).
  - `CreakCapacityThreshold` *(default: `0.75`)* — Capacity ratio (`0.0–1.0`) where pieces begin to creak and glow orange.
  - `SnapCapacityThreshold` *(default: `0.90`)* — Capacity ratio (`0.0–1.0`) where pieces crack, shed splinters, and glow red.
  - `StressSoundVolume` *(default: `0.85`)* — Master volume scale (`0.05–2.0`) for stress audio.

---

## Multiplayer & Save Compatibility
- **100% Vanilla Prefab Compatible:** Uses zero custom building pieces or prefabs, so it never pollutes or corrupts world `.db` saves.
- **Co-Op Worlds:** Because Valheim's chunk owner calculates building support, all players visiting a long-span truss structure in co-op should have **VikingTruss** enabled (in Gale or r2modman, share your profile via **Export -> Profile code**).
