using AprilTag;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Odpowiada wy³¹cznie za rysowanie overlayu AprilTag na ekranie:
/// - czerwonej osi X,
/// - zielonej osi Y,
/// - niebieskiej osi Z lub symbolu Z,
/// - bia³ego obrysu znacznika.
///
/// Ta klasa NIE wykonuje detekcji AprilTag.
/// Dostaje gotowy TagPose z biblioteki AprilTag i tylko zamienia go na linie 2D rysowane w OnGUI().
/// </summary>
public sealed class AprilTagAxisOverlayRenderer
{
    /// <summary>
    /// Pojedyncza linia rysowana w IMGUI.
    /// Trzymamy to prywatnie w rendererze, ¿eby nie tworzyæ osobnego pliku AxisGuiLine.cs.
    /// </summary>
    private readonly struct AxisGuiLine
    {
        public readonly Vector2 A;
        public readonly Vector2 B;
        public readonly Color Color;

        public AxisGuiLine(Vector2 a, Vector2 b, Color color)
        {
            A = a;
            B = b;
            Color = color;
        }
    }

    private readonly List<AxisGuiLine> _lines = new List<AxisGuiLine>();

    // Parametry projekcji kamery dla aktualnie przetwarzanej klatki.
    private float _fx;
    private float _fy;
    private float _cx;
    private float _cy;

    /// <summary>
    /// Czyœci linie overlayu przed przetwarzaniem nowej klatki.
    /// </summary>
    public void Clear()
    {
        _lines.Clear();
    }

    /// <summary>
    /// Dodaje overlay dla pojedynczego taga.
    /// Tryb rysowania zale¿y od tracker.useEdgeBasedOverlay:
    /// - true: osie X/Y liczone z prawdziwych naro¿ników 2D,
    /// - false: osie liczone z pozycji i rotacji 3D taga.
    /// </summary>
    public void AddAxes(
        TagPose tag,
        int imgW,
        int imgH,
        AprilTagMvpTracker tracker,
        float fx,
        float fy,
        float cx,
        float cy)
    {
        _fx = fx;
        _fy = fy;
        _cx = cx;
        _cy = cy;

        if (tracker.useEdgeBasedOverlay)
            AddAxesFromRealCorners2D(tag, imgW, imgH, tracker);
        else
            AddAxes2D(tag.Position, tag.Rotation, imgW, imgH, tracker);
    }

