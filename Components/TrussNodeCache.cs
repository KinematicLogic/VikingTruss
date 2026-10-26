using System.Collections.Generic;
using UnityEngine;
using VikingTruss.Patches;

namespace VikingTruss.Components
{
    /// <summary>
    /// Caches physically touching neighbor connections and their contact joint positions
    /// per <see cref="WearNTear"/> piece so closed-loop 3-node triangle checks (<c>A &lt;-&gt; B &lt;-&gt; C</c>)
    /// verify three distinct physical joints without repeated allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public class TrussNodeCache : MonoBehaviour
    {
        private static readonly Collider[] OverlapBuffer = new Collider[64];

        // Maximum distance between two colliders' closest points to count as a snapped joint (12 cm)
        public const float JointContactTolerance = 0.12f;

        // Minimum separation between the 3 joints (J_AB, J_BC, J_CA) of a structural triangle
        // so that 3 pieces merely meeting at the same single corner do not count as a truss loop.
        public const float MinTriangleJointSeparation = 0.45f;

        // Minimum sin(angle) between triangle edges to reject collinear arrangements (~14.5 degrees)
        public const float MinTriangleSinAngle = 0.25f;

        // Minimum cosine of angle between two collinear chord segments in a 4-piece triangle (~31 degrees tolerance for collider offset)
        public const float CollinearChordCosThreshold = 0.85f;

        private const float CacheLifetimeSeconds = 1.5f;

        private WearNTear _wearNTear;
        private Collider[] _colliders;
        private readonly Dictionary<WearNTear, Vector3> _neighborJoints = new Dictionary<WearNTear, Vector3>();
        private readonly Dictionary<WearNTear, bool> _triangleLookupCache = new Dictionary<WearNTear, bool>();
        private float _lastRebuildTime = -999f;
        private bool _isDirty = true;

        public IReadOnlyDictionary<WearNTear, Vector3> NeighborJoints
        {
            get
            {
                EnsureValidCache();
                return _neighborJoints;
            }
        }

        public Collider[] PieceColliders
        {
            get
            {
                if (_colliders == null || _colliders.Length == 0)
                {
                    _colliders = GetComponentsInChildren<Collider>();
                }
                return _colliders;
            }
        }

        private void Awake()
        {
            _wearNTear = GetComponent<WearNTear>();
            _colliders = GetComponentsInChildren<Collider>();
            _isDirty = true;
        }

        public void MarkCacheDirty()
        {
            _isDirty = true;
            _triangleLookupCache.Clear();
        }

        public void InvalidateAndNotifyNeighbors()
        {
            if (_neighborJoints.Count == 0 && _wearNTear != null)
            {
                RebuildNeighbors(Time.time);
            }

            _isDirty = true;
            _triangleLookupCache.Clear();

            foreach (KeyValuePair<WearNTear, Vector3> kvp in _neighborJoints)
            {
                WearNTear neighbor = kvp.Key;
                if (neighbor != null)
                {
                    TrussNodeCache neighborCache = neighbor.GetComponent<TrussNodeCache>();
                    if (neighborCache != null)
                    {
                        neighborCache.MarkCacheDirty();

                        // Also invalidate 2-hop neighbors (for 4-piece triangles where the opposite corner is 2 hops away)
                        foreach (KeyValuePair<WearNTear, Vector3> hop2 in neighborCache._neighborJoints)
                        {
                            if (hop2.Key != null && hop2.Key != _wearNTear)
                            {
                                TrussNodeCache hop2Cache = hop2.Key.GetComponent<TrussNodeCache>();
                                hop2Cache?.MarkCacheDirty();
                            }
                        }
                    }
                }
            }
            _neighborJoints.Clear();
        }

        public bool TryGetJointWith(WearNTear other, out Vector3 jointPos)
        {
            if (other == null || other == _wearNTear)
            {
                jointPos = Vector3.zero;
                return false;
            }

            EnsureValidCache();
            return _neighborJoints.TryGetValue(other, out jointPos);
        }

        /// <summary>
        /// Checks if <c>this</c> (Piece A) and <paramref name="other"/> (Piece B) form a closed
        /// structural triangle:
        /// <list type="bullet">
        ///   <item><description><b>3-piece triangle (<c>A &lt;-&gt; B &lt;-&gt; C</c>):</b> 3 pieces meeting at 3 non-collinear joints.</description></item>
        ///   <item><description><b>4-piece collinear-chord triangle (<c>A &lt;-&gt; B &lt;-&gt; C &lt;-&gt; D &lt;-&gt; A</c>):</b> 4 pieces where one side of the triangle (such as the 4m horizontal chord under two 45° or 26° diagonals) is composed of 2 collinear 2m pieces.</description></item>
        /// </list>
        /// </summary>
        public bool SharesMutualNeighborWith(WearNTear other)
        {
            if (_wearNTear == null || other == null || other == _wearNTear)
            {
                return false;
            }

            EnsureValidCache();

            if (_triangleLookupCache.TryGetValue(other, out bool cachedResult))
            {
                return cachedResult;
            }

            bool result = EvaluateStructuralTriangleWith(other);
            _triangleLookupCache[other] = result;
            return result;
        }

        private bool EvaluateStructuralTriangleWith(WearNTear other)
        {
            if (!_neighborJoints.TryGetValue(other, out Vector3 jointAB))
            {
                return false;
            }

            TrussNodeCache otherCache = GetOrAddCache(other);
            otherCache.EnsureValidCache();

            // 1. Check 3-piece closed loop (A <-> B <-> C)
            foreach (KeyValuePair<WearNTear, Vector3> kvp in _neighborJoints)
            {
                WearNTear candidateC = kvp.Key;
                if (candidateC == null || candidateC == _wearNTear || candidateC == other || !candidateC.m_supports)
                {
                    continue;
                }

                if (!otherCache._neighborJoints.TryGetValue(candidateC, out Vector3 jointBC))
                {
                    continue;
                }

                Vector3 jointCA = kvp.Value;
                if (IsValidTriangleJoints(jointAB, jointBC, jointCA))
                {
                    return true;
                }
            }

            // 2. Check 4-piece closed loop (A <-> B <-> C <-> D <-> A) where 2 pieces form a straight collinear chord
            // (e.g., two 2m horizontal beams + two 45° diagonal struts forming a 4m-base Warren truss triangle)
            bool aOrBIsStrut =
                WearNTearPatches.IsPieceOrientedAsDiagonalStrut(_wearNTear) ||
                WearNTearPatches.IsPieceOrientedAsDiagonalStrut(other);

            foreach (KeyValuePair<WearNTear, Vector3> kvpBC in otherCache._neighborJoints)
            {
                WearNTear candidateC = kvpBC.Key;
                if (candidateC == null || candidateC == _wearNTear || candidateC == other || !candidateC.m_supports)
                {
                    continue;
                }

                Vector3 jointBC = kvpBC.Value;
                TrussNodeCache cacheC = GetOrAddCache(candidateC);
                cacheC.EnsureValidCache();

                bool abcHasStrut = aOrBIsStrut || WearNTearPatches.IsPieceOrientedAsDiagonalStrut(candidateC);

                foreach (KeyValuePair<WearNTear, Vector3> kvpDA in _neighborJoints)
                {
                    WearNTear candidateD = kvpDA.Key;
                    if (candidateD == null || candidateD == _wearNTear || candidateD == other ||
                        candidateD == candidateC || !candidateD.m_supports)
                    {
                        continue;
                    }

                    if (!cacheC._neighborJoints.TryGetValue(candidateD, out Vector3 jointCD))
                    {
                        continue;
                    }

                    if (!abcHasStrut && !WearNTearPatches.IsPieceOrientedAsDiagonalStrut(candidateD))
                    {
                        continue;
                    }

                    Vector3 jointDA = kvpDA.Value;
                    if (IsValidFourPieceTriangle(jointAB, jointBC, jointCD, jointDA))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static TrussNodeCache GetOrAddCache(WearNTear piece)
        {
            TrussNodeCache cache = piece.GetComponent<TrussNodeCache>();
            if (cache == null)
            {
                cache = piece.gameObject.AddComponent<TrussNodeCache>();
            }
            return cache;
        }

        /// <summary>
        /// Verifies whether 4 cyclic joints (<paramref name="j0"/>, <paramref name="j1"/>, <paramref name="j2"/>, <paramref name="j3"/>)
        /// form a 3-sided geometric triangle in which one side is subdivided by a collinear midpoint joint.
        /// Rejects unbraced 4-corner rectangles/squares.
        /// </summary>
        public static bool IsValidFourPieceTriangle(Vector3 j0, Vector3 j1, Vector3 j2, Vector3 j3)
        {
            // Case 0: j0 is the midpoint along the straight chord between j3 and j1
            if (IsCollinearMidpoint(j3, j0, j1) && IsValidTriangleJoints(j3, j1, j2))
            {
                return true;
            }

            // Case 1: j1 is the midpoint along the straight chord between j0 and j2
            if (IsCollinearMidpoint(j0, j1, j2) && IsValidTriangleJoints(j0, j2, j3))
            {
                return true;
            }

            // Case 2: j2 is the midpoint along the straight chord between j1 and j3
            if (IsCollinearMidpoint(j1, j2, j3) && IsValidTriangleJoints(j1, j3, j0))
            {
                return true;
            }

            // Case 3: j3 is the midpoint along the straight chord between j2 and j0
            if (IsCollinearMidpoint(j2, j3, j0) && IsValidTriangleJoints(j2, j0, j1))
            {
                return true;
            }

            return false;
        }

        private static bool IsCollinearMidpoint(Vector3 start, Vector3 mid, Vector3 end)
        {
            Vector3 seg1 = mid - start;
            Vector3 seg2 = end - mid;
            float len1 = seg1.magnitude;
            float len2 = seg2.magnitude;

            if (len1 < MinTriangleJointSeparation || len2 < MinTriangleJointSeparation)
            {
                return false;
            }

            float cosAngle = Vector3.Dot(seg1 / len1, seg2 / len2);
            return cosAngle >= CollinearChordCosThreshold;
        }

        public static bool IsValidTriangleJoints(Vector3 jointAB, Vector3 jointBC, Vector3 jointCA)
        {
            float distAB_BC = Vector3.Distance(jointAB, jointBC);
            float distBC_CA = Vector3.Distance(jointBC, jointCA);
            float distCA_AB = Vector3.Distance(jointCA, jointAB);

            // Reject 3 pieces that merely touch at the same single corner/joint
            if (distAB_BC < MinTriangleJointSeparation ||
                distBC_CA < MinTriangleJointSeparation ||
                distCA_AB < MinTriangleJointSeparation)
            {
                return false;
            }

            // Reject collinear joints
            Vector3 edge1 = (jointBC - jointAB) / distAB_BC;
            Vector3 edge2 = (jointCA - jointAB) / distCA_AB;
            float sinAngle = Vector3.Cross(edge1, edge2).magnitude;

            return sinAngle >= MinTriangleSinAngle;
        }

        private void EnsureValidCache()
        {
            if (_wearNTear == null)
            {
                _wearNTear = GetComponent<WearNTear>();
                if (_wearNTear == null)
                {
                    return;
                }
            }

            float now = Time.time;
            if (!_isDirty && (now - _lastRebuildTime) < CacheLifetimeSeconds)
            {
                return;
            }

            RebuildNeighbors(now);
        }

        private void RebuildNeighbors(float now)
        {
            _neighborJoints.Clear();
            _triangleLookupCache.Clear();
            _isDirty = false;
            _lastRebuildTime = now;

            Collider[] myColliders = PieceColliders;
            if (myColliders == null || myColliders.Length == 0)
            {
                return;
            }

            int rayMask = WearNTearPatches.GetSupportRayMask();
            Vector3 margin = new Vector3(JointContactTolerance, JointContactTolerance, JointContactTolerance);

            for (int c = 0; c < myColliders.Length; c++)
            {
                Collider myCol = myColliders[c];
                if (myCol == null || !myCol.enabled || myCol.isTrigger || myCol.attachedRigidbody != null)
                {
                    continue;
                }

                int hitCount;
                if (myCol is BoxCollider box)
                {
                    Vector3 worldCenter = box.transform.TransformPoint(box.center);
                    Vector3 worldHalfExtents = Vector3.Scale(box.size * 0.5f, AbsVector(box.transform.lossyScale)) + margin;
                    hitCount = Physics.OverlapBoxNonAlloc(
                        worldCenter,
                        worldHalfExtents,
                        OverlapBuffer,
                        box.transform.rotation,
                        rayMask,
                        QueryTriggerInteraction.Ignore);
                }
                else
                {
                    Bounds b = myCol.bounds;
                    hitCount = Physics.OverlapBoxNonAlloc(
                        b.center,
                        b.extents + margin,
                        OverlapBuffer,
                        Quaternion.identity,
                        rayMask,
                        QueryTriggerInteraction.Ignore);
                }

                for (int i = 0; i < hitCount; i++)
                {
                    Collider otherCol = OverlapBuffer[i];
                    if (otherCol == null || !otherCol.enabled || otherCol.isTrigger || otherCol.attachedRigidbody != null)
                    {
                        continue;
                    }

                    WearNTear neighbor = otherCol.GetComponentInParent<WearNTear>();
                    if (neighbor == null || neighbor == _wearNTear || !neighbor.m_supports)
                    {
                        continue;
                    }

                    if (_neighborJoints.ContainsKey(neighbor))
                    {
                        continue;
                    }

                    if (WearNTearPatches.TryGetColliderContactPoint(myCol, otherCol, JointContactTolerance, out Vector3 contactPoint))
                    {
                        _neighborJoints[neighbor] = contactPoint;
                    }
                }
            }
        }

        private static Vector3 AbsVector(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }
    }
}
