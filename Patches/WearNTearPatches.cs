using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VikingTruss.Components;

namespace VikingTruss.Patches
{
    /// <summary>
    /// Stage 1 Harmony patches for <see cref="WearNTear"/>:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     <b>Target Signatures in <c>assembly_valheim.dll</c>:</b>
    ///     <para><c>private void WearNTear.UpdateSupport()</c></para>
    ///     <para><c>private void WearNTear.GetMaterialProperties(out float maxSupport, out float minSupport, out float horizontalLoss, out float verticalLoss)</c></para>
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>Mod Rule 1A (Diagonal Strut Efficiency):</b> Discounts <c>horizontalLoss</c> by
    ///     <see cref="Plugin.StrutLossMultiplier"/> (default <c>0.45f</c>) when a supporting piece is an angled strut
    ///     oriented between 20° and 65° from horizontal (targeting Valheim's 26° and 45° beams).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>Mod Rule 1B (Closed-Loop Triangle / Truss Detection):</b> Discounts support transfer loss by
    ///     <see cref="Plugin.TrussTriangleMultiplier"/> (default <c>0.50f</c>) when physically connected pieces
    ///     <c>A</c> and <c>B</c> share a mutual physically connected neighbor <c>C</c> forming a 3-joint triangle.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    [HarmonyPatch(typeof(WearNTear))]
    public static class WearNTearPatches
    {
        // Minimum and maximum vertical elevation angles (degrees from horizontal) for Diagonal Strut Efficiency
        public const float MinStrutAngleDeg = 20f;
        public const float MaxStrutAngleDeg = 65f;

        // Cached reflection handles for private WearNTear members in assembly_valheim.dll
        private static readonly FieldInfo SupportField =
            AccessTools.Field(typeof(WearNTear), "m_support");

        private static readonly FieldInfo NViewField =
            AccessTools.Field(typeof(WearNTear), "m_nview");

        private static readonly FieldInfo RayMaskField =
            AccessTools.Field(typeof(WearNTear), "s_rayMask") ??
            AccessTools.Field(typeof(WearNTear), "m_rayMask");

        private static readonly MethodInfo GetCOMMethod =
            AccessTools.Method(typeof(WearNTear), "GetCOM");

        private static readonly MethodInfo GetSupportMethod =
            AccessTools.Method(typeof(WearNTear), "GetSupport");

        private static readonly MethodInfo GetMaterialPropertiesMethod =
            AccessTools.Method(typeof(WearNTear), "GetMaterialProperties");

        private static readonly MethodInfo UpdateSupportMethod =
            AccessTools.Method(typeof(WearNTear), "UpdateSupport");

        private static readonly MethodInfo GetAllInstancesMethod =
            AccessTools.Method(typeof(WearNTear), "GetAllInstances");

        private static readonly int SupportZdoHash = "support".GetStableHashCode();

        private static int _fallbackRayMask;

        public static bool IsWarmupActive { get; set; }

        /// <summary>
        /// Attaches <see cref="TrussNodeCache"/> and <see cref="LiveLoadSensor"/> when a <see cref="WearNTear"/> piece initializes.
        /// </summary>
        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        private static void Awake_Postfix(WearNTear __instance)
        {
            if (__instance == null || !(Plugin.EnableMod?.Value ?? true))
            {
                return;
            }

            TrussNodeCache cache = __instance.GetComponent<TrussNodeCache>();
            if (cache == null)
            {
                cache = __instance.gameObject.AddComponent<TrussNodeCache>();
            }
            cache.InvalidateAndNotifyNeighbors();

            if (__instance.GetComponent<LiveLoadSensor>() == null)
            {
                __instance.gameObject.AddComponent<LiveLoadSensor>();
            }

            Plugin.NotifyPieceAwake();
        }

        /// <summary>
        /// Invalidates cached truss adjacency when a piece is destroyed so neighbors recalculate cleanly.
        /// </summary>
        [HarmonyPatch("OnDestroy")]
        [HarmonyPrefix]
        private static void OnDestroy_Prefix(WearNTear __instance)
        {
            if (__instance == null)
            {
                return;
            }

            TrussNodeCache cache = __instance.GetComponent<TrussNodeCache>();
            if (cache != null)
            {
                cache.InvalidateAndNotifyNeighbors();
            }
        }

