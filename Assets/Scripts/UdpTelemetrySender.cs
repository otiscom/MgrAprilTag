using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// Prosty nadajnik UDP odpowiedzialny wy³¹cznie za wysy³anie telemetrii z Unity.
/// 
/// Ramka UDP ma format tekstowy key:value:
/// 
/// AT1;seq:12;t:4.125;valid:1;deadman:0;move_en:0;speed_pct:20;x_m:-0.1360;y_m:-0.2620;z_m:0.0150;yaw_deg:119.10;fps_hz:30
/// 
/// Znaczenie pól:
/// AT1        - typ i wersja ramki: AprilTag telemetry v1
/// seq        - numer kolejnej ramki, pozwala wykryæ zgubione pakiety UDP
/// t          - czas dzia³ania aplikacji Unity [s]
/// valid      - 1, jeœli widoczne s¹ oba tagi i pomiar pozycji jest poprawny
/// deadman    - 1, jeœli u¿ytkownik trzyma przycisk zezwolenia na ruch
/// move_en    - 1, jeœli auto naprawdê mo¿e jechaæ: valid && deadman && speed_pct > 0
/// speed_pct  - limit / zadanie prêdkoœci w procentach 0-100
/// x_m        - pozycja X wzglêdem bazy [m]
/// y_m        - pozycja Y wzglêdem bazy [m]
/// z_m        - ró¿nica wysokoœci / diagnostyczny b³¹d Z [m]
/// yaw_deg    - obrót pojazdu wzglêdem bazy [deg]
/// fps_hz     - FPS aplikacji, tylko diagnostyka [Hz]
/// </summary>
public sealed class UdpTelemetrySender : IDisposable
{
    // Adres IP komputera / ESP, do którego wysy³amy UDP.
    private readonly string _pcIp;

    // Port UDP odbiornika, np. 5005.
    private readonly int _udpPort;

    // Maksymalna czêstotliwoœæ wysy³ania ramek UDP.
    private readonly float _sendRateHz;

    // Obiekt .NET odpowiedzialny za wysy³kê UDP.
    private UdpClient _udp;

    // Adres docelowy: IP + port.
    private IPEndPoint _endPoint;

    // Czas ostatniej wys³anej ramki. U¿ywany do ograniczenia czêstotliwoœci.
    private float _lastSendTime;

    // Numer kolejnej ramki. Zwiêkszany po ka¿dej wysy³ce.
    private uint _sequence = 0;

    public UdpTelemetrySender(string pcIp, int udpPort, float sendRateHz)
    {
        _pcIp = pcIp;
        _udpPort = udpPort;

        // Zabezpieczenie przed dzieleniem przez 0, gdyby ktoœ ustawi³ 0 Hz.
        _sendRateHz = Mathf.Max(sendRateHz, 1f);

        Init();
    }

    private void Init()
    {
        try
        {
            // Tworzymy klienta UDP.
            // Nie robimy bind() po stronie Unity, bo Unity tylko wysy³a.
            _udp = new UdpClient();

            // Parsujemy IP i tworzymy koñcówkê docelow¹.
            _endPoint = new IPEndPoint(IPAddress.Parse(_pcIp), _udpPort);

            Debug.Log($"[UDP] OK: {_pcIp}:{_udpPort}");
        }
        catch (Exception e)
        {
            // Jeœli IP jest b³êdne albo UDP nie mo¿e siê utworzyæ, zobaczysz to w Logcacie.
            Debug.LogError($"[UDP] Init error: {e.Message}");
        }
    }

    /// <summary>
    /// Wysy³a jedn¹ ramkê telemetrii.
    /// 
    /// x, y, z zostaj¹ floatami w metrach.
    /// yaw zostaje floatem w stopniach.
    /// fpsHz i speedPercent id¹ jako inty, bo nie wymagaj¹ du¿ej precyzji.
    /// </summary>
    public void Send(
        float x,
        float y,
        float z,
        float yaw,
        int fpsHz,
        int speedPercent,
        bool deadmanPressed,
        bool poseValid)
    {
        // Jeœli UDP nie zosta³o poprawnie utworzone, nie robimy nic.
        if (_udp == null || _endPoint == null)
            return;

        // Ograniczenie czêstotliwoœci wysy³ania.
        // Przyk³ad: sendRateHz = 30 -> wysy³ka maksymalnie co 1/30 s.
        if (Time.time - _lastSendTime < 1f / _sendRateHz)
            return;

        // Pobieramy aktualny numer ramki i dopiero potem zwiêkszamy licznik.
        uint seq = _sequence++;

        // Prêdkoœæ jako int 0-100.
        int speedClamped = Mathf.Clamp(speedPercent, 0, 100);

        // Ruch jest dozwolony tylko wtedy, gdy:
        // 1. u¿ytkownik trzyma deadmana,
        // 2. pozycja z AprilTag jest wa¿na,
        // 3. prêdkoœæ jest wiêksza od 0.
        bool moveEnabled = deadmanPressed && poseValid && speedClamped > 0;

        // Booli nie wysy³amy jako "true/false", tylko jako 0/1.
        // £atwiej to potem parsowaæ na ESP/STM32.
        int validInt = poseValid ? 1 : 0;
        int deadmanInt = deadmanPressed ? 1 : 0;
        int moveEnabledInt = moveEnabled ? 1 : 0;

        // Budowa ramki tekstowej.
        // CultureInfo.InvariantCulture wymusza kropkê jako separator dziesiêtny.
        // Bez tego na niektórych ustawieniach regionalnych mog³oby pojawiæ siê np. 0,123 zamiast 0.123.
        string data =
            "AT1;" +
            "seq:" + seq.ToString(CultureInfo.InvariantCulture) + ";" +
            "t:" + Time.time.ToString("F3", CultureInfo.InvariantCulture) + ";" +
            "valid:" + validInt.ToString(CultureInfo.InvariantCulture) + ";" +
            "deadman:" + deadmanInt.ToString(CultureInfo.InvariantCulture) + ";" +
            "move_en:" + moveEnabledInt.ToString(CultureInfo.InvariantCulture) + ";" +
            "speed_pct:" + speedClamped.ToString(CultureInfo.InvariantCulture) + ";" +
            "x_m:" + x.ToString("F4", CultureInfo.InvariantCulture) + ";" +
            "y_m:" + y.ToString("F4", CultureInfo.InvariantCulture) + ";" +
            "z_m:" + z.ToString("F4", CultureInfo.InvariantCulture) + ";" +
            "yaw_deg:" + yaw.ToString("F2", CultureInfo.InvariantCulture) + ";" +
            "fps_hz:" + fpsHz.ToString(CultureInfo.InvariantCulture) +
            "\n";

        try
        {
            // ASCII wystarcza, bo ramka zawiera tylko zwyk³e znaki.
            byte[] bytes = Encoding.ASCII.GetBytes(data);

            // Wys³anie pakietu UDP.
            _udp.Send(bytes, bytes.Length, _endPoint);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UDP] Send error: {e.Message}");
        }

        // Aktualizujemy czas ostatniej wysy³ki dopiero po próbie wys³ania.
        _lastSendTime = Time.time;
    }

    public void Dispose()
    {
        // Zamkniêcie socketu UDP przy niszczeniu skryptu.
        _udp?.Close();

        _udp = null;
        _endPoint = null;
    }
}