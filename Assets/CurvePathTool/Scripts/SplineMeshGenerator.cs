using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

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
        public bool useProfileClosedFlag = true;
        public bool forceClosed = false;

        [Header("Resolution")]
        [Min(0.05f)] public float metersPerRing = 1.5f;
        [Range(1, 128)] public int subdivisionsPerSegment = 16;
        [Min(2)] public int maxRings = 20000;

        [Header("Orientation")]
        public OrientationMode orientationMode = OrientationMode.KeepLevel;
        public bool keepLevelUseWorldUp = true;

        [Header("Scale (Profile Units -> World Meters)")]
        public float baseWidth = 1f;
        public float baseHeight = 1f;

        [Header("Taper Curves (0..1 along path)")]
        public AnimationCurve widthOverPath = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        public AnimationCurve heightOverPath = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [Header("UVs")]
        public float uScale = 1f;
        public float vMetersToUV = 0.25f;

        [Header("Collider (optional)")]
        public bool generateCollider = false;
        public float colliderMetersPerRing = -1f;

        [Header("Rebuild")]
        public bool autoRebuild = true;
        public bool rebuildInEditMode = true;

        [Header("Bake")]
        public bool freezeWhenBaked = true;
        public string bakedFolder = "Assets/SplineMeshTool/BakedMeshes";

        [SerializeField, HideInInspector] private Mesh bakedMeshAsset;
        [SerializeField, HideInInspector] private Mesh bakedColliderMeshAsset;
        [SerializeField, HideInInspector] private bool isBaked;

        public bool IsBaked => isBaked;
        public Mesh BakedMeshAsset => bakedMeshAsset;

        private MeshFilter _mf;
        private MeshCollider _mc;

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

            if (autoRebuild && !(freezeWhenBaked && isBaked))
                RebuildPreview();
        }

        private void OnDisable() => CleanupPreviewMeshes();
        private void OnDestroy() => CleanupPreviewMeshes();

        private void OnValidate()
        {
            metersPerRing = Mathf.Max(0.05f, metersPerRing);
            subdivisionsPerSegment = Mathf.Clamp(subdivisionsPerSegment, 1, 128);
            maxRings = Mathf.Max(2, maxRings);

            baseWidth = Mathf.Max(0.0001f, baseWidth);
            baseHeight = Mathf.Max(0.0001f, baseHeight);

            uScale = Mathf.Max(0.0001f, uScale);
            vMetersToUV = Mathf.Max(0.0001f, vMetersToUV);

            if (string.IsNullOrWhiteSpace(bakedFolder))
                bakedFolder = "Assets/SplineMeshTool/BakedMeshes";

            if (path == null) path = GetComponent<SplinePath>();
            if (_mf == null) _mf = GetComponent<MeshFilter>();
            if (_mc == null) _mc = GetComponent<MeshCollider>();

#if UNITY_EDITOR
            if (!Application.isPlaying && rebuildInEditMode && autoRebuild)
            {
                if (freezeWhenBaked && isBaked) return;
                if (path != null && profile != null)
                    RebuildPreview();
            }
#endif
        }

        [ContextMenu("Rebuild Preview")]
        public void RebuildPreview()
        {
            if (freezeWhenBaked && isBaked) return;

            if (_mf == null) _mf = GetComponent<MeshFilter>();
            if (_mc == null) _mc = GetComponent<MeshCollider>();
            if (path == null) path = GetComponent<SplinePath>();
            if (path == null || profile == null) return;

            // ----- Render mesh
            var raw = path.SampleByDistance(metersPerRing, subdivisionsPerSegment, maxRings);
            if (raw == null || raw.Count < 2) return;

            var frameRes = SplineFrameBuilder.BuildFrames(
                path,
                transform,
                raw,
                (SplineFrameBuilder.OrientationMode)orientationMode,
                keepLevelUseWorldUp,
                EvaluateRollDegreesApprox,
                EvaluateWidthScaleApprox,
                EvaluateHeightScaleApprox
            );

            bool closed = useProfileClosedFlag ? profile.closed : forceClosed;

            var settings = new SplineExtruder.BuildSettings
            {
                profile = profile,
                baseWidth = baseWidth,
                baseHeight = baseHeight,
                widthOverPath = widthOverPath,
                heightOverPath = heightOverPath,
                widthMultipliersPerRing = frameRes.widthMul,
                heightMultipliersPerRing = frameRes.heightMul,
                uScale = uScale * profile.uScale,
                vMetersToUV = vMetersToUV * profile.vScale,
                closed = closed
            };

            Mesh newMesh;
            try
            {
                newMesh = SplineExtruder.BuildMesh(frameRes.frames, settings, "SplineMesh_Preview");
            }
            catch
            {
                return;
            }

            AssignPreviewMesh(newMesh);

            // ----- Collider mesh (optional)
            if (!generateCollider)
            {
                AssignPreviewColliderMesh(null);
                return;
            }

            float colStep = colliderMetersPerRing > 0f ? colliderMetersPerRing : metersPerRing;
            var rawCol = path.SampleByDistance(colStep, subdivisionsPerSegment, maxRings);
            if (rawCol == null || rawCol.Count < 2)
            {
                AssignPreviewColliderMesh(null);
                return;
            }

            var frameResCol = SplineFrameBuilder.BuildFrames(
                path,
                transform,
                rawCol,
                (SplineFrameBuilder.OrientationMode)orientationMode,
                keepLevelUseWorldUp,
                EvaluateRollDegreesApprox,
                EvaluateWidthScaleApprox,
                EvaluateHeightScaleApprox
            );

            var colSettings = settings;
            colSettings.widthMultipliersPerRing = frameResCol.widthMul;
            colSettings.heightMultipliersPerRing = frameResCol.heightMul;

            Mesh colMesh;
            try
            {
                colMesh = SplineExtruder.BuildMesh(frameResCol.frames, colSettings, "SplineMesh_ColliderPreview");
            }
            catch
            {
                colMesh = null;
            }

            AssignPreviewColliderMesh(colMesh);
        }

