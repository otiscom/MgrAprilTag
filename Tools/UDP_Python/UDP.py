import socket
import struct

UDP_IP = "0.0.0.0"
UDP_PORT = 5005

def crc16_ccitt_false(data: bytes) -> int:
    crc = 0xFFFF

    for b in data:
        crc ^= b << 8

        for _ in range(8):
            if crc & 0x8000:
                crc = ((crc << 1) ^ 0x1021) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF

    return crc

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind((UDP_IP, UDP_PORT))

print(f"Serwer UDP aktywny. Slucham na porcie {UDP_PORT}...")

while True:
    data, addr = sock.recvfrom(1024)

    if data.startswith(b"AT1;"):
        print(f"\nTEXT od {addr}:")
        print(data.decode("ascii", errors="replace").strip())
        continue

    print(f"\nBIN od {addr}: len={len(data)}")
    print("HEX:", " ".join(f"{b:02X}" for b in data))

    if len(data) != 42:
        print("Niepoprawna dlugosc ATB1")
        continue

    if data[0] != 0xA1 or data[1] != 0x1A:
        print("Niepoprawny header")
        continue

    rx_crc = struct.unpack_from("<H", data, 40)[0]
    calc_crc = crc16_ccitt_false(data[:40])
    crc_ok = rx_crc == calc_crc

    (
        h0,
        h1,
        version,
        source_id,
        seq,
        t_ms,
        flags,
        speed_pct,
        fps_hz,
        reserved,
        x_m,
        y_m,
        z_m,
        yaw_deg,
        yaw_deg_unwrapped,
        yaw_rate_dps,
        crc
    ) = struct.unpack("<BBBBIIBBBBffffffH", data)

    valid = bool(flags & (1 << 0))
    deadman = bool(flags & (1 << 1))
    move_en = bool(flags & (1 << 2))

    print(
        f"ATB1 ver={version} src={source_id} seq={seq} t_ms={t_ms} "
        f"flags={flags} valid={int(valid)} deadman={int(deadman)} move_en={int(move_en)} "
        f"speed={speed_pct} fps={fps_hz} "
        f"x={x_m:.4f} y={y_m:.4f} z={z_m:.4f} "
        f"yaw={yaw_deg:.2f} unwrap={yaw_deg_unwrapped:.2f} rate={yaw_rate_dps:.2f} "
        f"crc_rx={rx_crc:04X} crc_calc={calc_crc:04X} crc_ok={crc_ok}"
    )