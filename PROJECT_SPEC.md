# Project Specification: Valheim Structural Engineering Mod ("VikingTruss")

## 1. Overview & Core Objective
Override Valheim's naive distance-decay building integrity system (`WearNTear`) using BepInEx and Harmony. Replace pure distance-based horizontal penalties with a lightweight, deterministic graph topology modifier that rewards real structural mechanics—specifically angled struts, closed triangular trusses, tension cables (Stage 2), and dynamic live-load collapse traps (Stage 3)—without tanking Unity main-thread frame times.

## 2. Target Environment & Dependencies
- **Target Game:** Valheim (Unity Mono / .NET Framework 4.7.2 or .NET Standard 2.1)
- **Mod Framework:** BepInEx 5.4.22+
- **Patching Library:** HarmonyX (`0Harmony.dll`)
- **Mod SDK (Optional for Stage 2 Prefabs):** Jotunn (Valheim Library) or PieceManager
- **Core Game Assemblies to Reference:**
  - `assembly_valheim.dll` (Specifically `WearNTear`, `Piece`, `Character`, `Player`, `Vagon`, `ZNetView`)
  - `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`

---

## 3. Staged Architecture & Implementation Plan

### Stage 1: Strut & Truss Topology Solver (Core Patch)
**Target Method:** `WearNTear.UpdateSupport()` and/or `WearNTear.GetMaterialProperties()`

1. **Vanilla Baseline Behavior:**
   - Support flows from grounded pieces (`m_support = MaxSupport`).
   - Each step decays by `HorizontalLoss` and `VerticalLoss` based on center-to-center vectors.
   - Multi-path connections only take the single highest parent value; closed loops and diagonals are punished due to extra node hops.

2. **Mod Rule 1A — Diagonal Strut Efficiency:**
   - Inspect the world orientation vector of the supporting piece or the vector between connected pieces.
   - If the vertical angle $\theta$ relative to horizontal sits between $20^\circ$ and $65^\circ$ (targeting Valheim's $26^\circ$ and $45^\circ$ beams):
   - Apply a configurable `StrutHorizontalDiscount` multiplier (default `0.45f`) to `HorizontalLoss`.

3. **Mod Rule 1B — Closed-Loop Triangle / Truss Detection:**
   - When Piece $A$ evaluates neighbor Piece $B$ as a support donor:
   - Query the cached neighbor lists (`m_Pieces` / `GetSupportingPieces` collider overlap).
   - Check if $A$ and $B$ share a mutual connected neighbor $C$ ($A \leftrightarrow B \leftrightarrow C$ forms a closed 3-node cycle).
   - If true, apply a `TrussLoopBonus` multiplier (default `0.50f` loss reduction) to the support transfer between $A$ and $B$.
   - *Constraint:* Must be deterministic across clients to prevent multiplayer `ZNetView` destruction desync.

---

### Stage 2: Tension Ropes & Stays
**Concept:** Pure tension members that allow suspension and cable-stayed bridges.

1. **Placement & Rendering:**
   - Two-point anchor tool or custom snap-piece using Unity `LineRenderer`.
   - Stores Endpoint A and Endpoint B GUIDs/ZDOIDs inside `ZDO` (ZNet Data Object) via `ZNetView`.
2. **Directional Tension Logic:**
   - Ropes only transmit support **downward** ($Y_{\text{anchor}} \ge Y_{\text{target}}$).
   - If a piece attempts to pull support upward from a rope below it, transmitted support = `0` (ropes cannot support compressive loads).
   - Minimal horizontal decay over distance, capped by a maximum cumulative suspended piece count before snapping.

---

### Stage 3: Live Loads ("Troll-Trap" Mechanics)
**Concept:** Dynamic entity weight temporarily penalizes local structural support, allowing fragile unbraced spans to collapse under heavy enemies or overloaded carts.

1. **Mass Extraction:**
   - `Character`: Query `m_body.mass` (`Rigidbody.mass`). Note: Trolls/Lox carry high Rigidbody mass compared to players/boars.
   - `Player`: `m_body.mass + m_inventory.GetTotalWeight()`.
   - `Vagon` (Carts): Base mass + attached container cargo weight.
2. **Execution:**
   - Attach a lightweight collision tracker or periodic overlap check on non-grounded floor/bridge pieces.
   - Calculate effective support:
     `EffectiveSupport = BaseSupport - (LiveLoadMass * LiveLoadSensitivity)`
   - If `EffectiveSupport < MinSupport`, trigger `WearNTear.Destroy()` immediately.

---

## 4. Proposed Repository Structure
```text
VikingTruss/
├── PROJECT_SPEC.md
├── VikingTruss.csproj
├── Plugin.cs                  # BepInEx entry point & ConfigEntry definitions
├── Patches/
│   └── WearNTearPatches.cs    # Harmony patches for UpdateSupport & Truss logic
├── Components/
│   ├── TrussNodeCache.cs      # Caches neighbor adjacency graphs for O(1) triangle checks
│   ├── LiveLoadSensor.cs      # Tracks Rigidbody mass colliding with structural pieces
│   └── TensionRope.cs         # Stage 2 LineRenderer + ZDO sync component
└── README.md