using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public enum UdpSendMode
{
    TextAT1,     // debug / cz³owiek czyta / Python / PC
    BinaryATB1   // tryb sprzêtowy dla ESP32 / Simulink
}

/// <summary>
/// Nadajnik UDP odpowiedzialny wy³¹cznie za wysy³anie telemetrii z Unity.
///
/// TextAT1:
/// Format tekstowy do debugowania, np. Wireshark / Python / konsola.
///
/// AT1;seq:7731;t:519.706;source_id:1;valid:1;deadman:1;move_en:1;speed_pct:89;x_m:0.1234;y_m:-0.0521;z_m:0.0000;yaw_deg:179.99;yaw_deg_unwrapped:539.99;yaw_rate_dps:-123.45;fps_hz:20;crc:B7E4
///
/// BinaryATB1:
/// Sta³a ramka binarna 42 bajty do ESP32 / Simulink.
///
/// Byte 0:      0xA1
/// Byte 1:      0x1A
/// Byte 2:      version = 1
/// Byte 3:      source_id
/// Byte 4-7:    seq uint32 LE
/// Byte 8-11:   t_ms uint32 LE
/// Byte 12:     flags uint8
///               bit0 = valid
///               bit1 = deadman
///               bit2 = move_en
/// Byte 13:     speed_pct uint8
/// Byte 14:     fps_hz uint8
/// Byte 15:     reserved = 0
/// Byte 16-19:  x_m float32 LE
/// Byte 20-23:  y_m float32 LE
/// Byte 24-27:  z_m float32 LE
/// Byte 28-31:  yaw_deg float32 LE, wrapped -180..180
/// Byte 32-35:  yaw_deg_unwrapped float32 LE, ci¹g³y
/// Byte 36-39:  yaw_rate_dps float32 LE
/// Byte 40-41:  crc16 uint16 LE, CRC po bajtach 0..39
/// </summary>
public sealed class UdpTelemetrySender : IDisposable
{
    private const int BinaryFrameLength = 42;

    private const byte BinaryHeader0 = 0xA1;
    private const byte BinaryHeader1 = 0x1A;
    private const byte BinaryVersion = 1;

    private string _targetIp;
    private int _targetPort;
    private float _sendRateHz;
    private UdpSendMode _sendMode;
    private byte _sourceId;
    private bool _debugBinaryLog;

    private UdpClient _udp;
    private IPEndPoint _endPoint;

    private float _lastSendTime;
    private float _lastBinaryDebugLogTime;

    private uint _sequence = 0;

    private readonly StringBuilder _frameBuilder = new StringBuilder(512);
    private readonly byte[] _textSendBuffer = new byte[1024];
    private readonly byte[] _binarySendBuffer = new byte[BinaryFrameLength];

    private bool _bufferWarningShown = false;

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatUIntUnion
    {
        [FieldOffset(0)] public float FloatValue;
        [FieldOffset(0)] public uint UIntValue;
    }

    public UdpTelemetrySender(
        string targetIp,
        int targetPort,
        float sendRateHz,
        UdpSendMode sendMode,
        byte sourceId,
        bool debugBinaryLog)
    {
        Configure(
            targetIp,
            targetPort,
            sendRateHz,
            sendMode,
            sourceId,
            debugBinaryLog,
            forceReconnect: true
        );
    }

