#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
hil_udp_session_logger_final.py

Finalny logger sesji HIL dla toru:
Unity / Android / AprilTag
 -> UDP ATB1, port 5005
 -> ESP32 / Simulink
 -> UART ATU1 do STM32
 + diagnostyka ESP32 ATD1
 + mirror UART ATU1 po UDP 5010

Logger odbiera równolegle:
  - ATB1 z Unity / Android / AprilTag       zwykle port 5005
  - ATD1 diagnostyka ESP32 / Simulink       zwykle port 5007 albo 5005
  - ATU1 stream / mirror UART do STM32      port 5010

Najważniejsze założenie:
  source_id = 1 -> Oppo
  source_id = 2 -> Realme

Oba telefony mogą nadawać na tym samym porcie 5005.
Rozdzielanie telefonów odbywa się po source_id z ramki ATB1,
a nie po porcie i nie po IP.

Workflow:
1. Ustaw SESSION_NAME i LOG_DIR.
2. Uruchom skrypt.
3. Uruchom ESP32 / Monitor & Tune.
4. Kliknij MEASURE w Unity.
5. Po pomiarze wciśnij Ctrl+C.
6. Skrypt zapisze komplet CSV + summary.

Jeżeli zamkniesz terminal bez Ctrl+C, proces zostanie ubity przez system.
Raw CSV są flushowane co sekundę, więc większość danych zostanie na dysku,
ale summary może nie zdążyć się zapisać. Najpewniejsze zakończenie to Ctrl+C.

