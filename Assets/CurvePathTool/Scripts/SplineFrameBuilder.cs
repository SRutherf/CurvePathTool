using System.Collections.Generic;
using UnityEngine;

namespace SplineMeshTool
{
    public static class SplineFrameBuilder
    {
        public enum OrientationMode
        {
            KeepLevel,
            FollowRoll
        }

        public struct FrameResult
        {
            public List<SplineExtruder.FrameSample> frames;
            public float[] widthMul;
            public float[] heightMul;
        }

        /// <summary>
        /// Builds stable frames in generator-local space from samples created by SplinePath.
        /// Recomputes distance in generator-local space from positions for correct spacing + UVs.
        /// </summary>
        public static FrameResult BuildFrames(
            SplinePath path,
            Transform generatorTransform,
            IReadOnlyList<SplinePath.Sample> samples,
            OrientationMode mode,
            bool keepLevelUseWorldUp,
            System.Func<float, float> evalRollDeg,
            System.Func<float, float> evalWidthScale,
            System.Func<float, float> evalHeightScale)
        {
            var result = new FrameResult
            {
                frames = new List<SplineExtruder.FrameSample>(samples.Count),
                widthMul = new float[samples.Count],
                heightMul = new float[samples.Count]
            };

            if (samples == null || samples.Count < 2 || path == null || generatorTransform == null)
                return result;

            // Reference up in generator-local space
            Vector3 upRefLocal = keepLevelUseWorldUp
                ? generatorTransform.InverseTransformDirection(Vector3.up).normalized
                : Vector3.up;

            // --- First sample: build an initial orthonormal frame
            Vector3 p0 = ToGeneratorLocalPos(path, generatorTransform, samples[0].positionLocal);
            Vector3 f0 = ToGeneratorLocalDir(path, generatorTransform, samples[0].tangentLocal);

            if (f0.sqrMagnitude < 1e-8f) f0 = Vector3.forward;
            f0.Normalize();

            Vector3 up0 = upRefLocal;
            Vector3 r0 = Vector3.Cross(up0, f0);
            if (r0.sqrMagnitude < 1e-8f)
                r0 = Vector3.Cross(Vector3.forward, f0);
            if (r0.sqrMagnitude < 1e-8f)
                r0 = Vector3.right;
            r0.Normalize();
            up0 = Vector3.Cross(f0, r0).normalized;

            // In FollowRoll we will apply roll on top of transported frame.
            if (mode == OrientationMode.FollowRoll)
            {
                float rollDeg = evalRollDeg != null ? evalRollDeg(samples[0].t) : 0f;
                if (Mathf.Abs(rollDeg) > 0.0001f)
                {
                    Quaternion q = Quaternion.AngleAxis(rollDeg, f0);
                    r0 = (q * r0).normalized;
                    up0 = (q * up0).normalized;
                }
            }

            float distAccum = 0f;
            result.frames.Add(new SplineExtruder.FrameSample
            {
                positionLocal = p0,
                forwardLocal = f0,
                rightLocal = r0,
                upLocal = up0,
                distance = 0f,
                s01 = 0f
            });

            result.widthMul[0] = Mathf.Max(0.0001f, evalWidthScale != null ? evalWidthScale(samples[0].t) : 1f);
            result.heightMul[0] = Mathf.Max(0.0001f, evalHeightScale != null ? evalHeightScale(samples[0].t) : 1f);

            // --- Subsequent samples: parallel transport the frame to reduce twist/jitter
            Vector3 prevPos = p0;
            Vector3 prevFwd = f0;
            Vector3 prevUp = up0;
            Vector3 prevRight = r0;

            for (int i = 1; i < samples.Count; i++)
            {
                Vector3 pos = ToGeneratorLocalPos(path, generatorTransform, samples[i].positionLocal);
                Vector3 fwd = ToGeneratorLocalDir(path, generatorTransform, samples[i].tangentLocal);

                if (fwd.sqrMagnitude < 1e-8f) fwd = prevFwd;
                fwd.Normalize();

                // Recompute distance in generator-local space
                distAccum += Vector3.Distance(prevPos, pos);

                Vector3 up, right;

                if (mode == OrientationMode.KeepLevel)
                {
                    // KeepLevel: gently pull "up" toward reference up to prevent flip, while staying perpendicular to fwd.
                    // (Simple stable approach: use reference up, orthonormalize.)
                    up = upRefLocal;
                    right = Vector3.Cross(up, fwd);
                    if (right.sqrMagnitude < 1e-8f)
                        right = prevRight;
                    right.Normalize();
                    up = Vector3.Cross(fwd, right).normalized;
                }
                else
                {
                    // FollowRoll: transport previous up to new forward with minimal twist.
                    // Rotate previous frame from prevFwd to fwd, then re-orthonormalize.
                    Quaternion transport = Quaternion.FromToRotation(prevFwd, fwd);
                    up = (transport * prevUp);
                    right = Vector3.Cross(up, fwd);

                    if (right.sqrMagnitude < 1e-8f)
                        right = prevRight;

                    right.Normalize();
                    up = Vector3.Cross(fwd, right).normalized;

                    // Apply roll for this sample
                    float rollDeg = evalRollDeg != null ? evalRollDeg(samples[i].t) : 0f;
                    if (Mathf.Abs(rollDeg) > 0.0001f)
                    {
                        Quaternion q = Quaternion.AngleAxis(rollDeg, fwd);
                        right = (q * right).normalized;
                        up = (q * up).normalized;
                    }
                }

                float s01 = (samples.Count == 1) ? 0f : (float)i / (samples.Count - 1);

                result.frames.Add(new SplineExtruder.FrameSample
                {
                    positionLocal = pos,
                    forwardLocal = fwd,
                    rightLocal = right,
                    upLocal = up,
                    distance = distAccum,
                    s01 = s01
                });

                result.widthMul[i] = Mathf.Max(0.0001f, evalWidthScale != null ? evalWidthScale(samples[i].t) : 1f);
                result.heightMul[i] = Mathf.Max(0.0001f, evalHeightScale != null ? evalHeightScale(samples[i].t) : 1f);

                prevPos = pos;
                prevFwd = fwd;
                prevUp = up;
                prevRight = right;
            }

            // Fix s01 based on distance fraction (better for taper curves)
            float total = distAccum;
            if (total > 1e-6f)
            {
                for (int i = 0; i < result.frames.Count; i++)
                {
                    var fs = result.frames[i];
                    fs.s01 = fs.distance / total;
                    result.frames[i] = fs;
                }
            }

            return result;
        }

        private static Vector3 ToGeneratorLocalPos(SplinePath path, Transform genT, Vector3 pathLocalPos)
        {
            Vector3 w = path.transform.TransformPoint(pathLocalPos);
            return genT.InverseTransformPoint(w);
        }

        private static Vector3 ToGeneratorLocalDir(SplinePath path, Transform genT, Vector3 pathLocalDir)
        {
            Vector3 w = path.transform.TransformDirection(pathLocalDir);
            return genT.InverseTransformDirection(w);
        }
    }
}
