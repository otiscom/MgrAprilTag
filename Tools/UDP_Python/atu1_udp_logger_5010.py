#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
atu1_udp_logger_5010.py

Odbiera surowe ramki ATU1 wysyłane przez ESP32 broadcastem UDP
na adres 255.255.255.255 i port 5010.

Założony format ATU1: 38 bajtów
  1-2   header: 0xA5 0x5A
  3     version
  4     source_id
  5-8   seq uint32 LE
  9-12  unity_t_ms uint32 LE
  13    flags
  14    speed_pct
  15    fps_hz
  16    reserved
  17-20 x_m float32 LE
  21-24 y_m float32 LE
  25-28 z_m float32 LE
  29-32 yaw_deg_unwrapped float32 LE
  33-36 yaw_rate_dps float32 LE
  37-38 CRC16/CCITT-FALSE uint16 LE

Działanie:
  - zapisuje KAŻDĄ odebraną ramkę do CSV,
  - wypisuje zbiorcze informacje w konsoli raz na sekundę,
  - sprawdza długość, nagłówek, wersję i CRC,
  - zapisuje również surową ramkę jako HEX.

Nie wymaga bibliotek zewnętrznych.
"""

from __future__ import annotations

import csv
import datetime as dt
import socket
import struct
import time
from pathlib import Path
from typing import Any

# ============================================================
# KONFIGURACJA
# ============================================================

LISTEN_IP = "0.0.0.0"
LISTEN_PORT = 5010

LOG_DIR = Path(".")
CSV_PREFIX = "esp32_uart_stream"

PRINT_INTERVAL_S = 1.0
SOCKET_TIMEOUT_S = 0.2
RECV_BUFFER_SIZE = 4096

ATU1_LEN = 38
ATU1_H0 = 0xA5
ATU1_H1 = 0x5A
ATU1_VERSION = 1

# < = little-endian
# BBBB = header0, header1, version, source_id
# II   = seq, unity_t_ms
# BBBB = flags, speed_pct, fps_hz, reserved
# fffff = x, y, z, yaw_unwrapped, yaw_rate
# H = crc
ATU1_STRUCT = struct.Struct("<BBBBIIBBBBfffffH")

CSV_COLUMNS = [
    "pc_time_iso",
    "pc_time_unix",
    "sender_ip",
    "sender_port",
    "packet_index",
    "pc_gap_ms",
    "packet_len",
    "frame_ok",
    "length_ok",
    "header_ok",
    "version_ok",
    "crc_ok",
    "version",
    "source_id",
    "seq",
    "seq_changed",
    "unity_t_ms",
    "flags",
    "flags_hex",
    "pose_valid",
    "deadman",
    "move_en",
    "data_ok",
    "safe_to_drive",
    "hold_mode",
    "soft_decay",
    "hard_stop",
    "speed_pct",
    "fps_hz",
    "reserved",
    "x_m",
    "y_m",
    "z_m",
    "yaw_deg_unwrapped",
    "yaw_rate_dps",
    "crc_rx",
    "crc_calc",
    "raw_hex",
]


# ============================================================
# CRC16/CCITT-FALSE
# poly=0x1021, init=0xFFFF, xorout=0x0000
# ============================================================

def crc16_ccitt_false(data: bytes) -> int:
    crc = 0xFFFF

    for byte in data:
        crc ^= byte << 8

        for _ in range(8):
            if crc & 0x8000:
                crc = ((crc << 1) ^ 0x1021) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF

    return crc


def now_fields() -> tuple[str, float]:
    unix_time = time.time()
    iso_time = (
        dt.datetime.fromtimestamp(unix_time)
        .astimezone()
        .isoformat(timespec="milliseconds")
    )
    return iso_time, unix_time


def hex_spaced(data: bytes) -> str:
    return " ".join(f"{byte:02X}" for byte in data)


def decode_flags(flags: int) -> dict[str, int]:
    return {
        "pose_valid": 1 if flags & (1 << 0) else 0,
        "deadman": 1 if flags & (1 << 1) else 0,
        "move_en": 1 if flags & (1 << 2) else 0,
        "data_ok": 1 if flags & (1 << 3) else 0,
        "safe_to_drive": 1 if flags & (1 << 4) else 0,
        "hold_mode": 1 if flags & (1 << 5) else 0,
        "soft_decay": 1 if flags & (1 << 6) else 0,
        "hard_stop": 1 if flags & (1 << 7) else 0,
    }


def make_empty_row(
    data: bytes,
    addr: tuple[str, int],
    packet_index: int,
    pc_gap_ms: float | None,
) -> dict[str, Any]:
    pc_time_iso, pc_time_unix = now_fields()

    return {
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "sender_ip": addr[0],
        "sender_port": addr[1],
        "packet_index": packet_index,
        "pc_gap_ms": "" if pc_gap_ms is None else f"{pc_gap_ms:.3f}",
        "packet_len": len(data),
        "frame_ok": 0,
        "length_ok": 0,
        "header_ok": 0,
        "version_ok": 0,
        "crc_ok": 0,
        "version": "",
        "source_id": "",
        "seq": "",
        "seq_changed": "",
        "unity_t_ms": "",
        "flags": "",
        "flags_hex": "",
        "pose_valid": "",
        "deadman": "",
        "move_en": "",
        "data_ok": "",
        "safe_to_drive": "",
        "hold_mode": "",
        "soft_decay": "",
        "hard_stop": "",
        "speed_pct": "",
        "fps_hz": "",
        "reserved": "",
        "x_m": "",
        "y_m": "",
        "z_m": "",
        "yaw_deg_unwrapped": "",
        "yaw_rate_dps": "",
        "crc_rx": "",
        "crc_calc": "",
        "raw_hex": hex_spaced(data),
    }


def decode_atu1(
    data: bytes,
    addr: tuple[str, int],
    packet_index: int,
    pc_gap_ms: float | None,
    previous_seq: int | None,
) -> tuple[dict[str, Any], dict[str, Any] | None]:
    row = make_empty_row(data, addr, packet_index, pc_gap_ms)

    length_ok = len(data) == ATU1_LEN
    row["length_ok"] = 1 if length_ok else 0

    if not length_ok:
        return row, None

    header_ok = data[0] == ATU1_H0 and data[1] == ATU1_H1
    row["header_ok"] = 1 if header_ok else 0

    (
        h0,
        h1,
        version,
        source_id,
        seq,
        unity_t_ms,
        flags,
        speed_pct,
        fps_hz,
        reserved,
        x_m,
        y_m,
        z_m,
        yaw_deg_unwrapped,
        yaw_rate_dps,
        crc_rx,
    ) = ATU1_STRUCT.unpack(data)

    version_ok = version == ATU1_VERSION
    crc_calc = crc16_ccitt_false(data[:36])
    crc_ok = crc_rx == crc_calc
    bits = decode_flags(flags)

    seq_changed = 1 if previous_seq is None or seq != previous_seq else 0
    frame_ok = length_ok and header_ok and version_ok and crc_ok

    row.update(
        {
            "frame_ok": 1 if frame_ok else 0,
            "version_ok": 1 if version_ok else 0,
            "crc_ok": 1 if crc_ok else 0,
            "version": version,
            "source_id": source_id,
            "seq": seq,
            "seq_changed": seq_changed,
            "unity_t_ms": unity_t_ms,
            "flags": flags,
            "flags_hex": f"0x{flags:02X}",
            **bits,
            "speed_pct": speed_pct,
            "fps_hz": fps_hz,
            "reserved": reserved,
            "x_m": f"{x_m:.6f}",
            "y_m": f"{y_m:.6f}",
            "z_m": f"{z_m:.6f}",
            "yaw_deg_unwrapped": f"{yaw_deg_unwrapped:.6f}",
            "yaw_rate_dps": f"{yaw_rate_dps:.6f}",
            "crc_rx": f"{crc_rx:04X}",
            "crc_calc": f"{crc_calc:04X}",
        }
    )

    info = {
        "frame_ok": frame_ok,
        "header_ok": header_ok,
        "version_ok": version_ok,
        "crc_ok": crc_ok,
        "source_id": source_id,
        "seq": seq,
        "unity_t_ms": unity_t_ms,
        "flags": flags,
        **bits,
        "speed_pct": speed_pct,
        "fps_hz": fps_hz,
        "x_m": x_m,
        "y_m": y_m,
        "z_m": z_m,
        "yaw_deg_unwrapped": yaw_deg_unwrapped,
        "yaw_rate_dps": yaw_rate_dps,
    }

    return row, info


def main() -> None:
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    timestamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    csv_path = LOG_DIR / f"{CSV_PREFIX}_{timestamp}.csv"

    sock: socket.socket | None = None
    csv_file = None

    packet_index = 0
    total_valid = 0
    total_bad_len = 0
    total_bad_header = 0
    total_bad_version = 0
    total_crc_bad = 0

    interval_packets = 0
    interval_valid = 0
    interval_crc_bad = 0
    interval_bad_len = 0

    last_packet_monotonic: float | None = None
    last_print_monotonic = time.monotonic()
    last_info: dict[str, Any] | None = None
    previous_seq: int | None = None

    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

        # Do odbioru broadcastu nie trzeba ustawiać SO_BROADCAST.
        # Wystarczy nasłuch na wszystkich interfejsach.
        sock.bind((LISTEN_IP, LISTEN_PORT))
        sock.settimeout(SOCKET_TIMEOUT_S)

        csv_file = open(csv_path, "w", newline="", encoding="utf-8")
        writer = csv.DictWriter(csv_file, fieldnames=CSV_COLUMNS)
        writer.writeheader()
        csv_file.flush()

        print("ATU1 UDP logger uruchomiony.")
        print(f"Nasłuch: {LISTEN_IP}:{LISTEN_PORT}")
        print(f"CSV: {csv_path.resolve()}")
        print("Konsola: podsumowanie raz na sekundę")
        print("Przerwanie: Ctrl+C\n")

        while True:
            try:
                data, addr = sock.recvfrom(RECV_BUFFER_SIZE)
            except socket.timeout:
                data = None
                addr = None

            now_monotonic = time.monotonic()

            if data is not None and addr is not None:
                packet_index += 1
                interval_packets += 1

                if last_packet_monotonic is None:
                    pc_gap_ms = None
                else:
                    pc_gap_ms = (now_monotonic - last_packet_monotonic) * 1000.0

                last_packet_monotonic = now_monotonic

                row, info = decode_atu1(
                    data=data,
                    addr=addr,
                    packet_index=packet_index,
                    pc_gap_ms=pc_gap_ms,
                    previous_seq=previous_seq,
                )

                writer.writerow(row)

                if len(data) != ATU1_LEN:
                    total_bad_len += 1
                    interval_bad_len += 1
                elif info is not None:
                    previous_seq = int(info["seq"])

                    if not info["header_ok"]:
                        total_bad_header += 1

                    if not info["version_ok"]:
                        total_bad_version += 1

                    if not info["crc_ok"]:
                        total_crc_bad += 1
                        interval_crc_bad += 1

                    if info["frame_ok"]:
                        total_valid += 1
                        interval_valid += 1
                        last_info = info

            if now_monotonic - last_print_monotonic >= PRINT_INTERVAL_S:
                elapsed = now_monotonic - last_print_monotonic
                rate_hz = interval_packets / elapsed if elapsed > 0 else 0.0

                if last_info is None:
                    print(
                        f"RX {rate_hz:6.1f} Hz | total={packet_index} "
                        f"ok={total_valid} bad_len={total_bad_len} "
                        f"crc_bad={total_crc_bad} | brak poprawnej ATU1"
                    )
                else:
                    print(
                        f"RX {rate_hz:6.1f} Hz | total={packet_index} "
                        f"ok={total_valid} crc_bad={total_crc_bad} "
                        f"bad_len={total_bad_len} | "
                        f"src={last_info['source_id']} "
                        f"seq={last_info['seq']} "
                        f"flags=0x{last_info['flags']:02X} "
                        f"valid={last_info['pose_valid']} "
                        f"deadman={last_info['deadman']} "
                        f"move={last_info['move_en']} "
                        f"safe={last_info['safe_to_drive']} "
                        f"hard={last_info['hard_stop']} "
                        f"speed={last_info['speed_pct']}% "
                        f"x={last_info['x_m']:.4f} "
                        f"y={last_info['y_m']:.4f} "
                        f"yaw={last_info['yaw_deg_unwrapped']:.2f}"
                    )

                # Flush raz na sekundę ogranicza narzut I/O, ale nadal
                # regularnie utrwala wszystkie zapisane rekordy.
                csv_file.flush()

                interval_packets = 0
                interval_valid = 0
                interval_crc_bad = 0
                interval_bad_len = 0
                last_print_monotonic = now_monotonic

    except KeyboardInterrupt:
        print("\nPrzerwano Ctrl+C. Zamykanie loggera...")

    finally:
        if csv_file is not None:
            csv_file.flush()
            csv_file.close()

        if sock is not None:
            sock.close()

        print("\n================ PODSUMOWANIE ================")
        print(f"Wszystkie datagramy: {packet_index}")
        print(f"Poprawne ATU1:       {total_valid}")
        print(f"Zła długość:         {total_bad_len}")
        print(f"Zły nagłówek:        {total_bad_header}")
        print(f"Zła wersja:          {total_bad_version}")
        print(f"Złe CRC:             {total_crc_bad}")
        print(f"CSV zapisany:        {csv_path.resolve()}")
        print("==============================================")


if __name__ == "__main__":
    main()