        /// <summary>
        /// Temporarily overrides <c>m_support</c> during <c>WearNTear.Highlight()</c> when a piece is under
        /// active live-load stress so both Hammer hover and automatic stress highlights display the real-time
        /// orange/red stressed state without altering ZDO support.
        /// </summary>
        [HarmonyPatch("Highlight")]
        [HarmonyPrefix]
        private static void Highlight_Prefix(WearNTear __instance, out float __state)
        {
            __state = -1f;
            if (__instance == null || !(Plugin.EnableMod?.Value ?? true))
            {
                return;
            }

            LiveLoadSensor sensor = __instance.GetComponent<LiveLoadSensor>();
            if (sensor != null && sensor.TryGetVisualHighlightSupportOverride(out float visualSupport))
            {
                __state = GetPieceSupportField(__instance);
                SetPieceSupportField(__instance, visualSupport);
            }
        }

        [HarmonyPatch("Highlight")]
        [HarmonyPostfix]
        private static void Highlight_Postfix(WearNTear __instance, float __state)
        {
            if (__instance != null && __state >= 0f)
            {
                SetPieceSupportField(__instance, __state);
            }
        }

        /// <summary>
        /// Stage 1A Hook on <c>WearNTear.GetMaterialProperties(out float maxSupport, out float minSupport, out float horizontalLoss, out float verticalLoss)</c>.
        /// Applies the diagonal strut horizontal loss discount ONLY when the piece itself is an angled strut (20° to 65° from horizontal).
        /// </summary>
        [HarmonyPatch("GetMaterialProperties")]
        [HarmonyPostfix]
        private static void GetMaterialProperties_Postfix(
            WearNTear __instance,
            ref float maxSupport,
            ref float minSupport,
            ref float horizontalLoss,
            ref float verticalLoss)
        {
            if (__instance == null || !(Plugin.EnableMod?.Value ?? true))
            {
                return;
            }

            if (IsPieceOrientedAsDiagonalStrut(__instance))
            {
                float strutMultiplier = Mathf.Clamp(Plugin.StrutLossMultiplier?.Value ?? 0.45f, 0.05f, 1.0f);
                horizontalLoss *= strutMultiplier;
            }
        }

        /// <summary>
        /// Stage 1A + 1B Hook on <c>WearNTear.UpdateSupport()</c>.
        /// Evaluates physically touching supporting neighbors via <see cref="TrussNodeCache"/> for:
        /// 1A) Diagonal strut donors/receivers (20°–65°), and
        /// 1B) Closed structural triangles (3-piece <c>A &lt;-&gt; B &lt;-&gt; C</c> or 4-piece with 2-beam chord),
        /// deterministically boosting <c>m_support</c> and syncing to <c>ZDO</c>.
        /// </summary>
        [HarmonyPatch("UpdateSupport")]
        [HarmonyPostfix]
        private static void UpdateSupport_Postfix(WearNTear __instance)
        {
            if (__instance == null || !__instance.m_supports || !(Plugin.EnableMod?.Value ?? true))
            {
                return;
            }

            ZNetView nview = GetNView(__instance);
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            GetVanillaMaterialProperties(
                __instance,
                out float maxSupport,
                out float minSupport,
                out float baseHorizontalLoss,
                out float baseVerticalLoss);

            float currentSupport = GetPieceSupportField(__instance);

            // Grounded pieces already sit at maxSupport
            if (currentSupport >= maxSupport)
            {
                return;
            }

            TrussNodeCache cacheA = __instance.GetComponent<TrussNodeCache>();
            if (cacheA == null)
            {
                cacheA = __instance.gameObject.AddComponent<TrussNodeCache>();
            }

            IReadOnlyDictionary<WearNTear, Vector3> neighborJoints = cacheA.NeighborJoints;
            if (neighborJoints.Count == 0)
            {
                return;
            }

            Vector3 comA = GetPieceCOM(__instance);
            float bestSupport = currentSupport;
            bool selfIsAngledStrut = IsPieceOrientedAsDiagonalStrut(__instance);
            float strutDiscount = Mathf.Clamp(Plugin.StrutLossMultiplier?.Value ?? 0.45f, 0.05f, 1.0f);
            float trussBonus = Mathf.Clamp(Plugin.TrussTriangleMultiplier?.Value ?? 0.50f, 0.5f * 0.1f, 1.0f);

            foreach (KeyValuePair<WearNTear, Vector3> kvp in neighborJoints)
            {
                WearNTear donorB = kvp.Key;
                if (donorB == null || donorB == __instance || !donorB.m_supports)
                {
                    continue;
                }

                float donorSupport = GetPieceSupport(donorB);
                if (donorSupport <= bestSupport)
                {
                    continue;
                }

                // Rule 1A: Diagonal Strut Efficiency (only when either connected piece is an angled strut in [20°, 65°])
                bool isStrutConnection = selfIsAngledStrut || IsPieceOrientedAsDiagonalStrut(donorB);

                // Rule 1B: Closed-Loop Triangle / Truss Detection (A <-> B <-> C with 3 distinct joints)
                bool formsTriangle = cacheA.SharesMutualNeighborWith(donorB);

                if (!isStrutConnection && !formsTriangle)
                {
                    continue;
                }

                float effectiveHorizontalLoss = baseHorizontalLoss;
                float effectiveVerticalLoss = baseVerticalLoss;

                if (isStrutConnection)
                {
                    effectiveHorizontalLoss *= strutDiscount;
                }

                if (formsTriangle)
                {
                    effectiveHorizontalLoss *= trussBonus;
                    effectiveVerticalLoss *= trussBonus;
                }

                Vector3 comB = GetPieceCOM(donorB);
                float dist = Vector3.Distance(comA, comB) + 0.1f;

                // Use the actual joint/support point between A and B just like vanilla FindSupportPoint
                Vector3 supportPoint = kvp.Value;
                float effectiveLoss = effectiveHorizontalLoss;

                if (supportPoint.y < comA.y + 0.05f)
                {
                    Vector3 normalized = (supportPoint - comA).normalized;
                    if (normalized.y < 0f)
                    {
                        float t = Mathf.Acos(1f - Mathf.Abs(normalized.y)) / (Mathf.PI * 0.5f);
                        effectiveLoss = Mathf.Lerp(effectiveHorizontalLoss, effectiveVerticalLoss, t);
                    }
                }

                float candidateSupport = donorSupport - (effectiveLoss * dist * donorSupport);
                if (candidateSupport > bestSupport)
                {
                    bestSupport = candidateSupport;
                }
            }

            float finalSupport = Mathf.Min(bestSupport, maxSupport);
            if (finalSupport > currentSupport)
            {
                SetPieceSupportAndZdo(__instance, nview, finalSupport);
            }

            if (!IsWarmupActive)
            {
                LiveLoadSensor sensor = __instance.GetComponent<LiveLoadSensor>();
                if (sensor != null)
                {
                    sensor.NotifySupportUpdated(maxSupport, minSupport, finalSupport);
                }
            }
        }

