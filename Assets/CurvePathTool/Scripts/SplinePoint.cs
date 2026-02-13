using System;
using UnityEngine;

namespace SplineMeshTool
{
    [Serializable]
    public struct SplinePoint
    {
        public Vector3 position;

        [Tooltip("Roll around the forward axis in degrees. Primarily used for pipes (FollowRoll mode).")]
        public float rollDegrees;

        [Tooltip("Optional per-point width multiplier (default 1).")]
        public float widthScale;

        [Tooltip("Optional per-point height multiplier (default 1).")]
        public float heightScale;

        public SplinePoint(Vector3 position)
        {
            this.position = position;
            rollDegrees = 0f;
            widthScale = 1f;
            heightScale = 1f;
        }
    }
}
