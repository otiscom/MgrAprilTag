using UnityEngine;

/// <summary>
/// Odpowiada wyłącznie za rysowanie interfejsu diagnostycznego na ekranie:
/// - przycisk kalibracji,
/// - menu kalibracyjne,
/// - panel DEADMAN + speed limit,
/// - zielony debug box z aktualnymi danymi.
///
/// Ta klasa NIE liczy pozycji AprilTagów i NIE wysyła UDP.
/// Dostaje referencję do AprilTagMvpTracker i tylko zmienia publiczne parametry trackera.
/// </summary>
public sealed class TrackerGuiOverlay
{
    private readonly GUIContent _debugContent = new GUIContent();

    private readonly Color _menuBgColor = new Color(0f, 0f, 0f, 0.95f);
    private readonly Color _panelBgColor = new Color(0f, 0f, 0f, 0.75f);
    private readonly Color _debugBgColor = new Color(0f, 0f, 0f, 0.72f);
    private readonly Color _scrollBgColor = new Color(0.08f, 0.08f, 0.08f, 0.95f);

    private readonly Color _deadmanOnColor = new Color(0f, 0.75f, 0.15f, 0.95f);
    private readonly Color _deadmanOffColor = new Color(0.75f, 0f, 0f, 0.95f);

    private GUIStyle _debugStyle;
    private GUIStyle _menuStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _deadmanStyle;
    private GUIStyle _smallLabelStyle;

    private bool _isMenuOpen = false;
    private bool _showAdvancedOverlay = false;

    private Vector2 _menuScroll = Vector2.zero;

    private int _styleScreenWidth = -1;
    private int _styleScreenHeight = -1;

    private GUIStyle _topButtonStyle;
    private GUIStyle _deadmanVerticalStyle;

    private GUIStyle _textFieldStyle;
    private GUIStyle _sliderStyle;
    private GUIStyle _sliderThumbStyle;

    private bool IsPortrait()
    {
        return Screen.height > Screen.width;
    }

    private Rect GetCalibrationMenuRect()
    {
        bool portrait = IsPortrait();

        float widthFactor = portrait ? 0.56f : 0.48f;

        float menuW = Mathf.Min(760f, Screen.width * widthFactor);
        float menuH = Mathf.Min(Screen.height - 130f, 940f);

        return new Rect(Screen.width - menuW - 30f, 100f, menuW, menuH);
    }

    public void Draw(
        AprilTagMvpTracker tracker,
        string buildMark,
        string debugText,
        float lastRawDistance,
        float lastCorrectedDistance)
        {
            EnsureStyles();

            DrawCalibrationButton();

            if (_isMenuOpen)
            {
                DrawCalibrationMenu(
                    tracker,
                    buildMark,
                    lastRawDistance,
                    lastCorrectedDistance
                );
            }

            DrawDriveControlPanel(tracker);

            Rect debugRect = GetDebugBoxRect(debugText);

            DrawUdpQuickSwitch(tracker, debugRect);
            DrawMeasurementStatusBox(tracker, debugRect);
            DrawDebugBox(debugText, debugRect);
    }


    private void DrawUdpQuickSwitch(AprilTagMvpTracker tracker, Rect debugRect)
    {
        float gap = 8f;
        float w = 210f;
        float h = 50f;

        float x = debugRect.x;
        float y = debugRect.y - h - gap;

        if (tracker.ShowMeasurementOverlay)
            y -= 58f + gap;

        Rect rect = new Rect(x, y, w, h);

        GUI.color = tracker.udpOutputEnabled
            ? new Color(0f, 0.45f, 0.10f, 0.55f)
            : new Color(0.55f, 0f, 0f, 0.55f);

        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        string text = tracker.udpOutputEnabled ? "UDP ON" : "UDP OFF";

        if (GUI.Button(rect, text, _buttonStyle))
        {
            tracker.udpOutputEnabled = !tracker.udpOutputEnabled;
            tracker.SaveUserSettings();
            GUIUtility.ExitGUI();
        }
    }

