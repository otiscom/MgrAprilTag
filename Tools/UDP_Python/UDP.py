#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
udp_atb1_atd1_logger.py

Logger UDP dla projektu Unity/Android/AprilTag -> ESP32/Simulink -> STM32.

Rozpoznaje na jednym porcie UDP 5005:
  - ATB1 z Unity: len=42,  header A1 1A
  - ATD1 z ESP32: len=96,  header AD D1
  - stare tekstowe AT1; z wcześniejszej wersji debugowej
  - UNKNOWN dla pozostałych ramek

Nie wymaga zewnętrznych bibliotek.
"""

import socket
import struct
import csv
import time
import datetime
from pathlib import Path

# ============================================================
# KONFIGURACJA - tutaj najczęściej będziesz coś zmieniał
# ============================================================

UDP_IP = "0.0.0.0"
UDP_PORT = 5005

# Nazwy plików CSV. Domyślnie zapis w tym samym folderze, z którego uruchamiasz skrypt.
# Jeśli chcesz folder logs, ustaw np. LOG_DIR = Path("logs")
LOG_DIR = Path(".")
ATD1_CSV_NAME = "esp32_udp_diag.csv"
ATB1_CSV_NAME = "unity_atb1_rx.csv"

# ATD1 zapisujemy zawsze. ATB1 można wyłączyć, jeżeli Unity wysyła tylko do ESP32
# i PC ma logować wyłącznie diagnostykę z ESP32.
LOG_ATB1_TO_CSV = True

# ATB1 może przychodzić np. 20 Hz, więc konsola może spamować.
# Do szybkiego debugowania zostaw True, do długich pomiarów możesz dać False.
PRINT_ATB1_EVERY_PACKET = True
PRINT_ATD1_EVERY_PACKET = True
PRINT_UNKNOWN = True

# Ile bajtów HEX wypisać dla UNKNOWN. None = cały pakiet.
UNKNOWN_HEX_MAX_BYTES = 96

# ============================================================
# FORMATY RAMEK
# ============================================================

ATB1_LEN = 42
ATB1_H0 = 0xA1
ATB1_H1 = 0x1A
ATB1_VERSION = 1
ATB1_STRUCT = struct.Struct("<BBBBIIBBBBffffffH")

ATD1_LEN = 96
ATD1_H0 = 0xAD
ATD1_H1 = 0xD1
ATD1_VERSION = 1
# 4 bajty nagłówka + 21 x uint32 + 8 x uint8 = 96 bajtów
ATD1_STRUCT = struct.Struct("<BBBB" + "I" * 21 + "BBBBBBBB")

LAST_ERROR_CODE_TEXT = {
    0: "none_or_unknown",
    1: "OK",
    2: "len_bad",
    3: "header_bad",
    4: "version_bad",
    5: "source_bad",
    6: "crc_bad",
    7: "duplicate_or_old",
}

ATD1_COLUMNS = [
    "pc_time_iso",
    "pc_time_unix",
    "sender_ip",
    "sender_port",
    "source_id",
    "now_ms",
    "tx_counter",
    "rx_ok_total",
    "crc_bad_total",
    "seq_lost_total",
    "duplicate_or_old_total",
    "valid_count",
    "deadman_count",
    "move_en_count",
    "last_seq",
    "last_gap_ms",
    "max_gap_ms",
    "avg_gap_ms",
    "loss_permille",
    "loss_percent",
    "valid_ratio_percent",
    "session_id",
    "session_rx_ok",
    "session_lost",
    "session_max_gap_ms",
    "age_ms",
    "last_unity_t_ms",
    "source_seen",
    "fresh_300ms",
    "in_session",
    "last_valid",
    "last_deadman",
    "last_move_en",
    "last_port_id",
    "last_error_code",
]

ATB1_COLUMNS = [
    "pc_time_iso",
    "pc_time_unix",
    "sender_ip",
    "sender_port",
    "source_id",
    "seq",
    "t_ms",
    "flags",
    "valid",
    "deadman",
    "move_en",
    "speed_pct",
    "fps_hz",
    "x_m",
    "y_m",
    "z_m",
    "yaw_deg",
    "yaw_deg_unwrapped",
    "yaw_rate_dps",
    "crc_rx",
    "crc_calc",
    "crc_ok",
]


# ============================================================
# CRC - zgodne z Unity/ESP32: CRC-16/CCITT-FALSE
# poly=0x1021, init=0xFFFF, xorout=0x0000
# ============================================================

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


# ============================================================
# Pomocnicze funkcje czasu / HEX / identyfikacji ramek
# ============================================================

def pc_time_fields():
    """Zwraca czas PC jako ISO i unix seconds."""
    pc_time_unix = time.time()
    pc_time_iso = datetime.datetime.fromtimestamp(pc_time_unix).astimezone().isoformat(timespec="milliseconds")
    return pc_time_iso, pc_time_unix


def hex_spaced(data: bytes, max_bytes=None) -> str:
    """HEX w formacie 'AA BB CC'. Przy max_bytes ucina długi pakiet."""
    if max_bytes is not None and len(data) > max_bytes:
        head = data[:max_bytes]
        return " ".join(f"{b:02X}" for b in head) + f" ... (+{len(data) - max_bytes} bytes)"
    return " ".join(f"{b:02X}" for b in data)


def is_atb1(data: bytes) -> bool:
    return len(data) == ATB1_LEN and data[0] == ATB1_H0 and data[1] == ATB1_H1


def is_atd1(data: bytes) -> bool:
    return len(data) == ATD1_LEN and data[0] == ATD1_H0 and data[1] == ATD1_H1


# ============================================================
# Dekodowanie ATB1 z Unity
# ============================================================

def decode_atb1(data: bytes, addr):
    """
    Dekoduje ATB1.
    Zakładamy, że długość i header zostały już sprawdzone przez is_atb1().
    CRC może być błędne, ale dane nadal są rozpakowywane diagnostycznie.
    """
    pc_time_iso, pc_time_unix = pc_time_fields()

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
        crc,
    ) = ATB1_STRUCT.unpack(data)

    valid = 1 if (flags & (1 << 0)) else 0
    deadman = 1 if (flags & (1 << 1)) else 0
    move_en = 1 if (flags & (1 << 2)) else 0

    row = {
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "sender_ip": addr[0],
        "sender_port": addr[1],
        "source_id": source_id,
        "seq": seq,
        "t_ms": t_ms,
        "flags": flags,
        "valid": valid,
        "deadman": deadman,
        "move_en": move_en,
        "speed_pct": speed_pct,
        "fps_hz": fps_hz,
        "x_m": f"{x_m:.6f}",
        "y_m": f"{y_m:.6f}",
        "z_m": f"{z_m:.6f}",
        "yaw_deg": f"{yaw_deg:.6f}",
        "yaw_deg_unwrapped": f"{yaw_deg_unwrapped:.6f}",
        "yaw_rate_dps": f"{yaw_rate_dps:.6f}",
        "crc_rx": f"{rx_crc:04X}",
        "crc_calc": f"{calc_crc:04X}",
        "crc_ok": 1 if crc_ok else 0,
    }

    return row, {
        "version": version,
        "source_id": source_id,
        "seq": seq,
        "t_ms": t_ms,
        "flags": flags,
        "valid": valid,
        "deadman": deadman,
        "move_en": move_en,
        "speed_pct": speed_pct,
        "fps_hz": fps_hz,
        "x_m": x_m,
        "y_m": y_m,
        "z_m": z_m,
        "yaw_deg": yaw_deg,
        "yaw_deg_unwrapped": yaw_deg_unwrapped,
        "yaw_rate_dps": yaw_rate_dps,
        "crc_rx": rx_crc,
        "crc_calc": calc_crc,
        "crc_ok": crc_ok,
    }


# ============================================================
# Dekodowanie ATD1 z ESP32
# ============================================================

def decode_atd1(data: bytes, addr):
    """
    Dekoduje ATD1.
    ATD1 według opisu nie ma CRC - traktujemy header/len/version jako diagnostykę poprawności.
    """
    pc_time_iso, pc_time_unix = pc_time_fields()

    values = ATD1_STRUCT.unpack(data)

    h0 = values[0]
    h1 = values[1]
    version = values[2]
    debug_source_id = values[3]

    u32 = values[4:25]
    flags8 = values[25:33]

    (
        now_ms,
        tx_counter,
        rx_ok_total,
        crc_bad_total,
        seq_lost_total,
        duplicate_or_old_total,
        valid_count,
        deadman_count,
        move_en_count,
        last_seq,
        last_gap_ms,
        max_gap_ms,
        avg_gap_ms,
        loss_permille,
        valid_ratio_percent,
        session_id,
        session_rx_ok,
        session_lost,
        session_max_gap_ms,
        age_ms,
        last_unity_t_ms,
    ) = u32

    (
        source_seen,
        fresh_300ms,
        in_session,
        last_valid,
        last_deadman,
        last_move_en,
        last_port_id,
        last_error_code,
    ) = flags8

    loss_percent = loss_permille / 10.0

    row = {
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "sender_ip": addr[0],
        "sender_port": addr[1],
        "source_id": debug_source_id,
        "now_ms": now_ms,
        "tx_counter": tx_counter,
        "rx_ok_total": rx_ok_total,
        "crc_bad_total": crc_bad_total,
        "seq_lost_total": seq_lost_total,
        "duplicate_or_old_total": duplicate_or_old_total,
        "valid_count": valid_count,
        "deadman_count": deadman_count,
        "move_en_count": move_en_count,
        "last_seq": last_seq,
        "last_gap_ms": last_gap_ms,
        "max_gap_ms": max_gap_ms,
        "avg_gap_ms": avg_gap_ms,
        "loss_permille": loss_permille,
        "loss_percent": f"{loss_percent:.1f}",
        "valid_ratio_percent": valid_ratio_percent,
        "session_id": session_id,
        "session_rx_ok": session_rx_ok,
        "session_lost": session_lost,
        "session_max_gap_ms": session_max_gap_ms,
        "age_ms": age_ms,
        "last_unity_t_ms": last_unity_t_ms,
        "source_seen": source_seen,
        "fresh_300ms": fresh_300ms,
        "in_session": in_session,
        "last_valid": last_valid,
        "last_deadman": last_deadman,
        "last_move_en": last_move_en,
        "last_port_id": last_port_id,
        "last_error_code": last_error_code,
    }

    info = dict(row)
    info["version"] = version
    info["loss_percent_float"] = loss_percent
    info["last_error_text"] = LAST_ERROR_CODE_TEXT.get(last_error_code, f"unknown_{last_error_code}")

    return row, info


# ============================================================
# Lokalne statystyki loggera PC
# ============================================================

class LocalStats:
    def __init__(self):
        self.atb1_total = 0
        self.atb1_crc_ok = 0
        self.atb1_crc_bad = 0
        self.atd1_total = 0
        self.text_at1_total = 0
        self.unknown_total = 0
        self.start_time = time.time()

    def print_summary(self):
        elapsed = time.time() - self.start_time
        print("\n================ LOGGER SUMMARY ================")
        print(f"czas pracy:          {elapsed:.1f} s")
        print(f"ATB1 total:          {self.atb1_total}")
        print(f"ATB1 crc ok:         {self.atb1_crc_ok}")
        print(f"ATB1 crc bad:        {self.atb1_crc_bad}")
        print(f"ATD1 total:          {self.atd1_total}")
        print(f"TEXT AT1 total:      {self.text_at1_total}")
        print(f"UNKNOWN total:       {self.unknown_total}")
        print("================================================")


# ============================================================
# Main
# ============================================================

def main():
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    atd1_csv_path = LOG_DIR / ATD1_CSV_NAME
    atb1_csv_path = LOG_DIR / ATB1_CSV_NAME

    stats = LocalStats()

    sock = None
    atd1_file = None
    atb1_file = None

    try:
        # UDP socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((UDP_IP, UDP_PORT))

        # Timeout nie jest wymagany do odbioru, ale ułatwia stabilną obsługę Ctrl+C na Windowsie.
        sock.settimeout(0.5)

        # CSV ATD1
        atd1_file = open(atd1_csv_path, mode="w", newline="", encoding="utf-8")
        atd1_writer = csv.DictWriter(atd1_file, fieldnames=ATD1_COLUMNS)
        atd1_writer.writeheader()
        atd1_file.flush()

        # CSV ATB1 opcjonalnie
        atb1_writer = None
        if LOG_ATB1_TO_CSV:
            atb1_file = open(atb1_csv_path, mode="w", newline="", encoding="utf-8")
            atb1_writer = csv.DictWriter(atb1_file, fieldnames=ATB1_COLUMNS)
            atb1_writer.writeheader()
            atb1_file.flush()

        print("Serwer UDP aktywny.")
        print(f"Slucham na {UDP_IP}:{UDP_PORT}")
        print(f"CSV ATD1: {atd1_csv_path}")
        if LOG_ATB1_TO_CSV:
            print(f"CSV ATB1: {atb1_csv_path}")
        else:
            print("CSV ATB1: OFF")
        print("Przerwanie: Ctrl+C\n")

        while True:
            try:
                data, addr = sock.recvfrom(1024)
            except socket.timeout:
                continue

            # Zachowanie kompatybilności z wcześniejszym tekstowym debugiem AT1.
            if data.startswith(b"AT1;"):
                stats.text_at1_total += 1
                print(f"\nTEXT AT1 from {addr[0]}:{addr[1]}:")
                print(data.decode("ascii", errors="replace").strip())
                continue

            if is_atb1(data):
                stats.atb1_total += 1
                row, info = decode_atb1(data, addr)

                if info["crc_ok"]:
                    stats.atb1_crc_ok += 1
                else:
                    stats.atb1_crc_bad += 1

                if atb1_writer is not None:
                    atb1_writer.writerow(row)
                    atb1_file.flush()

                if PRINT_ATB1_EVERY_PACKET:
                    version_note = "" if info["version"] == ATB1_VERSION else f" ver_bad={info['version']}"
                    print(
                        f"ATB1 from {addr[0]}:{addr[1]}: "
                        f"src={info['source_id']} seq={info['seq']} "
                        f"valid={info['valid']} deadman={info['deadman']} move={info['move_en']} "
                        f"x={info['x_m']:.4f} y={info['y_m']:.4f} yaw={info['yaw_deg']:.2f} "
                        f"crc_ok={info['crc_ok']}"
                        f"{version_note}"
                    )

                continue

            if is_atd1(data):
                stats.atd1_total += 1
                row, info = decode_atd1(data, addr)

                atd1_writer.writerow(row)
                atd1_file.flush()

                if PRINT_ATD1_EVERY_PACKET:
                    version_note = "" if info["version"] == ATD1_VERSION else f" ver_bad={info['version']}"
                    print(
                        f"ATD1 from ESP32 {addr[0]}:{addr[1]}: "
                        f"src={info['source_id']} tx={info['tx_counter']} "
                        f"rx={info['rx_ok_total']} lost={info['seq_lost_total']} "
                        f"dup={info['duplicate_or_old_total']} crc_bad={info['crc_bad_total']} "
                        f"gap={info['last_gap_ms']}ms max_gap={info['max_gap_ms']}ms "
                        f"avg_gap={info['avg_gap_ms']}ms loss={info['loss_percent_float']:.1f}% "
                        f"valid_ratio={info['valid_ratio_percent']}% "
                        f"session={info['session_id']} fresh={info['fresh_300ms']} "
                        f"err={info['last_error_code']}({info['last_error_text']})"
                        f"{version_note}"
                    )

                continue

            # Nieznana ramka: może być zły len, zły header albo coś obcego w sieci.
            stats.unknown_total += 1
            if PRINT_UNKNOWN:
                print(
                    f"UNKNOWN len={len(data)} from {addr[0]}:{addr[1]} "
                    f"hex={hex_spaced(data, UNKNOWN_HEX_MAX_BYTES)}"
                )

    except KeyboardInterrupt:
        print("\nPrzerwano Ctrl+C. Zamykanie loggera...")

    finally:
        if atd1_file is not None:
            atd1_file.flush()
            atd1_file.close()

        if atb1_file is not None:
            atb1_file.flush()
            atb1_file.close()

        if sock is not None:
            sock.close()

        stats.print_summary()
        print(f"CSV ATD1 zapisany: {atd1_csv_path}")
        if LOG_ATB1_TO_CSV:
            print(f"CSV ATB1 zapisany: {atb1_csv_path}")


if __name__ == "__main__":
    main()