#if UNITY_EDITOR
        [ContextMenu("Bake Mesh Asset")]
        public void BakeMeshAsset()
        {
            if (_mf == null) _mf = GetComponent<MeshFilter>();
            if (_mc == null) _mc = GetComponent<MeshCollider>();
            if (path == null) path = GetComponent<SplinePath>();
            if (path == null || profile == null) return;

            bool wasBaked = isBaked;
            isBaked = false;
            RebuildPreview();
            isBaked = wasBaked;

            if (_mf.sharedMesh == null) return;

            EnsureFolderExists(bakedFolder);

            string baseName = $"{gameObject.name}_{profile.name}";
            string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{bakedFolder}/{baseName}.mesh");

            Mesh baked = Object.Instantiate(_mf.sharedMesh);
            baked.name = System.IO.Path.GetFileNameWithoutExtension(meshPath);
            AssetDatabase.CreateAsset(baked, meshPath);

            bakedMeshAsset = baked;
            isBaked = true;

            _mf.sharedMesh = bakedMeshAsset;

            if (generateCollider && _mc != null && _mc.sharedMesh != null)
            {
                string colPath = AssetDatabase.GenerateUniqueAssetPath($"{bakedFolder}/{baseName}_Collider.mesh");
                Mesh bakedCol = Object.Instantiate(_mc.sharedMesh);
                bakedCol.name = System.IO.Path.GetFileNameWithoutExtension(colPath);
                AssetDatabase.CreateAsset(bakedCol, colPath);

                bakedColliderMeshAsset = bakedCol;
                _mc.sharedMesh = null;
                _mc.sharedMesh = bakedColliderMeshAsset;
            }
            else
            {
                bakedColliderMeshAsset = null;
                if (_mc != null) _mc.sharedMesh = null;
            }

            CleanupPreviewMeshes();

            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }

        [ContextMenu("Clear Bake")]
        public void ClearBake()
        {
            if (_mf == null) _mf = GetComponent<MeshFilter>();
            if (_mc == null) _mc = GetComponent<MeshCollider>();

            isBaked = false;
            bakedMeshAsset = null;
            bakedColliderMeshAsset = null;

            if (autoRebuild)
                RebuildPreview();
            else
                _mf.sharedMesh = null;

            if (_mc != null) _mc.sharedMesh = null;

            EditorUtility.SetDirty(this);
        }

        private static void EnsureFolderExists(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Split('/');
            string current = parts[0]; // Assets
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
#endif

        // ---- Control-point scalar eval (same as before)
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

        private void AssignPreviewMesh(Mesh newMesh)
        {
            if (_mf == null) _mf = GetComponent<MeshFilter>();

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
            _mf.sharedMesh = _previewMesh;
#else
            _mf.mesh = _previewMesh;
#endif
        }

        private void AssignPreviewColliderMesh(Mesh newMesh)
        {
            if (_mc == null) _mc = GetComponent<MeshCollider>();
            if (_mc == null) return;

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
