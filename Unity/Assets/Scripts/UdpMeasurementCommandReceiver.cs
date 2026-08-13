using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class UdpMeasurementCommandReceiver : MonoBehaviour
{
    public static UdpMeasurementCommandReceiver Instance { get; private set; }

    [Header("UDP Command Receiver")]
    public int listenPort = 5012;
    public bool autoStart = true;

    [Tooltip("Tracker, ktorym steruje zdalny START/STOP. Jesli puste, zostanie znaleziony automatycznie.")]
    public AprilTagMvpTracker tracker;

    [Header("Debug")]
    public bool showCommandOverlay = false;

    private UdpClient _udp;
    private Thread _thread;
    private volatile bool _running;

    private readonly object _lock = new object();

    private string _pendingCommand = "";
    private string _lastCommand = "";
    private string _lastSender = "";
    private string _status = "not started";
    private int _rxCount = 0;
    private float _lastApplyTime = -999f;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        if (tracker == null)
            tracker = FindObjectOfType<AprilTagMvpTracker>();

        if (autoStart)
            StartReceiver();
    }

    public void StartReceiver()
    {
        if (_running)
            return;

        try
        {
            _udp = new UdpClient();

            _udp.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true
            );

            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            _udp.Client.ReceiveTimeout = 250;

            _running = true;

            _thread = new Thread(ReceiveLoop);
            _thread.IsBackground = true;
            _thread.Start();

            SetStatus($"listening on {listenPort}");
            Debug.Log($"[UDP CMD] Listening on port {listenPort}");
        }
        catch (Exception ex)
        {
            SetStatus($"START FAILED: {ex.Message}");
            Debug.LogError($"[UDP CMD] Start failed: {ex}");
        }
    }

    private void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref remote);
                string cmd = Encoding.ASCII.GetString(data).Trim();

                lock (_lock)
                {
                    _pendingCommand = cmd;
                    _lastCommand = cmd;
                    _lastSender = remote.ToString();
                    _rxCount++;
                    _status = $"RX {_rxCount}: {cmd}";
                }
            }
            catch (SocketException)
            {
                // timeout - normalne
            }
            catch (ObjectDisposedException)
            {
                // normalne przy zamykaniu
            }
            catch (Exception ex)
            {
                SetStatus($"RX ERROR: {ex.Message}");
            }
        }
    }

    void Update()
    {
        string cmd = "";

        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_pendingCommand))
            {
                cmd = _pendingCommand;
                _pendingCommand = "";
            }
        }

        if (!string.IsNullOrEmpty(cmd))
            ApplyCommand(cmd);
    }

    private void ApplyCommand(string cmd)
    {
        if (tracker == null)
            tracker = FindObjectOfType<AprilTagMvpTracker>();

        if (tracker == null)
        {
            SetStatus("NO TRACKER");
            Debug.LogWarning("[UDP CMD] No AprilTagMvpTracker found.");
            return;
        }

        string normalized = cmd.Trim();

        if (normalized == "1" || normalized.Equals("START", StringComparison.OrdinalIgnoreCase))
        {
            tracker.ResetAutoDeadmanCycle();
            tracker.StartMeasurement();
            _lastApplyTime = Time.realtimeSinceStartup;

            SetStatus($"APPLY START simple: {normalized}");
            Debug.Log("[UDP CMD] APPLY START simple");
            return;
        }

        if (normalized == "0" || normalized.Equals("STOP", StringComparison.OrdinalIgnoreCase))
        {
            tracker.StopMeasurement();
            _lastApplyTime = Time.realtimeSinceStartup;

            SetStatus($"APPLY STOP simple: {normalized}");
            Debug.Log("[UDP CMD] APPLY STOP simple");
            return;
        }

        Dictionary<string, string> kv = ParseCommand(normalized);

        string action = GetString(kv, "cmd", "");
        if (string.IsNullOrEmpty(action))
            action = GetString(kv, "action", "");

        if (action.Equals("START", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetFloat(kv, "duration", out float duration))
                tracker.measurementDurationSec = Mathf.Clamp(duration, 1f, 120f);

            if (TryGetFloat(kv, "countdown", out float countdown))
                tracker.measurementCountdownSec = Mathf.Clamp(countdown, 1f, 10f);

            string dm = GetString(kv, "dm", "");

            if (dm.Equals("off", StringComparison.OrdinalIgnoreCase))
                tracker.autoDeadmanMode = AprilTagMvpTracker.AutoDeadmanMode.Off;
            else if (dm.Equals("hold", StringComparison.OrdinalIgnoreCase))
                tracker.autoDeadmanMode = AprilTagMvpTracker.AutoDeadmanMode.Hold;
            else if (dm.Equals("cycle", StringComparison.OrdinalIgnoreCase))
                tracker.autoDeadmanMode = AprilTagMvpTracker.AutoDeadmanMode.Cycle;

            if (TryGetFloat(kv, "on", out float onSec))
                tracker.autoDeadmanOnSec = Mathf.Clamp(onSec, 0.1f, 30f);

            if (TryGetFloat(kv, "off", out float offSec))
                tracker.autoDeadmanOffSec = Mathf.Clamp(offSec, 0.1f, 30f);

            tracker.ResetAutoDeadmanCycle();
            tracker.StartMeasurement();
            _lastApplyTime = Time.realtimeSinceStartup;

            SetStatus(
                $"APPLY START dur={tracker.measurementDurationSec:F0}s " +
                $"cnt={tracker.measurementCountdownSec:F0}s dm={tracker.autoDeadmanMode}"
            );

            Debug.Log(
                $"[UDP CMD] APPLY START duration={tracker.measurementDurationSec:F1}s " +
                $"countdown={tracker.measurementCountdownSec:F1}s dm={tracker.autoDeadmanMode}"
            );

            return;
        }

        if (action.Equals("STOP", StringComparison.OrdinalIgnoreCase))
        {
            tracker.StopMeasurement();
            _lastApplyTime = Time.realtimeSinceStartup;

            SetStatus("APPLY STOP");
            Debug.Log("[UDP CMD] APPLY STOP");
            return;
        }

        SetStatus($"UNKNOWN: {cmd}");
        Debug.LogWarning($"[UDP CMD] Unknown command: {cmd}");
    }

    private void SetStatus(string status)
    {
        lock (_lock)
        {
            _status = status;
        }
    }

    public string GetDebugLine()
    {
        lock (_lock)
        {
            string run = _running ? "ON" : "OFF";
            string last = string.IsNullOrEmpty(_lastCommand) ? "-" : _lastCommand;
            string sender = string.IsNullOrEmpty(_lastSender) ? "-" : _lastSender;

            string applyAge = _lastApplyTime > 0f
                ? $"{Time.realtimeSinceStartup - _lastApplyTime:F1}s ago"
                : "-";

            return $"CMD:{listenPort} {run} rx:{_rxCount} | {_status} | last:{last}";
        }
    }

    public static string GetGlobalDebugLine()
    {
        if (Instance == null)
            return "CMD: no receiver";

        return Instance.GetDebugLine();
    }

    private static Dictionary<string, string> ParseCommand(string cmd)
    {
        Dictionary<string, string> result = new Dictionary<string, string>();

        string[] parts = cmd.Split(
            new[] { ';', ',', ' ' },
            StringSplitOptions.RemoveEmptyEntries
        );

        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            int eq = part.IndexOf('=');

            if (eq > 0)
            {
                string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                string value = part.Substring(eq + 1).Trim();
                result[key] = value;
            }
            else if (!result.ContainsKey("cmd"))
            {
                result["cmd"] = part;
            }
        }

        return result;
    }

    private static string GetString(Dictionary<string, string> kv, string key, string fallback)
    {
        if (kv.TryGetValue(key.ToLowerInvariant(), out string value))
            return value;

        return fallback;
    }

    private static bool TryGetFloat(Dictionary<string, string> kv, string key, out float value)
    {
        value = 0f;

        if (!kv.TryGetValue(key.ToLowerInvariant(), out string text))
            return false;

        text = text.Trim().Replace(',', '.');

        return float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value
        );
    }

    void OnDestroy()
    {
        _running = false;

        if (Instance == this)
            Instance = null;

        try
        {
            _udp?.Close();
        }
        catch
        {
            // ignore
        }

        _udp = null;
    }

   
}