        public static void SetPieceSupportAndZdo(WearNTear piece, ZNetView nview, float supportValue)
        {
            SetPieceSupportField(piece, supportValue);
            if (nview != null && nview.IsValid() && nview.IsOwner())
            {
                ZDO zdo = nview.GetZDO();
                zdo?.Set(SupportZdoHash, supportValue);
            }
        }

        public static void InvokeUpdateSupport(WearNTear piece)
        {
            if (piece == null || UpdateSupportMethod == null)
            {
                return;
            }

            try
            {
                UpdateSupportMethod.Invoke(piece, null);
            }
            catch
            {
                // Ignore invocation errors on destroyed/invalid pieces
            }
        }

        public static List<WearNTear> GetAllWearNTearInstances()
        {
            if (GetAllInstancesMethod != null)
            {
                try
                {
                    return GetAllInstancesMethod.Invoke(null, null) as List<WearNTear>;
                }
                catch
                {
                    // Ignore
                }
            }

            return null;
        }

        /// <summary>
        /// Determines if two connected <see cref="WearNTear"/> pieces share a mutual 3rd neighbor
        /// forming a closed 3-joint structural triangle (<c>A &lt;-&gt; B &lt;-&gt; C</c>).
        /// </summary>
        public static bool FormsStructuralTriangle(WearNTear pieceA, WearNTear pieceB)
        {
            if (pieceA == null || pieceB == null || pieceA == pieceB)
            {
                return false;
            }

            TrussNodeCache cacheA = pieceA.GetComponent<TrussNodeCache>();
            if (cacheA == null)
            {
                cacheA = pieceA.gameObject.AddComponent<TrussNodeCache>();
            }

            return cacheA.SharesMutualNeighborWith(pieceB);
        }

        /// <summary>
        /// Checks whether two <see cref="WearNTear"/> pieces physically touch at their colliders within snap tolerance.
        /// </summary>
        public static bool ArePiecesConnected(WearNTear a, WearNTear b)
        {
            return TryGetConnectionJoint(a, b, out _);
        }

