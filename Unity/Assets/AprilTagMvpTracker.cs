using AprilTag;
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
    public enum AutoDeadmanMode
    {
        Off,
        Hold,
        Cycle
    }

    private const string BUILD_MARK = "V21_CMD_AUTODM_DEBUG";
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
    public UdpSendMode sendMode = UdpSendMode.TextAT1;

    [Tooltip("Adres IP odbiornika UDP, np. komputer albo ESP32.")]
    public string targetIp = "192.168.18.238";

    [Tooltip("Port UDP odbiornika. Port nie identyfikuje telefonu — telefon identyfikuje sourceId.")]
    public int targetPort = 5005;

    [Range(1, 4)]
    [Tooltip("ID źródła danych. Telefon 1 = 1, telefon 2 = 2, itd.")]
    public int sourceId = 1;

    [Tooltip("Loguje co ok. 1 s podstawowe informacje o binarnej ramce ATB1.")]
    public bool debugBinaryUdp = false;

    [Range(1f, 60f)]
    public float sendRateHz = 20f;

    [Header("UDP Output / Measurement")]
    [Tooltip("Jeśli false, aplikacja nie wysyła żadnych pakietów UDP poza aktywnym pomiarem.")]
    public bool udpOutputEnabled = true;

    [Range(1f, 120f)]
    [Tooltip("Czas właściwego pomiaru, podczas którego UDP jest włączone.")]
    public float measurementDurationSec = 10f;

    [Range(1f, 10f)]
    [Tooltip("Czas odliczania przed pomiarem. Dla 3 s: beep 3, beep 2, beep 1, długi beep START.")]
    public float measurementCountdownSec = 3f;

    [Header("Kalibracja z linijki")]
    public float knownCalibrationDistance = 0.16f;

    [Header("Korekcja osiowa X/Y")]
    [Tooltip("Dodatkowa korekcja osi X/Y po globalnym measurementScale.")]
    public bool useAxisScaleCorrection = true;

    [Range(0.5f, 2.0f)]
    [Tooltip("Mnożnik korekcyjny osi X wpisywany po kalibracji.")]
    public float xAxisScale = 1.0f;

    [Range(0.5f, 2.0f)]
    [Tooltip("Mnożnik korekcyjny osi Y wpisywany po kalibracji.")]
    public float yAxisScale = 1.0f;

    [Header("Auto Deadman / Test Mode")]
    [Tooltip("Automatyczny deadman do testów pomiarowych. Używać ostrożnie przy pojeździe na kołach.")]
    public AutoDeadmanMode autoDeadmanMode = AutoDeadmanMode.Off;

    [Tooltip("Jeśli true, auto-deadman działa tylko podczas właściwego okna pomiaru.")]
    public bool autoDeadmanOnlyDuringMeasurement = true;

    [Range(0.1f, 30f)]
    [Tooltip("Czas stanu deadman=ON w trybie Cycle.")]
    public float autoDeadmanOnSec = 2.0f;

    [Range(0.1f, 30f)]
    [Tooltip("Czas stanu deadman=OFF w trybie Cycle.")]
    public float autoDeadmanOffSec = 1.0f;

    private float _autoDeadmanCycleStartTime = 0f;

    public void ResetAutoDeadmanCycle()
    {
        _autoDeadmanCycleStartTime = Time.realtimeSinceStartup;
    }

    public bool GetAutoDeadmanState(float now)
    {
        if (autoDeadmanMode == AutoDeadmanMode.Off)
            return false;

        if (autoDeadmanOnlyDuringMeasurement &&
            _measurementState != MeasurementState.Logging)
            return false;

        if (autoDeadmanMode == AutoDeadmanMode.Hold)
            return true;

        float onSec = Mathf.Max(0.1f, autoDeadmanOnSec);
        float offSec = Mathf.Max(0.1f, autoDeadmanOffSec);
        float cycle = onSec + offSec;

        float phase = Mathf.Repeat(now - _autoDeadmanCycleStartTime, cycle);

        return phase < onSec;
    }

    public bool GetEffectiveDeadman(float now)
    {
        bool autoDeadman = GetAutoDeadmanState(now);

        // Palec może zawsze wymusić deadmana, a automat może go generować do testów.
        return deadmanPressed || autoDeadman;
    }

    private enum MeasurementState
    {
        Idle,
        Countdown,
        Logging
    }

    private MeasurementState _measurementState = MeasurementState.Idle;

    private float _measurementCountdownStartTime = 0f;
    private float _measurementLoggingStartTime = 0f;
    private float _measurementLoggingEndTime = 0f;

    private int _lastCountdownBeepIndex = -1;
    private bool _udpStateBeforeMeasurement = true;

    public string MeasurementStatusText { get; private set; } = "MEAS: IDLE";


    public string MeasurementOverlayText { get; private set; } = "";
    private float _measurementOverlayVisibleUntil = -999f;

    public bool ShowMeasurementOverlay
    {
        get
        {
            return _measurementState != MeasurementState.Idle ||
                   Time.realtimeSinceStartup < _measurementOverlayVisibleUntil;
        }
    }

    public bool IsMeasurementRunning
    {
        get
        {
            return _measurementState != MeasurementState.Idle;
        }
    }

    private AudioSource _beepSource;
    private AudioClip _beepShortClip;
    private AudioClip _beepLongClip;
    private AudioClip _beepEndClip;

    [Header("Sterowanie pojazdem - UDP")]
    [Range(0, 100)]
    public int speedPercent = 20;

    [Header("Tryb urządzenia")]
    [Tooltip("Jeśli false, telefon działa jako obserwator i nie wysyła deadman/move_en.")]
    public bool allowOperatorControl = true;

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

    // Historia kąta yaw używana do unwrapu.
    // +180° i -180° opisują prawie ten sam kierunek,
    // ale numerycznie wyglądają jak skok około 360°.
    // Dlatego do regulatora używamy yaw ciągłego.
    private bool _yawHistoryValid = false;
    private float _prevYawWrappedDeg = 0f;
    private float _yawUnwrappedDeg = 0f;
    private float _prevYawUnwrappedTime = 0f;
    private float _yawRateDps = 0f;
    // Ostatni stan vision zapamiętany przez OnFrame.
    // Update() wysyła ten stan przez UDP niezależnie od kamery.
    private bool _latestPoseValid = false;
    private float _lastValidPoseTime = -999f;

    private float _latestX = 0f;
    private float _latestY = 0f;
    private float _latestZ = 0f;
    private float _latestYawWrappedDeg = 0f;
    private float _latestYawUnwrappedDeg = 0f;
    private float _latestYawRateDps = 0f;
    private int _latestFpsHz = 0;

    private const string PrefUdpSendMode = "Vision.UdpSendMode";
    private const string PrefTargetIp = "Vision.TargetIp";
    private const string PrefTargetPort = "Vision.TargetPort";
    private const string PrefSourceId = "Vision.SourceId";
    private const string PrefSendRateHz = "Vision.SendRateHz";
    private const string PrefAllowOperatorControl = "Vision.AllowOperatorControl";

    private const string PrefFocalScale = "Vision.FocalScale";
    private const string PrefMeasurementScale = "Vision.MeasurementScale";

    private const string PrefUseAxisScaleCorrection = "Vision.UseAxisScaleCorrection";
    private const string PrefXAxisScale = "Vision.XAxisScale";
    private const string PrefYAxisScale = "Vision.YAxisScale";

    private const string PrefCpuMapping = "Vision.CpuMapping";
    private const string PrefMirrorCpuX = "Vision.MirrorCpuX";
    private const string PrefMirrorCpuY = "Vision.MirrorCpuY";


    private const string PrefUdpOutputEnabled = "Vision.UdpOutputEnabled";
    private const string PrefMeasurementDurationSec = "Vision.MeasurementDurationSec";
    private const string PrefMeasurementCountdownSec = "Vision.MeasurementCountdownSec";

    private const string PrefAutoDeadmanMode = "Vision.AutoDeadmanMode";
    private const string PrefAutoDeadmanOnlyDuringMeasurement = "Vision.AutoDeadmanOnlyDuringMeasurement";
    private const string PrefAutoDeadmanOnSec = "Vision.AutoDeadmanOnSec";
    private const string PrefAutoDeadmanOffSec = "Vision.AutoDeadmanOffSec";

    private bool _cameraFrameSubscribed = false;

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

        LoadUserSettings();


        if (camManager == null)
            camManager = FindObjectOfType<ARCameraManager>();

        if (camManager != null)
            _arCamera = camManager.GetComponent<Camera>();

        if (_arCamera == null)
            _arCamera = Camera.main;

        TrySubscribeCameraFrames();

        _telemetry = new UdpTelemetrySender(
            targetIp,
            targetPort,
            sendRateHz,
            sendMode,
            (byte)Mathf.Clamp(sourceId, 1, 4),
            debugBinaryUdp
        );

        _guiOverlay = new TrackerGuiOverlay();
        _axisOverlay = new AprilTagAxisOverlayRenderer();
        SetupBeepAudio();
    }

    void OnEnable()
    {
        TrySubscribeCameraFrames();
    }

    void OnDisable()
    {
        UnsubscribeCameraFrames();
    }

    void TrySubscribeCameraFrames()
    {
        if (_cameraFrameSubscribed)
            return;

        if (camManager == null)
            camManager = FindObjectOfType<ARCameraManager>();

        if (camManager == null)
            return;

        camManager.frameReceived += OnFrame;
        _cameraFrameSubscribed = true;
    }

    void UnsubscribeCameraFrames()
    {
        if (!_cameraFrameSubscribed)
            return;

        if (camManager != null)
            camManager.frameReceived -= OnFrame;

        _cameraFrameSubscribed = false;
    }

    void ConfigureUdpSender()
    {
        _telemetry?.Configure(
            targetIp,
            targetPort,
            sendRateHz,
            sendMode,
            (byte)Mathf.Clamp(sourceId, 1, 4),
            debugBinaryUdp
        );
    }

    void Update()
    {
        if (_telemetry == null)
            return;

        float now = Time.realtimeSinceStartup;

        UpdateMeasurementState(now);

        if (!udpOutputEnabled)
            return;

        bool poseFresh =
            _latestPoseValid &&
            now - _lastValidPoseTime <= staleOverlayTimeout;

        bool effectiveDeadman = allowOperatorControl && GetEffectiveDeadman(now);

        ConfigureUdpSender();


        if (poseFresh)
        {
            _telemetry.Send(
                _latestX,
                _latestY,
                _latestZ,
                _latestYawWrappedDeg,
                _latestYawUnwrappedDeg,
                _latestYawRateDps,
                _latestFpsHz,
                speedPercent,
                effectiveDeadman,
                true
            );
        }
        else
        {
            _telemetry.Send(
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                _latestFpsHz,
                speedPercent,
                effectiveDeadman,
                false
            );

            if (now - _lastSuccessfulCpuImageTime > staleOverlayTimeout)
            {
                _axisOverlay?.Clear();
                MarkYawInvalid();

                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"Brak świeżej klatki CPU / vision stale\n" +
                    $"Ostatni obraz > {staleOverlayTimeout:F2} s temu\n" +
                    $"UDP: {sendMode} {targetIp}:{targetPort} src:{sourceId} rate:{sendRateHz:F0}Hz\n" +
                    $"{UdpMeasurementCommandReceiver.GetGlobalDebugLine()}\n" +
                    $"OperatorCtrl:{allowOperatorControl} deadman:{deadmanPressed} speed:{speedPercent}%";
            }
        }
    }

    void OnFrame(ARCameraFrameEventArgs args)
    {
        float now = Time.realtimeSinceStartup;
        bool effectiveDeadmanForDebug = allowOperatorControl && GetEffectiveDeadman(now);

        if (camManager == null)
        {
            _debugText = $"BUILD: {BUILD_MARK}\nBrak ARCameraManager.";
            return;
        }

        if (now - _lastVisionProcessTime < 1f / Mathf.Max(visionRateHz, 1f))
            return;

        _lastVisionProcessTime = now;

        if (!camManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            _latestPoseValid = false;

            if (now - _lastSuccessfulCpuImageTime > staleOverlayTimeout)
            {
                _axisOverlay?.Clear();
                MarkYawInvalid();

                float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
                int fpsHz = Mathf.RoundToInt(fps);
                _latestFpsHz = fpsHz;

                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"Brak świeżej klatki CPU...\n" +
                    $"Ostatni obraz > {staleOverlayTimeout:F2} s temu\n" +
                    $"UDP: {sendMode} {targetIp}:{targetPort} src:{sourceId}\n" +
                    $"OperatorCtrl:{allowOperatorControl} deadman:{deadmanPressed} speed:{speedPercent}%";
            }

            return;
        }

        using (image)
        {
            _lastSuccessfulCpuImageTime = now;

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

            float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
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

                // Dodatkowa korekcja osiowa po globalnym measurementScale.
                // Nie jest przypisana do konkretnego telefonu — wartości wpisuje użytkownik.
                ApplyAxisScaleCorrection(ref x, ref y);

                float yawWrappedDeg = Mathf.Atan2(rel.m10, rel.m00) * Mathf.Rad2Deg;

                if (invertYaw)
                    yawWrappedDeg = -yawWrappedDeg;

                yawWrappedDeg = NormalizeAngle(yawWrappedDeg);

                float yawUnwrappedDeg = UpdateYawUnwrap(yawWrappedDeg);
                float yawRateDps = _yawRateDps;

                float correctedDistance = Mathf.Sqrt(x * x + y * y);

                _lastRawDistance = rawDistance;
                _lastCorrectedDistance = correctedDistance;

                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"ID{referenceTagId}: OK | ID{carTagId}: OK | FPS: {fps:F1}\n" +
                    $"Dystans: {correctedDistance:F3} m  RAW: {rawDistance:F3} m\n" +
                    $"X: {x:F3}  Y: {y:F3}  Z: {zError:F3}\n" +
                    $"AxisScale: {(useAxisScaleCorrection ? 1 : 0)}  Xs:{xAxisScale:F3}  Ys:{yAxisScale:F3}\n" +
                    $"Yaw: {yawWrappedDeg:F1}° | unwrap: {yawUnwrappedDeg:F1}° | rate: {yawRateDps:F1}°/s\n" +
                    $"UDP: {sendMode} {targetIp}:{targetPort} src:{sourceId} rate:{sendRateHz:F0}Hz\n" +
                    $"{UdpMeasurementCommandReceiver.GetGlobalDebugLine()}\n" +
                    $"CPU: {cpuToScreenMapping} | MX:{mirrorCpuX} MY:{mirrorCpuY}\n" +
                    $"Zmirror X:{mirrorZScreenX} Y:{mirrorZScreenY} | Zinv:{invertZAxisDirection}\n" +
                    $"AutoDM:{autoDeadmanMode} dmEff:{effectiveDeadmanForDebug}\n" +
                    $"OperatorCtrl:{allowOperatorControl} deadman:{deadmanPressed} speed:{speedPercent}%";


                _latestPoseValid = true;
                _lastValidPoseTime = now;

                _latestX = x;
                _latestY = y;
                _latestZ = zError;
                _latestYawWrappedDeg = yawWrappedDeg;
                _latestYawUnwrappedDeg = yawUnwrappedDeg;
                _latestYawRateDps = yawRateDps;
                _latestFpsHz = fpsHz;
            }
            else
            {
                MarkYawInvalid();

                _debugText =
                    $"BUILD: {BUILD_MARK}\n" +
                    $"Szukam tagów...\n" +
                    $"ID{referenceTagId}: {(tRef.HasValue ? "OK" : "SZUKAM")} | " +
                    $"ID{carTagId}: {(tCar.HasValue ? "OK" : "SZUKAM")}\n" +
                    $"Wykryte: {foundCount} | CPU: {w}x{h}\n" +
                    $"UDP: {sendMode} {targetIp}:{targetPort} src:{sourceId}\n" +
                    $"{UdpMeasurementCommandReceiver.GetGlobalDebugLine()}\n" +
                    $"CPU: {cpuToScreenMapping} | MX:{mirrorCpuX} MY:{mirrorCpuY}";

                _latestPoseValid = false;

                _latestX = 0f;
                _latestY = 0f;
                _latestZ = 0f;
                _latestYawWrappedDeg = 0f;
                _latestYawUnwrappedDeg = 0f;
                _latestYawRateDps = 0f;
                _latestFpsHz = fpsHz;
            }
        }
    }

    private void ApplyAxisScaleCorrection(ref float x, ref float y)
    {
        if (!useAxisScaleCorrection)
            return;

        x *= xAxisScale;
        y *= yAxisScale;
    }

    float NormalizeAngle(float angle)
    {
        while (angle > 180f)
            angle -= 360f;

        while (angle < -180f)
            angle += 360f;

        return angle;
    }

    float UpdateYawUnwrap(float yawWrappedDeg)
    {
        float now = Time.realtimeSinceStartup;

        if (!_yawHistoryValid)
        {
            float previousEquivalentWrapped = NormalizeAngle(_yawUnwrappedDeg);
            float deltaToCurrent = Mathf.DeltaAngle(previousEquivalentWrapped, yawWrappedDeg);

            _yawUnwrappedDeg += deltaToCurrent;

            _prevYawWrappedDeg = yawWrappedDeg;
            _prevYawUnwrappedTime = now;
            _yawRateDps = 0f;
            _yawHistoryValid = true;

            return _yawUnwrappedDeg;
        }

        float deltaYaw = Mathf.DeltaAngle(_prevYawWrappedDeg, yawWrappedDeg);
        float dt = Mathf.Max(now - _prevYawUnwrappedTime, 0.0001f);

        _yawUnwrappedDeg += deltaYaw;
        _yawRateDps = deltaYaw / dt;

        _prevYawWrappedDeg = yawWrappedDeg;
        _prevYawUnwrappedTime = now;

        return _yawUnwrappedDeg;
    }

    void MarkYawInvalid()
    {
        _yawHistoryValid = false;
        _yawRateDps = 0f;
    }

    public void SaveUserSettings()
    {
        PlayerPrefs.SetInt(PrefUdpSendMode, (int)sendMode);
        PlayerPrefs.SetString(PrefTargetIp, targetIp);
        PlayerPrefs.SetInt(PrefTargetPort, targetPort);
        PlayerPrefs.SetInt(PrefSourceId, sourceId);
        PlayerPrefs.SetFloat(PrefSendRateHz, sendRateHz);
        PlayerPrefs.SetInt(PrefAllowOperatorControl, allowOperatorControl ? 1 : 0);

        PlayerPrefs.SetFloat(PrefFocalScale, focalScale);
        PlayerPrefs.SetFloat(PrefMeasurementScale, measurementScale);
        PlayerPrefs.SetInt(PrefCpuMapping, (int)cpuToScreenMapping);
        PlayerPrefs.SetInt(PrefMirrorCpuX, mirrorCpuX ? 1 : 0);
        PlayerPrefs.SetInt(PrefMirrorCpuY, mirrorCpuY ? 1 : 0);

        PlayerPrefs.SetInt(PrefUdpOutputEnabled, udpOutputEnabled ? 1 : 0);
        PlayerPrefs.SetFloat(PrefMeasurementDurationSec, measurementDurationSec);
        PlayerPrefs.SetFloat(PrefMeasurementCountdownSec, measurementCountdownSec);
        PlayerPrefs.SetInt(PrefUseAxisScaleCorrection, useAxisScaleCorrection ? 1 : 0);
        PlayerPrefs.SetFloat(PrefXAxisScale, xAxisScale);
        PlayerPrefs.SetFloat(PrefYAxisScale, yAxisScale);

        PlayerPrefs.SetInt(PrefAutoDeadmanMode, (int)autoDeadmanMode);
        PlayerPrefs.SetInt(PrefAutoDeadmanOnlyDuringMeasurement, autoDeadmanOnlyDuringMeasurement ? 1 : 0);
        PlayerPrefs.SetFloat(PrefAutoDeadmanOnSec, autoDeadmanOnSec);
        PlayerPrefs.SetFloat(PrefAutoDeadmanOffSec, autoDeadmanOffSec);

        PlayerPrefs.Save();

        Debug.Log("[Settings] Saved user settings.");
    }

    public void LoadUserSettings()
    {
        sendMode = (UdpSendMode)PlayerPrefs.GetInt(PrefUdpSendMode, (int)sendMode);
        targetIp = PlayerPrefs.GetString(PrefTargetIp, targetIp);
        targetPort = PlayerPrefs.GetInt(PrefTargetPort, targetPort);
        sourceId = PlayerPrefs.GetInt(PrefSourceId, sourceId);
        sendRateHz = PlayerPrefs.GetFloat(PrefSendRateHz, sendRateHz);
        allowOperatorControl = PlayerPrefs.GetInt(PrefAllowOperatorControl, allowOperatorControl ? 1 : 0) != 0;

        focalScale = PlayerPrefs.GetFloat(PrefFocalScale, focalScale);
        measurementScale = PlayerPrefs.GetFloat(PrefMeasurementScale, measurementScale);

        useAxisScaleCorrection = PlayerPrefs.GetInt(PrefUseAxisScaleCorrection,useAxisScaleCorrection ? 1 : 0) != 0;

        xAxisScale = PlayerPrefs.GetFloat(PrefXAxisScale, xAxisScale);
        yAxisScale = PlayerPrefs.GetFloat(PrefYAxisScale, yAxisScale);

        xAxisScale = Mathf.Clamp(xAxisScale, 0.5f, 2.0f);
        yAxisScale = Mathf.Clamp(yAxisScale, 0.5f, 2.0f);

        cpuToScreenMapping = (CpuToScreenMapping)PlayerPrefs.GetInt(PrefCpuMapping, (int)cpuToScreenMapping);
        mirrorCpuX = PlayerPrefs.GetInt(PrefMirrorCpuX, mirrorCpuX ? 1 : 0) != 0;
        mirrorCpuY = PlayerPrefs.GetInt(PrefMirrorCpuY, mirrorCpuY ? 1 : 0) != 0;

        sourceId = Mathf.Clamp(sourceId, 1, 4);
        targetPort = Mathf.Clamp(targetPort, 1, 65535);
        sendRateHz = Mathf.Clamp(sendRateHz, 1f, 60f);

        udpOutputEnabled = PlayerPrefs.GetInt(PrefUdpOutputEnabled, udpOutputEnabled ? 1 : 0) != 0;
        measurementDurationSec = PlayerPrefs.GetFloat(PrefMeasurementDurationSec, measurementDurationSec);
        measurementCountdownSec = PlayerPrefs.GetFloat(PrefMeasurementCountdownSec, measurementCountdownSec);

        autoDeadmanMode = (AutoDeadmanMode)PlayerPrefs.GetInt(
            PrefAutoDeadmanMode,
            (int)autoDeadmanMode
        );

        autoDeadmanOnlyDuringMeasurement = PlayerPrefs.GetInt(
            PrefAutoDeadmanOnlyDuringMeasurement,
            autoDeadmanOnlyDuringMeasurement ? 1 : 0
        ) != 0;

        autoDeadmanOnSec = PlayerPrefs.GetFloat(PrefAutoDeadmanOnSec, autoDeadmanOnSec);
        autoDeadmanOffSec = PlayerPrefs.GetFloat(PrefAutoDeadmanOffSec, autoDeadmanOffSec);

        autoDeadmanOnSec = Mathf.Clamp(autoDeadmanOnSec, 0.1f, 30f);
        autoDeadmanOffSec = Mathf.Clamp(autoDeadmanOffSec, 0.1f, 30f);

        measurementDurationSec = Mathf.Clamp(measurementDurationSec, 1f, 120f);
        measurementCountdownSec = Mathf.Clamp(measurementCountdownSec, 1f, 10f);

        Debug.Log("[Settings] Loaded user settings.");
    }

    public void StartMeasurement()
    {
        if (_measurementState != MeasurementState.Idle)
            return;

        float now = Time.realtimeSinceStartup;

        _udpStateBeforeMeasurement = udpOutputEnabled;
        udpOutputEnabled = false;

        _measurementCountdownStartTime = now;
        _lastCountdownBeepIndex = -1;

        _measurementState = MeasurementState.Countdown;

        int countdown = Mathf.CeilToInt(measurementCountdownSec);

        MeasurementStatusText = $"COUNTDOWN: {countdown}";
        MeasurementOverlayText = $"POMIAR ZA: {countdown}";

        Debug.Log($"[Measurement] Countdown started. countdown={measurementCountdownSec:F1}s duration={measurementDurationSec:F1}s");
    }

    public void StopMeasurement()
    {
        if (_measurementState == MeasurementState.Idle)
            return;

        float now = Time.realtimeSinceStartup;

        _measurementState = MeasurementState.Idle;
        udpOutputEnabled = _udpStateBeforeMeasurement;

        MeasurementStatusText = "MEAS: STOPPED";
        MeasurementOverlayText = "POMIAR PRZERWANY";
        _measurementOverlayVisibleUntil = now + 2.5f;

        PlayEndBeep();

        Debug.Log("[Measurement] Stopped manually.");
    }

