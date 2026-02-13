using System.Collections.Generic;
using UnityEngine;

namespace SplineMeshTool
{
    /// <summary>
    /// Stores control points and provides Catmull-Rom evaluation + distance-based sampling.
    /// t is normalized [0..1] over the whole path (from first point to last point).
    /// </summary>
    [ExecuteAlways]
    public class SplinePath : MonoBehaviour
    {
        [Min(2)]
        public int minPoints = 2;

        [Tooltip("Control points in local space of this transform.")]
        public List<SplinePoint> points = new List<SplinePoint>();

        [Header("Preview")]
        [Min(1)] public int previewSubdivisionsPerSegment = 16;
        public bool drawGizmos = true;

        public int PointCount => points?.Count ?? 0;
        public int SegmentCount => Mathf.Max(0, PointCount - 1);

        private void Reset()
        {
            points = new List<SplinePoint>
            {
                new SplinePoint(new Vector3(0, 0, 0)),
                new SplinePoint(new Vector3(0, 0, 50))
            };
        }

        private void OnValidate()
        {
            if (points == null) points = new List<SplinePoint>();
            while (points.Count < minPoints)
            {
                Vector3 pos = points.Count == 0 ? Vector3.zero : points[points.Count - 1].position + Vector3.forward * 10f;
                points.Add(new SplinePoint(pos));
            }

            // Ensure sane defaults
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p.widthScale <= 0f) p.widthScale = 1f;
                if (p.heightScale <= 0f) p.heightScale = 1f;
                points[i] = p;
            }
        }

        // -----------------------------
        // Public API
        // -----------------------------

        /// <summary>
        /// Evaluate local-space position at normalized t [0..1] using Catmull-Rom (clamped endpoints).
        /// </summary>
        public Vector3 EvaluateLocalPosition(float t)
        {
            if (PointCount == 0) return Vector3.zero;
            if (PointCount == 1) return points[0].position;
            if (PointCount == 2) return Vector3.Lerp(points[0].position, points[1].position, Mathf.Clamp01(t));

            t = Mathf.Clamp01(t);

            // Map t across segments [0..SegmentCount)
            float scaled = t * SegmentCount;
            int seg = Mathf.Min(Mathf.FloorToInt(scaled), SegmentCount - 1);
            float u = scaled - seg;

            // Catmull-Rom uses p0,p1,p2,p3 with p1->p2 as the segment
            Vector3 p0 = GetPointClamped(seg - 1).position;
            Vector3 p1 = GetPointClamped(seg).position;
            Vector3 p2 = GetPointClamped(seg + 1).position;
            Vector3 p3 = GetPointClamped(seg + 2).position;

            return CatmullRom(p0, p1, p2, p3, u);
        }

        /// <summary>
        /// Evaluate local-space tangent (unit) at normalized t [0..1].
        /// </summary>
        public Vector3 EvaluateLocalTangent(float t)
        {
            if (PointCount < 2) return Vector3.forward;

            if (PointCount == 2)
            {
                Vector3 dir = (points[1].position - points[0].position);
                return dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.forward;
            }

            t = Mathf.Clamp01(t);

            float scaled = t * SegmentCount;
            int seg = Mathf.Min(Mathf.FloorToInt(scaled), SegmentCount - 1);
            float u = scaled - seg;

            Vector3 p0 = GetPointClamped(seg - 1).position;
            Vector3 p1 = GetPointClamped(seg).position;
            Vector3 p2 = GetPointClamped(seg + 1).position;
            Vector3 p3 = GetPointClamped(seg + 2).position;

            Vector3 d = CatmullRomDerivative(p0, p1, p2, p3, u);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
        }

        public Vector3 EvaluateWorldPosition(float t) => transform.TransformPoint(EvaluateLocalPosition(t));
        public Vector3 EvaluateWorldTangent(float t) => transform.TransformDirection(EvaluateLocalTangent(t)).normalized;

        public struct Sample
        {
            public Vector3 positionLocal;
            public Vector3 tangentLocal;   // unit
            public float distance;         // accumulated distance from start (local units)
            public float s01;              // normalized [0..1] distance fraction
            public float t;                // normalized param [0..1] (approx)
        }

        /// <summary>
        /// Returns samples approximately every stepMeters along the path (local units), including start and end.
        /// Uses a polyline approximation then resamples by distance for stability.
        /// </summary>
        public List<Sample> SampleByDistance(float stepMeters, int subdivisionsPerSegment = 16, int maxSamples = 20000)
        {
            stepMeters = Mathf.Max(0.01f, stepMeters);
            subdivisionsPerSegment = Mathf.Clamp(subdivisionsPerSegment, 1, 128);

            var poly = BuildPolyline(subdivisionsPerSegment);
            var outSamples = new List<Sample>(Mathf.Min(maxSamples, 1024));

            if (poly.Count < 2)
                return outSamples;

            // Compute cumulative distances
            float[] cum = new float[poly.Count];
            cum[0] = 0f;
            for (int i = 1; i < poly.Count; i++)
                cum[i] = cum[i - 1] + Vector3.Distance(poly[i], poly[i - 1]);

            float totalLen = cum[cum.Length - 1];
            if (totalLen <= 1e-6f)
            {
                outSamples.Add(new Sample
                {
                    positionLocal = poly[0],
                    tangentLocal = Vector3.forward,
                    distance = 0f,
                    s01 = 0f,
                    t = 0f
                });
                return outSamples;
            }

            // Always include start
            outSamples.Add(MakeSampleAtDistance(0f));

            // Place samples each stepMeters
            for (float d = stepMeters; d < totalLen; d += stepMeters)
            {
                if (outSamples.Count >= maxSamples) break;
                outSamples.Add(MakeSampleAtDistance(d));
            }

            // Always include end
            if (outSamples.Count < maxSamples)
                outSamples.Add(MakeSampleAtDistance(totalLen));

            return outSamples;

            Sample MakeSampleAtDistance(float targetD)
            {
                // Find segment in polyline
                int idx = System.Array.BinarySearch(cum, targetD);
                if (idx < 0) idx = ~idx;
                idx = Mathf.Clamp(idx, 1, poly.Count - 1);

                float d0 = cum[idx - 1];
                float d1 = cum[idx];
                float a = Mathf.InverseLerp(d0, d1, targetD);

                Vector3 p = Vector3.Lerp(poly[idx - 1], poly[idx], a);

                Vector3 dir = (poly[idx] - poly[idx - 1]);
                Vector3 tan = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.forward;

                float s01 = targetD / totalLen;

                // Approximate t from polyline index
                float tApprox = (idx - 1 + a) / (poly.Count - 1);

                return new Sample
                {
                    positionLocal = p,
                    tangentLocal = tan,
                    distance = targetD,
                    s01 = s01,
                    t = tApprox
                };
            }
        }

        // -----------------------------
        // Internals
        // -----------------------------

        private SplinePoint GetPointClamped(int i)
        {
            if (PointCount == 0) return default;
            i = Mathf.Clamp(i, 0, PointCount - 1);
            return points[i];
        }

        private List<Vector3> BuildPolyline(int subdivisionsPerSegment)
        {
            var poly = new List<Vector3>(SegmentCount * subdivisionsPerSegment + 1);

            if (PointCount == 0) return poly;
            if (PointCount == 1)
            {
                poly.Add(points[0].position);
                return poly;
            }

            int segs = SegmentCount;

            // Include first point
            poly.Add(EvaluateLocalPosition(0f));

            // Add interior samples
            int totalSteps = segs * subdivisionsPerSegment;
            for (int i = 1; i <= totalSteps; i++)
            {
                float t = (float)i / totalSteps;
                poly.Add(EvaluateLocalPosition(t));
            }

            return poly;
        }

        // Catmull-Rom (uniform) position
        public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            // 0.5 * ((2 p1) + (-p0 + p2) t + (2p0 - 5p1 + 4p2 - p3) t^2 + (-p0 + 3p1 - 3p2 + p3) t^3)
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        // Derivative of Catmull-Rom
        public static Vector3 CatmullRomDerivative(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            // 0.5 * ((-p0 + p2) + 2(2p0 -5p1 +4p2 -p3)t + 3(-p0 +3p1 -3p2 +p3)t^2)
            float t2 = t * t;

            return 0.5f * (
                (-p0 + p2) +
                2f * (2f * p0 - 5f * p1 + 4f * p2 - p3) * t +
                3f * (-p0 + 3f * p1 - 3f * p2 + p3) * t2
            );
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;
            if (PointCount < 2) return;

            Gizmos.matrix = transform.localToWorldMatrix;

            // Control points
            Gizmos.color = Color.yellow;
            for (int i = 0; i < PointCount; i++)
                Gizmos.DrawSphere(points[i].position, 0.25f);

            // Curve preview
            Gizmos.color = Color.cyan;
            var poly = BuildPolyline(previewSubdivisionsPerSegment);
            for (int i = 1; i < poly.Count; i++)
                Gizmos.DrawLine(poly[i - 1], poly[i]);
        }
    }
}
