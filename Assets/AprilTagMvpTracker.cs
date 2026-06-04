/*
AprilTagMvpTracker.cs
- nadzoruje ARCameraManager
- pobiera XRCpuImage
- odpala AprilTag detector
- liczy relację ID0 -> ID1
- wysyła UDP
- przekazuje dane do GUI i overlayu

TrackerGuiOverlay.cs
- menu
- deadman
- speed slider
- debug box

UdpTelemetrySender.cs
- ramka UDP
- CRC
- bufor wysyłki

AprilTagAxisOverlayRenderer.cs
- biały obrys
- osie X/Y/Z
- symbol Z
- mapowanie CPU image -> ekran
*/



using AprilTag;
using System;
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

    private const string BUILD_MARK = "V18_GUI_UDP_OVERLAY_SPLIT";

    private const bool FORCE_START_CALIBRATION = true;

    private const float START_TAG_SIZE = 0.13f;
    private const float START_FOCAL_SCALE = 1.00f;
    private const float START_MEASUREMENT_SCALE = 0.57f;

    [Header("Detekcja AprilTag")]
    public ARCameraManager camManager;
    public int referenceTagId = 0;
    public int carTagId = 1;

    [Header("Wydajność")]
    [Range(5f, 60f)]
    public float visionRateHz = 45f;
    private float _lastVisionProcessTime = -999f;

    [Tooltip("Po ilu sekundach bez świeżej klatki CPU overlay/debug uznajemy za nieaktualny.")]
    public float staleOverlayTimeout = 0.25f;
    private float _lastSuccessfulCpuImageTime = -999f;

    [Header("UDP Telemetria")]
    public string pcIp = "192.168.18.238";
    public int udpPort = 5005;
    public float sendRateHz = 30f;

    [Header("Sterowanie pojazdem - UDP")]
    [Range(0, 100)]
    public int speedPercent = 20;

    [Tooltip("Aktualny stan przycisku deadman. Ustawiany automatycznie przez przycisk na ekranie.")]
    public bool deadmanPressed = false;

    [Header("Matematyka UDP - kierunki")]
    public bool invertX = false;
    public bool invertY = false;
    public bool invertYaw = false;

    [Header("Wizualizacja diagnostyczna 2D")]
    public bool showAxisOverlay = true;
    public float axisLength = 0.05f;
    public bool showXAxis = true;
    public bool showYAxis = true;
    public bool showZAxis = true;

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
    
    private Camera _arCamera;
    private UdpTelemetrySender _telemetry;


    private string _debugText = "Szukam sygnału ARCore...";
    
    private int _currentDetectorWidth = 0;
    private int _currentDetectorHeight = 0;
        
    private TrackerGuiOverlay _guiOverlay;
    private AprilTagAxisOverlayRenderer _axisOverlay;

    private float _fx;
    private float _fy;
    private float _cx;
    private float _cy;

    private float _lastRawDistance = 0f;
    private float _lastCorrectedDistance = 0f;

    
    void Start()
    {
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;

        if (FORCE_START_CALIBRATION)
        {
            tagSize = START_TAG_SIZE;
            focalScale = START_FOCAL_SCALE;
            measurementScale = START_MEASUREMENT_SCALE;

            useEdgeBasedOverlay = true;
            showAxisOverlay = true;
            showXAxis = true;
            showYAxis = true;
            showZAxis = true;
            drawProjectedTagBorder = true;

            invertX = false;
            invertY = false;
            invertYaw = false;
            invertZAxisDirection = false;

            cpuToScreenMapping = CpuToScreenMapping.Cover180;
            mirrorCpuX = true;
            mirrorCpuY = true;
        }

        if (camManager == null)
            camManager = FindObjectOfType<ARCameraManager>();

        if (camManager != null)
            _arCamera = camManager.GetComponent<Camera>();

        if (_arCamera == null)
            _arCamera = Camera.main;

        _telemetry = new UdpTelemetrySender(pcIp, udpPort, sendRateHz);
        _guiOverlay = new TrackerGuiOverlay();
        _axisOverlay = new AprilTagAxisOverlayRenderer();
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
        if (camManager == null)
        {
            _debugText = $"BUILD: {BUILD_MARK}\nBrak ARCameraManager.";
            return;
        }

        if (Time.time - _lastVisionProcessTime < 1f / Mathf.Max(visionRateHz, 1f))
        {
            return;
        }

        _lastVisionProcessTime = Time.time;

        if (!camManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            // Pojedynczy brak CPU image jest normalny, więc nie czyścimy od razu.
            // Ale jeśli nie ma świeżej klatki dłużej niż staleOverlayTimeout,
            // to stary overlay/debug uznajemy za nieaktualny.
            if (Time.time - _lastSuccessfulCpuImageTime > staleOverlayTimeout)
            {
                _axisOverlay?.Clear();

                float fps = 1f / Mathf.Max(Time.deltaTime, 0.0001f);
                int fpsHz = Mathf.RoundToInt(fps);

                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"Brak świeżej klatki CPU...\n" +
                    $"Ostatni obraz > {staleOverlayTimeout:F2} s temu\n" +
                    $"CMD deadman:{deadmanPressed} speed:{speedPercent}%";

                _telemetry?.Send(
                    0f,
                    0f,
                    0f,
                    0f,
                    fpsHz,
                    speedPercent,
                    deadmanPressed,
                    false
                );
            }

            return;
        }

        using (image)
        {
            _lastSuccessfulCpuImageTime = Time.time;

            _axisOverlay?.Clear();
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
                    _axisOverlay?.AddAxes(
                        tag,
                        w,
                        h,
                        this,
                        _fx,
                        _fy,
                        _cx,
                        _cy
                    );
                }

                Matrix4x4 pose = Matrix4x4.TRS(tag.Position, tag.Rotation, Vector3.one);

                if (tag.ID == referenceTagId)
                    tRef = pose;

                if (tag.ID == carTagId)
                    tCar = pose;
            }

            float fps = 1f / Mathf.Max(Time.deltaTime, 0.0001f);
            int fpsHz = Mathf.RoundToInt(fps);

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
                float zError = invertZAxisDirection ? -correctedPos.z : correctedPos.z;

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
                    $"Zmirror X:{mirrorZScreenX} Y:{mirrorZScreenY} | Zinv:{invertZAxisDirection}\n" +
                    $"CMD deadman:{deadmanPressed} speed:{speedPercent}%";
                
                _telemetry?.Send(
                x,
                y,
                zError,
                yaw,
                fpsHz,
                speedPercent,
                deadmanPressed,
                true
                );
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
                _telemetry?.Send(
                0f,
                0f,
                0f,
                0f,
                fpsHz,
                speedPercent,
                deadmanPressed,
                false
                );
            }
        }
    }

    float NormalizeAngle(float angle)
    {
        while (angle > 180f)
            angle -= 360f;

        while (angle < -180f)
            angle += 360f;

        return angle;
    }

    void OnGUI()
    {
        _axisOverlay?.Draw(6f);

        _guiOverlay?.Draw(
            this,
            BUILD_MARK,
            _debugText,
            _lastRawDistance,
            _lastCorrectedDistance
        );
    }


    void OnDestroy()
    {
        _detector?.Dispose();
        _telemetry?.Dispose();
        _detector = null;
        _telemetry = null;
    }
}