    /// <summary>
    /// Rysuje wszystkie linie zebrane podczas ostatniej detekcji.
    /// Wywo³ywane z AprilTagMvpTracker.OnGUI().
    /// </summary>
    public void Draw(float lineWidth = 6f)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            AxisGuiLine line = _lines[i];
            DrawGuiLine(line.A, line.B, line.Color, lineWidth);
        }
    }

    /// <summary>
    /// Rysuje osie na podstawie pozycji i rotacji 3D zwróconej przez AprilTag.
    /// Ten tryb jest mniej odporny na rozjazd overlayu ni¿ REAL CORNERS,
    /// ale przydaje siê diagnostycznie.
    /// </summary>
    private void AddAxes2D(
        Vector3 rawPos,
        Quaternion rawRot,
        int imgW,
        int imgH,
        AprilTagMvpTracker tracker)
    {
        Vector3 origin = new Vector3(rawPos.x, -rawPos.y, rawPos.z);
        Quaternion rot = GetOverlayRotation(rawRot);

        Vector3 xAxis3D = rot * Vector3.right * tracker.axisLength;
        Vector3 yAxis3D = rot * Vector3.up * tracker.axisLength;
        Vector3 zAxis3D = rot * Vector3.forward * tracker.axisLength;

        if (tracker.invertX)
            xAxis3D = -xAxis3D;

        if (tracker.invertY)
            yAxis3D = -yAxis3D;

        if (tracker.invertZAxisDirection)
            zAxis3D = -zAxis3D;

        Vector3 xEnd3D = origin + xAxis3D;
        Vector3 yEnd3D = origin + yAxis3D;
        Vector3 zEnd3D = origin + zAxis3D;

        if (!ProjectCameraPointToScreen(origin, imgW, imgH, tracker, out Vector2 center))
            return;

        if (tracker.showXAxis && ProjectCameraPointToScreen(xEnd3D, imgW, imgH, tracker, out Vector2 xEnd))
            _lines.Add(new AxisGuiLine(center, xEnd, Color.red));

        if (tracker.showYAxis && ProjectCameraPointToScreen(yEnd3D, imgW, imgH, tracker, out Vector2 yEnd))
            _lines.Add(new AxisGuiLine(center, yEnd, Color.green));

        if (tracker.showZAxis && ProjectCameraPointToScreen(zEnd3D, imgW, imgH, tracker, out Vector2 zEnd))
            AddProjectedZAxisOrSymbol(center, center, zEnd, rawRot, 24f, tracker);
    }

    /// <summary>
    /// Rysuje osie X/Y z prawdziwych naro¿ników 2D taga.
    /// To jest g³ówny tryb diagnostyczny, bo najlepiej "przykleja" overlay do znacznika.
    /// </summary>
    private void AddAxesFromRealCorners2D(
        TagPose tag,
        int imgW,
        int imgH,
        AprilTagMvpTracker tracker)
    {
        Vector2 c0 = CpuImagePixelToScreen(tag.Corner0.x, tag.Corner0.y, imgW, imgH, tracker);
        Vector2 c1 = CpuImagePixelToScreen(tag.Corner1.x, tag.Corner1.y, imgW, imgH, tracker);
        Vector2 c2 = CpuImagePixelToScreen(tag.Corner2.x, tag.Corner2.y, imgW, imgH, tracker);
        Vector2 c3 = CpuImagePixelToScreen(tag.Corner3.x, tag.Corner3.y, imgW, imgH, tracker);

        Vector2 center = CpuImagePixelToScreen(tag.Center.x, tag.Center.y, imgW, imgH, tracker);

        Vector2 xDir = ((c1 - c0) + (c2 - c3)) * 0.5f;

        // Kierunek Y zosta³ dobrany tak, aby wizualna oœ Y by³a spójna
        // ze znakiem y_m u¿ywanym w logice relatywnej pozycji i UDP.
        Vector2 yDir = ((c3 - c0) + (c2 - c1)) * 0.5f;

        float xLen = xDir.magnitude;
        float yLen = yDir.magnitude;

        if (xLen < 1f || yLen < 1f)
            return;

        xDir /= xLen;
        yDir /= yLen;

        if (tracker.invertX)
            xDir = -xDir;

        if (tracker.invertY)
            yDir = -yDir;

        float axisPx = Mathf.Min(xLen, yLen) * 0.45f;

        if (tracker.showXAxis)
            _lines.Add(new AxisGuiLine(center, center + xDir * axisPx, Color.red));

        if (tracker.showYAxis)
            _lines.Add(new AxisGuiLine(center, center + yDir * axisPx, Color.green));

        if (tracker.showZAxis)
            AddProjectedZAxisFromPose(tag.Position, tag.Rotation, center, imgW, imgH, axisPx, tracker);

        if (tracker.drawProjectedTagBorder)
        {
            _lines.Add(new AxisGuiLine(c0, c1, Color.white));
            _lines.Add(new AxisGuiLine(c1, c2, Color.white));
            _lines.Add(new AxisGuiLine(c2, c3, Color.white));
            _lines.Add(new AxisGuiLine(c3, c0, Color.white));
        }
    }

    /// <summary>
    /// Dodaje oœ Z bazuj¹c¹ na estymowanej pozie 3D.
    /// X/Y w trybie REAL CORNERS s¹ z naro¿ników 2D, ale Z nadal wymaga rotacji 3D.
    /// </summary>
    private void AddProjectedZAxisFromPose(
        Vector3 rawPos,
        Quaternion rawRot,
        Vector2 realCenter2D,
        int imgW,
        int imgH,
        float axisPx,
        AprilTagMvpTracker tracker)
    {
        Vector3 origin = new Vector3(rawPos.x, -rawPos.y, rawPos.z);
        Quaternion rot = GetOverlayRotation(rawRot);

        Vector3 zDir3D = rot * Vector3.forward;

        if (tracker.invertZAxisDirection)
            zDir3D = -zDir3D;

        float safeScale = Mathf.Max(tracker.measurementScale, 0.001f);
        float visualZLength = (tracker.axisLength / safeScale) * tracker.zAxisVisualScale;

        Vector3 zEnd3D = origin + zDir3D * visualZLength;

        if (!ProjectCameraPointToScreen(origin, imgW, imgH, tracker, out Vector2 poseCenter2D))
            return;

        if (!ProjectCameraPointToScreen(zEnd3D, imgW, imgH, tracker, out Vector2 zEnd2D))
            return;

        AddProjectedZAxisOrSymbol(realCenter2D, poseCenter2D, zEnd2D, rawRot, axisPx, tracker);
    }

    /// <summary>
    /// Rysuje niebiesk¹ oœ Z.
    /// Jeœli rzut Z jest bardzo krótki albo tag jest prawie równoleg³y do ekranu,
    /// zamiast krótkiej niestabilnej kreski rysowany jest symbol:
    /// - kropka w kó³ku: Z skierowane do kamery,
    /// - krzy¿yk w kó³ku: Z skierowane od kamery.
    /// </summary>
    private void AddProjectedZAxisOrSymbol(
        Vector2 drawCenter,
        Vector2 projectedPoseCenter,
        Vector2 projectedZEnd,
        Quaternion rawRot,
        float axisPx,
        AprilTagMvpTracker tracker)
    {
        Vector2 zDir = projectedZEnd - projectedPoseCenter;

        if (tracker.mirrorZScreenX)
            zDir.x = -zDir.x;

        if (tracker.mirrorZScreenY)
            zDir.y = -zDir.y;

        float zLen = zDir.magnitude;
        float symbolRadius = Mathf.Clamp(axisPx * 0.22f, 9f, 30f);

        Vector3 normalCam = GetTagNormalCamera(rawRot);

        if (tracker.invertZAxisDirection)
            normalCam = -normalCam;

        float angleToCameraAxisDeg = 90f;

        if (normalCam.sqrMagnitude > 0.0001f)
        {
            normalCam.Normalize();

            float absZ = Mathf.Clamp(Mathf.Abs(normalCam.z), 0f, 1f);
            angleToCameraAxisDeg = Mathf.Acos(absZ) * Mathf.Rad2Deg;
        }

        bool useSymbolByAngle = angleToCameraAxisDeg <= tracker.zSymbolAngleThresholdDeg;
        bool useSymbolByPixel = zLen < tracker.zSymbolThresholdPx;

        if (useSymbolByAngle || useSymbolByPixel)
        {
            bool zTowardsCamera = IsTagZTowardsCamera(rawRot, tracker);
            AddZSymbol2D(drawCenter, symbolRadius, zTowardsCamera);
            return;
        }

        Vector2 zEnd = drawCenter + zDir.normalized * axisPx * 0.85f;

        _lines.Add(new AxisGuiLine(drawCenter, zEnd, Color.blue));

        Vector2 dir = (zEnd - drawCenter).normalized;
        Vector2 normal = new Vector2(-dir.y, dir.x);

        float arrowSize = Mathf.Clamp(axisPx * 0.12f, 5f, 16f);

        Vector2 arrowA = zEnd - dir * arrowSize + normal * arrowSize * 0.6f;
        Vector2 arrowB = zEnd - dir * arrowSize - normal * arrowSize * 0.6f;

        _lines.Add(new AxisGuiLine(zEnd, arrowA, Color.blue));
        _lines.Add(new AxisGuiLine(zEnd, arrowB, Color.blue));
    }

    /// <summary>
    /// Korekta rotacji u¿ywana tylko do overlayu.
    /// Dopasowuje uk³ad rotacji zwrócony przez AprilTag do uk³adu ekranu/Unity.
    /// </summary>
    private Quaternion GetOverlayRotation(Quaternion rawRot)
    {
        Quaternion rot = rawRot;
        rot.y = -rot.y;
        rot.w = -rot.w;
        return rot;
    }

    private Vector3 GetTagNormalCamera(Quaternion rawRot)
    {
        Quaternion rot = GetOverlayRotation(rawRot);
        return rot * Vector3.forward;
    }

    private bool IsTagZTowardsCamera(Quaternion rawRot, AprilTagMvpTracker tracker)
    {
        Vector3 normalCam = GetTagNormalCamera(rawRot);

        if (tracker.invertZAxisDirection)
            normalCam = -normalCam;

        bool zTowardsCamera = normalCam.z < 0f;

        if (tracker.invertZSymbol)
            zTowardsCamera = !zTowardsCamera;

        return zTowardsCamera;
    }

    /// <summary>
    /// Projektuje punkt 3D z uk³adu kamery na pozycjê 2D na ekranie.
    /// </summary>
    private bool ProjectCameraPointToScreen(
        Vector3 p,
        int imgW,
        int imgH,
        AprilTagMvpTracker tracker,
        out Vector2 screen)
    {
        screen = Vector2.zero;

        if (p.z <= 0.001f)
            return false;

        float u = _fx * (p.x / p.z) + _cx;
        float v = _cy - _fy * (p.y / p.z);

        screen = CpuImagePixelToScreen(u, v, imgW, imgH, tracker);
        return true;
    }

    /// <summary>
    /// Mapuje piksel z obrazu CPU kamery na wspó³rzêdne ekranu Unity.
    /// To miejsce kompensuje obrót obrazu, mirror i tryb cover/crop.
    /// </summary>
    private Vector2 CpuImagePixelToScreen(
        float u,
        float v,
        int imgW,
        int imgH,
        AprilTagMvpTracker tracker)
    {
        float nx = u / imgW;
        float ny = v / imgH;

        if (tracker.mirrorCpuX) nx = 1f - nx;
        if (tracker.mirrorCpuY) ny = 1f - ny;

        float tx = nx;
        float ty = ny;
        float imageAspect = (float)imgW / imgH;

        switch (tracker.cpuToScreenMapping)
        {
            case AprilTagMvpTracker.CpuToScreenMapping.Cover0:
                tx = nx;
                ty = ny;
                imageAspect = (float)imgW / imgH;
                break;

            case AprilTagMvpTracker.CpuToScreenMapping.Cover90CW:
                tx = 1f - ny;
                ty = nx;
                imageAspect = (float)imgH / imgW;
                break;

            case AprilTagMvpTracker.CpuToScreenMapping.Cover90CCW:
                tx = ny;
                ty = 1f - nx;
                imageAspect = (float)imgH / imgW;
                break;

            case AprilTagMvpTracker.CpuToScreenMapping.Cover180:
                tx = 1f - nx;
                ty = 1f - ny;
                imageAspect = (float)imgW / imgH;
                break;
        }

        float screenW = Screen.width;
        float screenH = Screen.height;
        float screenAspect = screenW / screenH;

        float drawW;
        float drawH;
        float offsetX;
        float offsetY;

        // Zachowujemy tryb Cover: obraz wype³nia ekran,
        // a nadmiar jest ucinany po bokach albo góra/dó³.
        if (imageAspect > screenAspect)
        {
            drawH = screenH;
            drawW = drawH * imageAspect;
            offsetX = (screenW - drawW) * 0.5f;
            offsetY = 0f;
        }
        else
        {
            drawW = screenW;
            drawH = drawW / imageAspect;
            offsetX = 0f;
            offsetY = (screenH - drawH) * 0.5f;
        }

        float xBase = offsetX + tx * drawW;
        float yBase = offsetY + ty * drawH;

        float x = ((xBase - screenW * 0.5f) * tracker.screenOverlayScale) + screenW * 0.5f;
        float y = ((yBase - screenH * 0.5f) * tracker.screenOverlayScale) + screenH * 0.5f;

        return new Vector2(x, y);
    }

    private void AddCircle2D(Vector2 center, float radius, Color color, int segments = 32)
    {
        float step = Mathf.PI * 2f / segments;

        for (int i = 0; i < segments; i++)
        {
            float a0 = i * step;
            float a1 = (i + 1) * step;

            Vector2 p0 = center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
            Vector2 p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;

            _lines.Add(new AxisGuiLine(p0, p1, color));
        }
    }

    private void AddX2D(Vector2 center, float radius, Color color)
    {
        float r = radius * 0.55f;

        _lines.Add(new AxisGuiLine(
            center + new Vector2(-r, -r),
            center + new Vector2(r, r),
            color
        ));

        _lines.Add(new AxisGuiLine(
            center + new Vector2(-r, r),
            center + new Vector2(r, -r),
            color
        ));
    }

    private void AddDot2D(Vector2 center, float radius, Color color)
    {
        float r = Mathf.Max(2.0f, radius * 0.14f);

        _lines.Add(new AxisGuiLine(
            center + new Vector2(-r, 0f),
            center + new Vector2(r, 0f),
            color
        ));

        _lines.Add(new AxisGuiLine(
            center + new Vector2(0f, -r),
            center + new Vector2(0f, r),
            color
        ));
    }

    private void AddZSymbol2D(Vector2 center, float radius, bool zTowardsCamera)
    {
        Color color = Color.blue;

        AddCircle2D(center, radius, color);

        if (zTowardsCamera)
            AddDot2D(center, radius, color);
        else
            AddX2D(center, radius, color);
    }

    /// <summary>
    /// Rysuje jedn¹ liniê IMGUI jako obrócony prostok¹t.
    /// </summary>
    private void DrawGuiLine(Vector2 a, Vector2 b, Color color, float width)
    {
        Matrix4x4 oldMatrix = GUI.matrix;
        Color oldColor = GUI.color;

        Vector2 delta = b - a;
        float length = delta.magnitude;

        if (length < 0.001f)
            return;

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), Texture2D.whiteTexture);

        GUI.matrix = oldMatrix;
        GUI.color = oldColor;
    }
}