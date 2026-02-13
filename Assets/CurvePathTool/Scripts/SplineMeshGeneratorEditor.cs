// Assets/SplineMeshTool/Editor/SplineMeshGeneratorEditor.cs
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using SplineMeshTool;

[CustomEditor(typeof(SplineMeshGenerator))]
public class SplineMeshGeneratorEditor : Editor
{
    private SplineMeshGenerator gen;
    private SplinePath path;

    private int selectedPoint = -1;

    // --- Snapping UI ---
    private bool snapToGrid = true;
    private float gridSize = 1f;

    // --- Horizontal Arc tool UI ---
    private enum ArcDirection { Left, Right }
    private ArcDirection arcDirection = ArcDirection.Left;

    private static readonly int[] ArcAngleOptions = { 15, 30, 45, 60, 90, 120, 180 };
    private int arcAngleIndex = 3; // default 60

    private float arcRadius = 25f;
    private int arcSubdivisions = 6;

    // --- Exit tangent anchor (horizontal arc) ---
    private bool addOrAlignExitTangentPoint = true;
    private float exitTangentDistance = 25f;

    // --- Selection behavior ---
    private bool selectNewEndAfterInsert = true;

    // --- Ramp (straight) tool UI ---
    private float rampLength = 50f;
    private float rampRise = 10f;
    private int rampSubdivisions = 6;
    private bool rampAffectsYOnly = true; // keep horizontal plan, only change Y

    // --- Vertical Arc tool UI (curved ramp / slide) ---
    private enum VerticalArcDirection { Up, Down }
    private VerticalArcDirection verticalArcDirection = VerticalArcDirection.Up;
    private int verticalArcAngleIndex = 3; // default 60
    private float verticalArcRadius = 25f;
    private int verticalArcSubdivisions = 6;

    // --- Exit tangent anchor (vertical arc) ---
    private bool addOrAlignExitTangentPointVertical = true;
    private float exitTangentDistanceVertical = 25f;

    // --- 3D Arc (Yaw + Pitch) ---
    private float arc3dYawDeg = 90f;     // + right, - left
    private float arc3dPitchDeg = 0f;    // + up, - down
    private float arc3dRadius = 25f;
    private int arc3dSubdivisions = 8;

    private bool addOrAlignExitTangentPoint3D = true;
    private float exitTangentDistance3D = 25f;

    // ============================================================
    // Step 9: Roll editing + gizmo
    // ============================================================
    private float rollNudgeDeg = 1f;
    private bool drawSelectedRollGizmo = true;
    private float rollGizmoScale = 1f;

    // ============================================================
    // Step 10: Auto-bank from curvature
    // ============================================================
    private float autoBankDegAt90 = 6f;         // bank amount when the turn is 90 degrees
    private float autoBankMaxAbs = 12f;         // clamp
    private bool autoBankPlanarXZ = true;       // ignore vertical turns (recommended for ramps)
    private bool autoBankInvert = false;        // flip sign if your roll direction feels backwards
    private bool autoBankKeepEndpointsZero = true;

    private int autoBankSmoothIterations = 2;   // smoothing passes
    private float autoBankSmoothStrength = 0.5f;// 0..1 blend to neighbor average each pass

