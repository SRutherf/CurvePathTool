using System.Collections.Generic;
using UnityEngine;

namespace SplineMeshTool
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class SplineMeshGenerator : MonoBehaviour
    {
        public enum OrientationMode
        {
            KeepLevel,
            FollowRoll
        }

        [Header("References")]
        public SplinePath path;

        [Header("Profile")]
        public CrossSectionProfile profile;

        [Tooltip("If true, uses profile.closed. If false, overrides with 'forceClosed'.")]
        public bool useProfileClosedFlag = true;

        [Tooltip("Only used if useProfileClosedFlag is false.")]
        public bool forceClosed = false;

        [Header("Resolution")]
        [Min(0.05f)] public float metersPerRing = 1.5f;
        [Range(1, 128)] public int subdivisionsPerSegment = 16;
        [Min(2)] public int maxRings = 20000;

        [Header("Orientation")]
        public OrientationMode orientationMode = OrientationMode.KeepLevel;

        [Tooltip("For KeepLevel mode: up vector is world-up, transformed into this object's local space.")]
        public bool keepLevelUseWorldUp = true;

        [Header("Scale (Profile Units -> World Meters)")]
        [Tooltip("Scales the profile's X (right) values.")]
        public float baseWidth = 1f;

        [Tooltip("Scales the profile's Y (up) values.")]
        public float baseHeight = 1f;

        [Header("Taper Curves (0..1 along path)")]
        public AnimationCurve widthOverPath = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        public AnimationCurve heightOverPath = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [Header("UVs")]
        [Tooltip("Multiply profile U (0..1 around perimeter) by this.")]
        public float uScale = 1f;

        [Tooltip("V = distanceMeters * vMetersToUV (then multiplied by profile.vScale).")]
        public float vMetersToUV = 0.25f;

        [Header("Collider (optional)")]
        public bool generateCollider = false;

        [Tooltip("If > 0, collider uses its own metersPerRing (coarser is faster). If <= 0, collider uses metersPerRing.")]
        public float colliderMetersPerRing = -1f;

        [Header("Rebuild")]
        public bool autoRebuild = true;

        [Tooltip("If true, regenerates when selected in editor via OnValidate/Update. Disable if scene gets heavy.")]
        public bool rebuildInEditMode = true;

        private MeshFilter _mf;
        private MeshCollider _mc;

        // We keep preview meshes alive so we can destroy them and avoid leaking meshes in Edit Mode.
        private Mesh _previewMesh;
        private Mesh _previewColliderMesh;

        private void Reset()
        {
            _mf = GetComponent<MeshFilter>();
            _mc = GetComponent<MeshCollider>();
            if (path == null) path = GetComponent<SplinePath>();
        }

        private void OnEnable()
        {
            _mf = GetComponent<MeshFilter>();
            _mc = GetComponent<MeshCollider>();
            if (path == null) path = GetComponent<SplinePath>();

            if (autoRebuild)
                RebuildPreview();
        }

        private void OnDisable()
        {
            CleanupPreviewMeshes();
        }

        private void OnDestroy()
        {
            CleanupPreviewMeshes();
        }

        private void OnValidate()
        {
            metersPerRing = Mathf.Max(0.05f, metersPerRing);
            subdivisionsPerSegment = Mathf.Clamp(subdivisionsPerSegment, 1, 128);
            maxRings = Mathf.Max(2, maxRings);

            baseWidth = Mathf.Max(0.0001f, baseWidth);
            baseHeight = Mathf.Max(0.0001f, baseHeight);

            uScale = Mathf.Max(0.0001f, uScale);
            vMetersToUV = Mathf.Max(0.0001f, vMetersToUV);

            if (path == null) path = GetComponent<SplinePath>();
            if (_mf == null) _mf = GetComponent<MeshFilter>();
            if (_mc == null) _mc = GetComponent<MeshCollider>();

#if UNITY_EDITOR
            if (!Application.isPlaying && rebuildInEditMode && autoRebuild)
            {
                // Avoid rebuilding during import/compile edge cases where references are null.
                if (path != null && profile != null)
                    RebuildPreview();
            }
#endif
        }

#if UNITY_EDITOR
        private void Update()
        {
            // Optional safety: if someone changes transform while not calling OnValidate, allow manual rebuild instead.
            // Keeping Update empty avoids constant rebuilds.
        }
#endif

        [ContextMenu("Rebuild Preview")]
        public void RebuildPreview()
        {
            if (_mf == null) _mf = GetComponent<MeshFilter>();
            if (_mc == null) _mc = GetComponent<MeshCollider>();
            if (path == null) path = GetComponent<SplinePath>();

            if (path == null || profile == null)
                return;

            // 1) Sample path (path-local), then convert to this object's local space
            List<SplinePath.Sample> raw = path.SampleByDistance(metersPerRing, subdivisionsPerSegment, maxRings);
            if (raw == null || raw.Count < 2)
                return;

            // 2) Build frame samples (this object's local space)
            var frames = new List<SplineExtruder.FrameSample>(raw.Count);

            // World-up expressed in THIS object's local space (used for KeepLevel)
            Vector3 upLocalRef = keepLevelUseWorldUp
                ? transform.InverseTransformDirection(Vector3.up).normalized
                : Vector3.up;

            // Per-ring width/height multipliers based on spline point scales (optional)
            float[] widthMul = new float[raw.Count];
            float[] heightMul = new float[raw.Count];

            for (int i = 0; i < raw.Count; i++)
            {
                var s = raw[i];

                // Convert sample pos/tangent from path-local -> world -> this-local
                Vector3 posWorld = path.transform.TransformPoint(s.positionLocal);
                Vector3 tanWorld = path.transform.TransformDirection(s.tangentLocal).normalized;

                Vector3 posLocal = transform.InverseTransformPoint(posWorld);
                Vector3 fwdLocal = transform.InverseTransformDirection(tanWorld).normalized;
                if (fwdLocal.sqrMagnitude < 1e-8f) fwdLocal = Vector3.forward;

                // Build orientation frame
                Vector3 rightLocal, upLocal;

                if (orientationMode == OrientationMode.KeepLevel)
                {
                    // Up is world-up (converted to this local), then orthonormalize.
                    upLocal = upLocalRef;
                    rightLocal = Vector3.Cross(upLocal, fwdLocal);
                    if (rightLocal.sqrMagnitude < 1e-8f)
                    {
                        // Forward is near parallel to up; choose a fallback axis.
                        rightLocal = Vector3.Cross(Vector3.forward, fwdLocal);
                        if (rightLocal.sqrMagnitude < 1e-8f)
                            rightLocal = Vector3.right;
                    }
                    rightLocal.Normalize();
                    upLocal = Vector3.Cross(fwdLocal, rightLocal).normalized;
                }
                else // FollowRoll
                {
                    // Start from keep-level-like frame for stability.
                    upLocal = upLocalRef;
                    rightLocal = Vector3.Cross(upLocal, fwdLocal);
                    if (rightLocal.sqrMagnitude < 1e-8f)
                    {
                        rightLocal = Vector3.Cross(Vector3.forward, fwdLocal);
                        if (rightLocal.sqrMagnitude < 1e-8f)
                            rightLocal = Vector3.right;
                    }
                    rightLocal.Normalize();
                    upLocal = Vector3.Cross(fwdLocal, rightLocal).normalized;

                    // Apply roll (degrees) around forward axis (roll is sampled from control points).
                    float rollDeg = EvaluateRollDegreesApprox(s.t);
                    if (Mathf.Abs(rollDeg) > 0.0001f)
                    {
                        Quaternion q = Quaternion.AngleAxis(rollDeg, fwdLocal);
                        rightLocal = (q * rightLocal).normalized;
                        upLocal = (q * upLocal).normalized;
                    }
                }

                // Per-ring multipliers (approx from control points at param t)
                widthMul[i] = Mathf.Max(0.0001f, EvaluateWidthScaleApprox(s.t));
                heightMul[i] = Mathf.Max(0.0001f, EvaluateHeightScaleApprox(s.t));

                frames.Add(new SplineExtruder.FrameSample
                {
                    positionLocal = posLocal,
                    forwardLocal = fwdLocal,
                    rightLocal = rightLocal,
                    upLocal = upLocal,
                    distance = s.distance, // NOTE: s.distance is in path-local polyline units; assumes same scale. OK if path & generator are same scale.
                    s01 = s.s01
                });
            }

            // If path and generator transforms differ in scale, distance from path-local may be off.
            // For typical usage (same object), this is correct. If needed later, recompute distance in this-local.

            // 3) Build mesh
            bool closed = useProfileClosedFlag ? profile.closed : forceClosed;

            var settings = new SplineExtruder.BuildSettings
            {
                profile = profile,
                baseWidth = baseWidth,
                baseHeight = baseHeight,
                widthOverPath = widthOverPath,
                heightOverPath = heightOverPath,
                widthMultipliersPerRing = widthMul,
                heightMultipliersPerRing = heightMul,
                uScale = uScale * profile.uScale,
                vMetersToUV = vMetersToUV * profile.vScale,
                closed = closed
            };

            Mesh newMesh = null;
            try
            {
                newMesh = SplineExtruder.BuildMesh(frames, settings, "SplineMesh_Preview");
            }
            catch
            {
                // If something goes wrong, don't break editor spam; just abort.
                return;
            }

            AssignPreviewMesh(newMesh);

            // 4) Optional collider
            if (generateCollider)
            {
                float colStep = colliderMetersPerRing > 0f ? colliderMetersPerRing : metersPerRing;

                List<SplinePath.Sample> rawCol = path.SampleByDistance(colStep, subdivisionsPerSegment, maxRings);
                if (rawCol != null && rawCol.Count >= 2)
                {
                    var framesCol = new List<SplineExtruder.FrameSample>(rawCol.Count);
                    float[] widthMulCol = new float[rawCol.Count];
                    float[] heightMulCol = new float[rawCol.Count];

                    for (int i = 0; i < rawCol.Count; i++)
                    {
                        var s = rawCol[i];

                        Vector3 posWorld = path.transform.TransformPoint(s.positionLocal);
                        Vector3 tanWorld = path.transform.TransformDirection(s.tangentLocal).normalized;

                        Vector3 posLocal = transform.InverseTransformPoint(posWorld);
                        Vector3 fwdLocal = transform.InverseTransformDirection(tanWorld).normalized;
                        if (fwdLocal.sqrMagnitude < 1e-8f) fwdLocal = Vector3.forward;

                        Vector3 rightLocal, upLocal;

                        if (orientationMode == OrientationMode.KeepLevel)
                        {
                            upLocal = upLocalRef;
                            rightLocal = Vector3.Cross(upLocal, fwdLocal);
                            if (rightLocal.sqrMagnitude < 1e-8f)
                                rightLocal = Vector3.right;
                            rightLocal.Normalize();
                            upLocal = Vector3.Cross(fwdLocal, rightLocal).normalized;
                        }
                        else
                        {
                            upLocal = upLocalRef;
                            rightLocal = Vector3.Cross(upLocal, fwdLocal);
                            if (rightLocal.sqrMagnitude < 1e-8f)
                                rightLocal = Vector3.right;
                            rightLocal.Normalize();
                            upLocal = Vector3.Cross(fwdLocal, rightLocal).normalized;

                            float rollDeg = EvaluateRollDegreesApprox(s.t);
                            if (Mathf.Abs(rollDeg) > 0.0001f)
                            {
                                Quaternion q = Quaternion.AngleAxis(rollDeg, fwdLocal);
                                rightLocal = (q * rightLocal).normalized;
                                upLocal = (q * upLocal).normalized;
                            }
                        }

                        widthMulCol[i] = Mathf.Max(0.0001f, EvaluateWidthScaleApprox(s.t));
                        heightMulCol[i] = Mathf.Max(0.0001f, EvaluateHeightScaleApprox(s.t));

                        framesCol.Add(new SplineExtruder.FrameSample
                        {
                            positionLocal = posLocal,
                            forwardLocal = fwdLocal,
                            rightLocal = rightLocal,
                            upLocal = upLocal,
                            distance = s.distance,
                            s01 = s.s01
                        });
                    }

                    var colSettings = settings;
                    colSettings.widthMultipliersPerRing = widthMulCol;
                    colSettings.heightMultipliersPerRing = heightMulCol;

                    Mesh colMesh = null;
                    try
                    {
                        colMesh = SplineExtruder.BuildMesh(framesCol, colSettings, "SplineMesh_ColliderPreview");
                    }
                    catch
                    {
                        colMesh = null;
                    }

                    AssignPreviewColliderMesh(colMesh);
                }
            }
            else
            {
                AssignPreviewColliderMesh(null);
            }
        }

        // -----------------------------
        // Approximations from control points (roll/width/height)
        // -----------------------------

        private float EvaluateRollDegreesApprox(float t01)
        {
            if (path == null || path.points == null || path.points.Count == 0) return 0f;
            return EvaluatePointScalarLinear(t01, p => p.rollDegrees, 0f);
        }

        private float EvaluateWidthScaleApprox(float t01)
        {
            if (path == null || path.points == null || path.points.Count == 0) return 1f;
            return EvaluatePointScalarLinear(t01, p => p.widthScale <= 0f ? 1f : p.widthScale, 1f);
        }

        private float EvaluateHeightScaleApprox(float t01)
        {
            if (path == null || path.points == null || path.points.Count == 0) return 1f;
            return EvaluatePointScalarLinear(t01, p => p.heightScale <= 0f ? 1f : p.heightScale, 1f);
        }

        private float EvaluatePointScalarLinear(float t01, System.Func<SplinePoint, float> selector, float fallback)
        {
            int count = path.points.Count;
            if (count == 1) return selector(path.points[0]);

            t01 = Mathf.Clamp01(t01);
            float scaled = t01 * (count - 1);
            int i0 = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, count - 2);
            int i1 = i0 + 1;
            float a = scaled - i0;

            float v0 = selector(path.points[i0]);
            float v1 = selector(path.points[i1]);
            if (float.IsNaN(v0) || float.IsInfinity(v0)) v0 = fallback;
            if (float.IsNaN(v1) || float.IsInfinity(v1)) v1 = fallback;

            return Mathf.Lerp(v0, v1, a);
        }

        // -----------------------------
        // Mesh assignment + cleanup
        // -----------------------------

        private void AssignPreviewMesh(Mesh newMesh)
        {
            if (_mf == null) _mf = GetComponent<MeshFilter>();

            // Destroy previous preview mesh if it was ours.
            if (_previewMesh != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(_previewMesh);
                else Destroy(_previewMesh);
#else
                Destroy(_previewMesh);
#endif
                _previewMesh = null;
            }

            _previewMesh = newMesh;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                _mf.sharedMesh = _previewMesh;
            else
                _mf.mesh = _previewMesh;
#else
            _mf.mesh = _previewMesh;
#endif
        }

        private void AssignPreviewColliderMesh(Mesh newMesh)
        {
            if (_mc == null) _mc = GetComponent<MeshCollider>();
            if (_mc == null) return;

            // Destroy previous collider preview mesh if it was ours.
            if (_previewColliderMesh != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(_previewColliderMesh);
                else Destroy(_previewColliderMesh);
#else
                Destroy(_previewColliderMesh);
#endif
                _previewColliderMesh = null;
            }

            _previewColliderMesh = newMesh;

            _mc.sharedMesh = null;
            if (_previewColliderMesh != null)
                _mc.sharedMesh = _previewColliderMesh;
        }

        private void CleanupPreviewMeshes()
        {
            if (_previewMesh != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(_previewMesh);
                else Destroy(_previewMesh);
#else
                Destroy(_previewMesh);
#endif
                _previewMesh = null;
            }

            if (_previewColliderMesh != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(_previewColliderMesh);
                else Destroy(_previewColliderMesh);
#else
                Destroy(_previewColliderMesh);
#endif
                _previewColliderMesh = null;
            }
        }
    }
}
