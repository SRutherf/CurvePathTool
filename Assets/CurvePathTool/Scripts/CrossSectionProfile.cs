using UnityEngine;

namespace SplineMeshTool
{
    /// <summary>
    /// A 2D cross-section in local (Right, Up) coordinates that will be extruded along a spline.
    /// Units are in meters in the generator's local space.
    ///
    /// Convention:
    /// - X = right, Y = up
    /// - Points are ordered along the perimeter/edge.
    /// - If closed = true, the last point connects back to the first.
    /// </summary>
    [CreateAssetMenu(menuName = "Spline Mesh Tool/Cross Section Profile", fileName = "CrossSectionProfile")]
    public class CrossSectionProfile : ScriptableObject
    {
        [Header("Profile Shape (2D)")]
        [Tooltip("Points in (Right, Up). For pipes, these should form a loop around the center. For roads/canyons, these can be an open polyline or a closed loop depending on your needs.")]
        public Vector2[] points = new Vector2[]
        {
            new Vector2(-5f, 0f),
            new Vector2( 5f, 0f),
        };

        [Tooltip("If true, connect the last point back to the first when generating triangles.")]
        public bool closed = false;

        [Header("UV Defaults")]
        [Tooltip("U tiling multiplier across the profile perimeter/width.")]
        public float uScale = 1f;

        [Tooltip("V tiling multiplier along the path length.")]
        public float vScale = 1f;

        [Header("Preview")]
        [Min(0.001f)]
        public float gizmoScale = 1f;

        public int PointCount => points?.Length ?? 0;

        private void OnValidate()
        {
            if (points == null) points = new Vector2[0];
            if (uScale <= 0f) uScale = 1f;
            if (vScale <= 0f) vScale = 1f;
            gizmoScale = Mathf.Max(0.001f, gizmoScale);
        }

        /// <summary>
        /// Returns a polyline length (open) or perimeter (closed), in profile units.
        /// Used later for UV mapping.
        /// </summary>
        public float GetPerimeterLength()
        {
            if (points == null || points.Length < 2) return 0f;

            float len = 0f;
            for (int i = 1; i < points.Length; i++)
                len += Vector2.Distance(points[i - 1], points[i]);

            if (closed && points.Length >= 3)
                len += Vector2.Distance(points[points.Length - 1], points[0]);

            return len;
        }
    }
}