    public void Configure(
        string targetIp,
        int targetPort,
        float sendRateHz,
        UdpSendMode sendMode,
        byte sourceId,
        bool debugBinaryLog,
        bool forceReconnect = false)
    {
        targetIp = string.IsNullOrWhiteSpace(targetIp) ? "127.0.0.1" : targetIp.Trim();
        targetPort = Mathf.Clamp(targetPort, 1, 65535);
        sendRateHz = Mathf.Max(sendRateHz, 1f);
        sourceId = (byte)Mathf.Clamp(sourceId, 1, 4);

        bool endpointChanged =
            forceReconnect ||
            _udp == null ||
            _endPoint == null ||
            _targetIp != targetIp ||
            _targetPort != targetPort;

        _targetIp = targetIp;
        _targetPort = targetPort;
        _sendRateHz = sendRateHz;
        _sendMode = sendMode;
        _sourceId = sourceId;
        _debugBinaryLog = debugBinaryLog;

        if (!endpointChanged)
            return;

        try
        {
            _udp?.Close();
            _udp = new UdpClient();

            IPAddress targetAddress = IPAddress.Parse(_targetIp);

            bool isBroadcast = IsBroadcastAddress(targetAddress);

            if (isBroadcast)
            {
                _udp.EnableBroadcast = true;
                _udp.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.Broadcast,
                    true
                );
            }

            _endPoint = new IPEndPoint(targetAddress, _targetPort);

            Debug.Log(
                $"[UDP] OK: {_targetIp}:{_targetPort}, mode={_sendMode}, " +
                $"source_id={_sourceId}, broadcast={(isBroadcast ? 1 : 0)}"
            );
        }
        catch (Exception e)
        {
            _udp = null;
            _endPoint = null;

            Debug.LogError($"[UDP] Configure error: {e.Message}");
        }
    }

    public void Send(
        float x,
        float y,
        float z,
        float yawWrappedDeg,
        float yawUnwrappedDeg,
        float yawRateDps,
        int fpsHz,
        int speedPercent,
        bool deadmanPressed,
        bool poseValid)
    {
        if (_udp == null || _endPoint == null)
            return;

        float now = Time.realtimeSinceStartup;

        if (now - _lastSendTime < 1f / _sendRateHz)
            return;

        uint seq = _sequence++;

        int speedClamped = Mathf.Clamp(speedPercent, 0, 100);
        int fpsClamped = Mathf.Clamp(fpsHz, 0, 255);

        // move_en oznacza zgodê operatora na ruch, a nie poprawnoœæ pomiaru pozycji.
        // Dziêki temu jeden telefon mo¿e dawaæ deadman/move_en,
        // a drugi telefon mo¿e dawaæ valid=1 i pozycjê.
        bool moveEnabled = deadmanPressed && speedClamped > 0;

        int validInt = poseValid ? 1 : 0;
        int deadmanInt = deadmanPressed ? 1 : 0;
        int moveEnabledInt = moveEnabled ? 1 : 0;

        try
        {
            if (_sendMode == UdpSendMode.TextAT1)
            {
                BuildTextAT1(
                    seq,
                    now,
                    validInt,
                    deadmanInt,
                    moveEnabledInt,
                    speedClamped,
                    x,
                    y,
                    z,
                    yawWrappedDeg,
                    yawUnwrappedDeg,
                    yawRateDps,
                    fpsClamped
                );

                int byteCount = CopyAsciiToSendBuffer(_frameBuilder, _textSendBuffer);

                if (byteCount > 0)
                    _udp.Send(_textSendBuffer, byteCount, _endPoint);
            }
            else
            {
                int byteCount = BuildBinaryATB1(
                    seq,
                    now,
                    validInt,
                    deadmanInt,
                    moveEnabledInt,
                    speedClamped,
                    fpsClamped,
                    x,
                    y,
                    z,
                    yawWrappedDeg,
                    yawUnwrappedDeg,
                    yawRateDps
                );

                _udp.Send(_binarySendBuffer, byteCount, _endPoint);

                if (_debugBinaryLog)
                {
                    DebugBinaryFrame(
                        seq,
                        validInt,
                        deadmanInt,
                        moveEnabledInt,
                        speedClamped,
                        fpsClamped,
                        x,
                        y,
                        yawWrappedDeg,
                        yawUnwrappedDeg
                    );
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UDP] Send error: {e.Message}");
        }

        _lastSendTime = now;
    }

    private void BuildTextAT1(
        uint seq,
        float timeSeconds,
        int validInt,
        int deadmanInt,
        int moveEnabledInt,
        int speedPercent,
        float x,
        float y,
        float z,
        float yawWrappedDeg,
        float yawUnwrappedDeg,
        float yawRateDps,
        int fpsHz)
    {
        _frameBuilder.Clear();

        int timeMs = Mathf.RoundToInt(timeSeconds * 1000f);

        int xScaled = Mathf.RoundToInt(x * 10000f);
        int yScaled = Mathf.RoundToInt(y * 10000f);
        int zScaled = Mathf.RoundToInt(z * 10000f);

        int yawWrappedScaled = Mathf.RoundToInt(yawWrappedDeg * 100f);
        int yawUnwrappedScaled = Mathf.RoundToInt(yawUnwrappedDeg * 100f);
        int yawRateScaled = Mathf.RoundToInt(yawRateDps * 100f);

        _frameBuilder.Append("AT1;");
        _frameBuilder.Append("seq:").Append(seq).Append(';');

        _frameBuilder.Append("t:");
        AppendScaledInt(_frameBuilder, timeMs, 3);
        _frameBuilder.Append(';');

        _frameBuilder.Append("source_id:").Append(_sourceId).Append(';');

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
        AppendScaledInt(_frameBuilder, yawWrappedScaled, 2);
        _frameBuilder.Append(';');

        _frameBuilder.Append("yaw_deg_unwrapped:");
        AppendScaledInt(_frameBuilder, yawUnwrappedScaled, 2);
        _frameBuilder.Append(';');

        _frameBuilder.Append("yaw_rate_dps:");
        AppendScaledInt(_frameBuilder, yawRateScaled, 2);
        _frameBuilder.Append(';');

        _frameBuilder.Append("fps_hz:").Append(fpsHz);

        ushort crc = ComputeCrc16CcittFalse(_frameBuilder);

        _frameBuilder.Append(";crc:");
        AppendHex4(_frameBuilder, crc);

        _frameBuilder.Append('\n');
    }

    private int BuildBinaryATB1(
        uint seq,
        float timeSeconds,
        int validInt,
        int deadmanInt,
        int moveEnabledInt,
        int speedPercent,
        int fpsHz,
        float x,
        float y,
        float z,
        float yawWrappedDeg,
        float yawUnwrappedDeg,
        float yawRateDps)
    {
        Array.Clear(_binarySendBuffer, 0, BinaryFrameLength);

        uint tMs = unchecked((uint)Mathf.FloorToInt(timeSeconds * 1000f));

        byte flags = 0;

        if (validInt != 0)
            flags |= 1 << 0;

        if (deadmanInt != 0)
            flags |= 1 << 1;

        if (moveEnabledInt != 0)
            flags |= 1 << 2;

        _binarySendBuffer[0] = BinaryHeader0;
        _binarySendBuffer[1] = BinaryHeader1;
        _binarySendBuffer[2] = BinaryVersion;
        _binarySendBuffer[3] = _sourceId;

        WriteUInt32LE(_binarySendBuffer, 4, seq);
        WriteUInt32LE(_binarySendBuffer, 8, tMs);

        _binarySendBuffer[12] = flags;
        _binarySendBuffer[13] = (byte)Mathf.Clamp(speedPercent, 0, 100);
        _binarySendBuffer[14] = (byte)Mathf.Clamp(fpsHz, 0, 255);
        _binarySendBuffer[15] = 0;

        WriteFloatLE(_binarySendBuffer, 16, x);
        WriteFloatLE(_binarySendBuffer, 20, y);
        WriteFloatLE(_binarySendBuffer, 24, z);
        WriteFloatLE(_binarySendBuffer, 28, yawWrappedDeg);
        WriteFloatLE(_binarySendBuffer, 32, yawUnwrappedDeg);
        WriteFloatLE(_binarySendBuffer, 36, yawRateDps);

        ushort crc = ComputeCrc16CcittFalse(_binarySendBuffer, 0, 40);
        WriteUInt16LE(_binarySendBuffer, 40, crc);

        return BinaryFrameLength;
    }

    private void DebugBinaryFrame(
        uint seq,
        int validInt,
        int deadmanInt,
        int moveEnabledInt,
        int speedPercent,
        int fpsHz,
        float x,
        float y,
        float yawWrappedDeg,
        float yawUnwrappedDeg)
    {
        float now = Time.realtimeSinceStartup;

        if (now - _lastBinaryDebugLogTime < 1f)
            return;

        _lastBinaryDebugLogTime = now;

        ushort crc = ReadUInt16LE(_binarySendBuffer, 40);

        Debug.Log(
            $"ATB1 len={BinaryFrameLength} " +
            $"header={_binarySendBuffer[0]:X2} {_binarySendBuffer[1]:X2} " +
            $"source={_sourceId} seq={seq} " +
            $"valid={validInt} deadman={deadmanInt} move_en={moveEnabledInt} " +
            $"speed={speedPercent} fps={fpsHz} " +
            $"x={x:F3} y={y:F3} " +
            $"yaw={yawWrappedDeg:F2} unwrap={yawUnwrappedDeg:F2} " +
            $"crc={crc:X4}"
        );
    }

    private static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
    {
        buffer[offset + 0] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static ushort ReadUInt16LE(byte[] buffer, int offset)
    {
        return (ushort)(buffer[offset + 0] | (buffer[offset + 1] << 8));
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteFloatLE(byte[] buffer, int offset, float value)
    {
        FloatUIntUnion union = new FloatUIntUnion
        {
            FloatValue = value
        };

        WriteUInt32LE(buffer, offset, union.UIntValue);
    }
    private static readonly ushort[] Crc16CcittFalseTable = new ushort[256];

    static UdpTelemetrySender()
    {
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);

            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc <<= 1;
            }

            Crc16CcittFalseTable[i] = crc;
        }
    }
    private static ushort ComputeCrc16CcittFalse(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;

        for (int i = 0; i < length; i++)
        {
            byte tableIndex = (byte)((crc >> 8) ^ data[offset + i]);
            crc = (ushort)((crc << 8) ^ Crc16CcittFalseTable[tableIndex]);
        }

        return crc;
    }

    private static ushort ComputeCrc16CcittFalse(StringBuilder sb)
    {
        ushort crc = 0xFFFF;

        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            byte b = c <= 0x7F ? (byte)c : (byte)'?';

            byte tableIndex = (byte)((crc >> 8) ^ b);
            crc = (ushort)((crc << 8) ^ Crc16CcittFalseTable[tableIndex]);
        }

        return crc;
    }

    private static void AppendHex4(StringBuilder sb, ushort value)
    {
        const string hex = "0123456789ABCDEF";

        sb.Append(hex[(value >> 12) & 0x0F]);
        sb.Append(hex[(value >> 8) & 0x0F]);
        sb.Append(hex[(value >> 4) & 0x0F]);
        sb.Append(hex[value & 0x0F]);
    }

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

        int divider = factor / 10;

        while (divider > 0)
        {
            int digit = fraction / divider;
            sb.Append((char)('0' + digit));

            fraction %= divider;
            divider /= 10;
        }
    }

    private static bool IsBroadcastAddress(IPAddress address)
    {
        if (address == null)
            return false;

        byte[] bytes = address.GetAddressBytes();

        // 255.255.255.255
        if (address.Equals(IPAddress.Broadcast))
            return true;

        // Typowy broadcast podsieci, np. 192.168.1.255 albo 192.168.43.255.
        // To wystarczy do Twoich testów LAN/hotspot.
        if (bytes.Length == 4 && bytes[3] == 255)
            return true;

        return false;
    }

    private int CopyAsciiToSendBuffer(StringBuilder sb, byte[] targetBuffer)
    {
        if (sb.Length > targetBuffer.Length)
        {
            if (!_bufferWarningShown)
            {
                Debug.LogWarning($"[UDP] Text frame too long: {sb.Length} chars, buffer size: {targetBuffer.Length}");
                _bufferWarningShown = true;
            }

            return 0;
        }

        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            targetBuffer[i] = c <= 0x7F ? (byte)c : (byte)'?';
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

