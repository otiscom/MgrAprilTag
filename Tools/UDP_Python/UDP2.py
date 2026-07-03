#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
udp_atb1_atd1_logger.py

Logger UDP dla projektu:

    Unity / Android / AprilTag
        -> UDP ATB1
        -> ESP32 / Simulink
        -> UART ATU1
        -> STM32

Na jednym porcie UDP 5005 rozpoznaje:

1. ATB1 z Unity:
   - długość: 42 B
   - header: A1 1A

2. Rozszerzone ATD1 z ESP32:
   - pierwsze 96 B: standardowa diagnostyka ATD1
   - kolejne bajty: snapshot uart_bytes wysyłanych do STM32
   - obecnie:
       96 B ATD1
       38 B UART
       razem 134 B

3. Stary tekstowy debug:
   - AT1;...

4. UNKNOWN:
   - wszystkie pozostałe ramki

Nie wymaga zewnętrznych bibliotek.
"""

import csv
import datetime
import socket
import struct
import time
from pathlib import Path
from typing import Any


# ============================================================
# KONFIGURACJA
# ============================================================

UDP_IP = "0.0.0.0"
UDP_PORT = 5005

LOG_DIR = Path(".")

ATD1_CSV_NAME = "esp32_udp_diag.csv"
ATB1_CSV_NAME = "unity_atb1_rx.csv"

# Zapis ATB1 z Unity do osobnego CSV.
LOG_ATB1_TO_CSV = True

# Wypisywanie ramek w konsoli.
PRINT_ATB1_EVERY_PACKET = True
PRINT_ATD1_EVERY_PACKET = True
PRINT_UART_SNAPSHOT_HEX = True
PRINT_UNKNOWN = True

# Aktualnie uart_bytes ma 38 bajtów.
# Jest to wyłącznie kontrola diagnostyczna — pakiet nie zostanie
# odrzucony, jeśli długość będzie inna.
EXPECTED_UART_SNAPSHOT_LEN = 38

# Maksymalna liczba bajtów pokazywana dla UNKNOWN.
# None oznacza pokazanie całego pakietu.
UNKNOWN_HEX_MAX_BYTES = 160

# Maksymalny rozmiar datagramu pobieranego przez recvfrom().
UDP_RECEIVE_BUFFER_SIZE = 2048


# ============================================================
# FORMAT ATB1: UNITY -> ESP32
# ============================================================

ATB1_LEN = 42

ATB1_H0 = 0xA1
ATB1_H1 = 0x1A
ATB1_VERSION = 1

# Format little-endian:
#
# B B B B
# I I
# B B B B
# f f f f f f
# H
#
# Razem 42 B.
ATB1_STRUCT = struct.Struct(
    "<BBBBIIBBBBffffffH"
)


# ============================================================
# FORMAT ATD1: ESP32 -> PC
# ============================================================

# Pierwsze 96 bajtów pozostaje standardowym ATD1.
ATD1_BASE_LEN = 96

ATD1_H0 = 0xAD
ATD1_H1 = 0xD1
ATD1_VERSION = 1

# 4 bajty:
#   header 0
#   header 1
#   version
#   source_id
#
# 21 x uint32 = 84 B
# 8 x uint8   = 8 B
#
# Razem:
#   4 + 84 + 8 = 96 B
ATD1_STRUCT = struct.Struct(
    "<BBBB" + "I" * 21 + "BBBBBBBB"
)


# ============================================================
# KODY BŁĘDÓW DIAGNOSTYKI ESP32
# ============================================================

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


# ============================================================
# KOLUMNY CSV ATD1
# ============================================================

ATD1_COLUMNS = [
    "pc_time_iso",
    "pc_time_unix",
    "sender_ip",
    "sender_port",

    "datagram_len",
    "atd1_base_len",

    "source_id",
    "version",
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
    "last_error_text",

    # Doklejony snapshot UART.
    "uart_snapshot_len",
    "uart_snapshot_expected_len",
    "uart_snapshot_len_ok",
    "uart_snapshot_header_ascii",
    "uart_snapshot_header_hex",
    "uart_snapshot_is_atu1",
    "uart_snapshot_hex",
]


# ============================================================
# KOLUMNY CSV ATB1
# ============================================================

ATB1_COLUMNS = [
    "pc_time_iso",
    "pc_time_unix",
    "sender_ip",
    "sender_port",

    "version",
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
# CRC-16/CCITT-FALSE
#
# poly   = 0x1021
# init   = 0xFFFF
# xorout = 0x0000
# ============================================================

def crc16_ccitt_false(data: bytes) -> int:
    """Oblicza CRC-16/CCITT-FALSE."""

    crc = 0xFFFF

    for byte in data:
        crc ^= byte << 8

        for _ in range(8):
            if crc & 0x8000:
                crc = ((crc << 1) ^ 0x1021) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF

    return crc


# ============================================================
# FUNKCJE POMOCNICZE
# ============================================================

def pc_time_fields() -> tuple[str, float]:
    """
    Zwraca:
      - czas lokalny PC w formacie ISO,
      - czas Unix jako float.
    """

    pc_time_unix = time.time()

    pc_time_iso = (
        datetime.datetime
        .fromtimestamp(pc_time_unix)
        .astimezone()
        .isoformat(timespec="milliseconds")
    )

    return pc_time_iso, pc_time_unix


def hex_spaced(
    data: bytes,
    max_bytes: int | None = None,
) -> str:
    """
    Zwraca bajty w formacie:

        AA BB CC DD

    Przy max_bytes ucina wynik i dopisuje liczbę pominiętych
    bajtów.
    """

    if max_bytes is not None and len(data) > max_bytes:
        head = data[:max_bytes]

        return (
            " ".join(f"{byte:02X}" for byte in head)
            + f" ... (+{len(data) - max_bytes} bytes)"
        )

    return " ".join(f"{byte:02X}" for byte in data)


def bytes_to_printable_ascii(data: bytes) -> str:
    """
    Zamienia bajty drukowalne na ASCII.

    Bajty spoza zakresu drukowalnego są przedstawiane jako kropka.
    """

    return "".join(
        chr(byte) if 32 <= byte <= 126 else "."
        for byte in data
    )


def is_atb1(data: bytes) -> bool:
    """Sprawdza długość i nagłówek ATB1."""

    return (
        len(data) == ATB1_LEN
        and data[0] == ATB1_H0
        and data[1] == ATB1_H1
    )


def is_atd1(data: bytes) -> bool:
    """
    Sprawdza nagłówek rozszerzonego ATD1.

    Akceptujemy:
      - stare ATD1 o długości dokładnie 96 B,
      - nowe ATD1 z doklejonym uart_bytes, np. 134 B.
    """

    return (
        len(data) >= ATD1_BASE_LEN
        and data[0] == ATD1_H0
        and data[1] == ATD1_H1
    )


# ============================================================
# DEKODOWANIE ATB1
# ============================================================

def decode_atb1(
    data: bytes,
    addr: tuple[str, int],
) -> tuple[dict[str, Any], dict[str, Any]]:
    """
    Dekoduje ramkę ATB1.

    CRC jest liczone po pierwszych 40 bajtach.
    Odebrane CRC znajduje się w bajtach 40..41.
    """

    if len(data) != ATB1_LEN:
        raise ValueError(
            f"Niepoprawna długość ATB1: "
            f"{len(data)} B, oczekiwano {ATB1_LEN} B"
        )

    pc_time_iso, pc_time_unix = pc_time_fields()

    rx_crc = struct.unpack_from("<H", data, 40)[0]
    calc_crc = crc16_ccitt_false(data[:40])

    crc_ok = rx_crc == calc_crc

    (
        header_0,
        header_1,
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
        unpacked_crc,
    ) = ATB1_STRUCT.unpack(data)

    del header_0
    del header_1
    del reserved
    del unpacked_crc

    valid = 1 if flags & (1 << 0) else 0
    deadman = 1 if flags & (1 << 1) else 0
    move_en = 1 if flags & (1 << 2) else 0

    row = {
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "sender_ip": addr[0],
        "sender_port": addr[1],

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

    info = {
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

    return row, info


# ============================================================
# DEKODOWANIE ROZSZERZONEGO ATD1
# ============================================================

def decode_atd1(
    data: bytes,
    addr: tuple[str, int],
) -> tuple[dict[str, Any], dict[str, Any]]:
    """
    Dekoduje rozszerzone ATD1.

    Układ datagramu:

        data[0:96]
            standardowa ramka ATD1

        data[96:]
            snapshot uart_bytes wysyłanych z modelu ESP32
            do STM32

    Dla aktualnego modelu:

        ATD1 = 96 B
        UART = 38 B
        razem = 134 B
    """

    if len(data) < ATD1_BASE_LEN:
        raise ValueError(
            f"ATD1 jest za krótkie: "
            f"{len(data)} B, wymagane minimum {ATD1_BASE_LEN} B"
        )

    pc_time_iso, pc_time_unix = pc_time_fields()

    atd1_data = data[:ATD1_BASE_LEN]
    uart_data = data[ATD1_BASE_LEN:]

    values = ATD1_STRUCT.unpack(atd1_data)

    header_0 = values[0]
    header_1 = values[1]
    version = values[2]
    source_id = values[3]

    if header_0 != ATD1_H0 or header_1 != ATD1_H1:
        raise ValueError(
            "Niepoprawny header ATD1: "
            f"{header_0:02X} {header_1:02X}"
        )

    # 21 wartości uint32.
    u32_values = values[4:25]

    # 8 flag uint8.
    u8_values = values[25:33]

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
    ) = u32_values

    (
        source_seen,
        fresh_300ms,
        in_session,

        last_valid,
        last_deadman,
        last_move_en,

        last_port_id,
        last_error_code,
    ) = u8_values

    loss_percent = loss_permille / 10.0

    last_error_text = LAST_ERROR_CODE_TEXT.get(
        last_error_code,
        f"unknown_{last_error_code}",
    )

    uart_header_bytes = uart_data[:4]

    uart_header_ascii = bytes_to_printable_ascii(
        uart_header_bytes
    )

    uart_header_hex = hex_spaced(
        uart_header_bytes
    )

    uart_snapshot_hex = hex_spaced(
        uart_data
    )

    uart_snapshot_len = len(uart_data)

    uart_snapshot_len_ok = (
        uart_snapshot_len == EXPECTED_UART_SNAPSHOT_LEN
    )

    uart_snapshot_is_atu1 = (
        uart_data.startswith(b"ATU1")
    )

    row = {
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "sender_ip": addr[0],
        "sender_port": addr[1],

        "datagram_len": len(data),
        "atd1_base_len": ATD1_BASE_LEN,

        "source_id": source_id,
        "version": version,
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
        "last_error_text": last_error_text,

        "uart_snapshot_len": uart_snapshot_len,
        "uart_snapshot_expected_len": (
            EXPECTED_UART_SNAPSHOT_LEN
        ),
        "uart_snapshot_len_ok": (
            1 if uart_snapshot_len_ok else 0
        ),
        "uart_snapshot_header_ascii": (
            uart_header_ascii
        ),
        "uart_snapshot_header_hex": (
            uart_header_hex
        ),
        "uart_snapshot_is_atu1": (
            1 if uart_snapshot_is_atu1 else 0
        ),
        "uart_snapshot_hex": uart_snapshot_hex,
    }

    info = dict(row)

    info["loss_percent_float"] = loss_percent
    info["uart_snapshot_bytes"] = uart_data
    info["uart_snapshot_len_ok_bool"] = (
        uart_snapshot_len_ok
    )
    info["uart_snapshot_is_atu1_bool"] = (
        uart_snapshot_is_atu1
    )

    return row, info


# ============================================================
# LOKALNE STATYSTYKI LOGGERA PC
# ============================================================

class LocalStats:
    def __init__(self) -> None:
        self.atb1_total = 0
        self.atb1_crc_ok = 0
        self.atb1_crc_bad = 0

        self.atd1_total = 0
        self.atd1_with_uart = 0
        self.atd1_uart_len_bad = 0

        self.text_at1_total = 0
        self.unknown_total = 0
        self.decode_error_total = 0

        self.start_time = time.time()

    def print_summary(self) -> None:
        elapsed = time.time() - self.start_time

        print()
        print("================ LOGGER SUMMARY ================")
        print(f"czas pracy:              {elapsed:.1f} s")
        print(f"ATB1 total:              {self.atb1_total}")
        print(f"ATB1 crc ok:             {self.atb1_crc_ok}")
        print(f"ATB1 crc bad:            {self.atb1_crc_bad}")
        print(f"ATD1 total:              {self.atd1_total}")
        print(f"ATD1 z UART snapshot:    {self.atd1_with_uart}")
        print(f"ATD1 UART zły len:       {self.atd1_uart_len_bad}")
        print(f"TEXT AT1 total:          {self.text_at1_total}")
        print(f"UNKNOWN total:           {self.unknown_total}")
        print(f"decode errors:           {self.decode_error_total}")
        print("================================================")


# ============================================================
# MAIN
# ============================================================

def main() -> None:
    LOG_DIR.mkdir(
        parents=True,
        exist_ok=True,
    )

    atd1_csv_path = LOG_DIR / ATD1_CSV_NAME
    atb1_csv_path = LOG_DIR / ATB1_CSV_NAME

    stats = LocalStats()

    sock: socket.socket | None = None

    atd1_file = None
    atb1_file = None

    try:
        # ----------------------------------------------------
        # Socket UDP
        # ----------------------------------------------------

        sock = socket.socket(
            socket.AF_INET,
            socket.SOCK_DGRAM,
        )

        sock.setsockopt(
            socket.SOL_SOCKET,
            socket.SO_REUSEADDR,
            1,
        )

        sock.bind(
            (
                UDP_IP,
                UDP_PORT,
            )
        )

        # Ułatwia poprawną obsługę Ctrl+C na Windows.
        sock.settimeout(0.5)

        # ----------------------------------------------------
        # CSV ATD1
        # ----------------------------------------------------

        atd1_file = open(
            atd1_csv_path,
            mode="w",
            newline="",
            encoding="utf-8",
        )

        atd1_writer = csv.DictWriter(
            atd1_file,
            fieldnames=ATD1_COLUMNS,
        )

        atd1_writer.writeheader()
        atd1_file.flush()

        # ----------------------------------------------------
        # CSV ATB1
        # ----------------------------------------------------

        atb1_writer = None

        if LOG_ATB1_TO_CSV:
            atb1_file = open(
                atb1_csv_path,
                mode="w",
                newline="",
                encoding="utf-8",
            )

            atb1_writer = csv.DictWriter(
                atb1_file,
                fieldnames=ATB1_COLUMNS,
            )

            atb1_writer.writeheader()
            atb1_file.flush()

        # ----------------------------------------------------
        # Informacje startowe
        # ----------------------------------------------------

        print("Serwer UDP aktywny.")
        print(f"Slucham na {UDP_IP}:{UDP_PORT}")
        print(f"CSV ATD1: {atd1_csv_path.resolve()}")

        if LOG_ATB1_TO_CSV:
            print(
                f"CSV ATB1: "
                f"{atb1_csv_path.resolve()}"
            )
        else:
            print("CSV ATB1: OFF")

        print(
            "Rozszerzone ATD1: "
            f"{ATD1_BASE_LEN} B diagnostyki + "
            f"{EXPECTED_UART_SNAPSHOT_LEN} B UART"
        )

        print("Przerwanie: Ctrl+C")
        print()

        # ----------------------------------------------------
        # Główna pętla
        # ----------------------------------------------------

        while True:
            try:
                data, addr = sock.recvfrom(
                    UDP_RECEIVE_BUFFER_SIZE
                )

            except socket.timeout:
                continue

            # ------------------------------------------------
            # Stary tekstowy debug AT1
            # ------------------------------------------------

            if data.startswith(b"AT1;"):
                stats.text_at1_total += 1

                print(
                    f"\nTEXT AT1 from "
                    f"{addr[0]}:{addr[1]}:"
                )

                print(
                    data.decode(
                        "ascii",
                        errors="replace",
                    ).strip()
                )

                continue

            # ------------------------------------------------
            # ATB1
            # ------------------------------------------------

            if is_atb1(data):
                try:
                    row, info = decode_atb1(
                        data,
                        addr,
                    )

                except (
                    ValueError,
                    struct.error,
                ) as error:
                    stats.decode_error_total += 1

                    print(
                        f"ATB1 DECODE ERROR "
                        f"from {addr[0]}:{addr[1]}: "
                        f"{error}"
                    )

                    continue

                stats.atb1_total += 1

                if info["crc_ok"]:
                    stats.atb1_crc_ok += 1
                else:
                    stats.atb1_crc_bad += 1

                if atb1_writer is not None:
                    atb1_writer.writerow(row)
                    atb1_file.flush()

                if PRINT_ATB1_EVERY_PACKET:
                    version_note = (
                        ""
                        if info["version"] == ATB1_VERSION
                        else (
                            f" ver_bad="
                            f"{info['version']}"
                        )
                    )

                    print(
                        f"ATB1 from "
                        f"{addr[0]}:{addr[1]}: "
                        f"src={info['source_id']} "
                        f"seq={info['seq']} "
                        f"valid={info['valid']} "
                        f"deadman={info['deadman']} "
                        f"move={info['move_en']} "
                        f"speed={info['speed_pct']}% "
                        f"x={info['x_m']:.4f} "
                        f"y={info['y_m']:.4f} "
                        f"yaw={info['yaw_deg']:.2f} "
                        f"crc_ok={info['crc_ok']}"
                        f"{version_note}"
                    )

                continue

            # ------------------------------------------------
            # ATD1 + snapshot UART
            # ------------------------------------------------

            if is_atd1(data):
                try:
                    row, info = decode_atd1(
                        data,
                        addr,
                    )

                except (
                    ValueError,
                    struct.error,
                ) as error:
                    stats.decode_error_total += 1

                    print(
                        f"ATD1 DECODE ERROR "
                        f"from {addr[0]}:{addr[1]}: "
                        f"{error}"
                    )

                    continue

                stats.atd1_total += 1

                if info["uart_snapshot_len"] > 0:
                    stats.atd1_with_uart += 1

                if not info["uart_snapshot_len_ok_bool"]:
                    stats.atd1_uart_len_bad += 1

                atd1_writer.writerow(row)
                atd1_file.flush()

                if PRINT_ATD1_EVERY_PACKET:
                    version_note = (
                        ""
                        if info["version"] == ATD1_VERSION
                        else (
                            f" ver_bad="
                            f"{info['version']}"
                        )
                    )

                    uart_len_note = (
                        ""
                        if info[
                            "uart_snapshot_len_ok_bool"
                        ]
                        else (
                            f" uart_len_expected="
                            f"{EXPECTED_UART_SNAPSHOT_LEN}"
                        )
                    )

                    print(
                        f"ATD1 from ESP32 "
                        f"{addr[0]}:{addr[1]}: "
                        f"src={info['source_id']} "
                        f"tx={info['tx_counter']} "
                        f"rx={info['rx_ok_total']} "
                        f"lost={info['seq_lost_total']} "
                        f"dup="
                        f"{info['duplicate_or_old_total']} "
                        f"crc_bad="
                        f"{info['crc_bad_total']} "
                        f"gap="
                        f"{info['last_gap_ms']}ms "
                        f"max_gap="
                        f"{info['max_gap_ms']}ms "
                        f"avg_gap="
                        f"{info['avg_gap_ms']}ms "
                        f"loss="
                        f"{info['loss_percent_float']:.1f}% "
                        f"valid_ratio="
                        f"{info['valid_ratio_percent']}% "
                        f"session="
                        f"{info['session_id']} "
                        f"fresh="
                        f"{info['fresh_300ms']} "
                        f"valid="
                        f"{info['last_valid']} "
                        f"deadman="
                        f"{info['last_deadman']} "
                        f"move="
                        f"{info['last_move_en']} "
                        f"err="
                        f"{info['last_error_code']}"
                        f"({info['last_error_text']}) "
                        f"total_len="
                        f"{info['datagram_len']} "
                        f"uart_len="
                        f"{info['uart_snapshot_len']} "
                        f"uart_header="
                        f"{info['uart_snapshot_header_ascii']} "
                        f"uart_is_ATU1="
                        f"{info['uart_snapshot_is_atu1']}"
                        f"{version_note}"
                        f"{uart_len_note}"
                    )

                    if (
                        PRINT_UART_SNAPSHOT_HEX
                        and info["uart_snapshot_len"] > 0
                    ):
                        print(
                            "  UART snapshot HEX: "
                            f"{info['uart_snapshot_hex']}"
                        )

                continue

            # ------------------------------------------------
            # UNKNOWN
            # ------------------------------------------------

            stats.unknown_total += 1

            if PRINT_UNKNOWN:
                print(
                    f"UNKNOWN len={len(data)} "
                    f"from {addr[0]}:{addr[1]} "
                    f"hex={hex_spaced(data, UNKNOWN_HEX_MAX_BYTES)}"
                )

    except KeyboardInterrupt:
        print()
        print("Przerwano Ctrl+C. Zamykanie loggera...")

    except OSError as error:
        print()
        print(f"Błąd socketu UDP: {error}")

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

        print(
            f"CSV ATD1 zapisany: "
            f"{atd1_csv_path.resolve()}"
        )

        if LOG_ATB1_TO_CSV:
            print(
                f"CSV ATB1 zapisany: "
                f"{atb1_csv_path.resolve()}"
            )


if __name__ == "__main__":
    main()