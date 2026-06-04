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
    // Jeden obiekt GUIContent używany wielokrotnie.
    // Dzięki temu nie tworzymy new GUIContent(debugText) co klatkę.
    private readonly GUIContent _debugContent = new GUIContent();

    // Kolory trzymane jako pola, żeby nie powtarzać new Color(...) w każdej metodzie rysującej.
    private readonly Color _menuBgColor = new Color(0f, 0f, 0f, 0.95f);
    private readonly Color _panelBgColor = new Color(0f, 0f, 0f, 0.75f);
    private readonly Color _debugBgColor = new Color(0f, 0f, 0f, 0.72f);
    private readonly Color _scrollBgColor = new Color(0.08f, 0.08f, 0.08f, 0.95f);

    private readonly Color _deadmanOnColor = new Color(0f, 0.75f, 0.15f, 0.95f);
    private readonly Color _deadmanOffColor = new Color(0.75f, 0f, 0f, 0.95f);

    // Style IMGUI tworzymy leniwie w EnsureStyles().
    private GUIStyle _debugStyle;
    private GUIStyle _menuStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _deadmanStyle;
    private GUIStyle _smallLabelStyle;

    // Stan menu kalibracji.
    private bool _isMenuOpen = false;

    // Stan rozwinięcia sekcji zaawansowanej overlayu.
    private bool _showAdvancedOverlay = false;

    // Pozycja przewijania menu kalibracji.
    private Vector2 _menuScroll = Vector2.zero;

    /// <summary>
    /// Rozpoznaje orientację ekranu.
    /// Używamy tego do innego layoutu w pionie i poziomie.
    /// </summary>
    private bool IsPortrait()
    {
        return Screen.height > Screen.width;
    }

    /// <summary>
    /// Zwraca prostokąt menu kalibracji.
    /// Funkcja jest wspólna dla menu i panelu DEADMAN,
    /// żeby oba elementy wiedziały, gdzie znajduje się menu.
    /// </summary>
    private Rect GetCalibrationMenuRect()
    {
        bool portrait = IsPortrait();

        float widthFactor = portrait ? 0.56f : 0.48f;

        float menuW = Mathf.Min(760f, Screen.width * widthFactor);
        float menuH = Mathf.Min(Screen.height - 130f, 940f);

        return new Rect(Screen.width - menuW - 30f, 100f, menuW, menuH);
    }

    /// <summary>
    /// Główna metoda rysowania GUI.
    /// Wywoływana z AprilTagMvpTracker.OnGUI().
    /// </summary>
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
        DrawDebugBox(debugText);
    }

    /// <summary>
    /// Tworzy style GUI tylko raz.
    /// Nie robimy tego w każdej klatce, bo GUIStyle to obiekty referencyjne.
    /// </summary>
    private void EnsureStyles()
    {
        if (_debugStyle != null &&
            _menuStyle != null &&
            _buttonStyle != null &&
            _deadmanStyle != null &&
            _smallLabelStyle != null)
        {
            return;
        }

        int debugFont = Mathf.RoundToInt(Screen.height * (IsPortrait() ? 0.018f : 0.022f));
        int menuFont = Mathf.RoundToInt(Screen.height * 0.021f);

        debugFont = Mathf.Clamp(debugFont, 18, 28);
        menuFont = Mathf.Clamp(menuFont, 18, 26);

        _debugStyle = new GUIStyle
        {
            fontSize = debugFont,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
            clipping = TextClipping.Clip
        };
        _debugStyle.normal.textColor = Color.green;

        _menuStyle = new GUIStyle
        {
            fontSize = menuFont,
            fontStyle = FontStyle.Normal,
            wordWrap = true
        };
        _menuStyle.normal.textColor = Color.white;

        _smallLabelStyle = new GUIStyle(_menuStyle)
        {
            fontSize = Mathf.Max(16, menuFont - 3),
            alignment = TextAnchor.MiddleCenter
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = menuFont,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };

        _deadmanStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 30,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
    }

    /// <summary>
    /// Przycisk otwierania/zamykania menu kalibracji.
    /// GUIUtility.ExitGUI() jest tutaj celowe, bo zmiana menu zmienia liczbę kontrolek IMGUI.
    /// </summary>
    private void DrawCalibrationButton()
    {
        Rect buttonRect = new Rect(Screen.width - 270f, 25f, 245f, 80f);

        if (GUI.Button(buttonRect, _isMenuOpen ? "ZAMKNIJ" : "KALIBRACJA"))
        {
            _isMenuOpen = !_isMenuOpen;
            GUIUtility.ExitGUI();
        }
    }

    /// <summary>
    /// Główne menu kalibracyjne.
    /// Zawiera parametry AprilTag, korektę skali, wizualizację i mapowanie CPU image -> ekran.
    /// </summary>
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

        // Lewa kolumna z dużymi przyciskami przewijania.
        // Zrobiona dlatego, że natywny scrollbar przy krawędzi był niewygodny na telefonie.
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

        // Ukrywamy natywny scrollbar, bo własny scroll jest po lewej.
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

        if (GUILayout.Button("Ustaw scale = znany dystans / RAW", _buttonStyle, GUILayout.Height(62)))
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

        // Tutaj ExitGUI jest celowe, bo rozwinięcie sekcji zmienia layout menu.
        if (GUILayout.Button($"Zaawansowane ustawienia overlayu: {advancedState}", _buttonStyle, GUILayout.Height(60)))
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

        GUILayout.Space(30);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    /// <summary>
    /// Duże przyciski przewijania menu po lewej stronie.
    /// Przytrzymanie przycisku przewija menu płynnie.
    /// </summary>
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

    /// <summary>
    /// Rysuje suwak float z większym obszarem dotyku.
    /// </summary>
    private float DrawFloatSlider(string label, float value, float min, float max)
    {
        GUILayout.Label(label, _menuStyle);

        value = GUILayout.HorizontalSlider(
            value,
            min,
            max,
            GUILayout.Height(48)
        );

        GUILayout.Space(8);

        return value;
    }

    /// <summary>
    /// Przycisk ON/OFF dla booli.
    /// Bez GUIUtility.ExitGUI(), bo zmiana wartości nie przebudowuje layoutu.
    /// </summary>
    private bool DrawOnOffButton(string label, bool currentValue)
    {
        string state = currentValue ? "ON" : "OFF";
        string text = $"{label}: {state}";

        if (GUILayout.Button(text, _buttonStyle, GUILayout.Height(60)))
        {
            currentValue = !currentValue;
        }

        return currentValue;
    }

    /// <summary>
    /// Przycisk zmiany kierunku osi.
    /// Używany dla X/Y/Z, gdzie NORMALNY/ODWRÓCONY wpływa na znak i/lub zwrot wektora.
    /// </summary>
    private bool DrawDirectionButton(string label, bool inverted)
    {
        string state = inverted ? "ODWRÓCONY" : "NORMALNY";
        string text = $"{label}: {state}";

        if (GUILayout.Button(text, _buttonStyle, GUILayout.Height(64)))
        {
            inverted = !inverted;
        }

        return inverted;
    }

    /// <summary>
    /// Przycisk wyboru trybu mapowania CPU image -> ekran.
    /// Aktywny tryb jest pokazywany w nawiasach kwadratowych.
    /// </summary>
    private bool DrawMappingButton(string label, bool isActive)
    {
        string text = isActive ? $"[{label}]" : label;
        return GUILayout.Button(text, _buttonStyle, GUILayout.Height(58));
    }

    /// <summary>
    /// Panel sterowania pojazdem:
    /// - DEADMAN jako przycisk trzymany palcem,
    /// - speed limit jako procent 0-100.
    /// </summary>
    private void DrawDriveControlPanel(AprilTagMvpTracker tracker)
    {
        bool portrait = IsPortrait();

        float margin = 25f;
        float panelH = 170f;

        float panelW;

        if (portrait)
        {
            // Stała szerokość w pionie, niezależna od otwarcia menu.
            // Szerokość liczona tak, żeby nie wchodzić pod menu po prawej.
            Rect menuRect = GetCalibrationMenuRect();
            panelW = Mathf.Clamp(menuRect.x - margin * 2f, 285f, 430f);
        }
        else
        {
            panelW = 430f;
        }

        Rect panelRect = new Rect(margin, 25f, panelW, panelH);

        GUI.color = _panelBgColor;
        GUI.DrawTexture(panelRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        Rect deadmanRect = new Rect(
            panelRect.x + 18f,
            panelRect.y + 14f,
            panelW - 36f,
            72f
        );

        // DEADMAN działa jako przycisk przytrzymywany.
        // Jeśli palec/mysz jest wewnątrz prostokąta, deadmanPressed = true.
        tracker.deadmanPressed = IsPointerDownInside(deadmanRect);

        GUI.color = tracker.deadmanPressed ? _deadmanOnColor : _deadmanOffColor;
        GUI.DrawTexture(deadmanRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(
            deadmanRect,
            tracker.deadmanPressed ? "DEADMAN ON" : "TRZYMAJ, ABY JECHAĆ",
            _deadmanStyle
        );

        Rect labelRect = new Rect(
            panelRect.x + 18f,
            panelRect.y + 96f,
            panelW - 36f,
            28f
        );

        GUI.Label(labelRect, $"Speed limit: {tracker.speedPercent} %", _menuStyle);

        Rect sliderRect = new Rect(
            panelRect.x + 18f,
            panelRect.y + 132f,
            panelW - 36f,
            32f
        );

        float speedSliderValue = GUI.HorizontalSlider(sliderRect, tracker.speedPercent, 0f, 100f);
        tracker.speedPercent = Mathf.RoundToInt(speedSliderValue);
    }

    /// <summary>
    /// Zielony debug box z aktualną telemetrią.
    /// Tło dopasowuje wysokość do liczby linii tekstu.
    /// </summary>
    private void DrawDebugBox(string debugText)
    {
        bool portrait = IsPortrait();
        Rect safe = Screen.safeArea;

        float extraBottomGapPx = 34f;
        float paddingX = 18f;
        float paddingY = 16f;

        float marginXPortrait = 24f;
        float marginXLandscapePx = 24f;

        float xBox = portrait
            ? safe.xMin + marginXPortrait
            : marginXLandscapePx;

        float width;

        if (portrait)
        {
            width = safe.width - marginXPortrait * 2f;
        }
        else
        {
            // W landscape celowo używamy Screen.width, nie safe.width,
            // żeby safeArea nie odsuwała ramki zbyt daleko od lewej krawędzi.
            width = Mathf.Min(Screen.width * 0.76f - marginXLandscapePx, 1350f);
        }

        width = Mathf.Max(width, 320f);

        // Bez new GUIContent(debugText) co klatkę.
        _debugContent.text = debugText;

        float textHeight = _debugStyle.CalcHeight(_debugContent, width - paddingX * 2f);

        float minHeight = portrait ? 285f : 260f;
        float maxHeight = safe.height * (portrait ? 0.36f : 0.42f);
        float height = Mathf.Clamp(textHeight + paddingY * 2f + 18f, minHeight, maxHeight);

        float yBox = Screen.height - safe.yMin - extraBottomGapPx - height;

        GUI.color = _debugBgColor;
        GUI.DrawTexture(new Rect(xBox, yBox, width, height), Texture2D.whiteTexture);

        GUI.color = Color.white;
        GUI.Label(
            new Rect(
                xBox + paddingX,
                yBox + paddingY,
                width - paddingX * 2f,
                height - paddingY * 2f
            ),
            _debugContent,
            _debugStyle
        );
    }

    /// <summary>
    /// Sprawdza, czy aktualnie użytkownik trzyma palec/mysz wewnątrz podanego prostokąta.
    /// Używane dla przycisku DEADMAN.
    /// </summary>
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