        /// <summary>
        /// Tests whether any enabled non-trigger colliders of <paramref name="a"/> and <paramref name="b"/>
        /// physically touch within <see cref="TrussNodeCache.JointContactTolerance"/>, returning the contact joint position.
        /// </summary>
        public static bool TryGetConnectionJoint(WearNTear a, WearNTear b, out Vector3 jointPos)
        {
            jointPos = Vector3.zero;
            if (a == null || b == null || a == b)
            {
                return false;
            }

            TrussNodeCache cacheA = a.GetComponent<TrussNodeCache>();
            if (cacheA != null)
            {
                return cacheA.TryGetJointWith(b, out jointPos);
            }

            Collider[] colsA = a.GetComponentsInChildren<Collider>();
            Collider[] colsB = b.GetComponentsInChildren<Collider>();

            for (int i = 0; i < colsA.Length; i++)
            {
                Collider colA = colsA[i];
                if (colA == null || !colA.enabled || colA.isTrigger || colA.attachedRigidbody != null)
                {
                    continue;
                }

                for (int j = 0; j < colsB.Length; j++)
                {
                    Collider colB = colsB[j];
                    if (colB == null || !colB.enabled || colB.isTrigger || colB.attachedRigidbody != null)
                    {
                        continue;
                    }

                    if (TryGetColliderContactPoint(colA, colB, TrussNodeCache.JointContactTolerance, out jointPos))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Computes whether two colliders are within <paramref name="maxDistance"/> of each other
        /// and outputs their midpoint contact joint.
        /// </summary>
        public static bool TryGetColliderContactPoint(
            Collider colA,
            Collider colB,
            float maxDistance,
            out Vector3 contactPoint)
        {
            contactPoint = Vector3.zero;
            if (colA == null || colB == null)
            {
                return false;
            }

            // Fast broad-phase AABB check
            Bounds boundsA = colA.bounds;
            Bounds boundsB = colB.bounds;
            boundsA.Expand(maxDistance * 2f);
            if (!boundsA.Intersects(boundsB))
            {
                return false;
            }

            Vector3 pointOnA = SafeClosestPoint(colA, boundsB.center);
            Vector3 pointOnB = SafeClosestPoint(colB, pointOnA);
            pointOnA = SafeClosestPoint(colA, pointOnB);

            float sqrDist = (pointOnA - pointOnB).sqrMagnitude;
            if (sqrDist <= maxDistance * maxDistance)
            {
                contactPoint = (pointOnA + pointOnB) * 0.5f;
                return true;
            }

            return false;
        }

        private static Vector3 SafeClosestPoint(Collider col, Vector3 target)
        {
            if (col is BoxCollider || col is SphereCollider || col is CapsuleCollider ||
                (col is MeshCollider mc && mc.convex))
            {
                return col.ClosestPoint(target);
            }

            return col.ClosestPointOnBounds(target);
        }

        /// <summary>
        /// Checks whether a vector's vertical angle relative to the horizontal plane is within [20°, 65°].
        /// </summary>
        public static bool IsVectorDiagonalStrutAngle(Vector3 delta)
        {
            float sqrMag = delta.sqrMagnitude;
            if (sqrMag < 0.0001f)
            {
                return false;
            }

            float sinTheta = Mathf.Clamp01(Mathf.Abs(delta.y) / Mathf.Sqrt(sqrMag));
            float angleDeg = Mathf.Asin(sinTheta) * Mathf.Rad2Deg;
            return angleDeg >= MinStrutAngleDeg && angleDeg <= MaxStrutAngleDeg;
        }

        /// <summary>
        /// Inspects a piece's prefab name and collider/transform pitch to detect genuine angled struts
        /// (such as Valheim's 26° and 45° diagonal beams/logs), while excluding flat horizontal (0°)
        /// and vertical (90°) pieces.
        /// </summary>
        public static bool IsPieceOrientedAsDiagonalStrut(WearNTear piece)
        {
            if (piece == null)
            {
                return false;
            }

            // 1. Check Valheim's angled beam/log prefab naming conventions (e.g., wood_beam_26, wood_beam_45, wood_log_26, wood_log_45)
            string name = piece.gameObject.name;
            if (!string.IsNullOrEmpty(name))
            {
                if (name.IndexOf("_26", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("_45", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("beam26", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("beam45", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            // 2. Check if the root transform itself is tilted in pitch/roll (for pieces rotated with gizmo/mods).
            // For standard un-tilted horizontal or vertical pieces, transform.up is (0, 1, 0) -> 90° from horizontal.
            // If transform.up is tilted so its elevation angle from horizontal is in [25°, 70°] (i.e. 20°–65° tilt from vertical),
            // the piece is pitched diagonally.
            Vector3 up = piece.transform.up;
            float upAngleFromHorizontal = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(up.y))) * Mathf.Rad2Deg;
            float tiltFromVertical = 90f - upAngleFromHorizontal;
            if (tiltFromVertical >= MinStrutAngleDeg && tiltFromVertical <= MaxStrutAngleDeg)
            {
                return true;
            }

            // 3. Check child BoxCollider orientations (in Valheim, angled beam prefabs often tilt the child collider transform)
            TrussNodeCache cache = piece.GetComponent<TrussNodeCache>();
            Collider[] colliders = cache != null ? cache.PieceColliders : piece.GetComponentsInChildren<Collider>();
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider col = colliders[i];
                    if (col == null || col.isTrigger)
                    {
                        continue;
                    }

                    Vector3 colUp = col.transform.up;
                    float colUpAngle = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(colUp.y))) * Mathf.Rad2Deg;
                    float colTilt = 90f - colUpAngle;
                    if (colTilt >= MinStrutAngleDeg && colTilt <= MaxStrutAngleDeg)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static Vector3 GetPieceCOM(WearNTear piece)
        {
            if (piece == null)
            {
                return Vector3.zero;
            }

            if (GetCOMMethod != null)
            {
                try
                {
                    return (Vector3)GetCOMMethod.Invoke(piece, null);
                }
                catch
                {
                    // Fall through to transform calculation
                }
            }

            return piece.transform.position + piece.transform.rotation * piece.m_comOffset;
        }

        public static float GetPieceSupport(WearNTear piece)
        {
            if (piece == null)
            {
                return 0f;
            }

            if (GetSupportMethod != null)
            {
                try
                {
                    return (float)GetSupportMethod.Invoke(piece, null);
                }
                catch
                {
                    // Fall through to field read
                }
            }

            return GetPieceSupportField(piece);
        }

        public static float GetPieceSupportField(WearNTear piece)
        {
            if (piece == null || SupportField == null)
            {
                return 0f;
            }

            return (float)SupportField.GetValue(piece);
        }

        public static void SetPieceSupportField(WearNTear piece, float value)
        {
            if (piece != null && SupportField != null)
            {
                SupportField.SetValue(piece, value);
            }
        }

        public static ZNetView GetNView(WearNTear piece)
        {
            if (piece == null)
            {
                return null;
            }

            if (NViewField != null)
            {
                ZNetView nv = NViewField.GetValue(piece) as ZNetView;
                if (nv != null)
                {
                    return nv;
                }
            }

            return piece.GetComponent<ZNetView>();
        }

        public static int GetSupportRayMask()
        {
            if (RayMaskField != null)
            {
                object val = RayMaskField.GetValue(null);
                if (val is int mask && mask != 0)
                {
                    return mask;
                }
            }

            if (_fallbackRayMask == 0)
            {
                _fallbackRayMask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain");
                if (_fallbackRayMask == 0)
                {
                    _fallbackRayMask = Physics.DefaultRaycastLayers;
                }
            }

            return _fallbackRayMask;
        }

        public static void GetVanillaMaterialProperties(
            WearNTear piece,
            out float maxSupport,
            out float minSupport,
            out float horizontalLoss,
            out float verticalLoss)
        {
            if (piece != null)
            {
                switch (piece.m_materialType)
                {
                    case WearNTear.MaterialType.Wood:
                        maxSupport = 100f;
                        minSupport = 10f;
                        verticalLoss = 0.125f;
                        horizontalLoss = 0.2f;
                        return;
                    case WearNTear.MaterialType.Stone:
                        maxSupport = 1000f;
                        minSupport = 100f;
                        verticalLoss = 0.125f;
                        horizontalLoss = 1f;
                        return;
                    case WearNTear.MaterialType.Iron:
                        maxSupport = 1500f;
                        minSupport = 20f;
                        verticalLoss = 0.07692308f;
                        horizontalLoss = 0.07692308f;
                        return;
                    case WearNTear.MaterialType.HardWood:
                        maxSupport = 140f;
                        minSupport = 10f;
                        verticalLoss = 0.1f;
                        horizontalLoss = 0.16666667f;
                        return;
                }

                if (GetMaterialPropertiesMethod != null)
                {
                    try
                    {
                        object[] args = new object[] { 0f, 0f, 0f, 0f };
                        GetMaterialPropertiesMethod.Invoke(piece, args);
                        maxSupport = (float)args[0];
                        minSupport = (float)args[1];
                        horizontalLoss = (float)args[2];
                        verticalLoss = (float)args[3];
                        return;
                    }
                    catch
                    {
                        // Fall through to default Wood baseline
                    }
                }
            }

            maxSupport = 100f;
            minSupport = 10f;
            verticalLoss = 0.125f;
            horizontalLoss = 0.2f;
        }
    }
}
