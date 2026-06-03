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
    private const string BUILD_MARK = "SCALE_DIAG_V8";

    // Je¿eli chcesz wymusiæ wartoœci przy ka¿dym uruchomieniu aplikacji,
    // zostaw true. Jak ju¿ skalibrujesz i chcesz sterowaæ z Inspectora/GUI,
    // mo¿esz zmieniæ na false.
    private const bool FORCE_START_CALIBRATION = true;

    private const float START_TAG_SIZE = 0.13f;
    private const float START_FOCAL_SCALE = 1.00f;

    // Z Twoich pomiarów: 0.16 / 0.28 ~= 0.57 oraz 0.50 / 0.88 ~= 0.57.
    // To jest koñcowa korekta skali wyniku, niezale¿na od biblioteki AprilTag.
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

    [Header("Kalibracja pomiarowa")]
    [Tooltip("Rozmiar taga przekazywany do biblioteki AprilTag. Zostaw 0.13, jeœli fizycznie czarny znacznik ma 13 cm.")]
    public float tagSize = 0.13f;

    [Range(0.4f, 2.0f)]
    [Tooltip("Korekta ogniskowej u¿ywanej do pose estimation. Do testów zostaw 1.0.")]
    public float focalScale = 1.0f;

    [Range(0.2f, 2.0f)]
    [Tooltip("Koñcowa korekta skali X/Y/Z po obliczeniu relatywnej pozycji tagów. To realnie skaluje wynik UDP.")]
    public float measurementScale = 0.57f;

    [Range(0.5f, 2.0f)]
    [Tooltip("Tylko wizualny zoom overlayu 2D. Nie wp³ywa na UDP.")]
    public float screenOverlayScale = 1.0f;

    [Header("Kalibracja z linijki")]
    public float knownCalibrationDistance = 0.16f;

    private TagDetector _detector;
    private UdpClient _udp;
    private IPEndPoint _endPoint;
    private Camera _arCamera;

    private string _debugText = "Szukam sygna³u ARCore...";
    private GUIStyle _guiStyle;
    private GUIStyle _menuStyle;
    private float _lastSendTime = 0f;

    private int _currentDetectorWidth = 0;
    private int _currentDetectorHeight = 0;
    private bool _isMenuOpen = false;

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
            _debugText = $"BUILD: {BUILD_MARK}\nOczekiwanie na klatkê CPU...";
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
                float rawZ = rawPos.z;
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
                    $"Baza (ID{referenceTagId}): OK | Pojazd (ID{carTagId}): OK\n" +
                    $"TagSize: {tagSize:F3} m | FocalScale: {focalScale:F2}x\n" +
                    $"MeasurementScale: {measurementScale:F3}x\n" +
                    $"RAW dystans: {rawDistance:F3} m\n" +
                    $"Dystans po korekcie: {correctedDistance:F3} m\n" +
                    $"X (Prawo): {x:F3} m\n" +
                    $"Y (Przód): {y:F3} m\n" +
                    $"Z (Wysokoœæ): {zError:F3} m\n" +
                    $"Yaw: {yaw:F1}° | FPS: {fps:F1}\n" +
                    $"fx/fy: {_fx:F0}/{_fy:F0} | CPU: {w}x{h}";

                SendTelemetry(x, y, yaw, fps);
            }
            else
            {
                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"Szukam markerów Standard41h12...\n" +
                    $"TagSize: {tagSize:F3} m | FocalScale: {focalScale:F2}x\n" +
                    $"MeasurementScale: {measurementScale:F3}x\n" +
                    $"Baza (ID {referenceTagId}): {(tRef.HasValue ? "OK" : "SZUKAM")}\n" +
                    $"Pojazd (ID {carTagId}): {(tCar.HasValue ? "OK" : "SZUKAM")}\n" +
                    $"Wykryte obiekty: {foundCount}\n" +
                    $"CPU: {w}x{h}";
            }
        }
    }

    void AddAxes2D(Vector3 rawPos, Quaternion rawRot, int imgW, int imgH)
    {
        Vector3 origin = new Vector3(rawPos.x, -rawPos.y, rawPos.z);

        Quaternion rot = rawRot;
        rot.y = -rot.y;
        rot.w = -rot.w;

        Vector3[] dirs =
        {
            Vector3.right,
            Vector3.up,
            Vector3.forward
        };

        Color[] colors =
        {
            Color.red,
            Color.green,
            Color.blue
        };

        for (int i = 0; i < 3; i++)
        {
            Vector3 end = origin + rot * dirs[i] * axisLength;

            if (ProjectCameraPointToScreen(origin, imgW, imgH, out Vector2 a) &&
                ProjectCameraPointToScreen(end, imgW, imgH, out Vector2 b))
            {
                _axisGuiLines.Add(new AxisGuiLine(a, b, colors[i]));
            }
        }
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
        float screenW = Screen.width;
        float screenH = Screen.height;

        float imgAspect = (float)imgW / imgH;
        float screenAspect = screenW / screenH;

        float drawW;
        float drawH;
        float offsetX;
        float offsetY;

        if (imgAspect > screenAspect)
        {
            drawH = screenH;
            drawW = drawH * imgAspect;
            offsetX = (screenW - drawW) * 0.5f;
            offsetY = 0f;
        }
        else
        {
            drawW = screenW;
            drawH = drawW / imgAspect;
            offsetX = 0f;
            offsetY = (screenH - drawH) * 0.5f;
        }

        float xBase = offsetX + (u / imgW) * drawW;
        float yBase = offsetY + (v / imgH) * drawH;

        float x = ((xBase - screenW * 0.5f) * screenOverlayScale) + screenW * 0.5f;
        float y = ((yBase - screenH * 0.5f) * screenOverlayScale) + screenH * 0.5f;

        return new Vector2(x, y);
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
        _guiStyle = new GUIStyle
        {
            fontSize = 34,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        _guiStyle.normal.textColor = Color.green;

        _menuStyle = new GUIStyle
        {
            fontSize = 25,
            fontStyle = FontStyle.Normal
        };
        _menuStyle.normal.textColor = Color.white;
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
        foreach (var line in _axisGuiLines)
        {
            DrawGuiLine(line.a, line.b, line.color, 6f);
        }

        Rect buttonRect = new Rect(Screen.width - 270f, 25f, 245f, 80f);

        if (GUI.Button(buttonRect, _isMenuOpen ? "ZAMKNIJ" : "KALIBRACJA"))
        {
            _isMenuOpen = !_isMenuOpen;
        }

        if (_isMenuOpen)
        {
            float menuW = 650f;
            float menuH = 780f;
            Rect menuRect = new Rect(Screen.width - menuW - 30f, 120f, menuW, menuH);

            GUI.color = new Color(0f, 0f, 0f, 0.95f);
            GUI.DrawTexture(menuRect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUILayout.BeginArea(new Rect(menuRect.x + 20f, menuRect.y + 15f, menuW - 40f, menuH - 30f));

            GUILayout.Label($"BUILD: {BUILD_MARK}", _menuStyle);
            GUILayout.Space(10);

            GUILayout.Label("--- 1. Parametry AprilTag ---", _menuStyle);

            GUILayout.Label($"TagSize: {tagSize:F3} m", _menuStyle);
            tagSize = GUILayout.HorizontalSlider(tagSize, 0.05f, 0.30f);

            GUILayout.Label($"FocalScale: {focalScale:F2}x", _menuStyle);
            focalScale = GUILayout.HorizontalSlider(focalScale, 0.4f, 2.0f);

            GUILayout.Space(20);

            GUILayout.Label("--- 2. Korekta koñcowa pomiaru ---", _menuStyle);

            GUILayout.Label($"MeasurementScale: {measurementScale:F3}x", _menuStyle);
            measurementScale = GUILayout.HorizontalSlider(measurementScale, 0.2f, 2.0f);

            GUILayout.Label($"Znany dystans kalibracyjny: {knownCalibrationDistance:F3} m", _menuStyle);
            knownCalibrationDistance = GUILayout.HorizontalSlider(knownCalibrationDistance, 0.05f, 1.50f);

            GUILayout.Label($"Ostatni RAW dystans: {_lastRawDistance:F3} m", _menuStyle);
            GUILayout.Label($"Ostatni dystans po korekcie: {_lastCorrectedDistance:F3} m", _menuStyle);

            if (GUILayout.Button("Ustaw scale = znany dystans / RAW", GUILayout.Height(60)))
            {
                if (_lastRawDistance > 0.001f)
                {
                    measurementScale = knownCalibrationDistance / _lastRawDistance;
                }
            }

            GUILayout.Space(20);

            GUILayout.Label("--- 3. Kierunki UDP ---", _menuStyle);
            invertX = GUILayout.Toggle(invertX, " Odwróæ znak X", _menuStyle);
            invertY = GUILayout.Toggle(invertY, " Odwróæ znak Y", _menuStyle);
            invertYaw = GUILayout.Toggle(invertYaw, " Odwróæ znak Yaw", _menuStyle);

            GUILayout.Space(20);

            GUILayout.Label("--- 4. Wizualizacja ---", _menuStyle);
            showAxisOverlay = GUILayout.Toggle(showAxisOverlay, " Renderuj linie diagnostyczne 2D", _menuStyle);

            GUILayout.Label($"ScreenOverlayScale: {screenOverlayScale:F2}", _menuStyle);
            screenOverlayScale = GUILayout.HorizontalSlider(screenOverlayScale, 0.5f, 2.0f);

            GUILayout.EndArea();
        }

        float width = Screen.width * 0.86f;
        float height = 430f;
        float xBox = (Screen.width - width) / 2f;
        float yBox = Screen.height * 0.50f;

        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(xBox, yBox, width, height), Texture2D.whiteTexture);

        GUI.color = Color.white;
        GUI.Label(new Rect(xBox + 25f, yBox + 18f, width - 50f, height - 36f), _debugText, _guiStyle);
    }

    void OnDestroy()
    {
        _detector?.Dispose();
        _udp?.Close();
    }
}