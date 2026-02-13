using System.Collections.Generic;
using UnityEngine;

namespace SplineMeshTool
{
    /// <summary>
    /// Builds a mesh by extruding a 2D CrossSectionProfile along a list of spline samples.
    /// This class is stateless; SplineMeshGenerator will own settings and call this.
    /// </summary>
    public static class SplineExtruder
    {
        public struct FrameSample
        {
            public Vector3 positionLocal;
            public Vector3 forwardLocal; // unit
            public Vector3 rightLocal;   // unit
            public Vector3 upLocal;      // unit

            public float distance; // meters from start (local units)
            public float s01;      // normalized [0..1] along length
        }

        public struct BuildSettings
        {
            public CrossSectionProfile profile;

            // Scales applied to profile points (x uses width, y uses height).
            public float baseWidth;
            public float baseHeight;

            // Optional curves: evaluate at s01 (0..1). If null, treated as 1.
            public AnimationCurve widthOverPath;
            public AnimationCurve heightOverPath;

            // Per-ring multipliers (optional): length == samplesCount, else ignored.
            public float[] widthMultipliersPerRing;
            public float[] heightMultipliersPerRing;

            // UV tiling: V uses distance * vMetersToUV.
            // U uses normalized perimeter distance * uScale.
            public float uScale;
            public float vMetersToUV;

            // If true, close the shape (connect last profile point back to first).
            public bool closed;
        }

        public static Mesh BuildMesh(
            IReadOnlyList<FrameSample> samples,
            BuildSettings settings,
            string meshName = "SplineExtrudedMesh")
        {
            if (settings.profile == null)
                throw new System.ArgumentNullException(nameof(settings.profile), "BuildSettings.profile is null.");

            Vector2[] prof = settings.profile.points ?? System.Array.Empty<Vector2>();
            int profCount = prof.Length;

            Mesh mesh = new Mesh();
            mesh.name = meshName;

            if (samples == null || samples.Count < 2 || profCount < 2)
            {
                mesh.SetVertices(new List<Vector3>());
                mesh.SetTriangles(new List<int>(), 0);
                return mesh;
            }

            bool closed = settings.closed;
            int ringCount = samples.Count;
            int vertsPerRing = profCount;
            int vertCount = ringCount * vertsPerRing;

            // Segment count around the profile:
            // - If closed: profCount edges (last -> first included)
            // - If open: profCount - 1 edges
            int profEdgeCount = closed ? profCount : (profCount - 1);
            if (profEdgeCount < 1)
            {
                mesh.SetVertices(new List<Vector3>());
                mesh.SetTriangles(new List<int>(), 0);
                return mesh;
            }

            // Triangles:
            // Each pair of rings forms profEdgeCount quads; each quad = 2 triangles = 6 indices
            int quadCount = (ringCount - 1) * profEdgeCount;
            int triIndexCount = quadCount * 6;

            var vertices = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var triangles = new int[triIndexCount];

            float perimeterLen = settings.profile.GetPerimeterLength();
            if (perimeterLen <= 1e-6f)
                perimeterLen = 1f;

            // Precompute U coordinate per profile point based on cumulative perimeter
            float[] u01 = ComputeProfileU01(prof, closed, perimeterLen);

            // Build vertices + uvs
            for (int i = 0; i < ringCount; i++)
            {
                var s = samples[i];

                float widthCurve = settings.widthOverPath != null ? settings.widthOverPath.Evaluate(s.s01) : 1f;
                float heightCurve = settings.heightOverPath != null ? settings.heightOverPath.Evaluate(s.s01) : 1f;

                float wMul = (settings.widthMultipliersPerRing != null && settings.widthMultipliersPerRing.Length == ringCount)
                    ? settings.widthMultipliersPerRing[i]
                    : 1f;

                float hMul = (settings.heightMultipliersPerRing != null && settings.heightMultipliersPerRing.Length == ringCount)
                    ? settings.heightMultipliersPerRing[i]
                    : 1f;

                float width = settings.baseWidth * widthCurve * wMul;
                float height = settings.baseHeight * heightCurve * hMul;

                float v = s.distance * settings.vMetersToUV;

                int ringBase = i * vertsPerRing;
                for (int j = 0; j < vertsPerRing; j++)
                {
                    Vector2 p2 = prof[j];

                    Vector3 offset =
                        s.rightLocal * (p2.x * width) +
                        s.upLocal * (p2.y * height);

                    int idx = ringBase + j;
                    vertices[idx] = s.positionLocal + offset;

                    float u = u01[j] * settings.uScale;
                    uvs[idx] = new Vector2(u, v);
                }
            }

            // Build triangles
            int tIndex = 0;
            for (int i = 0; i < ringCount - 1; i++)
            {
                int ringA = i * vertsPerRing;
                int ringB = (i + 1) * vertsPerRing;

                for (int e = 0; e < profEdgeCount; e++)
                {
                    int a0 = ringA + e;
                    int a1 = ringA + NextProfileIndex(e, profCount, closed);
                    int b0 = ringB + e;
                    int b1 = ringB + NextProfileIndex(e, profCount, closed);

                    // Two triangles (a0, b0, b1) and (a0, b1, a1)
                    triangles[tIndex++] = a0;
                    triangles[tIndex++] = b0;
                    triangles[tIndex++] = b1;

                    triangles[tIndex++] = a0;
                    triangles[tIndex++] = b1;
                    triangles[tIndex++] = a1;
                }
            }

            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);

            // For now: let Unity compute normals/tangents.
            // Later you can compute custom normals (better for hard edges).
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            return mesh;
        }

        private static int NextProfileIndex(int edgeIndex, int profCount, bool closed)
        {
            int next = edgeIndex + 1;
            if (next < profCount) return next;
            return closed ? 0 : (profCount - 1); // open case won't be called for last edge
        }

        private static float[] ComputeProfileU01(Vector2[] prof, bool closed, float perimeterLen)
        {
            int n = prof.Length;
            var u01 = new float[n];

            if (n == 0) return u01;

            u01[0] = 0f;
            float accum = 0f;

            for (int i = 1; i < n; i++)
            {
                accum += Vector2.Distance(prof[i - 1], prof[i]);
                u01[i] = accum / perimeterLen;
            }

            if (closed && n >= 3)
            {
                // For closed shapes, you may want U to wrap smoothly; the above is fine.
                // The seam is between last->first.
            }

            return u01;
        }
    }
}