private void UpdateMeasurementState(float now)
{
    if (_measurementState == MeasurementState.Idle)
    {
        MeasurementStatusText = udpOutputEnabled
            ? "UDP: ON | MEAS: IDLE"
            : "UDP: OFF | MEAS: IDLE";

        if (now >= _measurementOverlayVisibleUntil)
            MeasurementOverlayText = "";

        return;
    }

    if (_measurementState == MeasurementState.Countdown)
    {
        udpOutputEnabled = false;

        float elapsed = now - _measurementCountdownStartTime;
        int countdownTotal = Mathf.CeilToInt(measurementCountdownSec);

        int beepIndex = Mathf.FloorToInt(elapsed);

        if (beepIndex != _lastCountdownBeepIndex && beepIndex < countdownTotal)
        {
            _lastCountdownBeepIndex = beepIndex;
            PlayShortBeep();
        }

        int remaining = Mathf.Max(1, Mathf.CeilToInt(measurementCountdownSec - elapsed));

        MeasurementStatusText = $"COUNTDOWN: {remaining}";
        MeasurementOverlayText = $"POMIAR ZA: {remaining}";

        if (elapsed >= measurementCountdownSec)
        {
            _measurementLoggingStartTime = now;
            _measurementLoggingEndTime = now + measurementDurationSec;

            udpOutputEnabled = true;
            _measurementState = MeasurementState.Logging;
            
            ResetAutoDeadmanCycle();

            MeasurementStatusText = $"LOGGING: {measurementDurationSec:F1}s";
            MeasurementOverlayText = $"POMIAR TRWA: {measurementDurationSec:F1}s";

            PlayLongBeep();

            Debug.Log($"[Measurement] Logging started for {measurementDurationSec:F1}s.");
        }

        return;
    }

    if (_measurementState == MeasurementState.Logging)
    {
        udpOutputEnabled = true;

        float remaining = _measurementLoggingEndTime - now;
        float remainingClamped = Mathf.Max(0f, remaining);

        MeasurementStatusText = $"LOGGING: {remainingClamped:F1}s";
        MeasurementOverlayText = $"POMIAR TRWA: {remainingClamped:F1}s";

        if (now >= _measurementLoggingEndTime)
        {
            _measurementState = MeasurementState.Idle;
            udpOutputEnabled = _udpStateBeforeMeasurement;

            MeasurementStatusText = "MEAS: DONE";
            MeasurementOverlayText = "POMIAR ZAKOŃCZONY";
            _measurementOverlayVisibleUntil = now + 2.5f;

            PlayEndBeep();

            Debug.Log("[Measurement] Logging finished.");
        }
    }
}
    private void SetupBeepAudio()
    {
        _beepSource = GetComponent<AudioSource>();

        if (_beepSource == null)
            _beepSource = gameObject.AddComponent<AudioSource>();

        _beepSource.playOnAwake = false;
        _beepSource.volume = 1.0f;

        _beepShortClip = CreateBeepClip("beep_short", 880f, 0.10f, 0.35f);
        _beepLongClip = CreateBeepClip("beep_start", 880f, 0.45f, 0.45f);
        _beepEndClip = CreateBeepClip("beep_end", 440f, 0.25f, 0.35f);
    }

    private AudioClip CreateBeepClip(string name, float frequencyHz, float durationSec, float volume)
    {
        const int sampleRate = 44100;

        int samples = Mathf.CeilToInt(sampleRate * durationSec);
        float[] data = new float[samples];

        int fadeSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * 0.01f));

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float envelope = 1f;

            if (i < fadeSamples)
                envelope = (float)i / fadeSamples;
            else if (i > samples - fadeSamples)
                envelope = (float)(samples - i) / fadeSamples;

            envelope = Mathf.Clamp01(envelope);

            data[i] = Mathf.Sin(2f * Mathf.PI * frequencyHz * t) * volume * envelope;
        }

        AudioClip clip = AudioClip.Create(name, samples, 1, sampleRate, false);
        clip.SetData(data, 0);

        return clip;
    }

    private void PlayShortBeep()
    {
        if (_beepSource != null && _beepShortClip != null)
            _beepSource.PlayOneShot(_beepShortClip);
    }

    private void PlayLongBeep()
    {
        if (_beepSource != null && _beepLongClip != null)
            _beepSource.PlayOneShot(_beepLongClip);
    }

    private void PlayEndBeep()
    {
        if (_beepSource != null && _beepEndClip != null)
            _beepSource.PlayOneShot(_beepEndClip);
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
        UnsubscribeCameraFrames();

        _detector?.Dispose();
        _telemetry?.Dispose();

        _detector = null;
        _telemetry = null;

    }
}