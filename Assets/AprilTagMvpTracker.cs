using AprilTag;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class AprilTagMvpTracker : MonoBehaviour
{
    public enum CpuToScreenMapping
    {
        Cover0,
        Cover90CW,
        Cover90CCW,
        Cover180
    }

    private const string BUILD_MARK = "Z_MENU_FIX_V15";

    private const bool FORCE_START_CALIBRATION = true;

    private const float START_TAG_SIZE = 0.13f;
    private const float START_FOCAL_SCALE = 1.00f;
    private const float START_MEASUREMENT_SCALE = 0.57f;

    [Header("Detekcja AprilTag")]
    public ARCameraManager camManager;
    public int referenceTagId = 0;
    public int carTagId = 1;

    [Header("UDP Telemetria")]
    public string pcIp = "192.168.0.19";
    public int udpPort = 5005;
    public float sendRateHz = 30f;

    [Header("Matematyka UDP - kierunki")]
    public bool invertX = false;
    public bool invertY = false;
    public bool invertYaw = false;

    [Header("Wizualizacja diagnostyczna 2D")]
    public bool showAxisOverlay = true;
    public float axisLength = 0.05f;

    [Tooltip("REAL CORNERS = środek i X/Y z prawdziwych narożników detekcji. POSE = projekcja tag.Position.")]
    public bool useEdgeBasedOverlay = true;

    [Tooltip("Rysuje biały obrys z prawdziwych narożników detekcji.")]
    public bool drawProjectedTagBorder = true;

    [Tooltip("Gdy rzut osi Z jest bardzo krótki, rysowany jest symbol kropki/krzyżyka.")]
    public float zSymbolThresholdPx = 7f;

    [Tooltip("Margines kątowy dla symbolu Z. Np. 5° oznacza, że minimalne odchylenie nadal będzie symbolem ⊙/⊗.")]
    [Range(0f, 20f)]
    public float zSymbolAngleThresholdDeg = 5f;

    [Tooltip("Długość wizualna osi Z.")]
    [Range(0.2f, 3.0f)]
    public float zAxisVisualScale = 1.2f;

    [Tooltip("Odwraca zwrot rysowanej osi Z.")]
    public bool invertZAxisDirection = false;

    [Header("Korekta wizualna osi Z")]
    [Tooltip("Lustrzane odbicie tylko niebieskiej osi Z w poziomie ekranu.")]
    public bool mirrorZScreenX = false;

    [Tooltip("Lustrzane odbicie tylko niebieskiej osi Z w pionie ekranu.")]
    public bool mirrorZScreenY = false;

    [Header("Mapowanie CPU image -> ekran")]
    public CpuToScreenMapping cpuToScreenMapping = CpuToScreenMapping.Cover180;
    public bool mirrorCpuX = true;
    public bool mirrorCpuY = true;
    public bool invertZSymbol = false;

    [Header("Kalibracja pomiarowa")]
    [Tooltip("Rozmiar taga przekazywany do biblioteki AprilTag.")]
    public float tagSize = 0.13f;

    [Range(0.4f, 2.0f)]
    public float focalScale = 1.0f;

    [Range(0.2f, 2.0f)]
    [Tooltip("Końcowa korekta skali X/Y/Z po obliczeniu relatywnej pozycji tagów.")]
    public float measurementScale = 0.57f;

    [Range(0.5f, 2.0f)]
    [Tooltip("Tylko wizualny zoom overlayu 2D. Nie wpływa na UDP.")]
    public float screenOverlayScale = 1.0f;

    [Header("Kalibracja z linijki")]
    public float knownCalibrationDistance = 0.16f;

    private TagDetector _detector;
    private UdpClient _udp;
    private IPEndPoint _endPoint;
    private Camera _arCamera;

    private string _debugText = "Szukam sygnału ARCore...";
    private GUIStyle _guiStyle;
    private GUIStyle _menuStyle;
    private GUIStyle _buttonStyle;

    private float _lastSendTime = 0f;

    private int _currentDetectorWidth = 0;
    private int _currentDetectorHeight = 0;

    private bool _isMenuOpen = false;
    private Vector2 _menuScroll = Vector2.zero;

    private float _fx;
    private float _fy;
    private float _cx;
    private float _cy;

    private float _lastRawDistance = 0f;
    private float _lastCorrectedDistance = 0f;

    private struct AxisGuiLine
    {
        public Vector2 a;
        public Vector2 b;
        public Color color;

        public AxisGuiLine(Vector2 a, Vector2 b, Color color)
        {
            this.a = a;
            this.b = b;
            this.color = color;
        }
    }

    private readonly List<AxisGuiLine> _axisGuiLines = new List<AxisGuiLine>();

    void Start()
    {
        if (FORCE_START_CALIBRATION)
        {
            tagSize = START_TAG_SIZE;
            focalScale = START_FOCAL_SCALE;
            measurementScale = START_MEASUREMENT_SCALE;
        }

        if (camManager == null)
            camManager = FindObjectOfType<ARCameraManager>();

        if (camManager != null)
            _arCamera = camManager.GetComponent<Camera>();

        if (_arCamera == null)
            _arCamera = Camera.main;

        InitUdp();
        InitGui();
    }

    void OnEnable()
    {
        if (camManager != null)
            camManager.frameReceived += OnFrame;
    }

    void OnDisable()
    {
        if (camManager != null)
            camManager.frameReceived -= OnFrame;
    }

    void OnFrame(ARCameraFrameEventArgs args)
    {
        _axisGuiLines.Clear();

        if (camManager == null)
        {
            _debugText = $"BUILD: {BUILD_MARK}\nBrak ARCameraManager.";
            return;
        }

        if (!camManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            _debugText = $"BUILD: {BUILD_MARK}\nOczekiwanie na klatkę CPU...";
            return;
        }

        using (image)
        {
            int w = image.width;
            int h = image.height;

            var conv = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, w, h),
                outputDimensions = new Vector2Int(w, h),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorY
            };

            int size = image.GetConvertedDataSize(conv);
            using var rawBuf = new NativeArray<byte>(size, Allocator.Temp);
            image.Convert(conv, rawBuf);

            var colorBuf = rawBuf.Reinterpret<Color32>(1);

            if (_detector == null || _currentDetectorWidth != w || _currentDetectorHeight != h)
            {
                _detector?.Dispose();
                _detector = new TagDetector(w, h);
                _currentDetectorWidth = w;
                _currentDetectorHeight = h;

                Debug.Log($"[AprilTag] Detector init: {w}x{h}, BUILD={BUILD_MARK}");
            }

            float baseFx;
            float baseFy;

            if (camManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                float intrinW = intrinsics.resolution.x;
                float intrinH = intrinsics.resolution.y;
                float intrinFx = intrinsics.focalLength.x;
                float intrinFy = intrinsics.focalLength.y;

                if ((w > h && intrinW < intrinH) || (w < h && intrinW > intrinH))
                {
                    intrinW = intrinsics.resolution.y;
                    intrinH = intrinsics.resolution.x;
                    intrinFx = intrinsics.focalLength.y;
                    intrinFy = intrinsics.focalLength.x;
                }

                baseFx = intrinFx * ((float)w / intrinW);
                baseFy = intrinFy * ((float)h / intrinH);
            }
            else
            {
                float vFovDeg = _arCamera != null ? _arCamera.fieldOfView : 60f;
                float vFovRad = vFovDeg * Mathf.Deg2Rad;

                baseFy = (h / 2f) / Mathf.Tan(vFovRad / 2f);
                baseFx = baseFy;
            }

            _fx = baseFx * focalScale;
            _fy = baseFy * focalScale;
            _cx = w * 0.5f;
            _cy = h * 0.5f;

            float horizontalFovRad = 2f * Mathf.Atan((w / 2f) / _fx);

            _detector.ProcessImage(colorBuf.AsReadOnlySpan(), horizontalFovRad, tagSize);

            Matrix4x4? tRef = null;
            Matrix4x4? tCar = null;
            int foundCount = 0;

            foreach (var tag in _detector.DetectedTags)
            {
                foundCount++;

                if ((tag.ID == referenceTagId || tag.ID == carTagId) && showAxisOverlay)
                {
                    if (useEdgeBasedOverlay)
                        AddAxesFromRealCorners2D(tag, w, h);
                    else
                        AddAxes2D(tag.Position, tag.Rotation, w, h);
                }

                Matrix4x4 pose = Matrix4x4.TRS(tag.Position, tag.Rotation, Vector3.one);

                if (tag.ID == referenceTagId)
                    tRef = pose;

                if (tag.ID == carTagId)
                    tCar = pose;
            }

            float fps = 1f / Mathf.Max(Time.deltaTime, 0.0001f);

            if (tRef.HasValue && tCar.HasValue)
            {
                Matrix4x4 rel = tRef.Value.inverse * tCar.Value;

                Vector3 rawPos = rel.GetColumn(3);
                Vector3 correctedPos = rawPos * measurementScale;

                float rawX = rawPos.x;
                float rawY = rawPos.y;
                float rawDistance = Mathf.Sqrt(rawX * rawX + rawY * rawY);

                float x = invertX ? -correctedPos.x : correctedPos.x;
                float y = invertY ? -correctedPos.y : correctedPos.y;
                float zError = correctedPos.z;

                float yaw = Mathf.Atan2(rel.m10, rel.m00) * Mathf.Rad2Deg;
                if (invertYaw)
                    yaw = -yaw;

                yaw = NormalizeAngle(yaw);

                float correctedDistance = Mathf.Sqrt(x * x + y * y);

                _lastRawDistance = rawDistance;
                _lastCorrectedDistance = correctedDistance;

                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"ID{referenceTagId}: OK | ID{carTagId}: OK | FPS: {fps:F1}\n" +
                    $"Dystans: {correctedDistance:F3} m  RAW: {rawDistance:F3} m\n" +
                    $"X: {x:F3}  Y: {y:F3}  Z: {zError:F3}  Yaw: {yaw:F1}°\n" +
                    $"CPU: {cpuToScreenMapping} | MX:{mirrorCpuX} MY:{mirrorCpuY}\n" +
                    $"Zmirror X:{mirrorZScreenX} Y:{mirrorZScreenY} | Zinv:{invertZAxisDirection}";

                SendTelemetry(x, y, yaw, fps);
            }
            else
            {
                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"Szukam tagów...\n" +
                    $"ID{referenceTagId}: {(tRef.HasValue ? "OK" : "SZUKAM")} | " +
                    $"ID{carTagId}: {(tCar.HasValue ? "OK" : "SZUKAM")}\n" +
                    $"Wykryte: {foundCount} | CPU: {w}x{h}\n" +
                    $"CPU: {cpuToScreenMapping} | MX:{mirrorCpuX} MY:{mirrorCpuY}";
            }
        }
    }

    void AddAxes2D(Vector3 rawPos, Quaternion rawRot, int imgW, int imgH)
    {
        Vector3 origin = new Vector3(rawPos.x, -rawPos.y, rawPos.z);
        Quaternion rot = GetOverlayRotation(rawRot);

        Vector3 xEnd3D = origin + rot * Vector3.right * axisLength;
        Vector3 yEnd3D = origin + rot * Vector3.up * axisLength;
        Vector3 zEnd3D = origin + rot * Vector3.forward * axisLength;

        if (!ProjectCameraPointToScreen(origin, imgW, imgH, out Vector2 center))
            return;

        if (ProjectCameraPointToScreen(xEnd3D, imgW, imgH, out Vector2 xEnd))
            _axisGuiLines.Add(new AxisGuiLine(center, xEnd, Color.red));

        if (ProjectCameraPointToScreen(yEnd3D, imgW, imgH, out Vector2 yEnd))
            _axisGuiLines.Add(new AxisGuiLine(center, yEnd, Color.green));

        if (ProjectCameraPointToScreen(zEnd3D, imgW, imgH, out Vector2 zEnd))
            AddProjectedZAxisOrSymbol(center, center, zEnd, rawRot, 24f);
    }

    void AddAxesFromRealCorners2D(TagPose tag, int imgW, int imgH)
    {
        Vector2 c0 = CpuImagePixelToScreen(tag.Corner0.x, tag.Corner0.y, imgW, imgH);
        Vector2 c1 = CpuImagePixelToScreen(tag.Corner1.x, tag.Corner1.y, imgW, imgH);
        Vector2 c2 = CpuImagePixelToScreen(tag.Corner2.x, tag.Corner2.y, imgW, imgH);
        Vector2 c3 = CpuImagePixelToScreen(tag.Corner3.x, tag.Corner3.y, imgW, imgH);

        Vector2 center = CpuImagePixelToScreen(tag.Center.x, tag.Center.y, imgW, imgH);

        Vector2 xDir = ((c1 - c0) + (c2 - c3)) * 0.5f;
        Vector2 yDir = ((c0 - c3) + (c1 - c2)) * 0.5f;

        float xLen = xDir.magnitude;
        float yLen = yDir.magnitude;

        if (xLen < 1f || yLen < 1f)
            return;

        xDir /= xLen;
        yDir /= yLen;

        float axisPx = Mathf.Min(xLen, yLen) * 0.45f;

        _axisGuiLines.Add(new AxisGuiLine(center, center + xDir * axisPx, Color.red));
        _axisGuiLines.Add(new AxisGuiLine(center, center + yDir * axisPx, Color.green));

        AddProjectedZAxisFromPose(tag.Position, tag.Rotation, center, imgW, imgH, axisPx);

        if (drawProjectedTagBorder)
        {
            _axisGuiLines.Add(new AxisGuiLine(c0, c1, Color.white));
            _axisGuiLines.Add(new AxisGuiLine(c1, c2, Color.white));
            _axisGuiLines.Add(new AxisGuiLine(c2, c3, Color.white));
            _axisGuiLines.Add(new AxisGuiLine(c3, c0, Color.white));
        }
    }

    void AddProjectedZAxisFromPose(
        Vector3 rawPos,
        Quaternion rawRot,
        Vector2 realCenter2D,
        int imgW,
        int imgH,
        float axisPx)
    {
        Vector3 origin = new Vector3(rawPos.x, -rawPos.y, rawPos.z);
        Quaternion rot = GetOverlayRotation(rawRot);

        Vector3 zDir3D = rot * Vector3.forward;

        if (invertZAxisDirection)
            zDir3D = -zDir3D;

        float safeScale = Mathf.Max(measurementScale, 0.001f);
        float visualZLength = (axisLength / safeScale) * zAxisVisualScale;

        Vector3 zEnd3D = origin + zDir3D * visualZLength;

        if (!ProjectCameraPointToScreen(origin, imgW, imgH, out Vector2 poseCenter2D))
            return;

        if (!ProjectCameraPointToScreen(zEnd3D, imgW, imgH, out Vector2 zEnd2D))
            return;

        AddProjectedZAxisOrSymbol(realCenter2D, poseCenter2D, zEnd2D, rawRot, axisPx);
    }

    void AddProjectedZAxisOrSymbol(
        Vector2 drawCenter,
        Vector2 projectedPoseCenter,
        Vector2 projectedZEnd,
        Quaternion rawRot,
        float axisPx)
    {
        Vector2 zDir = projectedZEnd - projectedPoseCenter;

        if (mirrorZScreenX)
            zDir.x = -zDir.x;

        if (mirrorZScreenY)
            zDir.y = -zDir.y;

        float zLen = zDir.magnitude;

        float symbolRadius = Mathf.Clamp(axisPx * 0.22f, 9f, 30f);

        Vector3 normalCam = GetTagNormalCamera(rawRot);

        if (invertZAxisDirection)
            normalCam = -normalCam;

        float angleToCameraAxisDeg = 90f;

        if (normalCam.sqrMagnitude > 0.0001f)
        {
            normalCam.Normalize();

            float absZ = Mathf.Clamp(Mathf.Abs(normalCam.z), 0f, 1f);
            angleToCameraAxisDeg = Mathf.Acos(absZ) * Mathf.Rad2Deg;
        }

        bool useSymbolByAngle = angleToCameraAxisDeg <= zSymbolAngleThresholdDeg;
        bool useSymbolByPixel = zLen < zSymbolThresholdPx;

        if (useSymbolByAngle || useSymbolByPixel)
        {
            bool zTowardsCamera = IsTagZTowardsCamera(rawRot);
            AddZSymbol2D(drawCenter, symbolRadius, zTowardsCamera);
            return;
        }

        Vector2 zEnd = drawCenter + zDir.normalized * axisPx * 0.85f;

        _axisGuiLines.Add(new AxisGuiLine(drawCenter, zEnd, Color.blue));

        Vector2 dir = (zEnd - drawCenter).normalized;
        Vector2 normal = new Vector2(-dir.y, dir.x);

        float arrowSize = Mathf.Clamp(axisPx * 0.12f, 5f, 16f);

        Vector2 arrowA = zEnd - dir * arrowSize + normal * arrowSize * 0.6f;
        Vector2 arrowB = zEnd - dir * arrowSize - normal * arrowSize * 0.6f;

        _axisGuiLines.Add(new AxisGuiLine(zEnd, arrowA, Color.blue));
        _axisGuiLines.Add(new AxisGuiLine(zEnd, arrowB, Color.blue));
    }

    Quaternion GetOverlayRotation(Quaternion rawRot)
    {
        Quaternion rot = rawRot;
        rot.y = -rot.y;
        rot.w = -rot.w;
        return rot;
    }

    Vector3 GetTagNormalCamera(Quaternion rawRot)
    {
        Quaternion rot = GetOverlayRotation(rawRot);
        return rot * Vector3.forward;
    }

    bool IsTagZTowardsCamera(Quaternion rawRot)
    {
        Vector3 normalCam = GetTagNormalCamera(rawRot);

        if (invertZAxisDirection)
            normalCam = -normalCam;

        bool zTowardsCamera = normalCam.z < 0f;

        if (invertZSymbol)
            zTowardsCamera = !zTowardsCamera;

        return zTowardsCamera;
    }

    bool ProjectCameraPointToScreen(Vector3 p, int imgW, int imgH, out Vector2 screen)
    {
        screen = Vector2.zero;

        if (p.z <= 0.001f)
            return false;

        float u = _fx * (p.x / p.z) + _cx;
        float v = _cy - _fy * (p.y / p.z);

        screen = CpuImagePixelToScreen(u, v, imgW, imgH);
        return true;
    }

    Vector2 CpuImagePixelToScreen(float u, float v, int imgW, int imgH)
    {
        float nx = u / imgW;
        float ny = v / imgH;

        if (mirrorCpuX) nx = 1f - nx;
        if (mirrorCpuY) ny = 1f - ny;

        float tx = nx;
        float ty = ny;
        float imageAspect = (float)imgW / imgH;

        switch (cpuToScreenMapping)
        {
            case CpuToScreenMapping.Cover0:
                tx = nx;
                ty = ny;
                imageAspect = (float)imgW / imgH;
                break;

            case CpuToScreenMapping.Cover90CW:
                tx = 1f - ny;
                ty = nx;
                imageAspect = (float)imgH / imgW;
                break;

            case CpuToScreenMapping.Cover90CCW:
                tx = ny;
                ty = 1f - nx;
                imageAspect = (float)imgH / imgW;
                break;

            case CpuToScreenMapping.Cover180:
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

        float x = ((xBase - screenW * 0.5f) * screenOverlayScale) + screenW * 0.5f;
        float y = ((yBase - screenH * 0.5f) * screenOverlayScale) + screenH * 0.5f;

        return new Vector2(x, y);
    }

    void AddCircle2D(Vector2 center, float radius, Color color, int segments = 32)
    {
        float step = Mathf.PI * 2f / segments;

        for (int i = 0; i < segments; i++)
        {
            float a0 = i * step;
            float a1 = (i + 1) * step;

            Vector2 p0 = center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
            Vector2 p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;

            _axisGuiLines.Add(new AxisGuiLine(p0, p1, color));
        }
    }

    void AddX2D(Vector2 center, float radius, Color color)
    {
        float r = radius * 0.55f;

        _axisGuiLines.Add(new AxisGuiLine(
            center + new Vector2(-r, -r),
            center + new Vector2(r, r),
            color
        ));

        _axisGuiLines.Add(new AxisGuiLine(
            center + new Vector2(-r, r),
            center + new Vector2(r, -r),
            color
        ));
    }

    void AddDot2D(Vector2 center, float radius, Color color)
    {
        float r = Mathf.Max(2.0f, radius * 0.14f);

        _axisGuiLines.Add(new AxisGuiLine(
            center + new Vector2(-r, 0f),
            center + new Vector2(r, 0f),
            color
        ));

        _axisGuiLines.Add(new AxisGuiLine(
            center + new Vector2(0f, -r),
            center + new Vector2(0f, r),
            color
        ));
    }

    void AddZSymbol2D(Vector2 center, float radius, bool zTowardsCamera)
    {
        Color color = Color.blue;

        AddCircle2D(center, radius, color);

        if (zTowardsCamera)
        {
            AddDot2D(center, radius, color);
        }
        else
        {
            AddX2D(center, radius, color);
        }
    }

    void DrawGuiLine(Vector2 a, Vector2 b, Color color, float width)
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

    float NormalizeAngle(float angle)
    {
        while (angle > 180f)
            angle -= 360f;

        while (angle < -180f)
            angle += 360f;

        return angle;
    }

    void InitUdp()
    {
        try
        {
            _udp = new UdpClient();
            _endPoint = new IPEndPoint(IPAddress.Parse(pcIp), udpPort);
            Debug.Log($"[UDP] OK: {pcIp}:{udpPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UDP] Init error: {e.Message}");
        }
    }

    void InitGui()
    {
        int debugFont = Mathf.RoundToInt(Screen.height * 0.026f);
        int menuFont = Mathf.RoundToInt(Screen.height * 0.021f);

        debugFont = Mathf.Clamp(debugFont, 20, 30);
        menuFont = Mathf.Clamp(menuFont, 18, 26);

        _guiStyle = new GUIStyle
        {
            fontSize = debugFont,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = false
        };
        _guiStyle.normal.textColor = Color.green;

        _menuStyle = new GUIStyle
        {
            fontSize = menuFont,
            fontStyle = FontStyle.Normal,
            wordWrap = true
        };
        _menuStyle.normal.textColor = Color.white;

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = menuFont,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
    }

    void SendTelemetry(float x, float y, float yaw, float fps)
    {
        if (_udp == null || _endPoint == null)
            return;

        if (Time.time - _lastSendTime < 1f / Mathf.Max(sendRateHz, 1f))
            return;

        string data =
            Time.time.ToString("F2", CultureInfo.InvariantCulture) + "," +
            x.ToString("F3", CultureInfo.InvariantCulture) + "," +
            y.ToString("F3", CultureInfo.InvariantCulture) + "," +
            yaw.ToString("F1", CultureInfo.InvariantCulture) + "," +
            fps.ToString("F1", CultureInfo.InvariantCulture);

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(data);
            _udp.Send(bytes, bytes.Length, _endPoint);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UDP] Send error: {e.Message}");
        }

        _lastSendTime = Time.time;
    }

    void OnGUI()
    {
        if (_guiStyle == null || _menuStyle == null || _buttonStyle == null)
            InitGui();

        foreach (var line in _axisGuiLines)
        {
            DrawGuiLine(line.a, line.b, line.color, 6f);
        }

        Rect buttonRect = new Rect(Screen.width - 270f, 25f, 245f, 80f);

        if (GUI.Button(buttonRect, _isMenuOpen ? "ZAMKNIJ" : "KALIBRACJA"))
        {
            _isMenuOpen = !_isMenuOpen;
            GUIUtility.ExitGUI();
        }

        if (_isMenuOpen)
        {
            DrawCalibrationMenu();
        }

        DrawDebugBox();
    }

    void DrawCalibrationMenu()
    {
        float menuW = Mathf.Min(700f, Screen.width * 0.48f);
        float menuH = Mathf.Min(Screen.height - 130f, 900f);

        Rect menuRect = new Rect(Screen.width - menuW - 30f, 100f, menuW, menuH);

        GUI.color = new Color(0f, 0f, 0f, 0.95f);
        GUI.DrawTexture(menuRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(menuRect.x + 18f, menuRect.y + 12f, menuW - 36f, menuH - 24f));
        _menuScroll = GUILayout.BeginScrollView(_menuScroll, false, true);

        GUILayout.Label($"BUILD: {BUILD_MARK}", _menuStyle);
        GUILayout.Space(10);

        GUILayout.Label("--- 1. Parametry AprilTag ---", _menuStyle);

        GUILayout.Label($"TagSize: {tagSize:F3} m", _menuStyle);
        tagSize = GUILayout.HorizontalSlider(tagSize, 0.05f, 0.30f);

        GUILayout.Label($"FocalScale: {focalScale:F2}x", _menuStyle);
        focalScale = GUILayout.HorizontalSlider(focalScale, 0.4f, 2.0f);

        GUILayout.Space(15);

        GUILayout.Label("--- 2. Korekta pomiaru ---", _menuStyle);

        GUILayout.Label($"MeasurementScale: {measurementScale:F3}x", _menuStyle);
        measurementScale = GUILayout.HorizontalSlider(measurementScale, 0.2f, 2.0f);

        GUILayout.Label($"Znany dystans: {knownCalibrationDistance:F3} m", _menuStyle);
        knownCalibrationDistance = GUILayout.HorizontalSlider(knownCalibrationDistance, 0.05f, 1.50f);

        GUILayout.Label($"RAW: {_lastRawDistance:F3} m", _menuStyle);
        GUILayout.Label($"Po korekcie: {_lastCorrectedDistance:F3} m", _menuStyle);

        if (GUILayout.Button("Ustaw scale = znany dystans / RAW", _buttonStyle, GUILayout.Height(55)))
        {
            if (_lastRawDistance > 0.001f)
                measurementScale = knownCalibrationDistance / _lastRawDistance;

            GUIUtility.ExitGUI();
        }

        GUILayout.Space(15);

        GUILayout.Label("--- 3. Kierunki UDP ---", _menuStyle);

        invertX = GUILayout.Toggle(invertX, " Odwróć X", _menuStyle);
        invertY = GUILayout.Toggle(invertY, " Odwróć Y", _menuStyle);
        invertYaw = GUILayout.Toggle(invertYaw, " Odwróć Yaw", _menuStyle);

        GUILayout.Space(15);

        GUILayout.Label("--- 4. Wizualizacja ---", _menuStyle);

        showAxisOverlay = GUILayout.Toggle(showAxisOverlay, " Renderuj osie 2D", _menuStyle);
        useEdgeBasedOverlay = GUILayout.Toggle(useEdgeBasedOverlay, " Tryb REAL CORNERS", _menuStyle);
        drawProjectedTagBorder = GUILayout.Toggle(drawProjectedTagBorder, " Biały obrys narożników", _menuStyle);

        GUILayout.Label($"ScreenOverlayScale: {screenOverlayScale:F2}", _menuStyle);
        screenOverlayScale = GUILayout.HorizontalSlider(screenOverlayScale, 0.5f, 2.0f);

        GUILayout.Label($"Z symbol threshold: {zSymbolThresholdPx:F1} px", _menuStyle);
        zSymbolThresholdPx = GUILayout.HorizontalSlider(zSymbolThresholdPx, 0f, 30f);

        GUILayout.Label($"Z angle margin: {zSymbolAngleThresholdDeg:F1}°", _menuStyle);
        zSymbolAngleThresholdDeg = GUILayout.HorizontalSlider(zSymbolAngleThresholdDeg, 0f, 20f);

        GUILayout.Label($"Z Axis Visual Scale: {zAxisVisualScale:F2}x", _menuStyle);
        zAxisVisualScale = GUILayout.HorizontalSlider(zAxisVisualScale, 0.2f, 3.0f);

        invertZAxisDirection = GUILayout.Toggle(invertZAxisDirection, " Odwróć kierunek osi Z", _menuStyle);

        GUILayout.Label("--- 4A. Lustrzane odbicie samej osi Z ---", _menuStyle);
        mirrorZScreenX = GUILayout.Toggle(mirrorZScreenX, " Mirror Z Screen X", _menuStyle);
        mirrorZScreenY = GUILayout.Toggle(mirrorZScreenY, " Mirror Z Screen Y", _menuStyle);
        invertZSymbol = GUILayout.Toggle(invertZSymbol, " Odwróć symbol Z", _menuStyle);

        GUILayout.Space(15);

        GUILayout.Label("--- 5. CPU image -> ekran ---", _menuStyle);
        GUILayout.Label($"Aktualnie: {cpuToScreenMapping}", _menuStyle);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button(cpuToScreenMapping == CpuToScreenMapping.Cover0 ? "[Cover0]" : "Cover0", _buttonStyle, GUILayout.Height(50)))
        {
            cpuToScreenMapping = CpuToScreenMapping.Cover0;
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button(cpuToScreenMapping == CpuToScreenMapping.Cover90CW ? "[90CW]" : "90CW", _buttonStyle, GUILayout.Height(50)))
        {
            cpuToScreenMapping = CpuToScreenMapping.Cover90CW;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();

        if (GUILayout.Button(cpuToScreenMapping == CpuToScreenMapping.Cover90CCW ? "[90CCW]" : "90CCW", _buttonStyle, GUILayout.Height(50)))
        {
            cpuToScreenMapping = CpuToScreenMapping.Cover90CCW;
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button(cpuToScreenMapping == CpuToScreenMapping.Cover180 ? "[180]" : "180", _buttonStyle, GUILayout.Height(50)))
        {
            cpuToScreenMapping = CpuToScreenMapping.Cover180;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        mirrorCpuX = GUILayout.Toggle(mirrorCpuX, " Mirror CPU X", _menuStyle);
        mirrorCpuY = GUILayout.Toggle(mirrorCpuY, " Mirror CPU Y", _menuStyle);

        GUILayout.Space(30);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void DrawDebugBox()
    {
        float width = Screen.width * 0.70f;
        float height = 220f;
        float xBox = 35f;
        float yBox = Screen.height * 0.56f;

        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(xBox, yBox, width, height), Texture2D.whiteTexture);

        GUI.color = Color.white;
        GUI.Label(new Rect(xBox + 18f, yBox + 12f, width - 36f, height - 24f), _debugText, _guiStyle);
    }

    void OnDestroy()
    {
        _detector?.Dispose();
        _udp?.Close();
    }
}