Bez bibliotek zewnętrznych.
"""

from __future__ import annotations

import atexit
import csv
import datetime as dt
import math
import select
import signal
import socket
import struct
import sys
import time
from collections import defaultdict
from pathlib import Path
from typing import Any


# ============================================================
# KONFIGURACJA SESJI
# ============================================================

SESSION_NAME = "HIL_DYNAMIC_Square_manual_2SRC_20Hz_20s_rep2"

# Przykład pod Windows:
# LOG_DIR = Path(r"C:\Users\mateu\Desktop\Mgr\logs\HIL")
LOG_DIR = Path("logs/HIL_Tests/Direction Test/Square")

PHONE_NAMES = {
    0: "none_or_hardstop",
    1: "Oppo",
    2: "Realme",
    3: "src3",
    4: "src4",
}

LISTEN_IP = "0.0.0.0"

# 5005 - ATB1 z telefonów, opcjonalnie też stary wariant ATD1
# 5007 - diagnostyka ESP32 ATD1
# 5010 - ciągły mirror ATU1, czyli kopia tego, co idzie UART do STM32
LISTEN_PORTS = [5005, 5007, 5010]

EXPECTED_ATB1_HZ_PER_SOURCE = 20.0
EXPECTED_ATU1_HZ = 200.0

SESSION_GAP_MS = 2000.0
PRINT_INTERVAL_S = 1.0
SOCKET_SELECT_TIMEOUT_S = 0.2
RECV_BUFFER_SIZE = 4096
RAW_HEX_MAX_BYTES = 96
FLUSH_INTERVAL_S = 1.0

PRINT_UNKNOWN = True

# Pliki rozdzielone per telefon/source.
WRITE_SPLIT_ATB1_BY_SOURCE = True
WRITE_SPLIT_ATD1_BY_SOURCE = True

# ATU1 to jedno wyjście UART do STM32, więc domyślnie zostaje jednym plikiem.
WRITE_SPLIT_ATU1_BY_SELECTED_SOURCE = False

# Plik porównawczy Oppo vs Realme.
WRITE_SOURCE_COMPARISON_CSV = True


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
ATD1_STRUCT = struct.Struct("<BBBB" + "I" * 21 + "BBBBBBBB")

ATU1_LEN = 38
ATU1_H0 = 0xA5
ATU1_H1 = 0x5A
ATU1_VERSION = 1
ATU1_STRUCT = struct.Struct("<BBBBIIBBBBfffffH")

ATD1_WITH_ATU1_LEN = ATD1_LEN + ATU1_LEN

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
# KOLUMNY CSV
# ============================================================

ATB1_COLUMNS = [
    "test_name", "device_name", "pc_time_iso", "pc_time_unix", "session_elapsed_s",
    "local_port", "sender_ip", "sender_port",
    "frame_type", "len", "len_ok", "header_ok", "version", "version_ok",
    "source_id", "seq", "t_ms",
    "flags", "valid", "deadman", "move_en",
    "speed_pct", "fps_hz", "reserved",
    "x_m", "y_m", "z_m", "yaw_deg", "yaw_deg_unwrapped", "yaw_rate_dps",
    "crc_rx", "crc_calc", "crc_ok",
    "dt_rx_ms", "rx_hz_inst", "seq_delta", "lost_est", "duplicate_or_old", "gap_over_2s",
    "raw_hex",
]

ATB1_SUMMARY_COLUMNS = [
    "test_name", "source_id", "device_name",
    "rx_total", "rx_good", "crc_bad", "version_bad",
    "valid_count", "deadman_count", "move_en_count",
    "duplicate_or_old_total", "lost_est_total", "gap_over_2s_total",
    "first_pc_time_unix", "last_pc_time_unix", "duration_s",
    "expected_hz", "actual_hz_good", "valid_ratio_percent",
    "dt_mean_ms", "dt_max_ms",
]

ATD1_COLUMNS = [
    "test_name", "device_name", "pc_time_iso", "pc_time_unix", "session_elapsed_s",
    "local_port", "sender_ip", "sender_port",
    "frame_type", "len", "version", "version_ok",
    "source_id", "now_ms", "tx_counter",
    "rx_ok_total", "crc_bad_total", "seq_lost_total", "duplicate_or_old_total",
    "valid_count", "deadman_count", "move_en_count",
    "last_seq", "last_gap_ms", "max_gap_ms", "avg_gap_ms",
    "loss_permille", "loss_percent", "valid_ratio_percent",
    "session_id", "session_rx_ok", "session_lost", "session_max_gap_ms",
    "age_ms", "last_unity_t_ms",
    "source_seen", "fresh_300ms", "in_session",
    "last_valid", "last_deadman", "last_move_en",
    "last_port_id", "last_error_code", "last_error_text",
    "has_atu1_snapshot",
    "raw_hex",
]

ATU1_COLUMNS = [
    "test_name", "device_name", "pc_time_iso", "pc_time_unix", "session_elapsed_s",
    "local_port", "sender_ip", "sender_port",
    "frame_type", "len", "packet_index",
    "pc_gap_ms", "rx_hz_inst",
    "length_ok", "header_ok", "version", "version_ok", "crc_ok", "frame_ok",
    "diag_source_id", "diag_tx_counter",
    "source_id", "seq", "seq_changed", "unity_t_ms",
    "flags", "flags_hex",
    "valid", "deadman", "move_en", "fused_available",
    "safe_to_drive", "hold_mode", "soft_decay", "hard_stop",
    "speed_pct", "fps_hz", "reserved",
    "x_m", "y_m", "z_m", "yaw_deg_unwrapped", "yaw_rate_dps",
    "crc_rx", "crc_calc",
    "raw_hex",
]

ATU1_SUMMARY_COLUMNS = [
    "test_name", "frame_type",
    "total", "frame_ok", "length_bad", "header_bad", "version_bad", "crc_bad",
    "safe_to_drive_count", "hard_stop_count", "hold_mode_count", "soft_decay_count",
    "deadman_count", "move_en_count", "valid_count",
    "source_0_count", "source_1_count", "source_2_count",
    "first_pc_time_unix", "last_pc_time_unix", "duration_s",
    "expected_hz", "actual_hz_ok", "dt_mean_ms", "dt_max_ms",
]

SOURCE_COMPARE_COLUMNS = [
    "test_name", "pc_time_iso", "pc_time_unix", "session_elapsed_s",
    "trigger_source_id", "trigger_device_name",
    "src1_seq", "src2_seq",
    "src1_t_ms", "src2_t_ms",
    "src1_pc_age_ms", "src2_pc_age_ms",
    "dt_pc_src1_minus_src2_ms",
    "dt_unity_src1_minus_src2_ms",
    "both_crc_ok", "both_version_ok", "both_valid",
    "src1_x_m", "src2_x_m", "dx_src1_minus_src2_m",
    "src1_y_m", "src2_y_m", "dy_src1_minus_src2_m",
    "src1_z_m", "src2_z_m", "dz_src1_minus_src2_m",
    "src1_yaw_deg_unwrapped", "src2_yaw_deg_unwrapped", "dyaw_src1_minus_src2_deg",
    "src1_yaw_rate_dps", "src2_yaw_rate_dps", "dyaw_rate_src1_minus_src2_dps",
]

UNKNOWN_COLUMNS = [
    "test_name", "pc_time_iso", "pc_time_unix", "session_elapsed_s",
    "local_port", "sender_ip", "sender_port", "len", "raw_hex",
]

OVERALL_COLUMNS = ["test_name", "metric", "value"]


# ============================================================
# HELPERS
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


def pc_time_fields() -> tuple[str, float]:
    pc_time_unix = time.time()
    pc_time_iso = (
        dt.datetime.fromtimestamp(pc_time_unix)
        .astimezone()
        .isoformat(timespec="milliseconds")
    )
    return pc_time_iso, pc_time_unix


def hex_spaced(data: bytes, max_bytes: int | None = None) -> str:
    if max_bytes is not None and len(data) > max_bytes:
        head = data[:max_bytes]
        return " ".join(f"{byte:02X}" for byte in head) + f" ... (+{len(data) - max_bytes} bytes)"
    return " ".join(f"{byte:02X}" for byte in data)


def fmt_float(value: Any, digits: int = 6) -> str:
    if value == "" or value is None:
        return ""
    value_float = float(value)
    if math.isnan(value_float) or math.isinf(value_float):
        return ""
    return f"{value_float:.{digits}f}"


def empty_row(columns: list[str]) -> dict[str, Any]:
    return {column: "" for column in columns}


def is_atb1(data: bytes) -> bool:
    return (
        len(data) == ATB1_LEN
        and data[0] == ATB1_H0
        and data[1] == ATB1_H1
    )


def is_atd1(data: bytes) -> bool:
    return (
        len(data) in (ATD1_LEN, ATD1_WITH_ATU1_LEN)
        and data[0] == ATD1_H0
        and data[1] == ATD1_H1
    )


def is_atu1(data: bytes) -> bool:
    return (
        len(data) == ATU1_LEN
        and data[0] == ATU1_H0
        and data[1] == ATU1_H1
    )


def source_name(source_id: int) -> str:
    return PHONE_NAMES.get(int(source_id), f"src{int(source_id)}")


def source_suffix(source_id: int) -> str:
    name = source_name(source_id)
    safe_name = "".join(ch if ch.isalnum() else "_" for ch in name)
    return f"src{int(source_id)}_{safe_name}"


def wrap_deg(angle_deg: float) -> float:
    return (angle_deg + 180.0) % 360.0 - 180.0


def decode_atu1_flags(flags: int) -> dict[str, int]:
    """
    Zgodne z aktualnym ATU_Output_View:
      0x01 valid
      0x02 deadman
      0x04 move_en
      0x08 fused_available
      0x10 safe_to_drive
      0x20 hold_mode
      0x40 soft_decay
      0x80 hard_stop
    """
    return {
        "valid": 1 if flags & 0x01 else 0,
        "deadman": 1 if flags & 0x02 else 0,
        "move_en": 1 if flags & 0x04 else 0,
        "fused_available": 1 if flags & 0x08 else 0,
        "safe_to_drive": 1 if flags & 0x10 else 0,
        "hold_mode": 1 if flags & 0x20 else 0,
        "soft_decay": 1 if flags & 0x40 else 0,
        "hard_stop": 1 if flags & 0x80 else 0,
    }


class SplitCsvWriters:
    """
    Leniwie tworzy osobne CSV per source_id.
    """
    def __init__(self, base_dir: Path, prefix: str, file_tag: str, columns: list[str]) -> None:
        self.base_dir = base_dir
        self.prefix = prefix
        self.file_tag = file_tag
        self.columns = columns
        self.files: dict[int, Any] = {}
        self.writers: dict[int, csv.DictWriter] = {}

    def writer_for(self, source_id: int) -> csv.DictWriter:
        source_id = int(source_id)
        if source_id not in self.writers:
            path = self.base_dir / f"{self.prefix}_{self.file_tag}_{source_suffix(source_id)}.csv"
            file = open(path, "w", newline="", encoding="utf-8")
            writer = csv.DictWriter(file, fieldnames=self.columns)
            writer.writeheader()
            file.flush()
            self.files[source_id] = file
            self.writers[source_id] = writer
        return self.writers[source_id]

    def writerow(self, source_id: int, row: dict[str, Any]) -> None:
        self.writer_for(source_id).writerow(row)

    def flush(self) -> None:
        for file in self.files.values():
            file.flush()

    def close(self) -> None:
        for file in self.files.values():
            try:
                file.flush()
                file.close()
            except Exception:
                pass


# ============================================================
# STATS
# ============================================================

class Atb1SourceStats:
    def __init__(self) -> None:
        self.rx_total = 0
        self.rx_good = 0
        self.crc_bad = 0
        self.version_bad = 0
        self.valid_count = 0
        self.deadman_count = 0
        self.move_en_count = 0
        self.duplicate_or_old_total = 0
        self.lost_est_total = 0
        self.gap_over_2s_total = 0
        self.last_seq: int | None = None
        self.last_rx_time: float | None = None
        self.first_good_time: float | None = None
        self.last_good_time: float | None = None
        self.dt_values_ms: list[float] = []


class Atu1Stats:
    def __init__(self, frame_type: str) -> None:
        self.frame_type = frame_type
        self.total = 0
        self.frame_ok = 0
        self.length_bad = 0
        self.header_bad = 0
        self.version_bad = 0
        self.crc_bad = 0
        self.safe_to_drive_count = 0
        self.hard_stop_count = 0
        self.hold_mode_count = 0
        self.soft_decay_count = 0
        self.deadman_count = 0
        self.move_en_count = 0
        self.valid_count = 0
        self.source_counts = defaultdict(int)
        self.first_ok_time: float | None = None
        self.last_ok_time: float | None = None
        self.last_rx_time: float | None = None
        self.dt_values_ms: list[float] = []
        self.previous_seq: int | None = None
        self.last_info: dict[str, Any] | None = None


class LocalStats:
    def __init__(self) -> None:
        self.start_unix = time.time()

        self.atb1_total = 0
        self.atb1_crc_ok = 0
        self.atb1_crc_bad = 0

        self.atd1_total = 0
        self.atu1_snapshot_total = 0
        self.atu1_stream_total = 0
        self.unknown_total = 0

        self.interval_atb1 = 0
        self.interval_atb1_by_source = defaultdict(int)
        self.interval_atd1 = 0
        self.interval_atu1 = 0

        self.last_atd1_info_by_source: dict[int, dict[str, Any]] = {}
        self.last_atu1_stream_info: dict[str, Any] | None = None
        self.last_atb1_info_by_source: dict[int, dict[str, Any]] = {}


# ============================================================
# DECODE + UPDATE
# ============================================================

def update_atb1_stats(stats: Atb1SourceStats, info: dict[str, Any], pc_time_unix: float) -> dict[str, Any]:
    stats.rx_total += 1

    if not info["version_ok"]:
        stats.version_bad += 1

    if not info["crc_ok"]:
        stats.crc_bad += 1

    dt_rx_ms = ""
    rx_hz_inst = ""
    seq_delta = ""
    lost_est = 0
    duplicate_or_old = 0
    gap_over_2s = 0

    if stats.last_rx_time is not None:
        dt_ms = (pc_time_unix - stats.last_rx_time) * 1000.0
        dt_rx_ms = dt_ms

        if dt_ms > 0:
            rx_hz_inst = 1000.0 / dt_ms

        if dt_ms > SESSION_GAP_MS:
            gap_over_2s = 1
            stats.gap_over_2s_total += 1
        else:
            stats.dt_values_ms.append(dt_ms)

    if stats.last_seq is not None:
        seq_delta_val = int(info["seq"]) - int(stats.last_seq)
        seq_delta = seq_delta_val

        if seq_delta_val <= 0:
            duplicate_or_old = 1
            stats.duplicate_or_old_total += 1
        elif seq_delta_val > 1 and not gap_over_2s:
            lost_est = seq_delta_val - 1
            stats.lost_est_total += lost_est

    if info["crc_ok"] and info["version_ok"]:
        stats.rx_good += 1

        if stats.first_good_time is None:
            stats.first_good_time = pc_time_unix
        stats.last_good_time = pc_time_unix

        if info["valid"]:
            stats.valid_count += 1
        if info["deadman"]:
            stats.deadman_count += 1
        if info["move_en"]:
            stats.move_en_count += 1

    stats.last_rx_time = pc_time_unix
    stats.last_seq = int(info["seq"])

    return {
        "dt_rx_ms": dt_rx_ms,
        "rx_hz_inst": rx_hz_inst,
        "seq_delta": seq_delta,
        "lost_est": lost_est,
        "duplicate_or_old": duplicate_or_old,
        "gap_over_2s": gap_over_2s,
    }


def decode_atb1(
    data: bytes,
    addr: tuple[str, int],
    local_port: int,
    session_elapsed_s: float,
) -> tuple[dict[str, Any], dict[str, Any]]:

    pc_time_iso, pc_time_unix = pc_time_fields()

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
        crc_rx,
    ) = ATB1_STRUCT.unpack(data)

    crc_calc = crc16_ccitt_false(data[:40])
    crc_ok = crc_rx == crc_calc

    valid = 1 if flags & 0x01 else 0
    deadman = 1 if flags & 0x02 else 0
    move_en = 1 if flags & 0x04 else 0

    info = {
        "pc_time_unix_float": pc_time_unix,
        "version": version,
        "version_ok": int(version == ATB1_VERSION),
        "source_id": source_id,
        "seq": seq,
        "t_ms": t_ms,
        "flags": flags,
        "valid": valid,
        "deadman": deadman,
        "move_en": move_en,
        "speed_pct": speed_pct,
        "fps_hz": fps_hz,
        "reserved": reserved,
        "x_m": x_m,
        "y_m": y_m,
        "z_m": z_m,
        "yaw_deg": yaw_deg,
        "yaw_deg_unwrapped": yaw_deg_unwrapped,
        "yaw_rate_dps": yaw_rate_dps,
        "crc_rx": crc_rx,
        "crc_calc": crc_calc,
        "crc_ok": crc_ok,
    }

    row = empty_row(ATB1_COLUMNS)
    row.update({
        "test_name": SESSION_NAME,
        "device_name": source_name(source_id),
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "session_elapsed_s": f"{session_elapsed_s:.6f}",
        "local_port": local_port,
        "sender_ip": addr[0],
        "sender_port": addr[1],
        "frame_type": "ATB1",
        "len": len(data),
        "len_ok": 1,
        "header_ok": 1,
        "version": version,
        "version_ok": int(version == ATB1_VERSION),
        "source_id": source_id,
        "seq": seq,
        "t_ms": t_ms,
        "flags": flags,
        "valid": valid,
        "deadman": deadman,
        "move_en": move_en,
        "speed_pct": speed_pct,
        "fps_hz": fps_hz,
        "reserved": reserved,
        "x_m": f"{x_m:.6f}",
        "y_m": f"{y_m:.6f}",
        "z_m": f"{z_m:.6f}",
        "yaw_deg": f"{yaw_deg:.6f}",
        "yaw_deg_unwrapped": f"{yaw_deg_unwrapped:.6f}",
        "yaw_rate_dps": f"{yaw_rate_dps:.6f}",
        "crc_rx": f"{crc_rx:04X}",
        "crc_calc": f"{crc_calc:04X}",
        "crc_ok": 1 if crc_ok else 0,
        "raw_hex": hex_spaced(data),
    })

    return row, info


def decode_atd1(
    data: bytes,
    addr: tuple[str, int],
    local_port: int,
    session_elapsed_s: float,
) -> tuple[dict[str, Any], dict[str, Any]]:

    pc_time_iso, pc_time_unix = pc_time_fields()
    values = ATD1_STRUCT.unpack(data[:ATD1_LEN])

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
    has_snapshot = 1 if len(data) == ATD1_WITH_ATU1_LEN else 0

    row = empty_row(ATD1_COLUMNS)
    row.update({
        "test_name": SESSION_NAME,
        "device_name": source_name(debug_source_id),
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "session_elapsed_s": f"{session_elapsed_s:.6f}",
        "local_port": local_port,
        "sender_ip": addr[0],
        "sender_port": addr[1],
        "frame_type": "ATD1",
        "len": len(data),
        "version": version,
        "version_ok": 1 if version == ATD1_VERSION else 0,
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
        "last_error_text": LAST_ERROR_CODE_TEXT.get(last_error_code, f"unknown_{last_error_code}"),
        "has_atu1_snapshot": has_snapshot,
        "raw_hex": hex_spaced(data, RAW_HEX_MAX_BYTES),
    })

    info = dict(row)
    info["pc_time_unix_float"] = pc_time_unix
    info["loss_percent_float"] = loss_percent

    return row, info


def decode_atu1(
    data: bytes,
    addr: tuple[str, int],
    local_port: int,
    session_elapsed_s: float,
    packet_index: int,
    previous_seq: int | None,
    previous_rx_time: float | None,
    frame_type: str,
    diag_source_id: int | None = None,
    diag_tx_counter: int | None = None,
) -> tuple[dict[str, Any], dict[str, Any]]:

    pc_time_iso, pc_time_unix = pc_time_fields()

    row = empty_row(ATU1_COLUMNS)
    row.update({
        "test_name": SESSION_NAME,
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "session_elapsed_s": f"{session_elapsed_s:.6f}",
        "local_port": local_port,
        "sender_ip": addr[0],
        "sender_port": addr[1],
        "frame_type": frame_type,
        "len": len(data),
        "packet_index": packet_index,
        "length_ok": 1 if len(data) == ATU1_LEN else 0,
        "raw_hex": hex_spaced(data),
    })

    pc_gap_ms = ""
    rx_hz_inst = ""
    if previous_rx_time is not None:
        dt_ms = (pc_time_unix - previous_rx_time) * 1000.0
        pc_gap_ms = dt_ms
        if dt_ms > 0:
            rx_hz_inst = 1000.0 / dt_ms

    row["pc_gap_ms"] = fmt_float(pc_gap_ms, 3)
    row["rx_hz_inst"] = fmt_float(rx_hz_inst, 3)

    if len(data) != ATU1_LEN:
        return row, {
            "pc_time_unix_float": pc_time_unix,
            "frame_ok": False,
            "length_ok": False,
            "header_ok": False,
            "version_ok": False,
            "crc_ok": False,
        }

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

    header_ok = h0 == ATU1_H0 and h1 == ATU1_H1
    version_ok = version == ATU1_VERSION
    crc_calc = crc16_ccitt_false(data[:36])
    crc_ok = crc_rx == crc_calc
    frame_ok = header_ok and version_ok and crc_ok
    bits = decode_atu1_flags(flags)
    seq_changed = 1 if previous_seq is None or seq != previous_seq else 0

    row.update({
        "device_name": source_name(source_id),
        "header_ok": 1 if header_ok else 0,
        "version": version,
        "version_ok": 1 if version_ok else 0,
        "crc_ok": 1 if crc_ok else 0,
        "frame_ok": 1 if frame_ok else 0,
        "diag_source_id": "" if diag_source_id is None else diag_source_id,
        "diag_tx_counter": "" if diag_tx_counter is None else diag_tx_counter,
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
    })

    info = {
        "pc_time_unix_float": pc_time_unix,
        "frame_ok": frame_ok,
        "length_ok": True,
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
        "pc_gap_ms": pc_gap_ms,
    }

    return row, info


def update_atu1_stats(stats: Atu1Stats, info: dict[str, Any]) -> None:
    stats.total += 1

    if not info.get("length_ok", False):
        stats.length_bad += 1
        return

    if not info.get("header_ok", False):
        stats.header_bad += 1
    if not info.get("version_ok", False):
        stats.version_bad += 1
    if not info.get("crc_ok", False):
        stats.crc_bad += 1

    if info.get("frame_ok", False):
        stats.frame_ok += 1

        pc_time = info["pc_time_unix_float"]
        if stats.first_ok_time is None:
            stats.first_ok_time = pc_time
        stats.last_ok_time = pc_time

        if stats.last_rx_time is not None:
            dt_ms = (pc_time - stats.last_rx_time) * 1000.0
            if dt_ms >= 0:
                stats.dt_values_ms.append(dt_ms)

        stats.last_rx_time = pc_time
        stats.previous_seq = int(info["seq"])
        stats.last_info = info

        if info["safe_to_drive"]:
            stats.safe_to_drive_count += 1
        if info["hard_stop"]:
            stats.hard_stop_count += 1
        if info["hold_mode"]:
            stats.hold_mode_count += 1
        if info["soft_decay"]:
            stats.soft_decay_count += 1
        if info["deadman"]:
            stats.deadman_count += 1
        if info["move_en"]:
            stats.move_en_count += 1
        if info["valid"]:
            stats.valid_count += 1

        stats.source_counts[int(info["source_id"])] += 1


def build_source_comparison_row(
    trigger_source_id: int,
    latest_by_source: dict[int, dict[str, Any]],
    session_elapsed_s: float,
) -> dict[str, Any] | None:

    if 1 not in latest_by_source or 2 not in latest_by_source:
        return None

    src1 = latest_by_source[1]
    src2 = latest_by_source[2]

    pc_time_iso, pc_time_unix = pc_time_fields()

    src1_pc_age_ms = (pc_time_unix - float(src1["pc_time_unix_float"])) * 1000.0
    src2_pc_age_ms = (pc_time_unix - float(src2["pc_time_unix_float"])) * 1000.0

    return {
        "test_name": SESSION_NAME,
        "pc_time_iso": pc_time_iso,
        "pc_time_unix": f"{pc_time_unix:.6f}",
        "session_elapsed_s": f"{session_elapsed_s:.6f}",
        "trigger_source_id": trigger_source_id,
        "trigger_device_name": source_name(trigger_source_id),

        "src1_seq": src1["seq"],
        "src2_seq": src2["seq"],
        "src1_t_ms": src1["t_ms"],
        "src2_t_ms": src2["t_ms"],

        "src1_pc_age_ms": f"{src1_pc_age_ms:.3f}",
        "src2_pc_age_ms": f"{src2_pc_age_ms:.3f}",
        "dt_pc_src1_minus_src2_ms": f"{(float(src1['pc_time_unix_float']) - float(src2['pc_time_unix_float'])) * 1000.0:.3f}",
        "dt_unity_src1_minus_src2_ms": int(src1["t_ms"]) - int(src2["t_ms"]),

        "both_crc_ok": 1 if src1["crc_ok"] and src2["crc_ok"] else 0,
        "both_version_ok": 1 if src1["version_ok"] and src2["version_ok"] else 0,
        "both_valid": 1 if src1["valid"] and src2["valid"] else 0,

        "src1_x_m": f"{src1['x_m']:.6f}",
        "src2_x_m": f"{src2['x_m']:.6f}",
        "dx_src1_minus_src2_m": f"{src1['x_m'] - src2['x_m']:.6f}",

        "src1_y_m": f"{src1['y_m']:.6f}",
        "src2_y_m": f"{src2['y_m']:.6f}",
        "dy_src1_minus_src2_m": f"{src1['y_m'] - src2['y_m']:.6f}",

        "src1_z_m": f"{src1['z_m']:.6f}",
        "src2_z_m": f"{src2['z_m']:.6f}",
        "dz_src1_minus_src2_m": f"{src1['z_m'] - src2['z_m']:.6f}",

        "src1_yaw_deg_unwrapped": f"{src1['yaw_deg_unwrapped']:.6f}",
        "src2_yaw_deg_unwrapped": f"{src2['yaw_deg_unwrapped']:.6f}",
        "dyaw_src1_minus_src2_deg": f"{wrap_deg(src1['yaw_deg_unwrapped'] - src2['yaw_deg_unwrapped']):.6f}",

        "src1_yaw_rate_dps": f"{src1['yaw_rate_dps']:.6f}",
        "src2_yaw_rate_dps": f"{src2['yaw_rate_dps']:.6f}",
        "dyaw_rate_src1_minus_src2_dps": f"{src1['yaw_rate_dps'] - src2['yaw_rate_dps']:.6f}",
    }


# ============================================================
# SUMMARY WRITERS
# ============================================================

def write_atb1_summary(path: Path, source_stats: dict[int, Atb1SourceStats]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, fieldnames=ATB1_SUMMARY_COLUMNS)
        writer.writeheader()

        for source_id in sorted(source_stats.keys()):
            st = source_stats[source_id]

            duration_s = (
                max(0.0, st.last_good_time - st.first_good_time)
                if st.first_good_time is not None and st.last_good_time is not None
                else 0.0
            )

            actual_hz_good = st.rx_good / duration_s if duration_s > 0 else 0.0
            valid_ratio = 100.0 * st.valid_count / st.rx_good if st.rx_good > 0 else 0.0
            dt_mean_ms = sum(st.dt_values_ms) / len(st.dt_values_ms) if st.dt_values_ms else 0.0
            dt_max_ms = max(st.dt_values_ms) if st.dt_values_ms else 0.0

            writer.writerow({
                "test_name": SESSION_NAME,
                "source_id": source_id,
                "device_name": source_name(source_id),
                "rx_total": st.rx_total,
                "rx_good": st.rx_good,
                "crc_bad": st.crc_bad,
                "version_bad": st.version_bad,
                "valid_count": st.valid_count,
                "deadman_count": st.deadman_count,
                "move_en_count": st.move_en_count,
                "duplicate_or_old_total": st.duplicate_or_old_total,
                "lost_est_total": st.lost_est_total,
                "gap_over_2s_total": st.gap_over_2s_total,
                "first_pc_time_unix": f"{st.first_good_time:.6f}" if st.first_good_time is not None else "",
                "last_pc_time_unix": f"{st.last_good_time:.6f}" if st.last_good_time is not None else "",
                "duration_s": f"{duration_s:.3f}",
                "expected_hz": f"{EXPECTED_ATB1_HZ_PER_SOURCE:.3f}",
                "actual_hz_good": f"{actual_hz_good:.3f}",
                "valid_ratio_percent": f"{valid_ratio:.2f}",
                "dt_mean_ms": f"{dt_mean_ms:.3f}",
                "dt_max_ms": f"{dt_max_ms:.3f}",
            })


def write_atu1_summary(path: Path, stats_list: list[Atu1Stats]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, fieldnames=ATU1_SUMMARY_COLUMNS)
        writer.writeheader()

        for st in stats_list:
            duration_s = (
                max(0.0, st.last_ok_time - st.first_ok_time)
                if st.first_ok_time is not None and st.last_ok_time is not None
                else 0.0
            )

            actual_hz_ok = st.frame_ok / duration_s if duration_s > 0 else 0.0
            dt_mean_ms = sum(st.dt_values_ms) / len(st.dt_values_ms) if st.dt_values_ms else 0.0
            dt_max_ms = max(st.dt_values_ms) if st.dt_values_ms else 0.0

            writer.writerow({
                "test_name": SESSION_NAME,
                "frame_type": st.frame_type,
                "total": st.total,
                "frame_ok": st.frame_ok,
                "length_bad": st.length_bad,
                "header_bad": st.header_bad,
                "version_bad": st.version_bad,
                "crc_bad": st.crc_bad,
                "safe_to_drive_count": st.safe_to_drive_count,
                "hard_stop_count": st.hard_stop_count,
                "hold_mode_count": st.hold_mode_count,
                "soft_decay_count": st.soft_decay_count,
                "deadman_count": st.deadman_count,
                "move_en_count": st.move_en_count,
                "valid_count": st.valid_count,
                "source_0_count": st.source_counts.get(0, 0),
                "source_1_count": st.source_counts.get(1, 0),
                "source_2_count": st.source_counts.get(2, 0),
                "first_pc_time_unix": f"{st.first_ok_time:.6f}" if st.first_ok_time is not None else "",
                "last_pc_time_unix": f"{st.last_ok_time:.6f}" if st.last_ok_time is not None else "",
                "duration_s": f"{duration_s:.3f}",
                "expected_hz": f"{EXPECTED_ATU1_HZ:.3f}" if st.frame_type == "ATU1_STREAM" else "",
                "actual_hz_ok": f"{actual_hz_ok:.3f}",
                "dt_mean_ms": f"{dt_mean_ms:.3f}",
                "dt_max_ms": f"{dt_max_ms:.3f}",
            })


def write_overall_summary(path: Path, stats: LocalStats) -> None:
    elapsed = time.time() - stats.start_unix

    rows = [
        ("duration_s", f"{elapsed:.3f}"),
        ("listen_ports", ",".join(str(port) for port in LISTEN_PORTS)),
        ("source_1_device", source_name(1)),
        ("source_2_device", source_name(2)),
        ("atb1_total", stats.atb1_total),
        ("atb1_crc_ok", stats.atb1_crc_ok),
        ("atb1_crc_bad", stats.atb1_crc_bad),
        ("atd1_total", stats.atd1_total),
        ("atu1_snapshot_total", stats.atu1_snapshot_total),
        ("atu1_stream_total", stats.atu1_stream_total),
        ("unknown_total", stats.unknown_total),
    ]

    with open(path, "w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, fieldnames=OVERALL_COLUMNS)
        writer.writeheader()

        for metric, value in rows:
            writer.writerow({
                "test_name": SESSION_NAME,
                "metric": metric,
                "value": value,
            })


# ============================================================
# SOCKETS
# ============================================================

def open_udp_sockets() -> dict[socket.socket, int]:
    sockets: dict[socket.socket, int] = {}

    for port in sorted(set(LISTEN_PORTS)):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

        try:
            sock.bind((LISTEN_IP, port))
        except OSError as exc:
            sock.close()
            raise RuntimeError(
                f"Nie mogę zbindować portu UDP {port}. "
                f"Najpewniej działa już inny logger albo inny program używa tego portu. "
                f"Zamknij stare skrypty i uruchom ponownie. Szczegóły: {exc}"
            ) from exc

        sock.setblocking(False)
        sockets[sock] = port

    return sockets


def close_udp_sockets(sockets: dict[socket.socket, int]) -> None:
    for sock in sockets:
        try:
            sock.close()
        except OSError:
            pass


# ============================================================
# SESSION APP
# ============================================================

class HilLoggerSession:
    def __init__(self) -> None:
        self.finalized = False

        self.stats = LocalStats()
        self.atb1_source_stats: dict[int, Atb1SourceStats] = defaultdict(Atb1SourceStats)
        self.atu1_stream_stats = Atu1Stats("ATU1_STREAM")
        self.atu1_snapshot_stats = Atu1Stats("ATU1_SNAPSHOT")

        self.files: list[Any] = []
        self.split_managers: list[SplitCsvWriters] = []
        self.sockets: dict[socket.socket, int] = {}

        self.atu1_stream_packet_index = 0
        self.atu1_snapshot_packet_index = 0

        self.last_print_mono = time.monotonic()
        self.last_flush_mono = time.monotonic()

        timestamp = dt.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        self.prefix = f"{SESSION_NAME}_{timestamp}"

        self.paths = {
            "atb1_all": LOG_DIR / f"{self.prefix}_atb1_ALL_sources_udp_all.csv",
            "atb1_summary": LOG_DIR / f"{self.prefix}_atb1_sources_summary.csv",
            "atd1_all": LOG_DIR / f"{self.prefix}_atd1_ALL_sources_esp_diag.csv",
            "atu1_snapshot_all": LOG_DIR / f"{self.prefix}_atu1_snapshot_ALL_from_atd1.csv",
            "atu1_stream_all": LOG_DIR / f"{self.prefix}_atu1_uart_stream_ALL_5010.csv",
            "atu1_summary": LOG_DIR / f"{self.prefix}_atu1_summary.csv",
            "source_compare": LOG_DIR / f"{self.prefix}_atb1_compare_src1_Oppo_vs_src2_Realme.csv",
            "unknown": LOG_DIR / f"{self.prefix}_unknown.csv",
            "overall": LOG_DIR / f"{self.prefix}_hil_summary.csv",
        }

        self.atb1_writer: csv.DictWriter | None = None
        self.atd1_writer: csv.DictWriter | None = None
        self.atu1_snapshot_writer: csv.DictWriter | None = None
        self.atu1_stream_writer: csv.DictWriter | None = None
        self.unknown_writer: csv.DictWriter | None = None
        self.source_compare_writer: csv.DictWriter | None = None

        self.split_atb1: SplitCsvWriters | None = None
        self.split_atd1: SplitCsvWriters | None = None
        self.split_atu1_stream: SplitCsvWriters | None = None
        self.split_atu1_snapshot: SplitCsvWriters | None = None

    def setup(self) -> None:
        LOG_DIR.mkdir(parents=True, exist_ok=True)

        atb1_file = open(self.paths["atb1_all"], "w", newline="", encoding="utf-8")
        atd1_file = open(self.paths["atd1_all"], "w", newline="", encoding="utf-8")
        atu1_snapshot_file = open(self.paths["atu1_snapshot_all"], "w", newline="", encoding="utf-8")
        atu1_stream_file = open(self.paths["atu1_stream_all"], "w", newline="", encoding="utf-8")
        unknown_file = open(self.paths["unknown"], "w", newline="", encoding="utf-8")

        self.files.extend([
            atb1_file,
            atd1_file,
            atu1_snapshot_file,
            atu1_stream_file,
            unknown_file,
        ])

        self.atb1_writer = csv.DictWriter(atb1_file, fieldnames=ATB1_COLUMNS)
        self.atd1_writer = csv.DictWriter(atd1_file, fieldnames=ATD1_COLUMNS)
        self.atu1_snapshot_writer = csv.DictWriter(atu1_snapshot_file, fieldnames=ATU1_COLUMNS)
        self.atu1_stream_writer = csv.DictWriter(atu1_stream_file, fieldnames=ATU1_COLUMNS)
        self.unknown_writer = csv.DictWriter(unknown_file, fieldnames=UNKNOWN_COLUMNS)

        self.atb1_writer.writeheader()
        self.atd1_writer.writeheader()
        self.atu1_snapshot_writer.writeheader()
        self.atu1_stream_writer.writeheader()
        self.unknown_writer.writeheader()

        if WRITE_SOURCE_COMPARISON_CSV:
            source_compare_file = open(self.paths["source_compare"], "w", newline="", encoding="utf-8")
            self.files.append(source_compare_file)
            self.source_compare_writer = csv.DictWriter(source_compare_file, fieldnames=SOURCE_COMPARE_COLUMNS)
            self.source_compare_writer.writeheader()

        if WRITE_SPLIT_ATB1_BY_SOURCE:
            self.split_atb1 = SplitCsvWriters(LOG_DIR, self.prefix, "atb1_udp_all", ATB1_COLUMNS)
            self.split_managers.append(self.split_atb1)

        if WRITE_SPLIT_ATD1_BY_SOURCE:
            self.split_atd1 = SplitCsvWriters(LOG_DIR, self.prefix, "atd1_esp_diag", ATD1_COLUMNS)
            self.split_managers.append(self.split_atd1)

        if WRITE_SPLIT_ATU1_BY_SELECTED_SOURCE:
            self.split_atu1_stream = SplitCsvWriters(
                LOG_DIR,
                self.prefix,
                "atu1_uart_stream_5010_selected",
                ATU1_COLUMNS,
            )
            self.split_atu1_snapshot = SplitCsvWriters(
                LOG_DIR,
                self.prefix,
                "atu1_snapshot_from_atd1_selected",
                ATU1_COLUMNS,
            )
            self.split_managers.extend([self.split_atu1_stream, self.split_atu1_snapshot])

        self.sockets = open_udp_sockets()

        self.print_startup()

    def print_startup(self) -> None:
        print("================================================")
        print("HIL UDP SESSION LOGGER - FINAL")
        print("================================================")
        print(f"Test/session: {SESSION_NAME}")
        print(f"source_id=1:  {source_name(1)}")
        print(f"source_id=2:  {source_name(2)}")
        print(f"Listening IP: {LISTEN_IP}")
        print(f"Ports:        {', '.join(str(port) for port in sorted(set(LISTEN_PORTS)))}")
        print(f"Log dir:      {LOG_DIR.resolve()}")
        print("")
        print("Najważniejsze CSV:")
        print(f"  ATB1 all:        {self.paths['atb1_all']}")
        print(f"  ATB1 summary:    {self.paths['atb1_summary']}")
        print(f"  ATB1 compare:    {self.paths['source_compare']}")
        print(f"  ATD1 all:        {self.paths['atd1_all']}")
        print(f"  ATU1 stream all: {self.paths['atu1_stream_all']}")
        print(f"  HIL summary:     {self.paths['overall']}")
        print("")
        print("Osobne pliki ATB1/ATD1 per source_id utworzą się automatycznie po odebraniu danych.")
        print("ATU1 jest jednym głównym plikiem, bo do STM32 idzie jedno wyjście UART.")
        print("")
        print("Zakończenie i zapis summary: Ctrl+C")
        print("Zamknięcie terminala ubije proces; raw CSV są flushowane co sekundę.")
        print("================================================\n")

    def flush_all(self) -> None:
        for file in self.files:
            try:
                file.flush()
            except Exception:
                pass

        for manager in self.split_managers:
            manager.flush()

    def close_all(self) -> None:
        self.flush_all()

        for file in self.files:
            try:
                file.close()
            except Exception:
                pass

        for manager in self.split_managers:
            manager.close()

        close_udp_sockets(self.sockets)

    def run(self) -> None:
        while True:
            readable, _, _ = select.select(
                list(self.sockets.keys()),
                [],
                [],
                SOCKET_SELECT_TIMEOUT_S,
            )

            for sock in readable:
                local_port = self.sockets[sock]

                try:
                    data, addr = sock.recvfrom(RECV_BUFFER_SIZE)
                except BlockingIOError:
                    continue

                self.handle_packet(data, addr, local_port)

            now_mono = time.monotonic()

            if now_mono - self.last_flush_mono >= FLUSH_INTERVAL_S:
                self.flush_all()
                self.last_flush_mono = now_mono

            if now_mono - self.last_print_mono >= PRINT_INTERVAL_S:
                self.print_periodic(now_mono - self.last_print_mono)
                self.last_print_mono = now_mono

    def handle_packet(self, data: bytes, addr: tuple[str, int], local_port: int) -> None:
        session_elapsed_s = time.time() - self.stats.start_unix

        if data.startswith(b"AT1;"):
            self.stats.unknown_total += 1
            self.write_unknown(data.decode("ascii", errors="replace").strip().encode("utf-8"), addr, local_port, session_elapsed_s, text_mode=True)
            return

        if is_atb1(data):
            self.handle_atb1(data, addr, local_port, session_elapsed_s)
            return

        if is_atd1(data):
            self.handle_atd1(data, addr, local_port, session_elapsed_s)
            return

        if is_atu1(data):
            self.handle_atu1_stream(data, addr, local_port, session_elapsed_s)
            return

        self.stats.unknown_total += 1
        self.write_unknown(data, addr, local_port, session_elapsed_s, text_mode=False)

        if PRINT_UNKNOWN:
            print(
                f"UNKNOWN port={local_port} len={len(data)} from {addr[0]}:{addr[1]} "
                f"hex={hex_spaced(data, RAW_HEX_MAX_BYTES)}"
            )

    def handle_atb1(
        self,
        data: bytes,
        addr: tuple[str, int],
        local_port: int,
        session_elapsed_s: float,
    ) -> None:

        assert self.atb1_writer is not None

        row, info = decode_atb1(data, addr, local_port, session_elapsed_s)
        source_id = int(info["source_id"])

        self.stats.atb1_total += 1
        self.stats.interval_atb1 += 1
        self.stats.interval_atb1_by_source[source_id] += 1

        dyn = update_atb1_stats(
            self.atb1_source_stats[source_id],
            info,
            info["pc_time_unix_float"],
        )

        row["dt_rx_ms"] = fmt_float(dyn["dt_rx_ms"], 3)
        row["rx_hz_inst"] = fmt_float(dyn["rx_hz_inst"], 3)
        row["seq_delta"] = dyn["seq_delta"]
        row["lost_est"] = dyn["lost_est"]
        row["duplicate_or_old"] = dyn["duplicate_or_old"]
        row["gap_over_2s"] = dyn["gap_over_2s"]

        if info["crc_ok"]:
            self.stats.atb1_crc_ok += 1
        else:
            self.stats.atb1_crc_bad += 1

        self.atb1_writer.writerow(row)

        if self.split_atb1 is not None:
            self.split_atb1.writerow(source_id, row)

        self.stats.last_atb1_info_by_source[source_id] = info

        if self.source_compare_writer is not None:
            comp_row = build_source_comparison_row(
                trigger_source_id=source_id,
                latest_by_source=self.stats.last_atb1_info_by_source,
                session_elapsed_s=session_elapsed_s,
            )
            if comp_row is not None:
                self.source_compare_writer.writerow(comp_row)

    def handle_atd1(
        self,
        data: bytes,
        addr: tuple[str, int],
        local_port: int,
        session_elapsed_s: float,
    ) -> None:

        assert self.atd1_writer is not None
        assert self.atu1_snapshot_writer is not None

        row, info = decode_atd1(data, addr, local_port, session_elapsed_s)
        source_id = int(info["source_id"])

        self.stats.atd1_total += 1
        self.stats.interval_atd1 += 1
        self.stats.last_atd1_info_by_source[source_id] = info

        self.atd1_writer.writerow(row)

        if self.split_atd1 is not None:
            self.split_atd1.writerow(source_id, row)

        if len(data) == ATD1_WITH_ATU1_LEN:
            self.stats.atu1_snapshot_total += 1
            atu1_payload = data[ATD1_LEN:ATD1_WITH_ATU1_LEN]
            self.atu1_snapshot_packet_index += 1

            row_s, info_s = decode_atu1(
                data=atu1_payload,
                addr=addr,
                local_port=local_port,
                session_elapsed_s=session_elapsed_s,
                packet_index=self.atu1_snapshot_packet_index,
                previous_seq=self.atu1_snapshot_stats.previous_seq,
                previous_rx_time=self.atu1_snapshot_stats.last_rx_time,
                frame_type="ATU1_SNAPSHOT",
                diag_source_id=source_id,
                diag_tx_counter=int(info["tx_counter"]),
            )

            update_atu1_stats(self.atu1_snapshot_stats, info_s)
            self.atu1_snapshot_writer.writerow(row_s)

            if self.split_atu1_snapshot is not None:
                selected_id = int(info_s.get("source_id", 0))
                self.split_atu1_snapshot.writerow(selected_id, row_s)

    def handle_atu1_stream(
        self,
        data: bytes,
        addr: tuple[str, int],
        local_port: int,
        session_elapsed_s: float,
    ) -> None:

        assert self.atu1_stream_writer is not None

        self.atu1_stream_packet_index += 1
        self.stats.atu1_stream_total += 1
        self.stats.interval_atu1 += 1

        row, info = decode_atu1(
            data=data,
            addr=addr,
            local_port=local_port,
            session_elapsed_s=session_elapsed_s,
            packet_index=self.atu1_stream_packet_index,
            previous_seq=self.atu1_stream_stats.previous_seq,
            previous_rx_time=self.atu1_stream_stats.last_rx_time,
            frame_type="ATU1_STREAM",
        )

        update_atu1_stats(self.atu1_stream_stats, info)

        if info.get("frame_ok", False):
            self.stats.last_atu1_stream_info = info

        self.atu1_stream_writer.writerow(row)

        if self.split_atu1_stream is not None:
            selected_id = int(info.get("source_id", 0))
            self.split_atu1_stream.writerow(selected_id, row)

    def write_unknown(
        self,
        data: bytes,
        addr: tuple[str, int],
        local_port: int,
        session_elapsed_s: float,
        text_mode: bool,
    ) -> None:

        assert self.unknown_writer is not None

        pc_time_iso, pc_time_unix = pc_time_fields()

        if text_mode:
            try:
                raw_repr = data.decode("utf-8", errors="replace")
            except Exception:
                raw_repr = hex_spaced(data, RAW_HEX_MAX_BYTES)
        else:
            raw_repr = hex_spaced(data, RAW_HEX_MAX_BYTES)

        self.unknown_writer.writerow({
            "test_name": SESSION_NAME,
            "pc_time_iso": pc_time_iso,
            "pc_time_unix": f"{pc_time_unix:.6f}",
            "session_elapsed_s": f"{session_elapsed_s:.6f}",
            "local_port": local_port,
            "sender_ip": addr[0],
            "sender_port": addr[1],
            "len": len(data),
            "raw_hex": raw_repr,
        })

    def print_periodic(self, elapsed_s: float) -> None:
        atb1_hz = self.stats.interval_atb1 / elapsed_s if elapsed_s > 0 else 0.0
        atb1_src1_hz = self.stats.interval_atb1_by_source[1] / elapsed_s if elapsed_s > 0 else 0.0
        atb1_src2_hz = self.stats.interval_atb1_by_source[2] / elapsed_s if elapsed_s > 0 else 0.0
        atd1_hz = self.stats.interval_atd1 / elapsed_s if elapsed_s > 0 else 0.0
        atu1_hz = self.stats.interval_atu1 / elapsed_s if elapsed_s > 0 else 0.0

        src1_diag = self.stats.last_atd1_info_by_source.get(1)
        src2_diag = self.stats.last_atd1_info_by_source.get(2)
        atu = self.stats.last_atu1_stream_info

        src1_diag_text = self.format_atd1_diag_text(1, src1_diag)
        src2_diag_text = self.format_atd1_diag_text(2, src2_diag)

        if atu is None:
            atu_text = "ATU1: brak poprawnej ramki"
        else:
            atu_text = (
                f"ATU1 selected src={atu['source_id']}({source_name(int(atu['source_id']))}) "
                f"seq={atu['seq']} flags=0x{atu['flags']:02X} "
                f"valid={atu['valid']} dead={atu['deadman']} move={atu['move_en']} "
                f"fused={atu['fused_available']} safe={atu['safe_to_drive']} hard={atu['hard_stop']} "
                f"speed={atu['speed_pct']}% x={atu['x_m']:.3f} y={atu['y_m']:.3f} "
                f"yaw={atu['yaw_deg_unwrapped']:.2f}"
            )

        print(
            f"RX/s: ATB1_ALL={atb1_hz:5.1f} "
            f"| src1 {source_name(1)}={atb1_src1_hz:5.1f} "
            f"| src2 {source_name(2)}={atb1_src2_hz:5.1f} "
            f"| ATD1={atd1_hz:4.1f} | ATU1={atu1_hz:6.1f} "
            f"| totals: ATB1={self.stats.atb1_total} ATD1={self.stats.atd1_total} "
            f"ATU1={self.stats.atu1_stream_total} UNKNOWN={self.stats.unknown_total} | "
            f"{src1_diag_text} | {src2_diag_text} | {atu_text}"
        )

        self.stats.interval_atb1 = 0
        self.stats.interval_atb1_by_source.clear()
        self.stats.interval_atd1 = 0
        self.stats.interval_atu1 = 0

    @staticmethod
    def format_atd1_diag_text(source_id: int, diag: dict[str, Any] | None) -> str:
        if diag is None:
            return f"ATD1 src{source_id}: brak"

        return (
            f"ATD1 src{source_id} rx={diag['rx_ok_total']} lost={diag['seq_lost_total']} "
            f"age={diag['age_ms']}ms fresh={diag['fresh_300ms']} "
            f"err={diag['last_error_code']}({diag['last_error_text']})"
        )

    def finalize(self) -> None:
        if self.finalized:
            return

        self.finalized = True

        try:
            self.close_all()

            write_atb1_summary(self.paths["atb1_summary"], self.atb1_source_stats)
            write_atu1_summary(
                self.paths["atu1_summary"],
                [self.atu1_stream_stats, self.atu1_snapshot_stats],
            )
            write_overall_summary(self.paths["overall"], self.stats)

            elapsed = time.time() - self.stats.start_unix

            print("\n================ PODSUMOWANIE HIL ================")
            print(f"Test/session:       {SESSION_NAME}")
            print(f"Czas pracy:          {elapsed:.1f} s")
            print(f"ATB1 total:          {self.stats.atb1_total}")
            print(f"ATB1 crc ok:         {self.stats.atb1_crc_ok}")
            print(f"ATB1 crc bad:        {self.stats.atb1_crc_bad}")
            print(f"ATD1 total:          {self.stats.atd1_total}")
            print(f"ATU1 snapshot total: {self.stats.atu1_snapshot_total}")
            print(f"ATU1 stream total:   {self.stats.atu1_stream_total}")
            print(f"UNKNOWN total:       {self.stats.unknown_total}")
            print("")
            print("ATB1 per source:")

            for source_id in sorted(self.atb1_source_stats.keys()):
                st = self.atb1_source_stats[source_id]

                duration_s = (
                    max(0.0, st.last_good_time - st.first_good_time)
                    if st.first_good_time is not None and st.last_good_time is not None
                    else 0.0
                )

                actual_hz = st.rx_good / duration_s if duration_s > 0 else 0.0
                valid_ratio = 100.0 * st.valid_count / st.rx_good if st.rx_good > 0 else 0.0
                dt_mean = sum(st.dt_values_ms) / len(st.dt_values_ms) if st.dt_values_ms else 0.0
                dt_max = max(st.dt_values_ms) if st.dt_values_ms else 0.0

                print(
                    f"  src={source_id} {source_name(source_id)}: "
                    f"rx_total={st.rx_total}, rx_good={st.rx_good}, crc_bad={st.crc_bad}, "
                    f"lost={st.lost_est_total}, dup={st.duplicate_or_old_total}, "
                    f"valid_ratio={valid_ratio:.2f}%, actual_hz={actual_hz:.2f}, "
                    f"dt_mean={dt_mean:.2f}ms, dt_max={dt_max:.2f}ms"
                )

            print("")
            print("ATU1 stream:")
            print(
                f"  total={self.atu1_stream_stats.total}, ok={self.atu1_stream_stats.frame_ok}, "
                f"crc_bad={self.atu1_stream_stats.crc_bad}, "
                f"hard_stop={self.atu1_stream_stats.hard_stop_count}, "
                f"safe_to_drive={self.atu1_stream_stats.safe_to_drive_count}"
            )
            print("")
            print(f"CSV zapisane w: {LOG_DIR.resolve()}")
            print("==================================================")

        except Exception as exc:
            print(f"\nBłąd podczas finalizacji loggera: {exc}", file=sys.stderr)


def main() -> None:
    session = HilLoggerSession()
    session.setup()

    atexit.register(session.finalize)

    def stop_handler(signum: int, frame: Any) -> None:
        raise KeyboardInterrupt

    try:
        signal.signal(signal.SIGINT, stop_handler)
        signal.signal(signal.SIGTERM, stop_handler)
    except Exception:
        pass

    try:
        session.run()
    except KeyboardInterrupt:
        print("\nPrzerwano. Zamykanie sesji...")
    finally:
        session.finalize()


if __name__ == "__main__":
    main()
