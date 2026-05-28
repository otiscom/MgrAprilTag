using AprilTag;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class AprilTagMvpTracker : MonoBehaviour
{
    [Header("Detekcja AprilTag")]
    public ARCameraManager camManager;
    public float tagSize = 0.13f;
    public int referenceTagId = 0;
    public int carTagId = 1;

    [Header("UDP")]
    public string pcIp = "192.168.0.19";
    public int udpPort = 5005;
    public float sendRateHz = 30f;

    [Header("Debug")]
    public bool sendDebugUdp = false;
    public bool invertYaw = false;

    private TagDetector _detector;
    private UdpClient _udp;
    private IPEndPoint _endPoint;

    private GUIStyle _guiStyle;
    private string _debugText = "Czekam na tagi...";

    private float _lastSendTime = 0f;
    private float _lastDebugTime = 0f;

    void Start()
    {
        if (camManager == null)
            camManager = FindObjectOfType<ARCameraManager>();

        try
        {
            _udp = new UdpClient();
            _endPoint = new IPEndPoint(IPAddress.Parse(pcIp), udpPort);
            Debug.Log($"UDP OK: {pcIp}:{udpPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"UDP Error: {e.Message}");
        }

        _guiStyle = new GUIStyle();
        _guiStyle.fontSize = 42;
        _guiStyle.fontStyle = FontStyle.Bold;
        _guiStyle.normal.textColor = Color.green;
        _guiStyle.alignment = TextAnchor.UpperLeft;
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
        if (!camManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            _debugText = "Brak CPU image";
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

            if (_detector == null)
            {
                _detector = new TagDetector(w, h);
                Debug.Log($"AprilTag detector OK: {w}x{h}");
            }

            float fovRad = GetHorizontalFovRad(w, h);

            _detector.ProcessImage(colorBuf.AsReadOnlySpan(), fovRad, tagSize);

            Matrix4x4? tRef = null;
            Matrix4x4? tCar = null;

            float refRawYaw = 0f;
            float carRawYaw = 0f;

            int foundCount = 0;

            foreach (var tag in _detector.DetectedTags)
            {
                foundCount++;

                Matrix4x4 pose = Matrix4x4.TRS(
                    tag.Position,
                    tag.Rotation,
                    Vector3.one
                );

                float rawYaw = GetPlaneYawDeg(pose);

                if (tag.ID == referenceTagId)
                {
                    tRef = pose;
                    refRawYaw = rawYaw;
                }

                if (tag.ID == carTagId)
                {
                    tCar = pose;
                    carRawYaw = rawYaw;
                }
            }

            float fps = 1f / Time.deltaTime;

            if (tRef.HasValue && tCar.HasValue)
            {
                Matrix4x4 rel = tRef.Value.inverse * tCar.Value;

                Vector3 pos = GetPosition(rel);

                float x = pos.x;
                float y = pos.y;
                float zError = pos.z;

                float yaw = GetPlaneYawDeg(rel);

                if (invertYaw)
                    yaw = -yaw;

                yaw = NormalizeAngle(yaw);

                _debugText =
                    $"ID0: OK   ID1: OK\n" +
                    $"X: {x:F3} m   Y: {y:F3} m\n" +
                    $"Yaw rel: {yaw:F1} deg\n" +
                    $"Yaw ID0: {refRawYaw:F1}   ID1: {carRawYaw:F1}\n" +
                    $"Zerr: {zError:F3} m\n" +
                    $"FPS: {fps:F1}";

                if (Time.time - _lastSendTime >= 1f / sendRateHz)
                {
                    string data = $"{Time.time:F2},{x:F3},{y:F3},{yaw:F1},{fps:F1}";
                    SendData(data);
                    _lastSendTime = Time.time;
                }

                if (sendDebugUdp && Time.time - _lastDebugTime > 1f)
                {
                    SendData($"DEBUG,tags={foundCount},refYaw={refRawYaw:F1},carYaw={carRawYaw:F1}");
                    _lastDebugTime = Time.time;
                }
            }
            else
            {
                bool hasRef = tRef.HasValue;
                bool hasCar = tCar.HasValue;

                _debugText =
                    $"Szukam tagow...\n" +
                    $"ID0/ref: {(hasRef ? "OK" : "BRAK")}\n" +
                    $"ID1/car: {(hasCar ? "OK" : "BRAK")}\n" +
                    $"Wykryto: {foundCount}\n" +
                    $"FPS: {fps:F1}";
            }
        }
    }

    float GetHorizontalFovRad(int imageWidth, int imageHeight)
    {
        float vFovDeg = Camera.main != null ? Camera.main.fieldOfView : 60f;
        float vFovRad = vFovDeg * Mathf.Deg2Rad;

        float aspect = (float)imageWidth / imageHeight;
        float hFovRad = 2f * Mathf.Atan(Mathf.Tan(vFovRad / 2f) * aspect);

        return hFovRad;
    }

    Vector3 GetPosition(Matrix4x4 matrix)
    {
        return new Vector3(matrix.m03, matrix.m13, matrix.m23);
    }

    float GetPlaneYawDeg(Matrix4x4 matrix)
    {
        Vector4 col = matrix.GetColumn(0);

        Vector3 xAxis = new Vector3(
            col.x,
            col.y,
            col.z
        );

        float yaw = Mathf.Atan2(xAxis.y, xAxis.x) * Mathf.Rad2Deg;

        return NormalizeAngle(yaw);
    }

    float NormalizeAngle(float angle)
    {
        while (angle > 180f)
            angle -= 360f;

        while (angle < -180f)
            angle += 360f;

        return angle;
    }

    void SendData(string msg)
    {
        try
        {
            if (_udp == null || _endPoint == null)
                return;

            byte[] bytes = Encoding.UTF8.GetBytes(msg);
            _udp.Send(bytes, bytes.Length, _endPoint);
        }
        catch
        {
            // Dla MVP ignorujemy chwilowe b³êdy UDP.
        }
    }

    void OnGUI()
    {
        float width = Screen.width * 0.88f;
        float height = 330f;

        float x = (Screen.width - width) / 2f;
        float y = Screen.height * 0.58f;

        Rect bgRect = new Rect(x, y, width, height);
        Rect textRect = new Rect(x + 25f, y + 20f, width - 50f, height - 40f);

        Color previousColor = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(bgRect, Texture2D.whiteTexture);

        GUI.color = Color.green;
        GUI.Label(textRect, _debugText, _guiStyle);

        GUI.color = previousColor;
    }

    void OnDestroy()
    {
        _detector?.Dispose();
        _udp?.Close();
    }
}