    private void DrawMeasurementStatusBox(AprilTagMvpTracker tracker, Rect debugRect)
    {
        if (!tracker.ShowMeasurementOverlay)
            return;

        float gap = 8f;
        float w = Mathf.Min(360f, debugRect.width);
        float h = 52f;

        float x = debugRect.x;
        float y = debugRect.y - h - gap;

        Rect rect = new Rect(x, y, w, h);

        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(
            new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f),
            tracker.MeasurementOverlayText,
            _menuStyle
        );
    }
    private void EnsureStyles()
    {
    if (_debugStyle != null &&
        _menuStyle != null &&
        _buttonStyle != null &&
        _deadmanStyle != null &&
        _smallLabelStyle != null &&
        _topButtonStyle != null &&
        _deadmanVerticalStyle != null &&
        _textFieldStyle != null &&
        _sliderStyle != null &&
        _sliderThumbStyle != null &&
        _styleScreenWidth == Screen.width &&
        _styleScreenHeight == Screen.height)
        {
            return;
        }

        _styleScreenWidth = Screen.width;
        _styleScreenHeight = Screen.height;

        bool portrait = IsPortrait();

        int debugFont = Mathf.RoundToInt(Screen.height * (portrait ? 0.018f : 0.023f));
        int menuFont = Mathf.RoundToInt(Screen.height * (portrait ? 0.020f : 0.022f));
        int buttonFont = Mathf.RoundToInt(Screen.height * (portrait ? 0.020f : 0.022f));
        int topButtonFont = Mathf.RoundToInt(Screen.height * (portrait ? 0.022f : 0.024f));
        int deadmanFont = Mathf.RoundToInt(Screen.height * (portrait ? 0.028f : 0.030f));

        debugFont = Mathf.Clamp(debugFont, 18, 34);
        menuFont = Mathf.Clamp(menuFont, 18, 30);
        buttonFont = Mathf.Clamp(buttonFont, 18, 30);
        topButtonFont = Mathf.Clamp(topButtonFont, 20, 32);
        deadmanFont = Mathf.Clamp(deadmanFont, 24, 42);

        _debugStyle = new GUIStyle
        {
            fontSize = debugFont,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
            clipping = TextClipping.Clip
        };
        _debugStyle.normal.textColor = Color.green;
        _debugStyle.padding = new RectOffset(6, 6, 6, 6);

        _menuStyle = new GUIStyle
        {
            fontSize = menuFont,
            fontStyle = FontStyle.Normal,
            wordWrap = true,
            clipping = TextClipping.Clip
        };
        _menuStyle.normal.textColor = Color.white;
        _menuStyle.padding = new RectOffset(6, 6, 4, 4);

        _smallLabelStyle = new GUIStyle(_menuStyle)
        {
            fontSize = Mathf.Max(16, menuFont - 3),
            alignment = TextAnchor.MiddleCenter
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = buttonFont,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
            padding = new RectOffset(16, 16, 10, 10)
        };

        _topButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = topButtonFont,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
            padding = new RectOffset(16, 16, 10, 10)
        };

        _deadmanStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = deadmanFont,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
            clipping = TextClipping.Clip,
            padding = new RectOffset(22, 22, 8, 8)
        };
        _deadmanStyle.normal.textColor = Color.white;

        _deadmanVerticalStyle = new GUIStyle(_deadmanStyle)
        {
            fontSize = Mathf.Max(22, deadmanFont - 2),
            wordWrap = false,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Overflow,
            padding = new RectOffset(12, 12, 8, 8)
        };
        _textFieldStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = buttonFont,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
            padding = new RectOffset(14, 14, 8, 8)
        };

        _sliderStyle = new GUIStyle(GUI.skin.horizontalSlider)
        {
            fixedHeight = 16f,
            margin = new RectOffset(8, 8, 16, 16),
            padding = new RectOffset(0, 0, 0, 0)
        };

        _sliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
        {
            fixedWidth = portrait ? 44f : 38f,
            fixedHeight = portrait ? 44f : 38f
        };
    }


    private void DrawCalibrationButton()
    {
        Rect safe = Screen.safeArea;

        float w = IsPortrait() ? 230f : 245f;
        float h = IsPortrait() ? 66f : 80f;

        Rect buttonRect = new Rect(
            safe.xMax - w - 18f,
            safe.yMin + 18f,
            w,
            h
        );

        if (GUI.Button(buttonRect, _isMenuOpen ? "ZAMKNIJ" : "KALIBRACJA", _topButtonStyle))
        {
            _isMenuOpen = !_isMenuOpen;
            GUIUtility.ExitGUI();
        }
    }

    private void DrawCalibrationMenu(
        AprilTagMvpTracker tracker,
        string buildMark,
        float lastRawDistance,
        float lastCorrectedDistance)
    {
        Rect menuRect = GetCalibrationMenuRect();

        float menuW = menuRect.width;
        float menuH = menuRect.height;

        GUI.color = _menuBgColor;
        GUI.DrawTexture(menuRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        float scrollColumnW = 78f;

        Rect leftScrollRect = new Rect(
            menuRect.x + 10f,
            menuRect.y + 12f,
            scrollColumnW,
            menuH - 24f
        );

        DrawLeftScrollControls(leftScrollRect);

        Rect contentRect = new Rect(
            menuRect.x + scrollColumnW + 18f,
            menuRect.y + 12f,
            menuW - scrollColumnW - 32f,
            menuH - 24f
        );

        GUILayout.BeginArea(contentRect);

        _menuScroll = GUILayout.BeginScrollView(_menuScroll, false, false);

        GUILayout.Label($"BUILD: {buildMark}", _menuStyle);
        GUILayout.Space(10);

        GUILayout.Label("--- 1. Parametry AprilTag ---", _menuStyle);

        tracker.tagSize = DrawFloatSlider(
            $"TagSize: {tracker.tagSize:F3} m",
            tracker.tagSize,
            0.05f,
            0.30f
        );

        tracker.focalScale = DrawFloatSlider(
            $"FocalScale: {tracker.focalScale:F2}x",
            tracker.focalScale,
            0.4f,
            2.0f
        );

        GUILayout.Space(18);

        GUILayout.Label("--- 2. Korekta pomiaru ---", _menuStyle);

        tracker.measurementScale = DrawFloatSlider(
            $"MeasurementScale: {tracker.measurementScale:F3}x",
            tracker.measurementScale,
            0.2f,
            2.0f
        );

        tracker.knownCalibrationDistance = DrawFloatSlider(
            $"Znany dystans: {tracker.knownCalibrationDistance:F3} m",
            tracker.knownCalibrationDistance,
            0.05f,
            1.50f
        );

        GUILayout.Label($"RAW: {lastRawDistance:F3} m", _menuStyle);
        GUILayout.Label($"Po korekcie: {lastCorrectedDistance:F3} m", _menuStyle);

        if (GUILayout.Button("Ustaw scale = znany dystans / RAW", _buttonStyle, GUILayout.Height(80)))
        {
            if (lastRawDistance > 0.001f)
                tracker.measurementScale = tracker.knownCalibrationDistance / lastRawDistance;

            GUIUtility.ExitGUI();
        }

        GUILayout.Space(18);

        GUILayout.Label("--- 3. Wizualizacja ---", _menuStyle);

        tracker.drawProjectedTagBorder = DrawOnOffButton(
            "Biały obrys taga",
            tracker.drawProjectedTagBorder
        );

        GUILayout.Space(8);

        GUILayout.Label("Widoczność osi:", _menuStyle);

        tracker.showXAxis = DrawOnOffButton("Oś X / czerwona", tracker.showXAxis);
        tracker.showYAxis = DrawOnOffButton("Oś Y / zielona", tracker.showYAxis);
        tracker.showZAxis = DrawOnOffButton("Oś Z / niebieska", tracker.showZAxis);

        GUILayout.Space(12);

        GUILayout.Label("Kierunek wektorów osi, spójny z UDP:", _menuStyle);

        tracker.invertX = DrawDirectionButton(
            "Kierunek X oraz znak x_m w UDP",
            tracker.invertX
        );

        tracker.invertY = DrawDirectionButton(
            "Kierunek Y oraz znak y_m w UDP",
            tracker.invertY
        );

        tracker.invertZAxisDirection = DrawDirectionButton(
            "Kierunek Z oraz znak z_m w UDP",
            tracker.invertZAxisDirection
        );

        GUILayout.Space(12);

        string advancedState = _showAdvancedOverlay ? "ON" : "OFF";

        if (GUILayout.Button($"Zaawansowane ustawienia overlayu: {advancedState}", _buttonStyle, GUILayout.Height(80)))
        {
            _showAdvancedOverlay = !_showAdvancedOverlay;
            GUIUtility.ExitGUI();
        }

        if (_showAdvancedOverlay)
        {
            GUILayout.Space(8);

            tracker.screenOverlayScale = DrawFloatSlider(
                $"ScreenOverlayScale: {tracker.screenOverlayScale:F2}",
                tracker.screenOverlayScale,
                0.5f,
                2.0f
            );

            tracker.zSymbolThresholdPx = DrawFloatSlider(
                $"Z symbol threshold: {tracker.zSymbolThresholdPx:F1} px",
                tracker.zSymbolThresholdPx,
                0f,
                30f
            );

            tracker.zSymbolAngleThresholdDeg = DrawFloatSlider(
                $"Z angle margin: {tracker.zSymbolAngleThresholdDeg:F1}°",
                tracker.zSymbolAngleThresholdDeg,
                0f,
                20f
            );

            tracker.zAxisVisualScale = DrawFloatSlider(
                $"Z Axis Visual Scale: {tracker.zAxisVisualScale:F2}x",
                tracker.zAxisVisualScale,
                0.2f,
                3.0f
            );

            tracker.mirrorZScreenX = DrawOnOffButton(
                "Mirror Z Screen X",
                tracker.mirrorZScreenX
            );

            tracker.mirrorZScreenY = DrawOnOffButton(
                "Mirror Z Screen Y",
                tracker.mirrorZScreenY
            );

            tracker.invertZSymbol = DrawOnOffButton(
                "Odwróć symbol Z",
                tracker.invertZSymbol
            );
        }

        GUILayout.Space(18);

        GUILayout.Label("--- 4. CPU image -> ekran ---", _menuStyle);
        GUILayout.Label($"Aktualnie: {tracker.cpuToScreenMapping}", _menuStyle);

        GUILayout.BeginHorizontal();

        if (DrawMappingButton("Cover0", tracker.cpuToScreenMapping == AprilTagMvpTracker.CpuToScreenMapping.Cover0))
        {
            tracker.cpuToScreenMapping = AprilTagMvpTracker.CpuToScreenMapping.Cover0;
            GUIUtility.ExitGUI();
        }

        if (DrawMappingButton("90CW", tracker.cpuToScreenMapping == AprilTagMvpTracker.CpuToScreenMapping.Cover90CW))
        {
            tracker.cpuToScreenMapping = AprilTagMvpTracker.CpuToScreenMapping.Cover90CW;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();

        if (DrawMappingButton("90CCW", tracker.cpuToScreenMapping == AprilTagMvpTracker.CpuToScreenMapping.Cover90CCW))
        {
            tracker.cpuToScreenMapping = AprilTagMvpTracker.CpuToScreenMapping.Cover90CCW;
            GUIUtility.ExitGUI();
        }

        if (DrawMappingButton("180", tracker.cpuToScreenMapping == AprilTagMvpTracker.CpuToScreenMapping.Cover180))
        {
            tracker.cpuToScreenMapping = AprilTagMvpTracker.CpuToScreenMapping.Cover180;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        tracker.mirrorCpuX = DrawOnOffButton("Mirror CPU X", tracker.mirrorCpuX);
        tracker.mirrorCpuY = DrawOnOffButton("Mirror CPU Y", tracker.mirrorCpuY);

        GUILayout.Space(18);

        DrawUdpSection(tracker);

        GUILayout.Space(30);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawUdpSection(AprilTagMvpTracker tracker)
    {
        GUILayout.Label("--- 5. UDP Hardware Output ---", _menuStyle);

        GUILayout.Label($"Send Mode: {tracker.sendMode}", _menuStyle);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("TextAT1", _buttonStyle, GUILayout.Height(50)))
        {
            tracker.sendMode = UdpSendMode.TextAT1;
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("BinaryATB1", _buttonStyle, GUILayout.Height(50)))
        {
            tracker.sendMode = UdpSendMode.BinaryATB1;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(8);

        GUILayout.Label("Device presets:", _menuStyle);

        if (GUILayout.Button("Phone 1 Operator / Port 5005", _buttonStyle, GUILayout.Height(80)))
        {
            tracker.sourceId = 1;
            tracker.targetPort = 5005;
            tracker.allowOperatorControl = true;
            tracker.sendMode = UdpSendMode.BinaryATB1;
            tracker.SaveUserSettings();
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("Phone 2 Observer / Same Port 5005", _buttonStyle, GUILayout.Height(80)))
        {
            tracker.sourceId = 2;
            tracker.targetPort = 5005;
            tracker.allowOperatorControl = false;
            tracker.sendMode = UdpSendMode.BinaryATB1;
            tracker.SaveUserSettings();
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("Phone 2 Observer / Split Port 5006", _buttonStyle, GUILayout.Height(80)))
        {
            tracker.sourceId = 2;
            tracker.targetPort = 5006;
            tracker.allowOperatorControl = false;
            tracker.sendMode = UdpSendMode.BinaryATB1;
            tracker.SaveUserSettings();
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("Phone 2 Co-Operator / Port 5005", _buttonStyle, GUILayout.Height(80)))
        {
            tracker.sourceId = 2;
            tracker.targetPort = 5005;
            tracker.allowOperatorControl = true;
            tracker.sendMode = UdpSendMode.BinaryATB1;
            tracker.SaveUserSettings();
            GUIUtility.ExitGUI();
        }

        GUILayout.Space(8);

        GUILayout.Label("Target IP:", _menuStyle);
        tracker.targetIp = GUILayout.TextField(
            tracker.targetIp,
            _textFieldStyle,
            GUILayout.Height(66)
        );

        GUILayout.Label($"UDP Port: {tracker.targetPort}", _menuStyle);
        string portText = GUILayout.TextField(
            tracker.targetPort.ToString(),
            _textFieldStyle,
            GUILayout.Height(66)
        );

        if (int.TryParse(portText, out int parsedPort))
            tracker.targetPort = Mathf.Clamp(parsedPort, 1, 65535);

        GUILayout.Space(8);

        GUILayout.Label($"Source ID: {tracker.sourceId}", _menuStyle);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Phone 1", _buttonStyle, GUILayout.Height(50)))
        {
            tracker.sourceId = 1;
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("Phone 2", _buttonStyle, GUILayout.Height(50)))
        {
            tracker.sourceId = 2;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Phone 3", _buttonStyle, GUILayout.Height(50)))
        {
            tracker.sourceId = 3;
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("Phone 4", _buttonStyle, GUILayout.Height(50)))
        {
            tracker.sourceId = 4;
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        tracker.sourceId = Mathf.RoundToInt(
            DrawLargeHorizontalSlider(tracker.sourceId, 1f, 4f)
        );
        tracker.sourceId = Mathf.Clamp(tracker.sourceId, 1, 4);

        GUILayout.Label($"Send Rate: {tracker.sendRateHz:F0} Hz", _menuStyle);

        tracker.sendRateHz = DrawLargeHorizontalSlider(
            tracker.sendRateHz,
            1f,
            60f
        );

        tracker.udpOutputEnabled = DrawOnOffButton("UDP Output",
            tracker.udpOutputEnabled
        );

        GUILayout.Space(8);

        GUILayout.Label(tracker.MeasurementStatusText, _menuStyle);

        tracker.measurementCountdownSec = DrawFloatSlider(
            $"Countdown: {tracker.measurementCountdownSec:F0} s",
            tracker.measurementCountdownSec,
            1f,
            10f
        );

        tracker.measurementDurationSec = DrawFloatSlider(
            $"Measurement duration: {tracker.measurementDurationSec:F0} s",
            tracker.measurementDurationSec,
            1f,
            120f
        );

        DrawMeasurementMenuButton(tracker);

        tracker.allowOperatorControl = DrawOnOffButton($"Operator Control / deadman source",tracker.allowOperatorControl);

        tracker.debugBinaryUdp = DrawDebugBinaryButton(tracker.debugBinaryUdp);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("SAVE SETTINGS", _buttonStyle, GUILayout.Height(80)))
        {
            tracker.SaveUserSettings();
            GUIUtility.ExitGUI();
        }

        if (GUILayout.Button("LOAD SETTINGS", _buttonStyle, GUILayout.Height(80)))
        {
            tracker.LoadUserSettings();
            GUIUtility.ExitGUI();
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(8);

        GUILayout.Label($"Deadman: {(tracker.deadmanPressed ? 1 : 0)}", _menuStyle);
        GUILayout.Label($"Speed: {tracker.speedPercent}%", _menuStyle);
    }

    private void DrawMeasurementMenuButton(AprilTagMvpTracker tracker)
    {
        bool measurementActive = tracker.IsMeasurementRunning;

        string text = measurementActive ? "STOP" : "MEASURE";

        Color oldBg = GUI.backgroundColor;

        GUI.backgroundColor = measurementActive
            ? new Color(0.90f, 0.12f, 0.12f, 1f)
            : new Color(0.35f, 0.35f, 0.35f, 1f);

        if (GUILayout.Button(text, _buttonStyle, GUILayout.Height(76)))
        {
            if (measurementActive)
                tracker.StopMeasurement();
            else
                tracker.StartMeasurement();

            GUIUtility.ExitGUI();
        }

        GUI.backgroundColor = oldBg;
    }

    private bool DrawDebugBinaryButton(bool currentValue)
    {
        string text = currentValue
            ? "Debug Binary Log: ON"
            : "Debug Binary Log: OFF";

        Color oldBg = GUI.backgroundColor;

        GUI.backgroundColor = currentValue
            ? new Color(0.10f, 0.45f, 0.10f, 1f)
            : new Color(0.35f, 0.35f, 0.35f, 1f);

        if (GUILayout.Button(text, _buttonStyle, GUILayout.Height(76)))
        {
            currentValue = !currentValue;
        }

        GUI.backgroundColor = oldBg;

        return currentValue;
    }

    private void DrawLeftScrollControls(Rect rect)
    {
        GUI.color = _scrollBgColor;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        Rect upRect = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 74f);
        Rect downRect = new Rect(rect.x + 8f, rect.yMax - 82f, rect.width - 16f, 74f);
        Rect labelRect = new Rect(rect.x + 6f, rect.y + 92f, rect.width - 12f, rect.height - 184f);

        if (GUI.RepeatButton(upRect, "▲", _buttonStyle))
            _menuScroll.y = Mathf.Max(0f, _menuScroll.y - 18f);

        if (GUI.RepeatButton(downRect, "▼", _buttonStyle))
            _menuScroll.y += 18f;

        GUI.Label(labelRect, "SCROLL", _smallLabelStyle);
    }
    private float DrawLargeHorizontalSlider(float value, float min, float max)
    {
        GUILayout.Space(4);

        value = GUILayout.HorizontalSlider(
            value,
            min,
            max,
            _sliderStyle,
            _sliderThumbStyle,
            GUILayout.Height(64)
        );

        GUILayout.Space(6);

        return value;
    }
    private float DrawFloatSlider(string label, float value, float min, float max)
    {
        GUILayout.Label(label, _menuStyle);

        value = DrawLargeHorizontalSlider(value, min, max);

        return value;
    }

    private bool DrawOnOffButton(string label, bool currentValue)
    {
        string state = currentValue ? "ON" : "OFF";
        string text = $"{label}: {state}";

        if (GUILayout.Button(text, _buttonStyle, GUILayout.Height(80)))
            currentValue = !currentValue;

        return currentValue;
    }

    private bool DrawDirectionButton(string label, bool inverted)
    {
        string state = inverted ? "ODWRÓCONY" : "NORMALNY";
        string text = $"{label}: {state}";

        if (GUILayout.Button(text, _buttonStyle, GUILayout.Height(80)))
            inverted = !inverted;

        return inverted;
    }

    private bool DrawMappingButton(string label, bool isActive)
    {
        string text = isActive ? $"[{label}]" : label;
        return GUILayout.Button(text, _buttonStyle, GUILayout.Height(64));
    }

    private void DrawRotatedLabel(Rect rect, string text, GUIStyle style, float angleDeg)
    {
        Matrix4x4 oldMatrix = GUI.matrix;
        Vector2 pivot = rect.center;

        GUIUtility.RotateAroundPivot(angleDeg, pivot);
        GUI.Label(rect, text, style);

        GUI.matrix = oldMatrix;
    }

    private bool DrawMeasurementButtonRect(Rect rect, AprilTagMvpTracker tracker)
    {
        bool measurementActive = tracker.IsMeasurementRunning;

        string text = measurementActive ? "STOP" : "MEASURE";

        Color oldBg = GUI.backgroundColor;

        GUI.backgroundColor = measurementActive
            ? new Color(0.90f, 0.12f, 0.12f, 1f)
            : new Color(0.35f, 0.35f, 0.35f, 1f);

        bool clicked = GUI.Button(rect, text, _buttonStyle);

        GUI.backgroundColor = oldBg;

        if (clicked)
        {
            if (measurementActive)
                tracker.StopMeasurement();
            else
                tracker.StartMeasurement();

            GUIUtility.ExitGUI();
        }

        return clicked;
    }

    private void DrawDriveControlPanel(AprilTagMvpTracker tracker)
    {
        if (IsPortrait())
            DrawDriveControlPanelPortrait(tracker);
        else
            DrawDriveControlPanelLandscape(tracker);
    }

    private void DrawDriveControlPanelLandscape(AprilTagMvpTracker tracker)
    {
        Rect safe = Screen.safeArea;

        float margin = 18f;
        float topY = safe.yMin + 24f;

        // Lewy panel: measurement + speed, trochę węższy,
        // żeby deadman miał więcej miejsca.
        float leftPanelW = 245f;
        float leftPanelH = 165f;

        Rect leftPanelRect = new Rect(
            safe.xMin + margin,
            topY,
            leftPanelW,
            leftPanelH
        );

        GUI.color = _panelBgColor;
        GUI.DrawTexture(leftPanelRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        Rect measurementRect = new Rect(
            leftPanelRect.x + 10f,
            leftPanelRect.y + 10f,
            leftPanelRect.width - 20f,
            62f
        );

        DrawMeasurementButtonRect(measurementRect, tracker);

        Rect labelRect = new Rect(
            leftPanelRect.x + 12f,
            measurementRect.yMax + 10f,
            leftPanelRect.width - 24f,
            30f
        );

        GUI.Label(labelRect, $"Speed: {tracker.speedPercent}%", _menuStyle);

        Rect sliderRect = new Rect(
            leftPanelRect.x + 12f,
            labelRect.yMax + 8f,
            leftPanelRect.width - 24f,
            54f
        );

        float speedSliderValue = GUI.HorizontalSlider(
            sliderRect,
            tracker.speedPercent,
            0f,
            100f,
            _sliderStyle,
            _sliderThumbStyle
        );

        tracker.speedPercent = Mathf.RoundToInt(speedSliderValue);

        // Deadman: maksymalnie szeroki do menu.
        float rightLimit = _isMenuOpen
            ? GetCalibrationMenuRect().x - 10f
            : safe.xMax - 18f;

        float deadmanX = leftPanelRect.xMax + 10f;
        float deadmanY = topY;
        float deadmanH = 160f;
        float deadmanW = Mathf.Max(320f, rightLimit - deadmanX);

        Rect deadmanRect = new Rect(deadmanX, deadmanY, deadmanW, deadmanH);

        tracker.deadmanPressed = IsPointerDownInside(deadmanRect);

        GUI.color = tracker.deadmanPressed ? _deadmanOnColor : _deadmanOffColor;
        GUI.DrawTexture(deadmanRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        Rect deadmanTextRect = new Rect(
            deadmanRect.x + 18f,
            deadmanRect.y + 8f,
            deadmanRect.width - 36f,
            deadmanRect.height - 16f
        );

        GUI.Label(
            deadmanTextRect,
            tracker.deadmanPressed ? "DEADMAN ON" : "TRZYMAJ, ABY JECHAĆ",
            _deadmanStyle
        );
    }

    private void DrawDriveControlPanelPortrait(AprilTagMvpTracker tracker)
    {
        Rect safe = Screen.safeArea;

        float margin = 12f;

        // Górny panel: MEASURE/STOP + speed.
        // Szerszy i wyższy, żeby była widoczna wartość speed.
        float topPanelX = safe.xMin + margin;
        float topPanelY = safe.yMin + 14f;
        float topPanelW = 260f;
        float topPanelH = 225f;

        Rect topPanelRect = new Rect(
            topPanelX,
            topPanelY,
            topPanelW,
            topPanelH
        );

        GUI.color = _panelBgColor;
        GUI.DrawTexture(topPanelRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        Rect measurementRect = new Rect(
            topPanelRect.x + 10f,
            topPanelRect.y + 10f,
            topPanelRect.width - 20f,
            58f
        );

        DrawMeasurementButtonRect(measurementRect, tracker);

        Rect labelRect = new Rect(
            topPanelRect.x + 14f,
            measurementRect.yMax + 16f,
            topPanelRect.width - 28f,
            42f
        );

        // Krótszy napis, żeby zawsze mieściła się liczba.
        GUI.Label(labelRect, $"Speed: {tracker.speedPercent}%", _menuStyle);

        Rect sliderRect = new Rect(
            topPanelRect.x + 18f,
            labelRect.yMax + 12f,
            topPanelRect.width - 36f,
            58f
        );

        float speedSliderValue = GUI.HorizontalSlider(
            sliderRect,
            tracker.speedPercent,
            0f,
            100f,
            _sliderStyle,
            _sliderThumbStyle
        );

        tracker.speedPercent = Mathf.RoundToInt(speedSliderValue);

        // Deadman pionowy po lewej stronie.
        Rect debugRect = GetDebugBoxRect("");
        float udpAndStatusReserved = tracker.ShowMeasurementOverlay ? 126f : 68f;

        float deadmanX = safe.xMin + margin;
        float deadmanY = topPanelRect.yMax + 14f;
        float deadmanW = 150f;
        float deadmanBottom = debugRect.y - udpAndStatusReserved - 14f;

        float deadmanH = deadmanBottom - deadmanY;
        deadmanH = Mathf.Clamp(deadmanH, 260f, safe.height * 0.60f);

        Rect deadmanRect = new Rect(
            deadmanX,
            deadmanY,
            deadmanW,
            deadmanH
        );

        tracker.deadmanPressed = IsPointerDownInside(deadmanRect);

        GUI.color = tracker.deadmanPressed ? _deadmanOnColor : _deadmanOffColor;
        GUI.DrawTexture(deadmanRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        string deadmanText = tracker.deadmanPressed
            ? "DEADMAN ON"
            : "TRZYMAJ, ABY JECHAĆ";

        Rect rotatedTextRect = new Rect(
            deadmanRect.center.x - deadmanRect.height * 0.5f,
            deadmanRect.center.y - deadmanRect.width * 0.5f,
            deadmanRect.height,
            deadmanRect.width
        );

        DrawRotatedLabel(rotatedTextRect, deadmanText, _deadmanVerticalStyle, 90f);
    }

    private Rect GetDebugBoxRect(string debugText)
    {
        bool portrait = IsPortrait();
        Rect safe = Screen.safeArea;

        float paddingX = 18f;
        float paddingY = 16f;
        float bottomGap = 34f;

        float x = portrait ? safe.xMin + 24f : 24f;
        float width = portrait
            ? safe.width - 48f
            : Mathf.Min(Screen.width * 0.76f - 24f, 1350f);

        width = Mathf.Max(width, 320f);

        _debugContent.text = debugText;

        float textHeight = _debugStyle.CalcHeight(_debugContent, width - paddingX * 2f);
        float minHeight = portrait ? 285f : 260f;
        float maxHeight = safe.height * (portrait ? 0.36f : 0.42f);
        float height = Mathf.Clamp(textHeight + paddingY * 2f + 18f, minHeight, maxHeight);

        float y = Screen.height - safe.yMin - bottomGap - height;

        return new Rect(x, y, width, height);
    }

    private void DrawDebugBox(string debugText, Rect debugRect)
    {
        float paddingX = 18f;
        float paddingY = 16f;

        _debugContent.text = debugText;

        GUI.color = _debugBgColor;
        GUI.DrawTexture(debugRect, Texture2D.whiteTexture);

        GUI.color = Color.white;
        GUI.Label(
            new Rect(
                debugRect.x + paddingX,
                debugRect.y + paddingY,
                debugRect.width - paddingX * 2f,
                debugRect.height - paddingY * 2f
            ),
            _debugContent,
            _debugStyle
        );
    }

    private bool IsPointerDownInside(Rect rect)
    {
        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);

            if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                return false;

            Vector2 pointerGuiPos = new Vector2(
                touch.position.x,
                Screen.height - touch.position.y
            );

            return rect.Contains(pointerGuiPos);
        }

        if (Input.GetMouseButton(0))
        {
            Vector2 pointerGuiPos = new Vector2(
                Input.mousePosition.x,
                Screen.height - Input.mousePosition.y
            );

            return rect.Contains(pointerGuiPos);
        }

        return false;
    }
}