    private void OnEnable()
    {
        gen = (SplineMeshGenerator)target;
        if (gen != null)
            path = gen.GetComponent<SplinePath>();
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Spline Editing", EditorStyles.boldLabel);

        if (path == null)
        {
            EditorGUILayout.HelpBox("SplinePath component not found on this GameObject.", MessageType.Warning);
            return;
        }

        // -------------------------
        // Snapping
        // -------------------------
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Snapping", EditorStyles.boldLabel);

        snapToGrid = EditorGUILayout.Toggle("Snap To Grid", snapToGrid);
        using (new EditorGUI.DisabledScope(!snapToGrid))
        {
            gridSize = EditorGUILayout.FloatField("Grid Size (local)", gridSize);
            gridSize = Mathf.Max(0.0001f, gridSize);
        }

        // -------------------------
        // Basic point operations
        // -------------------------
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Points", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Point (at end)"))
                AddPointAtEnd();

            if (GUILayout.Button("Rebuild Preview"))
                gen.RebuildPreview();
        }

        using (new EditorGUI.DisabledScope(selectedPoint < 0 || selectedPoint >= path.points.Count))
        {
            if (GUILayout.Button("Delete Selected Point"))
                DeleteSelectedPoint();
        }

        // ============================================================
        // Step 9: Selected point roll editing
        // ============================================================
        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Selected Point (Roll / Bank)", EditorStyles.boldLabel);

        if (selectedPoint < 0 || selectedPoint >= path.points.Count)
        {
            EditorGUILayout.HelpBox("Select a point in the Scene view to edit roll.", MessageType.Info);
        }
        else
        {
            var sp = path.points[selectedPoint];

            EditorGUILayout.LabelField($"Selected: #{selectedPoint}");
            EditorGUILayout.Vector3Field("Position (local)", sp.position);

            EditorGUI.BeginChangeCheck();
            float newRoll = EditorGUILayout.FloatField("Roll Degrees", sp.rollDegrees);
            if (EditorGUI.EndChangeCheck())
            {
                SetPointRoll(selectedPoint, newRoll);
            }

            rollNudgeDeg = EditorGUILayout.FloatField("Nudge Degrees", rollNudgeDeg);
            rollNudgeDeg = Mathf.Max(0.01f, rollNudgeDeg);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button($"-{rollNudgeDeg:0.##}°"))
                    SetPointRoll(selectedPoint, sp.rollDegrees - rollNudgeDeg);

                if (GUILayout.Button($"+{rollNudgeDeg:0.##}°"))
                    SetPointRoll(selectedPoint, sp.rollDegrees + rollNudgeDeg);

                if (GUILayout.Button("Reset"))
                    SetPointRoll(selectedPoint, 0f);
            }

            drawSelectedRollGizmo = EditorGUILayout.Toggle("Draw Roll Gizmo (Scene)", drawSelectedRollGizmo);
            using (new EditorGUI.DisabledScope(!drawSelectedRollGizmo))
            {
                rollGizmoScale = EditorGUILayout.FloatField("Gizmo Scale", rollGizmoScale);
                rollGizmoScale = Mathf.Max(0.01f, rollGizmoScale);
            }
        }

        // ============================================================
        // Step 10: Auto-bank from curvature
        // ============================================================
        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Auto Bank From Curvature (Step 10)", EditorStyles.boldLabel);

        autoBankDegAt90 = EditorGUILayout.FloatField("Bank @ 90° Turn (deg)", autoBankDegAt90);
        autoBankDegAt90 = Mathf.Max(0f, autoBankDegAt90);

        autoBankMaxAbs = EditorGUILayout.FloatField("Max Abs Bank (deg)", autoBankMaxAbs);
        autoBankMaxAbs = Mathf.Max(0f, autoBankMaxAbs);

        autoBankPlanarXZ = EditorGUILayout.Toggle("Planar (XZ only)", autoBankPlanarXZ);
        autoBankInvert = EditorGUILayout.Toggle("Invert Bank Direction", autoBankInvert);
        autoBankKeepEndpointsZero = EditorGUILayout.Toggle("Keep Endpoints = 0°", autoBankKeepEndpointsZero);

        EditorGUILayout.Space(6);
        autoBankSmoothIterations = EditorGUILayout.IntField("Smoothing Iterations", autoBankSmoothIterations);
        autoBankSmoothIterations = Mathf.Clamp(autoBankSmoothIterations, 0, 25);

        autoBankSmoothStrength = EditorGUILayout.Slider("Smoothing Strength", autoBankSmoothStrength, 0f, 1f);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto Bank (All Points)"))
                AutoBankAllPoints();

            if (GUILayout.Button("Clear Bank (All Points)"))
                ClearBankAllPoints();
        }

        // -------------------------
        // Horizontal Arc insertion tool
        // -------------------------
        EditorGUILayout.Space(14);
        EditorGUILayout.LabelField("Insert Horizontal Arc After Selected", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(selectedPoint < 0 || selectedPoint >= path.points.Count))
        {
            arcDirection = (ArcDirection)EditorGUILayout.EnumPopup("Direction", arcDirection);

            arcAngleIndex = EditorGUILayout.Popup("Angle (deg)", arcAngleIndex, ToAngleLabels(ArcAngleOptions));
            int angleDeg = ArcAngleOptions[Mathf.Clamp(arcAngleIndex, 0, ArcAngleOptions.Length - 1)];

            arcRadius = EditorGUILayout.FloatField("Radius (local meters)", arcRadius);
            arcRadius = Mathf.Max(0.01f, arcRadius);

            arcSubdivisions = EditorGUILayout.IntField("Subdivisions (points)", arcSubdivisions);
            arcSubdivisions = Mathf.Clamp(arcSubdivisions, 1, 256);

            EditorGUILayout.Space(6);
            addOrAlignExitTangentPoint = EditorGUILayout.Toggle("Add/Align Exit Tangent Point", addOrAlignExitTangentPoint);
            using (new EditorGUI.DisabledScope(!addOrAlignExitTangentPoint))
            {
                exitTangentDistance = EditorGUILayout.FloatField("Exit Tangent Distance", exitTangentDistance);
                exitTangentDistance = Mathf.Max(0.01f, exitTangentDistance);
            }

            EditorGUILayout.Space(6);
            selectNewEndAfterInsert = EditorGUILayout.Toggle("Select New End After Insert", selectNewEndAfterInsert);

            if (GUILayout.Button("Insert Horizontal Arc"))
                InsertHorizontalArcAfterSelected(angleDeg, arcRadius, arcSubdivisions, arcDirection);
        }

        // -------------------------
        // Straight Ramp tool
        // -------------------------
        EditorGUILayout.Space(14);
        EditorGUILayout.LabelField("Insert Ramp After Selected", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(selectedPoint < 0 || selectedPoint >= path.points.Count))
        {
            rampLength = EditorGUILayout.FloatField("Ramp Length", rampLength);
            rampLength = Mathf.Max(0.01f, rampLength);

            rampRise = EditorGUILayout.FloatField("Ramp Rise (Y delta)", rampRise);

            rampSubdivisions = EditorGUILayout.IntField("Subdivisions (points)", rampSubdivisions);
            rampSubdivisions = Mathf.Clamp(rampSubdivisions, 1, 256);

            rampAffectsYOnly = EditorGUILayout.Toggle("Keep XY Plan (Y only)", rampAffectsYOnly);

            if (GUILayout.Button("Insert Ramp"))
                InsertRampAfterSelected(rampLength, rampRise, rampSubdivisions, rampAffectsYOnly);
        }

        // -------------------------
        // Vertical Arc tool
        // -------------------------
        EditorGUILayout.Space(14);
        EditorGUILayout.LabelField("Insert Vertical Arc After Selected", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(selectedPoint < 0 || selectedPoint >= path.points.Count))
        {
            verticalArcDirection = (VerticalArcDirection)EditorGUILayout.EnumPopup("Direction", verticalArcDirection);

            verticalArcAngleIndex = EditorGUILayout.Popup("Angle (deg)", verticalArcAngleIndex, ToAngleLabels(ArcAngleOptions));
            int vAngleDeg = ArcAngleOptions[Mathf.Clamp(verticalArcAngleIndex, 0, ArcAngleOptions.Length - 1)];

            verticalArcRadius = EditorGUILayout.FloatField("Radius (local meters)", verticalArcRadius);
            verticalArcRadius = Mathf.Max(0.01f, verticalArcRadius);

            verticalArcSubdivisions = EditorGUILayout.IntField("Subdivisions (points)", verticalArcSubdivisions);
            verticalArcSubdivisions = Mathf.Clamp(verticalArcSubdivisions, 1, 256);

            EditorGUILayout.Space(6);
            addOrAlignExitTangentPointVertical = EditorGUILayout.Toggle("Add/Align Exit Tangent Point", addOrAlignExitTangentPointVertical);
            using (new EditorGUI.DisabledScope(!addOrAlignExitTangentPointVertical))
            {
                exitTangentDistanceVertical = EditorGUILayout.FloatField("Exit Tangent Distance", exitTangentDistanceVertical);
                exitTangentDistanceVertical = Mathf.Max(0.01f, exitTangentDistanceVertical);
            }

            EditorGUILayout.Space(6);
            selectNewEndAfterInsert = EditorGUILayout.Toggle("Select New End After Insert", selectNewEndAfterInsert);

            if (GUILayout.Button("Insert Vertical Arc"))
                InsertVerticalArcAfterSelected(vAngleDeg, verticalArcRadius, verticalArcSubdivisions, verticalArcDirection);
        }

        // -------------------------
        // 3D Arc tool
        // -------------------------
        EditorGUILayout.Space(14);
        EditorGUILayout.LabelField("Insert 3D Arc (Yaw + Pitch) After Selected", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(selectedPoint < 0 || selectedPoint >= path.points.Count))
        {
            arc3dYawDeg = EditorGUILayout.FloatField("Yaw Degrees (+right)", arc3dYawDeg);
            arc3dPitchDeg = EditorGUILayout.FloatField("Pitch Degrees (+up)", arc3dPitchDeg);

            arc3dRadius = EditorGUILayout.FloatField("Radius (local meters)", arc3dRadius);
            arc3dRadius = Mathf.Max(0.01f, arc3dRadius);

            arc3dSubdivisions = EditorGUILayout.IntField("Subdivisions (points)", arc3dSubdivisions);
            arc3dSubdivisions = Mathf.Clamp(arc3dSubdivisions, 1, 512);

            EditorGUILayout.Space(6);
            addOrAlignExitTangentPoint3D = EditorGUILayout.Toggle("Add/Align Exit Tangent Point", addOrAlignExitTangentPoint3D);
            using (new EditorGUI.DisabledScope(!addOrAlignExitTangentPoint3D))
            {
                exitTangentDistance3D = EditorGUILayout.FloatField("Exit Tangent Distance", exitTangentDistance3D);
                exitTangentDistance3D = Mathf.Max(0.01f, exitTangentDistance3D);
            }

            EditorGUILayout.Space(6);
            selectNewEndAfterInsert = EditorGUILayout.Toggle("Select New End After Insert", selectNewEndAfterInsert);

            if (GUILayout.Button("Insert 3D Arc"))
                Insert3DArcAfterSelected(arc3dYawDeg, arc3dPitchDeg, arc3dRadius, arc3dSubdivisions);
        }

        // -------------------------
        // Bake
        // -------------------------
        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);

        if (gen.IsBaked)
            EditorGUILayout.HelpBox("This object is baked. Preview rebuild is suppressed until you Clear Bake.", MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Bake Mesh Asset"))
                gen.BakeMeshAsset();

            if (GUILayout.Button("Clear Bake"))
                gen.ClearBake();
        }
    }

    private void OnSceneGUI()
    {
        if (gen == null) return;

        path = gen.GetComponent<SplinePath>();
        if (path == null || path.points == null) return;

        Transform t = path.transform;

        // Draw spline preview line
        Handles.color = Color.cyan;
        var samples = path.SampleByDistance(1f, 8, 2000);
        for (int i = 1; i < samples.Count; i++)
        {
            Vector3 a = t.TransformPoint(samples[i - 1].positionLocal);
            Vector3 b = t.TransformPoint(samples[i].positionLocal);
            Handles.DrawLine(a, b);
        }

        // Draw control point handles
        for (int i = 0; i < path.points.Count; i++)
        {
            Vector3 worldPos = t.TransformPoint(path.points[i].position);
            float handleSize = HandleUtility.GetHandleSize(worldPos) * 0.10f;

            Handles.color = (i == selectedPoint) ? Color.green : Color.yellow;

            if (Handles.Button(worldPos, Quaternion.identity, handleSize, handleSize, Handles.SphereHandleCap))
            {
                selectedPoint = i;
                Repaint();
            }

            Handles.color = Color.white;
            Handles.Label(worldPos + Vector3.up * handleSize * 1.5f, $"#{i}");

            if (i == selectedPoint)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newWorldPos = Handles.PositionHandle(worldPos, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(path, "Move Spline Point");

                    Vector3 newLocal = t.InverseTransformPoint(newWorldPos);
                    if (snapToGrid)
                        newLocal = SnapVector3(newLocal, gridSize);

                    var p = path.points[i];
                    p.position = newLocal;
                    path.points[i] = p;

                    EditorUtility.SetDirty(path);

                    if (gen.autoRebuild && !gen.IsBaked)
                        gen.RebuildPreview();
                }

                if (drawSelectedRollGizmo)
                    DrawRollGizmoWorld(i, worldPos);
            }
        }
    }

    // ============================================================
    // Step 10 implementation
    // ============================================================
    private void AutoBankAllPoints()
    {
        if (path == null || path.points == null) return;
        int n = path.points.Count;
        if (n < 3) return;

        Undo.RecordObject(path, "Auto Bank From Curvature");

        float[] rolls = new float[n];

        // Optional endpoints
        if (autoBankKeepEndpointsZero)
        {
            rolls[0] = 0f;
            rolls[n - 1] = 0f;
        }
        else
        {
            rolls[0] = path.points[0].rollDegrees;
            rolls[n - 1] = path.points[n - 1].rollDegrees;
        }

        // Compute per-point curvature-based roll
        for (int i = 1; i < n - 1; i++)
        {
            Vector3 pPrev = path.points[i - 1].position;
            Vector3 p = path.points[i].position;
            Vector3 pNext = path.points[i + 1].position;

            Vector3 a = (p - pPrev);
            Vector3 b = (pNext - p);

            if (autoBankPlanarXZ)
            {
                a.y = 0f;
                b.y = 0f;
            }

            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f)
            {
                rolls[i] = path.points[i].rollDegrees;
                continue;
            }

            a.Normalize();
            b.Normalize();

            // Signed angle around local up (we assume path uses local Y-up editing)
            float signedTurnDeg = Vector3.SignedAngle(a, b, Vector3.up); // + = left turn, - = right turn

            float roll = (signedTurnDeg / 90f) * autoBankDegAt90;         // linear mapping
            roll = Mathf.Clamp(roll, -autoBankMaxAbs, autoBankMaxAbs);

            if (autoBankInvert)
                roll = -roll;

            rolls[i] = roll;
        }

        // Smoothing passes (keeps endpoints stable)
        int start = 0;
        int end = n - 1;

        for (int it = 0; it < autoBankSmoothIterations; it++)
        {
            float[] next = (float[])rolls.Clone();

            for (int i = 1; i < n - 1; i++)
            {
                // If endpoints are locked at 0, keep them that way.
                if (autoBankKeepEndpointsZero && (i == start || i == end))
                    continue;

                float avg = 0.5f * (rolls[i - 1] + rolls[i + 1]);
                next[i] = Mathf.Lerp(rolls[i], avg, autoBankSmoothStrength);
            }

            rolls = next;
        }

        // Write back
        for (int i = 0; i < n; i++)
        {
            var sp = path.points[i];
            sp.rollDegrees = rolls[i];
            path.points[i] = sp;
        }

        EditorUtility.SetDirty(path);

        if (gen != null && gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        Repaint();
        SceneView.RepaintAll();
    }

    private void ClearBankAllPoints()
    {
        if (path == null || path.points == null) return;
        int n = path.points.Count;
        if (n == 0) return;

        Undo.RecordObject(path, "Clear Bank");

        for (int i = 0; i < n; i++)
        {
            var sp = path.points[i];
            sp.rollDegrees = 0f;
            path.points[i] = sp;
        }

        EditorUtility.SetDirty(path);

        if (gen != null && gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        Repaint();
        SceneView.RepaintAll();
    }

    // ============================================================
    // Step 9 helpers
    // ============================================================
    private void SetPointRoll(int index, float newRollDeg)
    {
        if (path == null) return;
        if (index < 0 || index >= path.points.Count) return;

        Undo.RecordObject(path, "Change Roll Degrees");

        var sp = path.points[index];
        sp.rollDegrees = newRollDeg;
        path.points[index] = sp;

        EditorUtility.SetDirty(path);

        if (gen != null && gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        Repaint();
        SceneView.RepaintAll();
    }

    private void DrawRollGizmoWorld(int pointIndex, Vector3 worldPos)
    {
        if (path == null || pointIndex < 0 || pointIndex >= path.points.Count) return;

        float rollDeg = path.points[pointIndex].rollDegrees;

        Vector3 fwd = EstimateWorldForward(pointIndex);
        if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.forward;
        fwd.Normalize();

        // Build a world frame from fwd + world up, then roll about fwd
        Vector3 up = Vector3.up;
        Vector3 right = Vector3.Cross(up, fwd);
        if (right.sqrMagnitude < 1e-8f) right = Vector3.right;
        right.Normalize();
        up = Vector3.Cross(fwd, right).normalized;

        Quaternion q = Quaternion.AngleAxis(rollDeg, fwd);
        Vector3 upRolled = (q * up).normalized;
        Vector3 rightRolled = (q * right).normalized;

        float size = HandleUtility.GetHandleSize(worldPos) * 0.35f * rollGizmoScale;

        Handles.color = Color.magenta; // forward
        Handles.DrawLine(worldPos, worldPos + fwd * size);

        Handles.color = Color.green; // up (rolled)
        Handles.DrawLine(worldPos, worldPos + upRolled * size);

        Handles.color = Color.red; // right (rolled)
        Handles.DrawLine(worldPos, worldPos + rightRolled * size);

        Handles.color = Color.white;
        Handles.Label(worldPos + upRolled * (size * 1.05f), $"Roll {rollDeg:0.#}°");
    }

    private Vector3 EstimateWorldForward(int pointIndex)
    {
        Transform t = path.transform;

        Vector3 p = t.TransformPoint(path.points[pointIndex].position);

        Vector3 dir;
        if (pointIndex < path.points.Count - 1)
            dir = t.TransformPoint(path.points[pointIndex + 1].position) - p;
        else if (pointIndex > 0)
            dir = p - t.TransformPoint(path.points[pointIndex - 1].position);
        else
            dir = t.TransformDirection(Vector3.forward);

        if (dir.sqrMagnitude < 1e-8f)
            dir = t.TransformDirection(Vector3.forward);

        return dir.normalized;
    }

    // ============================================================
    // Existing point ops
    // ============================================================
    private void AddPointAtEnd()
    {
        if (path == null) return;

        Undo.RecordObject(path, "Add Spline Point");

        Vector3 newPos;

        if (path.points.Count >= 2)
        {
            Vector3 last = path.points[path.points.Count - 1].position;
            Vector3 prev = path.points[path.points.Count - 2].position;
            Vector3 dir = (last - prev);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();

            newPos = last + dir * 10f;
        }
        else if (path.points.Count == 1)
        {
            newPos = path.points[0].position + Vector3.forward * 10f;
        }
        else
        {
            newPos = Vector3.zero;
        }

        if (snapToGrid)
            newPos = SnapVector3(newPos, gridSize);

        path.points.Add(new SplinePoint(newPos));
        selectedPoint = path.points.Count - 1;

        EditorUtility.SetDirty(path);

        if (gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        Repaint();
        SceneView.RepaintAll();
    }

    private void DeleteSelectedPoint()
    {
        if (path == null) return;
        if (selectedPoint < 0 || selectedPoint >= path.points.Count) return;

        Undo.RecordObject(path, "Delete Spline Point");
        path.points.RemoveAt(selectedPoint);
        selectedPoint = Mathf.Clamp(selectedPoint - 1, -1, path.points.Count - 1);

        EditorUtility.SetDirty(path);

        if (gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        Repaint();
        SceneView.RepaintAll();
    }

    // ============================================================
    // HORIZONTAL ARC (turn left/right in XZ plane)
    // ============================================================
    private void InsertHorizontalArcAfterSelected(int angleDeg, float radius, int subdivisions, ArcDirection dir)
    {
        if (path == null) return;
        if (selectedPoint < 0 || selectedPoint >= path.points.Count) return;

        int startIndex = selectedPoint;

        Vector3 p0 = path.points[startIndex].position;
        Vector3 fwd0 = EstimateLocalForwardHorizontal(startIndex);

        Vector3 up = Vector3.up;

        Vector3 right0 = Vector3.Cross(up, fwd0);
        if (right0.sqrMagnitude < 1e-6f) right0 = Vector3.right;
        right0.Normalize();
        Vector3 left0 = -right0;

        bool turnLeft = (dir == ArcDirection.Left);

        Vector3 side = turnLeft ? left0 : right0;
        Vector3 center = p0 + side * radius;

        Vector3 radial0 = p0 - center;

        float sign = turnLeft ? -1f : 1f;

        int nextIndexBefore = startIndex + 1;
        bool hadNext = nextIndexBefore < path.points.Count;
        Vector3 nextPosBefore = hadNext ? path.points[nextIndexBefore].position : Vector3.zero;

        Undo.RecordObject(path, "Insert Horizontal Arc");

        int insertIndex = startIndex + 1;

        Vector3 lastInsertedPos = p0;
        int lastInsertedIndex = startIndex;

        for (int i = 1; i <= subdivisions; i++)
        {
            float a01 = (float)i / subdivisions;
            float angDeg = sign * angleDeg * a01;

            Quaternion rot = Quaternion.AngleAxis(angDeg, up);
            Vector3 newPos = center + (rot * radial0);

            if (snapToGrid)
                newPos = SnapVector3(newPos, gridSize);

            path.points.Insert(insertIndex, new SplinePoint(newPos));
            lastInsertedPos = newPos;
            lastInsertedIndex = insertIndex;

            insertIndex++;
        }

        Vector3 exitFwd = (Quaternion.AngleAxis(sign * angleDeg, up) * fwd0);
        exitFwd.y = 0f;
        if (exitFwd.sqrMagnitude < 1e-6f) exitFwd = Vector3.forward;
        exitFwd.Normalize();

        bool addedAnchor = false;
        int anchorIndex = -1;

        if (addOrAlignExitTangentPoint)
        {
            int nextIndexAfter = hadNext ? (nextIndexBefore + subdivisions) : -1;

            if (hadNext && nextIndexAfter >= 0 && nextIndexAfter < path.points.Count)
            {
                float preserveDist = Vector3.Distance(nextPosBefore, lastInsertedPos);
                if (preserveDist < 0.01f) preserveDist = exitTangentDistance;

                Vector3 aligned = lastInsertedPos + exitFwd * preserveDist;
                if (snapToGrid)
                    aligned = SnapVector3(aligned, gridSize);

                var sp = path.points[nextIndexAfter];
                sp.position = aligned;
                path.points[nextIndexAfter] = sp;

                addedAnchor = true;
                anchorIndex = nextIndexAfter;
            }
            else
            {
                Vector3 anchor = lastInsertedPos + exitFwd * exitTangentDistance;
                if (snapToGrid)
                    anchor = SnapVector3(anchor, gridSize);

                path.points.Insert(insertIndex, new SplinePoint(anchor));

                addedAnchor = true;
                anchorIndex = insertIndex;
            }
        }

        EditorUtility.SetDirty(path);

        if (gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        if (selectNewEndAfterInsert)
        {
            selectedPoint = addedAnchor ? anchorIndex : lastInsertedIndex;
            Repaint();
            SceneView.RepaintAll();
        }
    }

    // ============================================================
    // STRAIGHT RAMP (incline over distance)
    // ============================================================
    private void InsertRampAfterSelected(float length, float rise, int subdivisions, bool yOnly)
    {
        if (path == null) return;
        if (selectedPoint < 0 || selectedPoint >= path.points.Count) return;

        int startIndex = selectedPoint;

        Vector3 p0 = path.points[startIndex].position;

        Vector3 fwdPlan = EstimateLocalForwardHorizontal(startIndex);
        Vector3 fwdFull = EstimateLocalForwardFull(startIndex);

        Vector3 dir = yOnly ? fwdPlan : fwdFull;
        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
        dir.Normalize();

        Undo.RecordObject(path, "Insert Ramp");

        int insertIndex = startIndex + 1;
        int lastInsertedIndex = startIndex;

        for (int i = 1; i <= subdivisions; i++)
        {
            float a01 = (float)i / subdivisions;

            Vector3 newPos = p0 + dir * (length * a01);
            newPos.y = p0.y + rise * a01;

            if (snapToGrid)
                newPos = SnapVector3(newPos, gridSize);

            path.points.Insert(insertIndex, new SplinePoint(newPos));
            lastInsertedIndex = insertIndex;
            insertIndex++;
        }

        EditorUtility.SetDirty(path);

        if (gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        if (selectNewEndAfterInsert)
        {
            selectedPoint = lastInsertedIndex;
            Repaint();
            SceneView.RepaintAll();
        }
    }

    // ============================================================
    // VERTICAL ARC (curved ramp/slide in the forward+up plane)
    // ============================================================
    private void InsertVerticalArcAfterSelected(int angleDeg, float radius, int subdivisions, VerticalArcDirection dir)
    {
        if (path == null) return;
        if (selectedPoint < 0 || selectedPoint >= path.points.Count) return;

        int startIndex = selectedPoint;

        Vector3 p0 = path.points[startIndex].position;

        Vector3 fwd0 = EstimateLocalForwardHorizontal(startIndex);
        if (fwd0.sqrMagnitude < 1e-6f) fwd0 = Vector3.forward;
        fwd0.Normalize();

        Vector3 up = Vector3.up;

        Vector3 right = Vector3.Cross(up, fwd0);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
        right.Normalize();

        bool pitchUp = (dir == VerticalArcDirection.Up);

        // Unity: AngleAxis(+θ, right) pitches DOWN, so UP must be negative.
        float sign = pitchUp ? -1f : 1f;

        Vector3 center = p0 + (pitchUp ? up : -up) * radius;
        Vector3 radial0 = p0 - center;

        int nextIndexBefore = startIndex + 1;
        bool hadNext = nextIndexBefore < path.points.Count;
        Vector3 nextPosBefore = hadNext ? path.points[nextIndexBefore].position : Vector3.zero;

        Undo.RecordObject(path, "Insert Vertical Arc");

        int insertIndex = startIndex + 1;

        Vector3 lastInsertedPos = p0;
        int lastInsertedIndex = startIndex;

        for (int i = 1; i <= subdivisions; i++)
        {
            float a01 = (float)i / subdivisions;
            float angDeg = sign * angleDeg * a01;

            Quaternion rot = Quaternion.AngleAxis(angDeg, right);
            Vector3 newPos = center + (rot * radial0);

            if (snapToGrid)
                newPos = SnapVector3(newPos, gridSize);

            path.points.Insert(insertIndex, new SplinePoint(newPos));
            lastInsertedPos = newPos;
            lastInsertedIndex = insertIndex;
            insertIndex++;
        }

        Vector3 exitFwd = (Quaternion.AngleAxis(sign * angleDeg, right) * fwd0).normalized;

        bool addedAnchor = false;
        int anchorIndex = -1;

        if (addOrAlignExitTangentPointVertical)
        {
            int nextIndexAfter = hadNext ? (nextIndexBefore + subdivisions) : -1;

            if (hadNext && nextIndexAfter >= 0 && nextIndexAfter < path.points.Count)
            {
                float preserveDist = Vector3.Distance(nextPosBefore, lastInsertedPos);
                if (preserveDist < 0.01f) preserveDist = exitTangentDistanceVertical;

                Vector3 aligned = lastInsertedPos + exitFwd * preserveDist;
                if (snapToGrid)
                    aligned = SnapVector3(aligned, gridSize);

                var sp = path.points[nextIndexAfter];
                sp.position = aligned;
                path.points[nextIndexAfter] = sp;

                addedAnchor = true;
                anchorIndex = nextIndexAfter;
            }
            else
            {
                Vector3 anchor = lastInsertedPos + exitFwd * exitTangentDistanceVertical;
                if (snapToGrid)
                    anchor = SnapVector3(anchor, gridSize);

                path.points.Insert(insertIndex, new SplinePoint(anchor));

                addedAnchor = true;
                anchorIndex = insertIndex;
            }
        }

        EditorUtility.SetDirty(path);

        if (gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        if (selectNewEndAfterInsert)
        {
            selectedPoint = addedAnchor ? anchorIndex : lastInsertedIndex;
            Repaint();
            SceneView.RepaintAll();
        }
    }

    // ============================================================
    // 3D ARC (Yaw + Pitch combined)
    // ============================================================
    private void Insert3DArcAfterSelected(float yawDeg, float pitchDeg, float radius, int subdivisions)
    {
        if (path == null) return;
        if (selectedPoint < 0 || selectedPoint >= path.points.Count) return;

        int startIndex = selectedPoint;
        Vector3 p0 = path.points[startIndex].position;

        Vector3 fwd0 = EstimateLocalForwardFull(startIndex);
        if (fwd0.sqrMagnitude < 1e-6f) fwd0 = Vector3.forward;
        fwd0.Normalize();

        Vector3 up = Vector3.up;
        Vector3 right0 = Vector3.Cross(up, fwd0);
        if (right0.sqrMagnitude < 1e-6f) right0 = Vector3.right;
        right0.Normalize();

        Quaternion qPitch = Quaternion.AngleAxis(-pitchDeg, right0);
        Quaternion qYaw = Quaternion.AngleAxis(yawDeg, up);

        Vector3 fwdTarget = (qYaw * (qPitch * fwd0)).normalized;

        float totalAngle = Vector3.Angle(fwd0, fwdTarget);
        if (totalAngle < 0.001f)
            return;

        Vector3 axis = Vector3.Cross(fwd0, fwdTarget);
        if (axis.sqrMagnitude < 1e-8f)
            axis = right0;
        axis.Normalize();

        Vector3 side0 = Vector3.Cross(axis, fwd0);
        if (side0.sqrMagnitude < 1e-8f)
            side0 = Vector3.Cross(right0, fwd0);
        side0.Normalize();

        Vector3 center = p0 + side0 * radius;
        Vector3 radial0 = p0 - center;

        int nextIndexBefore = startIndex + 1;
        bool hadNext = nextIndexBefore < path.points.Count;
        Vector3 nextPosBefore = hadNext ? path.points[nextIndexBefore].position : Vector3.zero;

        Undo.RecordObject(path, "Insert 3D Arc");

        int insertIndex = startIndex + 1;

        Vector3 lastInsertedPos = p0;
        int lastInsertedIndex = startIndex;

        for (int i = 1; i <= subdivisions; i++)
        {
            float a01 = (float)i / subdivisions;
            float angDeg = totalAngle * a01;

            Quaternion rot = Quaternion.AngleAxis(angDeg, axis);
            Vector3 newPos = center + (rot * radial0);

            if (snapToGrid)
                newPos = SnapVector3(newPos, gridSize);

            path.points.Insert(insertIndex, new SplinePoint(newPos));
            lastInsertedPos = newPos;
            lastInsertedIndex = insertIndex;
            insertIndex++;
        }

        Vector3 exitFwd = (Quaternion.AngleAxis(totalAngle, axis) * fwd0).normalized;

        bool addedAnchor = false;
        int anchorIndex = -1;

        if (addOrAlignExitTangentPoint3D)
        {
            int nextIndexAfter = hadNext ? (nextIndexBefore + subdivisions) : -1;

            if (hadNext && nextIndexAfter >= 0 && nextIndexAfter < path.points.Count)
            {
                float preserveDist = Vector3.Distance(nextPosBefore, lastInsertedPos);
                if (preserveDist < 0.01f) preserveDist = exitTangentDistance3D;

                Vector3 aligned = lastInsertedPos + exitFwd * preserveDist;
                if (snapToGrid)
                    aligned = SnapVector3(aligned, gridSize);

                var sp = path.points[nextIndexAfter];
                sp.position = aligned;
                path.points[nextIndexAfter] = sp;

                addedAnchor = true;
                anchorIndex = nextIndexAfter;
            }
            else
            {
                Vector3 anchor = lastInsertedPos + exitFwd * exitTangentDistance3D;
                if (snapToGrid)
                    anchor = SnapVector3(anchor, gridSize);

                path.points.Insert(insertIndex, new SplinePoint(anchor));

                addedAnchor = true;
                anchorIndex = insertIndex;
            }
        }

        EditorUtility.SetDirty(path);

        if (gen.autoRebuild && !gen.IsBaked)
            gen.RebuildPreview();

        if (selectNewEndAfterInsert)
        {
            selectedPoint = addedAnchor ? anchorIndex : lastInsertedIndex;
            Repaint();
            SceneView.RepaintAll();
        }
    }

    // ============================================================
    // Forward estimation helpers
    // ============================================================
    private Vector3 EstimateLocalForwardHorizontal(int pointIndex)
    {
        int count = path.points.Count;
        Vector3 p = path.points[pointIndex].position;

        Vector3 dir;
        if (pointIndex < count - 1)
            dir = path.points[pointIndex + 1].position - p;
        else if (pointIndex > 0)
            dir = p - path.points[pointIndex - 1].position;
        else
            dir = Vector3.forward;

        dir.y = 0f;

        if (dir.sqrMagnitude < 1e-6f)
            dir = Vector3.forward;

        return dir.normalized;
    }

    private Vector3 EstimateLocalForwardFull(int pointIndex)
    {
        int count = path.points.Count;
        Vector3 p = path.points[pointIndex].position;

        Vector3 dir;
        if (pointIndex < count - 1)
            dir = path.points[pointIndex + 1].position - p;
        else if (pointIndex > 0)
            dir = p - path.points[pointIndex - 1].position;
        else
            dir = Vector3.forward;

        if (dir.sqrMagnitude < 1e-6f)
            dir = Vector3.forward;

        return dir.normalized;
    }

    private static Vector3 SnapVector3(Vector3 v, float snap)
    {
        return new Vector3(
            Mathf.Round(v.x / snap) * snap,
            Mathf.Round(v.y / snap) * snap,
            Mathf.Round(v.z / snap) * snap
        );
    }

    private static string[] ToAngleLabels(int[] angles)
    {
        var labels = new string[angles.Length];
        for (int i = 0; i < angles.Length; i++)
            labels[i] = angles[i].ToString();
        return labels;
    }
}
#endif
