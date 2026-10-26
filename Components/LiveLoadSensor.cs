using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VikingTruss.Patches;

namespace VikingTruss.Components
{
    /// <summary>
    /// Stage 3 ("Troll-Trap" Mechanics): Tracks dynamic live-load mass (Characters, Players + Inventory,
    /// Vagons/Carts + Container cargo) resting on non-grounded structural pieces.
    /// Computes <c>EffectiveSupport = BaseSupport - (ExcessLiveLoadMass * LiveLoadSensitivity)</c> and
    /// triggers <c>WearNTear.Destroy()</c> immediately when <c>EffectiveSupport &lt; MinSupport</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public class LiveLoadSensor : MonoBehaviour
    {
        private static readonly Collider[] OverlapColliders = new Collider[48];

        // Grace period after a piece is spawned/placed so truss support propagation settles before live load checks
        private const float PlacementGracePeriodSeconds = 4.0f;

        // Cached reflection fields for Character, Player, Vagon, and WearNTear.Destroy
        private static readonly FieldInfo CharacterBodyField =
            AccessTools.Field(typeof(Character), "m_body");

        private static readonly FieldInfo PlayerInventoryField =
            AccessTools.Field(typeof(Player), "m_inventory") ??
            AccessTools.Field(typeof(Humanoid), "m_inventory");

        private static readonly FieldInfo VagonBodyField =
            AccessTools.Field(typeof(Vagon), "m_body");

        private static readonly FieldInfo VagonBodiesField =
            AccessTools.Field(typeof(Vagon), "m_bodies");

        private static readonly FieldInfo VagonContainerField =
            AccessTools.Field(typeof(Vagon), "m_container");

        private static readonly MethodInfo DestroyMethod =
            AccessTools.Method(typeof(WearNTear), "Destroy");

        private static int _liveLoadLayerMask;

        private readonly HashSet<int> _visitedEntities = new HashSet<int>();
        private readonly List<LiveLoadSensor> _supportingSensorsBuffer = new List<LiveLoadSensor>(8);
        private WearNTear _wearNTear;
        private float _spawnTime;
        private float _nextCheckTime;
        private float _collisionMassAccumulator;
        private bool _collisionHasMovement;
        private float _transferredMassAccumulator;
        private bool _transferredHasMovement;
        private float _transferredMassTimestamp = -999f;
        private float _previousTotalMass;
        private bool _wasMovingLastCheck;
        private float _visualHighlightSupportOverride = -1f;
        private float _visualHighlightExpireTime = -999f;
        private float _nextAcousticTime;
        private float _lastNotifiedCapacity;
        private bool _initializedBaselineSupport;
        private bool _hasDestroyed;

        public float CurrentLiveLoadMass { get; private set; }
        public float EffectiveSupport { get; private set; }

        private void Awake()
        {
            _wearNTear = GetComponent<WearNTear>();
            _spawnTime = Time.time;
            float interval = Mathf.Max(0.05f, Plugin.LiveLoadCheckInterval?.Value ?? 0.25f);
            _nextCheckTime = _spawnTime + Random.Range(0.05f, interval);
        }

        private void Start()
        {
            // Record whether this piece was loaded from an existing save (already has calculated support < maxSupport)
            // vs. freshly placed by the player (starts at maxSupport / 1f before its first UpdateSupport pass).
            if (_wearNTear != null)
            {
                WearNTearPatches.GetVanillaMaterialProperties(
                    _wearNTear,
                    out float maxSupport,
                    out float minSupport,
                    out _,
                    out _);
                float initialSupport = WearNTearPatches.GetPieceSupportField(_wearNTear);
                if (initialSupport > minSupport && initialSupport < maxSupport - 0.1f)
                {
                    _lastNotifiedCapacity = StructuralStressAudio.ComputeCapacityUtilization(
                        maxSupport,
                        minSupport,
                        initialSupport,
                        initialSupport);
                    _initializedBaselineSupport = true;
                }
            }
        }

        public void SyncBaselineAfterWarmup(float maxSupport, float minSupport, float settledSupport)
        {
            _lastNotifiedCapacity = StructuralStressAudio.ComputeCapacityUtilization(
                maxSupport,
                minSupport,
                settledSupport,
                settledSupport);
            _initializedBaselineSupport = true;
        }

        /// <summary>
        /// Used by <c>WearNTearPatches.Highlight_Prefix</c> so both Hammer hover and automatic live-load
        /// stress highlights render the stressed orange/red color without modifying ZDO support.
        /// </summary>
        public bool TryGetVisualHighlightSupportOverride(out float visualSupport)
        {
            if (_visualHighlightSupportOverride >= 0f && Time.time <= _visualHighlightExpireTime)
            {
                visualSupport = _visualHighlightSupportOverride;
                return true;
            }

            visualSupport = 0f;
            return false;
        }

        /// <summary>
        /// Receives live load transferred downward from a deck/floor piece resting on this beam/support.
        /// </summary>
        public void AddTransferredLiveLoad(float mass, bool isMoving, float now)
        {
            if (mass <= 0f || _hasDestroyed)
            {
                return;
            }

            // Reset transferred accumulator if the previous transfer window expired
            float interval = Mathf.Max(0.05f, Plugin.LiveLoadCheckInterval?.Value ?? 0.25f);
            if (now - _transferredMassTimestamp > interval * 1.6f)
            {
                _transferredMassAccumulator = 0f;
                _transferredHasMovement = false;
            }

            _transferredMassTimestamp = now;
            _transferredMassAccumulator = Mathf.Max(_transferredMassAccumulator, mass);
            if (isMoving)
            {
                _transferredHasMovement = true;
            }
        }

        /// <summary>
        /// Called from <see cref="WearNTearPatches.UpdateSupport_Postfix"/> when a piece's structural support
        /// settles or drops, triggering a creak (75%+) or snap/crack (90%+) if stress newly enters a warning zone.
        /// </summary>
        public void NotifySupportUpdated(float maxSupport, float minSupport, float currentSupport)
        {
            if (_hasDestroyed || _wearNTear == null)
            {
                return;
            }

            float capacity = StructuralStressAudio.ComputeCapacityUtilization(
                maxSupport,
                minSupport,
                currentSupport,
                currentSupport);

            if (!_initializedBaselineSupport)
            {
                _initializedBaselineSupport = true;
            }

            float creakThreshold = Mathf.Clamp(Plugin.CreakCapacityThreshold?.Value ?? 0.75f, 0.1f, 0.99f);
            float snapThreshold = Mathf.Clamp(Plugin.SnapCapacityThreshold?.Value ?? 0.90f, creakThreshold, 0.995f);
            float now = Time.time;
            float interval = Mathf.Max(0.05f, Plugin.LiveLoadCheckInterval?.Value ?? 0.25f);

            if (now >= _nextAcousticTime)
            {
                if (capacity >= snapThreshold && _lastNotifiedCapacity < snapThreshold - 0.02f)
                {
                    _nextAcousticTime = now + Random.Range(2.5f, 4.0f);
                    if (Plugin.EnableStressSounds?.Value ?? true)
                    {
                        StructuralStressAudio.PlayStressFeedback(_wearNTear, isSnapCrack: true, capacity);
                    }
                    if (Plugin.EnableStressVisualHighlight?.Value ?? true)
                    {
                        TriggerStressVisualHighlight(maxSupport, minSupport, capacity, creakThreshold, snapThreshold, now, interval);
                    }
                }
                else if (capacity >= creakThreshold && _lastNotifiedCapacity < creakThreshold - 0.02f)
                {
                    _nextAcousticTime = now + Random.Range(3.0f, 4.5f);
                    if (Plugin.EnableStressSounds?.Value ?? true)
                    {
                        StructuralStressAudio.PlayStressFeedback(_wearNTear, isSnapCrack: false, capacity);
                    }
                    if (Plugin.EnableStressVisualHighlight?.Value ?? true)
                    {
                        TriggerStressVisualHighlight(maxSupport, minSupport, capacity, creakThreshold, snapThreshold, now, interval);
                    }
                }
            }

            _lastNotifiedCapacity = capacity;
        }

        private void OnCollisionStay(Collision collision)
        {
            if (_hasDestroyed || collision == null || !(Plugin.EnableMod?.Value ?? true))
            {
                return;
            }

            // Only count downward/top-bearing contacts where the entity rests on top of this piece
            bool restsOnTop = false;
            int contactCount = collision.contactCount;
            for (int i = 0; i < contactCount; i++)
            {
                ContactPoint cp = collision.GetContact(i);
                if (cp.normal.y < -0.5f)
                {
                    restsOnTop = true;
                    break;
                }
            }

            if (!restsOnTop)
            {
                return;
            }

            float entityMass = ExtractEntityMassFromCollider(collision.collider, out int rootId, out bool isMoving);
            if (entityMass > 0f && rootId != 0 && _visitedEntities.Add(rootId))
            {
                _collisionMassAccumulator += entityMass;
                if (isMoving)
                {
                    _collisionHasMovement = true;
                }
            }
        }

        private void FixedUpdate()
        {
            if (_hasDestroyed || _wearNTear == null || !(Plugin.EnableMod?.Value ?? true))
            {
                _visitedEntities.Clear();
                _collisionMassAccumulator = 0f;
                _collisionHasMovement = false;
                return;
            }

            float now = Time.time;
            if (now < _nextCheckTime)
            {
                _visitedEntities.Clear();
                _collisionMassAccumulator = 0f;
                _collisionHasMovement = false;
                return;
            }

            float interval = Mathf.Max(0.05f, Plugin.LiveLoadCheckInterval?.Value ?? 0.25f);
            _nextCheckTime = now + interval;

            EvaluateLiveLoadAndCollapse(now, interval);

            _visitedEntities.Clear();
            _collisionMassAccumulator = 0f;
            _collisionHasMovement = false;
        }

        private void EvaluateLiveLoadAndCollapse(float now, float interval)
        {
            if (!_wearNTear.m_supports)
            {
                return;
            }

            // Diagonal struts (e.g., 26° / 45° web members) transfer axial loads; live loads act on deck/floor/horizontal spans
            if (WearNTearPatches.IsPieceOrientedAsDiagonalStrut(_wearNTear))
            {
                return;
            }

            ZNetView nview = WearNTearPatches.GetNView(_wearNTear);
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            WearNTearPatches.GetVanillaMaterialProperties(
                _wearNTear,
                out float maxSupport,
                out float minSupport,
                out _,
                out _);

            float baseSupport = WearNTearPatches.GetPieceSupport(_wearNTear);

            // Grounded pieces (baseSupport >= maxSupport) do not collapse or creak from live loads
            if (baseSupport <= 0f || baseSupport >= maxSupport - 0.01f)
            {
                CurrentLiveLoadMass = 0f;
                EffectiveSupport = baseSupport;
                _previousTotalMass = 0f;
                _wasMovingLastCheck = false;
                return;
            }

            float overlapMass = SampleOverlapLiveLoadMass(out bool overlapMoving);
            float directMass = _collisionMassAccumulator + overlapMass;
            bool directMoving = _collisionHasMovement || overlapMoving;

            if (directMass > 0f)
            {
                PropagateDirectLoadToSupportingNeighbors(directMass, directMoving, now);
            }

            bool hasActiveTransfer = (now - _transferredMassTimestamp <= interval * 1.8f);
            float transferredMass = hasActiveTransfer ? _transferredMassAccumulator : 0f;
            bool transferredMoving = hasActiveTransfer && _transferredHasMovement;

            float totalMass = Mathf.Max(directMass, transferredMass);
            bool hasMovingLoad = directMoving || transferredMoving;
            bool loadJustIncreased = (_previousTotalMass < 20f && totalMass >= 20f) || (totalMass > _previousTotalMass + 35f);

            CurrentLiveLoadMass = totalMass;

            float threshold = Mathf.Max(0f, Plugin.LiveLoadMassThreshold?.Value ?? 350f);
            float sensitivity = Mathf.Max(0f, Plugin.LiveLoadSensitivity?.Value ?? 0.04f);
            float excessMass = Mathf.Max(0f, totalMass - threshold);

            EffectiveSupport = baseSupport - (excessMass * sensitivity);

            // Evaluate acoustic creaking (>= 75%), cracking/snapping (>= 90%), and automatic visual stress highlight
            // whenever an active live load (>= 20kg) is on this piece or transferred from a floor tile above it
            if (totalMass >= 20f)
            {
                // Even if totalMass is below the collapse threshold (e.g., normal player walking on a stressed span),
                // include a felt live-load term so footsteps on near-capacity spans groan/crack and glow underfoot.
                float acousticPenalty = excessMass > 0f
                    ? (excessMass * sensitivity) + (threshold * sensitivity * 0.25f)
                    : (totalMass * sensitivity * 0.35f);
                float acousticEffectiveSupport = baseSupport - acousticPenalty;

                float capacityRatio = StructuralStressAudio.ComputeCapacityUtilization(
                    maxSupport,
                    minSupport,
                    baseSupport,
                    acousticEffectiveSupport);

                float creakThreshold = Mathf.Clamp(Plugin.CreakCapacityThreshold?.Value ?? 0.75f, 0.1f, 0.99f);
                float snapThreshold = Mathf.Clamp(Plugin.SnapCapacityThreshold?.Value ?? 0.90f, creakThreshold, 0.995f);

                if (capacityRatio >= creakThreshold && (Plugin.EnableStressVisualHighlight?.Value ?? true))
                {
                    TriggerStressVisualHighlight(maxSupport, minSupport, capacityRatio, creakThreshold, snapThreshold, now, interval);
                }

                // When the player stops moving on a piece, push out the next acoustic timer so sounds immediately quiet down
                if (!loadJustIncreased && !hasMovingLoad && _wasMovingLastCheck)
                {
                    _nextAcousticTime = Mathf.Max(_nextAcousticTime, now + Random.Range(12.0f, 18.0f));
                }

                if ((Plugin.EnableStressSounds?.Value ?? true) && now >= _nextAcousticTime && capacityRatio >= creakThreshold)
                {
                    bool isCritical = capacityRatio >= snapThreshold;
                    if (loadJustIncreased)
                    {
                        // Stepping onto a stressed span: full response + sawdust/splinter puff if >= 90%
                        _nextAcousticTime = now + Random.Range(4.0f, 6.0f);
                        StructuralStressAudio.PlayStressFeedback(
                            _wearNTear,
                            isSnapCrack: isCritical,
                            capacityRatio,
                            spawnParticles: isCritical,
                            volumeMultiplier: 1.0f);
                    }
                    else if (hasMovingLoad)
                    {
                        // Actively walking across a stressed span: moderate creaks/cracks without constant particle spam
                        _nextAcousticTime = now + Random.Range(4.5f, 7.0f);
                        StructuralStressAudio.PlayStressFeedback(
                            _wearNTear,
                            isSnapCrack: isCritical,
                            capacityRatio,
                            spawnParticles: false,
                            volumeMultiplier: 0.75f);
                    }
                    else
                    {
                        // Standing completely still: rare, subtle, quiet settling creaks (never loud snaps or particle bursts)
                        _nextAcousticTime = now + Random.Range(15.0f, 24.0f);
                        StructuralStressAudio.PlayStressFeedback(
                            _wearNTear,
                            isSnapCrack: false,
                            capacityRatio,
                            spawnParticles: false,
                            volumeMultiplier: 0.22f);
                    }
                }
            }

            _previousTotalMass = totalMass;
            _wasMovingLastCheck = hasMovingLoad;

            // Only the ZDO owner may trigger actual structural collapse, and only after placement grace period
            if (!(Plugin.EnableLiveLoads?.Value ?? true) ||
                !nview.IsOwner() ||
                (now - _spawnTime) < PlacementGracePeriodSeconds ||
                excessMass <= 0f)
            {
                return;
            }

            if (EffectiveSupport < minSupport)
            {
                _hasDestroyed = true;
                Plugin.Log?.LogInfo(
                    $"[VikingTruss] Live load collapse on '{_wearNTear.gameObject.name}': " +
                    $"BaseSupport={baseSupport:F1}, LiveLoadMass={totalMass:F1}kg (Threshold={threshold:F0}kg), " +
                    $"EffectiveSupport={EffectiveSupport:F1} < MinSupport={minSupport:F1}");

                TriggerPieceDestroy(_wearNTear);
            }
        }

        private void TriggerStressVisualHighlight(
            float maxSupport,
            float minSupport,
            float capacityRatio,
            float creakThreshold,
            float snapThreshold,
            float now,
            float interval)
        {
            float halfMax = Mathf.Max(minSupport + 1f, maxSupport * 0.5f);
            Color targetColor;
            if (capacityRatio >= snapThreshold)
            {
                // >= 90% capacity: Red highlight (right at minSupport)
                _visualHighlightSupportOverride = minSupport;
                targetColor = new Color(1f, 0.05f, 0.02f);
            }
            else
            {
                // 75% to 89% capacity: Warm Yellow-Orange shifting to Deep Orange-Red
                float t = Mathf.Clamp01((capacityRatio - creakThreshold) / Mathf.Max(0.01f, snapThreshold - creakThreshold));
                _visualHighlightSupportOverride = Mathf.Lerp(
                    minSupport + (halfMax - minSupport) * 0.28f,
                    minSupport + (halfMax - minSupport) * 0.05f,
                    t);
                targetColor = Color.Lerp(new Color(1f, 0.60f, 0.05f), new Color(1f, 0.22f, 0.02f), t);
            }

            // Clear 0.35s after the last stressed check (longer than the 0.25s check interval so it never flashes)
            float holdDuration = Mathf.Max(0.35f, interval + 0.10f);
            _visualHighlightExpireTime = now + holdDuration;

            // Vanilla WearNTear.Highlight() boosts HSV value to 1.2x on _Color and sets _EmissionColor = color * 0.4f.
            // Here we boost the active channel above 1.0 and scale emission linearly with StressHighlightIntensity
            // (so 0.45-0.50 = true ~50% of vanilla Hammer red/orange glow, never darkening the wood).
            float intensity = Mathf.Clamp(Plugin.StressHighlightIntensity?.Value ?? 0.45f, 0.05f, 1.0f);
            Color boostedTargetColor = new Color(
                1f + (0.30f * targetColor.r),
                Mathf.Lerp(0.65f, 1.15f, targetColor.g),
                0.65f);
            Color subtleColor = Color.Lerp(Color.white, boostedTargetColor, intensity);
            Color subtleEmission = targetColor * (0.4f * intensity);

            if (MaterialMan.instance != null)
            {
                MaterialMan.instance.SetValue<Color>(_wearNTear.gameObject, ShaderProps._EmissionColor, subtleEmission);
                MaterialMan.instance.SetValue<Color>(_wearNTear.gameObject, ShaderProps._Color, subtleColor);
            }
            else
            {
                _wearNTear.Highlight();
            }

            _wearNTear.CancelInvoke("ResetHighlight");
            _wearNTear.Invoke("ResetHighlight", holdDuration);
        }

        /// <summary>
        /// Transfers live load from a deck/floor piece to the structural beams/supports directly underneath it.
        /// </summary>
        private void PropagateDirectLoadToSupportingNeighbors(float directMass, bool isMoving, float now)
        {
            TrussNodeCache cache = _wearNTear.GetComponent<TrussNodeCache>();
            if (cache == null)
            {
                return;
            }

            IReadOnlyDictionary<WearNTear, Vector3> neighbors = cache.NeighborJoints;
            if (neighbors.Count == 0)
            {
                return;
            }

            Vector3 myCom = WearNTearPatches.GetPieceCOM(_wearNTear);
            _supportingSensorsBuffer.Clear();

            foreach (KeyValuePair<WearNTear, Vector3> kvp in neighbors)
            {
                WearNTear neighbor = kvp.Key;
                if (neighbor == null || neighbor == _wearNTear || !neighbor.m_supports)
                {
                    continue;
                }

                // Transfer load to supporting beams/chords underneath or at the same level as this deck piece
                Vector3 neighborCom = WearNTearPatches.GetPieceCOM(neighbor);
                if (neighborCom.y <= myCom.y + 0.35f || kvp.Value.y <= myCom.y + 0.15f)
                {
                    LiveLoadSensor neighborSensor = neighbor.GetComponent<LiveLoadSensor>();
                    if (neighborSensor != null)
                    {
                        _supportingSensorsBuffer.Add(neighborSensor);
                    }
                }
            }

            int count = _supportingSensorsBuffer.Count;
            if (count > 0)
            {
                float sharedLoad = (directMass * 0.85f) / Mathf.Sqrt(count);
                for (int i = 0; i < count; i++)
                {
                    _supportingSensorsBuffer[i].AddTransferredLiveLoad(sharedLoad, isMoving, now);
                }
                _supportingSensorsBuffer.Clear();
            }
        }

        private float SampleOverlapLiveLoadMass(out bool anyMoving)
        {
            anyMoving = false;
            Vector3 com = WearNTearPatches.GetPieceCOM(_wearNTear);
            Vector3 probeCenter = com + Vector3.up * 0.45f;
            Vector3 halfExtents = new Vector3(0.95f, 0.45f, 0.95f);

            int hitCount = Physics.OverlapBoxNonAlloc(
                probeCenter,
                halfExtents,
                OverlapColliders,
                transform.rotation,
                GetLiveLoadLayerMask(),
                QueryTriggerInteraction.Ignore);

            float overlapMass = 0f;
            for (int i = 0; i < hitCount; i++)
            {
                Collider col = OverlapColliders[i];
                if (col == null)
                {
                    continue;
                }

                // Ensure the entity is actually above/resting on this piece, not below or beside it
                if (col.bounds.min.y < com.y - 0.25f)
                {
                    continue;
                }

                float mass = ExtractEntityMassFromCollider(col, out int rootId, out bool isMoving);
                if (mass > 0f && rootId != 0 && _visitedEntities.Add(rootId))
                {
                    overlapMass += mass;
                    if (isMoving)
                    {
                        anyMoving = true;
                    }
                }
            }

            return overlapMass;
        }

        /// <summary>
        /// Extracts live-load mass according to Stage 3 specification:
        /// <list type="bullet">
        ///   <item><description><see cref="Player"/>: <c>m_body.mass + m_inventory.GetTotalWeight()</c> (ignored if debug-flying)</description></item>
        ///   <item><description><see cref="Character"/>: <c>m_body.mass</c> scaled for heavy Valheim creatures (Trolls, Lox, Golems, etc.)</description></item>
        ///   <item><description><see cref="Vagon"/>: Base mass + attached container cargo weight (<c>m_container.GetInventory().GetTotalWeight()</c>)</description></item>
        /// </list>
        /// </summary>
        public static float ExtractEntityMassFromCollider(Collider col, out int entityInstanceId, out bool isMoving)
        {
            entityInstanceId = 0;
            isMoving = false;
            if (col == null)
            {
                return 0f;
            }

            // 1. Check Player / Character
            Character character = col.GetComponentInParent<Character>();
            if (character != null)
            {
                if (character.IsDebugFlying() || !character.IsOnGround())
                {
                    return 0f;
                }

                entityInstanceId = character.GetInstanceID();
                Rigidbody body = GetCharacterBody(character) ?? col.attachedRigidbody;
                float baseMass = body != null ? body.mass : 0f;
                if (body != null)
                {
                    Vector3 vel = body.velocity;
                    isMoving = new Vector2(vel.x, vel.z).sqrMagnitude > 0.12f;
                }

                if (character is Player player)
                {
                    Inventory inv = GetPlayerInventory(player);
                    float invWeight = inv != null ? inv.GetTotalWeight() : 0f;
                    return baseMass + invWeight;
                }

                return GetEffectiveCreatureMass(character, baseMass);
            }

            // 2. Check Vagon (Carts)
            Vagon vagon = col.GetComponentInParent<Vagon>();
            if (vagon != null)
            {
                entityInstanceId = vagon.GetInstanceID();
                Rigidbody vagonBody = col.attachedRigidbody;
                if (vagonBody != null)
                {
                    Vector3 vel = vagonBody.velocity;
                    isMoving = new Vector2(vel.x, vel.z).sqrMagnitude > 0.06f;
                }
                float vagonMass = GetVagonTotalMass(vagon, vagonBody);
                return vagonMass;
            }

            return 0f;
        }

        /// <summary>
        /// Ensures heavy Valheim monsters (Trolls, Lox, Stone Golems, Abominations, Berserkers)
        /// exert realistic structural live loads ("Troll-Trap" mechanics) even when their vanilla
        /// <c>Rigidbody.mass</c> is understated.
        /// </summary>
        private static float GetEffectiveCreatureMass(Character character, float rigidbodyMass)
        {
            float effectiveMass = rigidbodyMass;
            string name = character.gameObject.name;
            if (!string.IsNullOrEmpty(name))
            {
                string lower = name.ToLowerInvariant();
                if (lower.Contains("stonegolem") || lower.Contains("abomination") ||
                    lower.Contains("morgen") || lower.Contains("gjall") ||
                    lower.Contains("bonemass") || lower.Contains("moder") ||
                    lower.Contains("goblinking") || lower.Contains("seekerqueen") ||
                    lower.Contains("fader"))
                {
                    effectiveMass = Mathf.Max(effectiveMass, 1100f);
                }
                else if (lower.Contains("troll"))
                {
                    effectiveMass = Mathf.Max(effectiveMass, 950f);
                }
                else if (lower.Contains("lox") || lower.Contains("asksvin") || lower.Contains("seekerbrute"))
                {
                    effectiveMass = Mathf.Max(effectiveMass, 800f);
                }
                else if (lower.Contains("goblinbrute") || lower.Contains("draugr_elite") || lower.Contains("fenring_cultist"))
                {
                    effectiveMass = Mathf.Max(effectiveMass, 550f);
                }
            }

            float mult = Mathf.Max(0f, Plugin.CreatureMassMultiplier?.Value ?? 1.0f);
            return effectiveMass * mult;
        }

        private static Rigidbody GetCharacterBody(Character character)
        {
            if (character == null)
            {
                return null;
            }

            if (CharacterBodyField != null)
            {
                Rigidbody rb = CharacterBodyField.GetValue(character) as Rigidbody;
                if (rb != null)
                {
                    return rb;
                }
            }

            return character.GetComponent<Rigidbody>();
        }

        private static Inventory GetPlayerInventory(Player player)
        {
            if (player == null)
            {
                return null;
            }

            Inventory inv = player.GetInventory();
            if (inv != null)
            {
                return inv;
            }

            return PlayerInventoryField?.GetValue(player) as Inventory;
        }

        private static float GetVagonTotalMass(Vagon vagon, Rigidbody fallbackBody)
        {
            float totalMass = 0f;

            if (VagonBodiesField != null && VagonBodiesField.GetValue(vagon) is Rigidbody[] bodies && bodies.Length > 0)
            {
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (bodies[i] != null)
                    {
                        totalMass += bodies[i].mass;
                    }
                }
            }
            else
            {
                Rigidbody mainBody = (VagonBodyField?.GetValue(vagon) as Rigidbody) ?? vagon.GetComponent<Rigidbody>() ?? fallbackBody;
                if (mainBody != null)
                {
                    totalMass += mainBody.mass;
                }
            }

            Container container = (VagonContainerField?.GetValue(vagon) as Container) ?? vagon.GetComponentInChildren<Container>();
            if (container != null)
            {
                Inventory cargo = container.GetInventory();
                if (cargo != null)
                {
                    totalMass += cargo.GetTotalWeight();
                }
            }

            return totalMass;
        }

        private static int GetLiveLoadLayerMask()
        {
            if (_liveLoadLayerMask == 0)
            {
                _liveLoadLayerMask = LayerMask.GetMask("character", "character_net", "character_nonenv", "item", "piece", "Default", "vehicle");
                if (_liveLoadLayerMask == 0)
                {
                    _liveLoadLayerMask = Physics.AllLayers;
                }
            }

            return _liveLoadLayerMask;
        }

        private static void TriggerPieceDestroy(WearNTear piece)
        {
            if (piece == null || DestroyMethod == null)
            {
                return;
            }

            ParameterInfo[] parameters = DestroyMethod.GetParameters();
            if (parameters.Length == 0)
            {
                DestroyMethod.Invoke(piece, null);
            }
            else if (parameters.Length == 2)
            {
                DestroyMethod.Invoke(piece, new object[] { null, false });
            }
            else
            {
                object[] args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                }
                DestroyMethod.Invoke(piece, args);
            }
        }
    }
}
