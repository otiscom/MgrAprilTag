using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// Nadajnik UDP odpowiedzialny wy³¹cznie za wysy³anie telemetrii z Unity.
///
/// Ramka UDP ma format tekstowy key:value:
///
/// AT1;seq:7731;t:519.706;valid:1;deadman:1;move_en:1;speed_pct:89;x_m:0.1234;y_m:-0.0521;z_m:0.0000;yaw_deg:184.25;fps_hz:20;crc:XXXX
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
/// yaw_deg    - ci¹g³y obrót pojazdu wzglêdem bazy [deg], po unwrap; u¿ywany przez regulator
/// fps_hz     - FPS aplikacji, tylko diagnostyka [Hz]
/// crc        - CRC16-CCITT-FALSE policzone z tekstu przed polem ;crc:
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

    // Numer kolejnej ramki. Zwiêkszany po ka¿dej poprawnej próbie wys³ania.
    private uint _sequence = 0;

    // StringBuilder ogranicza liczbê tymczasowych stringów przy budowaniu ramki.
    // 256 znaków wystarcza dla obecnego formatu z zapasem.
    private readonly StringBuilder _frameBuilder = new StringBuilder(256);

    // Bufor bajtów u¿ywany ponownie przy ka¿dej wysy³ce.
    // Dziêki temu nie tworzymy nowego byte[] co pakiet.
    private readonly byte[] _sendBuffer = new byte[512];

    // Flaga, ¿eby nie spamowaæ logów, jeœli ramka nagle zrobi siê za d³uga.
    private bool _bufferWarningShown = false;

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
            Debug.LogError($"[UDP] Init error: {e.Message}");
        }
    }

    /// <summary>
    /// Wysy³a jedn¹ ramkê telemetrii.
    ///
    /// x, y, z s¹ wysy³ane jako metry z 4 miejscami po przecinku.
    /// yaw jest wysy³any jako stopnie z 2 miejscami po przecinku.
    /// fpsHz i speedPercent id¹ jako inty.
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
        if (Time.time - _lastSendTime < 1f / _sendRateHz)
            return;

        uint seq = _sequence++;

        int speedClamped = Mathf.Clamp(speedPercent, 0, 100);

        // Ruch jest dozwolony tylko wtedy, gdy:
        // 1. u¿ytkownik trzyma deadmana,
        // 2. pozycja z AprilTag jest wa¿na,
        // 3. prêdkoœæ jest wiêksza od 0.
        bool moveEnabled = deadmanPressed && poseValid && speedClamped > 0;

        int validInt = poseValid ? 1 : 0;
        int deadmanInt = deadmanPressed ? 1 : 0;
        int moveEnabledInt = moveEnabled ? 1 : 0;

        BuildFrame(
            seq,
            Time.time,
            validInt,
            deadmanInt,
            moveEnabledInt,
            speedClamped,
            x,
            y,
            z,
            yaw,
            fpsHz
        );

        int byteCount = CopyAsciiToSendBuffer(_frameBuilder);

        if (byteCount <= 0)
            return;

        try
        {
            _udp.Send(_sendBuffer, byteCount, _endPoint);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UDP] Send error: {e.Message}");
        }

        _lastSendTime = Time.time;
    }

    /// <summary>
    /// Buduje kompletn¹ ramkê tekstow¹ i dopisuje CRC.
    /// CRC liczone jest z czêœci ramki przed polem ;crc:.
    /// </summary>
    private void BuildFrame(
        uint seq,
        float timeSeconds,
        int validInt,
        int deadmanInt,
        int moveEnabledInt,
        int speedPercent,
        float x,
        float y,
        float z,
        float yaw,
        int fpsHz)
    {
        _frameBuilder.Clear();

        // Czas trzymamy jako fixed-point: sekundy z 3 miejscami po przecinku.
        int timeMs = Mathf.RoundToInt(timeSeconds * 1000f);

        // Pozycje fixed-point: metry z 4 miejscami po przecinku.
        int xScaled = Mathf.RoundToInt(x * 10000f);
        int yScaled = Mathf.RoundToInt(y * 10000f);
        int zScaled = Mathf.RoundToInt(z * 10000f);

        // Yaw fixed-point: stopnie z 2 miejscami po przecinku.
        int yawScaled = Mathf.RoundToInt(yaw * 100f);

        _frameBuilder.Append("AT1;");
        _frameBuilder.Append("seq:").Append(seq).Append(';');

        _frameBuilder.Append("t:");
        AppendScaledInt(_frameBuilder, timeMs, 3);
        _frameBuilder.Append(';');

        _frameBuilder.Append("valid:").Append(validInt).Append(';');
        _frameBuilder.Append("deadman:").Append(deadmanInt).Append(';');
        _frameBuilder.Append("move_en:").Append(moveEnabledInt).Append(';');
        _frameBuilder.Append("speed_pct:").Append(speedPercent).Append(';');

        _frameBuilder.Append("x_m:");
        AppendScaledInt(_frameBuilder, xScaled, 4);
        _frameBuilder.Append(';');

        _frameBuilder.Append("y_m:");
        AppendScaledInt(_frameBuilder, yScaled, 4);
        _frameBuilder.Append(';');

        _frameBuilder.Append("z_m:");
        AppendScaledInt(_frameBuilder, zScaled, 4);
        _frameBuilder.Append(';');

        _frameBuilder.Append("yaw_deg:");
        AppendScaledInt(_frameBuilder, yawScaled, 2);
        _frameBuilder.Append(';');

        _frameBuilder.Append("fps_hz:").Append(fpsHz);

        // CRC liczymy przed dopisaniem pola crc.
        ushort crc = ComputeCrc16CcittFalse(_frameBuilder);

        _frameBuilder.Append(";crc:");
        AppendHex4(_frameBuilder, crc);

        _frameBuilder.Append('\n');
    }

    /// <summary>
    /// Dopisuje liczbê ca³kowit¹ jako fixed-point.
    ///
    /// Przyk³ady:
    /// scaledValue = 4125, decimals = 3 -> 4.125
    /// scaledValue = -1360, decimals = 4 -> -0.1360
    /// scaledValue = 11910, decimals = 2 -> 119.10
    /// </summary>
    private static void AppendScaledInt(StringBuilder sb, int scaledValue, int decimals)
    {
        if (scaledValue < 0)
        {
            sb.Append('-');
            scaledValue = -scaledValue;
        }

        int factor = 1;

        for (int i = 0; i < decimals; i++)
            factor *= 10;

        int whole = scaledValue / factor;
        int fraction = scaledValue % factor;

        sb.Append(whole);

        if (decimals <= 0)
            return;

        sb.Append('.');

        // Dopisujemy zera wiod¹ce czêœci u³amkowej.
        int divider = factor / 10;

        while (divider > 0)
        {
            int digit = fraction / divider;
            sb.Append((char)('0' + digit));

            fraction %= divider;
            divider /= 10;
        }
    }

    /// <summary>
    /// CRC16-CCITT-FALSE:
    /// - polynomial: 0x1021
    /// - initial value: 0xFFFF
    /// - no reflection
    /// - no xorout
    ///
    /// Liczone po znakach ASCII z aktualnego StringBuildera.
    /// </summary>
    private static ushort ComputeCrc16CcittFalse(StringBuilder sb)
    {
        ushort crc = 0xFFFF;

        for (int i = 0; i < sb.Length; i++)
        {
            byte b = (byte)(sb[i] & 0x7F);

            crc ^= (ushort)(b << 8);

            for (int bit = 0; bit < 8; bit++)
            {
                bool msbSet = (crc & 0x8000) != 0;
                crc <<= 1;

                if (msbSet)
                    crc ^= 0x1021;
            }
        }

        return crc;
    }

    /// <summary>
    /// Dopisuje 16-bitow¹ wartoœæ jako 4 znaki HEX.
    /// Np. 0xA4EF -> A4EF.
    /// </summary>
    private static void AppendHex4(StringBuilder sb, ushort value)
    {
        const string hex = "0123456789ABCDEF";

        sb.Append(hex[(value >> 12) & 0x0F]);
        sb.Append(hex[(value >> 8) & 0x0F]);
        sb.Append(hex[(value >> 4) & 0x0F]);
        sb.Append(hex[value & 0x0F]);
    }

    /// <summary>
    /// Kopiuje znaki ASCII ze StringBuildera do sta³ego bufora bajtów.
    /// Nie tworzy nowej tablicy byte[] co ramkê.
    /// </summary>
    private int CopyAsciiToSendBuffer(StringBuilder sb)
    {
        if (sb.Length > _sendBuffer.Length)
        {
            if (!_bufferWarningShown)
            {
                Debug.LogWarning($"[UDP] Frame too long: {sb.Length} chars, buffer size: {_sendBuffer.Length}");
                _bufferWarningShown = true;
            }

            return 0;
        }

        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];

            // Ramka ma zawieraæ tylko ASCII.
            // Gdyby przypadkiem trafi³ znak spoza ASCII, zamieniamy go na '?'.
            _sendBuffer[i] = c <= 0x7F ? (byte)c : (byte)'?';
        }

        return sb.Length;
    }

    public void Dispose()
    {
        _udp?.Close();

        _udp = null;
        _endPoint = null;
    }
}