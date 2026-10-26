using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using VikingTruss.Components;
using VikingTruss.Patches;

namespace VikingTruss
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.kinematiclogic.vikingtruss";
        public const string ModName = "VikingTruss";
        public const string ModVersion = "1.0.0";

        internal static ManualLogSource Log;

        public static ConfigEntry<bool> EnableMod;
        public static ConfigEntry<float> StrutLossMultiplier;
        public static ConfigEntry<float> TrussTriangleMultiplier;
        public static ConfigEntry<bool> EnableLiveLoads;
        public static ConfigEntry<float> LiveLoadMassThreshold;
        public static ConfigEntry<float> LiveLoadSensitivity;
        public static ConfigEntry<float> LiveLoadCheckInterval;
        public static ConfigEntry<float> CreatureMassMultiplier;
        public static ConfigEntry<bool> PlayerLoadDamagesBeforeDestroy;
        public static ConfigEntry<bool> HostileLoadDestroysImmediately;
        public static ConfigEntry<float> PostFractureGraceSeconds;
        public static ConfigEntry<bool> EnableStressSounds;
        public static ConfigEntry<bool> EnableStressVisualHighlight;
        public static ConfigEntry<float> StressHighlightIntensity;
        public static ConfigEntry<float> CreakCapacityThreshold;
        public static ConfigEntry<float> SnapCapacityThreshold;
        public static ConfigEntry<float> StressSoundVolume;

        private readonly Harmony _harmony = new Harmony(ModGUID);

        private static float _batchSpawnWindowStart;
        private static int _batchSpawnCount;
        private static float _scheduledWarmupTime = -1f;
        private static bool _warmupCoroutineRunning;
        private bool _hadReadyLocalPlayer;

        public static void NotifyPieceAwake()
        {
            float now = Time.time;
            if (now - _batchSpawnWindowStart > 1.2f)
            {
                _batchSpawnWindowStart = now;
                _batchSpawnCount = 0;
            }

            _batchSpawnCount++;

            // When a chunk/world loads (>= 8 pieces spawned in a 1.2s window), schedule a fast structural convergence pass
            if (_batchSpawnCount >= 8)
            {
                _scheduledWarmupTime = now + 0.5f;
            }
        }

        private void Awake()
        {
            Log = Logger;

            EnableMod = Config.Bind(
                "0. General",
                "EnableMod",
                true,
                "Master switch to enable or disable all VikingTruss physics modifications (set to false for vanilla multiplayer).");

            StrutLossMultiplier = Config.Bind(
                "1. Trusses & Struts",
                "StrutLossMultiplier",
                0.45f,
                "Horizontal loss multiplier for angled struts (20 to 65 degrees). Lower = stronger angled struts.");

            TrussTriangleMultiplier = Config.Bind(
                "1. Trusses & Struts",
                "TrussTriangleMultiplier",
                0.50f,
                "Support loss multiplier when connected pieces form a closed structural triangle (3-piece or 4-piece with 2-beam chord).");

            EnableLiveLoads = Config.Bind(
                "2. Live Loads",
                "EnableLiveLoads",
                true,
                "If enabled, heavy entities (Trolls, Lox, loaded carts, over-encumbered players) apply dynamic support penalties to spans.");

            LiveLoadMassThreshold = Config.Bind(
                "2. Live Loads",
                "LiveLoadMassThreshold",
                350f,
                "Minimum cumulative live load mass (kg) required before live load begins penalizing a structure (Player base mass is 100kg).");

            LiveLoadSensitivity = Config.Bind(
                "2. Live Loads",
                "LiveLoadSensitivity",
                0.04f,
                "Support penalty per kg of excess live load mass: EffectiveSupport = BaseSupport - ((LiveLoadMass - Threshold) * LiveLoadSensitivity).");

            LiveLoadCheckInterval = Config.Bind(
                "2. Live Loads",
                "LiveLoadCheckInterval",
                0.25f,
                "Interval in seconds between live-load overlap evaluations on non-grounded structural pieces.");

            CreatureMassMultiplier = Config.Bind(
                "2. Live Loads",
                "CreatureMassMultiplier",
                1.0f,
                "Multiplier applied to non-player creature live-load mass (Trolls, Lox, Golems, etc.).");

            PlayerLoadDamagesBeforeDestroy = Config.Bind(
                "2. Live Loads",
                "PlayerLoadDamagesBeforeDestroy",
                true,
                "If enabled, player-influenced overloads (Players, Carts, Tamed creatures) fracture healthy pieces into their damaged state (20% HP) first, and only destroy pieces that are already damaged.");

            HostileLoadDestroysImmediately = Config.Bind(
                "2. Live Loads",
                "HostileLoadDestroysImmediately",
                true,
                "If enabled, untamed hostile creatures (Trolls, Stone Golems, etc.) bypass the fracture stage and immediately destroy overloaded spans.");

            PostFractureGraceSeconds = Config.Bind(
                "2. Live Loads",
                "PostFractureGraceSeconds",
                25.0f,
                "Grace period in seconds after a healthy piece fractures into its damaged state before continued player/cart overload destroys it.");

            EnableStressSounds = Config.Bind(
                "3. Stress Feedback (Audio & Visual)",
                "EnableStressSounds",
                true,
                "If enabled, structural pieces creak when nearing capacity and snap/crack when critically stressed.");

            EnableStressVisualHighlight = Config.Bind(
                "3. Stress Feedback (Audio & Visual)",
                "EnableStressVisualHighlight",
                true,
                "If enabled, pieces under active live load automatically glow orange (>=75% capacity) or red (>=90% capacity) even without the Hammer equipped.");

            StressHighlightIntensity = Config.Bind(
                "3. Stress Feedback (Audio & Visual)",
                "StressHighlightIntensity",
                0.45f,
                "Visual intensity (0.05 to 1.0) of the automatic live-load stress highlight (0.45 = ~45% of vanilla Hammer highlight glow).");

            CreakCapacityThreshold = Config.Bind(
                "3. Stress Feedback (Audio & Visual)",
                "CreakCapacityThreshold",
                0.75f,
                "Capacity utilization fraction (0.0 to 1.0) at which pieces begin to creak/groan and show stress highlight under live load.");

            SnapCapacityThreshold = Config.Bind(
                "3. Stress Feedback (Audio & Visual)",
                "SnapCapacityThreshold",
                0.90f,
                "Capacity utilization fraction (0.0 to 1.0) at which pieces emit sharp cracking/snapping sounds, red highlight, and splinter particles.");

            StressSoundVolume = Config.Bind(
                "3. Stress Feedback (Audio & Visual)",
                "StressSoundVolume",
                0.85f,
                "Master volume multiplier (0.05 to 2.0) for structural creak and snap/crack sound effects.");

            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded and patched.");
        }

        private void Update()
        {
            if (!(EnableMod?.Value ?? true))
            {
                return;
            }

            Player localPlayer = Player.m_localPlayer;
            bool playerReady = localPlayer != null && !localPlayer.IsTeleporting() && !localPlayer.IsDead();

            if (playerReady && !_hadReadyLocalPlayer)
            {
                _scheduledWarmupTime = Time.time + 0.45f;
            }
            _hadReadyLocalPlayer = playerReady;

            if (playerReady && !_warmupCoroutineRunning && _scheduledWarmupTime > 0f && Time.time >= _scheduledWarmupTime)
            {
                _scheduledWarmupTime = -1f;
                StartCoroutine(WarmUpZoneSupportCoroutine());
            }
        }

        /// <summary>
        /// Vanilla Valheim initializes all loaded <see cref="WearNTear"/> pieces to <c>m_support = maxSupport</c> (<c>100</c>)
        /// and sleeps <c>WearNTearUpdater</c> for up to 60s, after which ungrounded truss bays mutually hold each other near
        /// <c>100</c> for 20+ slow passes. This coroutine converges all loaded pieces near the player in ~0.5s after spawn.
        /// </summary>
        private IEnumerator WarmUpZoneSupportCoroutine()
        {
            _warmupCoroutineRunning = true;
            WearNTearPatches.IsWarmupActive = true;

            yield return null;

            Player localPlayer = Player.m_localPlayer;
            List<WearNTear> allInstances = WearNTearPatches.GetAllWearNTearInstances();
            if (localPlayer == null || allInstances == null || allInstances.Count == 0)
            {
                WearNTearPatches.IsWarmupActive = false;
                _warmupCoroutineRunning = false;
                yield break;
            }

            Vector3 playerPos = localPlayer.transform.position;
            const float maxWarmupRadiusSqr = 140f * 140f;

            List<WearNTear> activePieces = new List<WearNTear>(256);
            for (int i = 0; i < allInstances.Count; i++)
            {
                WearNTear piece = allInstances[i];
                if (piece == null || !piece.m_supports)
                {
                    continue;
                }

                if ((piece.transform.position - playerPos).sqrMagnitude > maxWarmupRadiusSqr)
                {
                    continue;
                }

                ZNetView nview = WearNTearPatches.GetNView(piece);
                if (nview == null || !nview.IsValid() || !nview.IsOwner())
                {
                    continue;
                }

                // Reset initial 100f world-load support to minSupport before Pass 0 so only genuinely grounded pieces
                // (touching terrain/rocks/trees) become maxSupport on Pass 0.
                WearNTearPatches.GetVanillaMaterialProperties(piece, out _, out float minSupport, out _, out _);
                WearNTearPatches.SetPieceSupportField(piece, minSupport);
                activePieces.Add(piece);
            }

            if (activePieces.Count == 0)
            {
                WearNTearPatches.IsWarmupActive = false;
                _warmupCoroutineRunning = false;
                yield break;
            }

            // Pass 0: Identify genuinely grounded pieces (m_support >= maxSupport - 0.01f)
            List<Vector3> groundedPositions = new List<Vector3>(64);
            List<WearNTear> nonGroundedPieces = new List<WearNTear>(activePieces.Count);

            for (int i = 0; i < activePieces.Count; i++)
            {
                WearNTear piece = activePieces[i];
                if (piece == null)
                {
                    continue;
                }

                WearNTearPatches.InvokeUpdateSupport(piece);
                WearNTearPatches.GetVanillaMaterialProperties(piece, out float maxSupport, out float minSupport, out _, out _);
                float sup = WearNTearPatches.GetPieceSupportField(piece);

                if (sup >= maxSupport - 0.01f)
                {
                    groundedPositions.Add(piece.transform.position);
                }
                else
                {
                    WearNTearPatches.SetPieceSupportField(piece, minSupport);
                    nonGroundedPieces.Add(piece);
                }
            }

            yield return null;

            if (groundedPositions.Count > 0 && nonGroundedPieces.Count > 0)
            {
                // Sort non-grounded pieces by distance to the nearest grounded piece so Gauss-Seidel passes
                // propagate support from the grounded piers all the way to the cantilever tip in each pass.
                Dictionary<WearNTear, float> distToGround = new Dictionary<WearNTear, float>(nonGroundedPieces.Count);
                for (int i = 0; i < nonGroundedPieces.Count; i++)
                {
                    WearNTear p = nonGroundedPieces[i];
                    Vector3 pos = p.transform.position;
                    float bestSqr = float.MaxValue;
                    for (int g = 0; g < groundedPositions.Count; g++)
                    {
                        float sqr = (pos - groundedPositions[g]).sqrMagnitude;
                        if (sqr < bestSqr)
                        {
                            bestSqr = sqr;
                        }
                    }
                    distToGround[p] = bestSqr;
                }

                nonGroundedPieces.Sort((a, b) => distToGround[a].CompareTo(distToGround[b]));

                // Run 6 Gauss-Seidel passes from piers to tip across 6 frames (~0.1s) so cyclic truss triangles converge
                for (int pass = 0; pass < 6; pass++)
                {
                    for (int i = 0; i < nonGroundedPieces.Count; i++)
                    {
                        WearNTear piece = nonGroundedPieces[i];
                        if (piece != null)
                        {
                            WearNTearPatches.InvokeUpdateSupport(piece);
                        }
                    }
                    yield return null;
                }
            }

            // Safety floor and baseline sync
            for (int i = 0; i < nonGroundedPieces.Count; i++)
            {
                WearNTear piece = nonGroundedPieces[i];
                if (piece == null)
                {
                    continue;
                }

                WearNTearPatches.GetVanillaMaterialProperties(piece, out float maxSupport, out float minSupport, out _, out _);
                float settled = WearNTearPatches.GetPieceSupportField(piece);
                if (settled <= minSupport)
                {
                    settled = minSupport + 1f;
                    ZNetView nview = WearNTearPatches.GetNView(piece);
                    WearNTearPatches.SetPieceSupportAndZdo(piece, nview, settled);
                }

                LiveLoadSensor sensor = piece.GetComponent<LiveLoadSensor>();
                if (sensor != null)
                {
                    sensor.SyncBaselineAfterWarmup(maxSupport, minSupport, settled);
                }
            }

            WearNTearPatches.IsWarmupActive = false;
            _warmupCoroutineRunning = false;
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
