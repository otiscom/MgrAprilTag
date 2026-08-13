#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
udp_atb1_session_logger.py

Logger sesji UDP ATB1 dla testów Unity/Android/AprilTag.

Workflow:
1. Zmień SESSION_NAME i LOG_DIR.
2. Uruchom skrypt.
3. Kliknij MEASURE w aplikacji Unity.
4. Po zakończeniu pomiaru wciśnij Ctrl+C.
5. Skrypt zapisze:
   - CSV z każdą odebraną ramką,
   - CSV ze statystyką sesji per source_id.

Nie wymaga zewnętrznych bibliotek.
"""

import socket
import struct
import csv
import time
import datetime
from pathlib import Path
from collections import defaultdict

# ============================================================
# KONFIGURACJA POMIARU
# ============================================================

UDP_IP = "0.0.0.0"
UDP_PORT = 5005

# Zmieniaj przed każdym testem.
SESSION_NAME = ("Realme_STATIC_POSE_D_X69_Y-69_Yaw-135_rep3_20Hz_10s")

# Możesz dać np. Path(r"C:\Users\mateu\Desktop\Mgr\logs")
LOG_DIR = Path("logs/Realme/Test_3_D")

# Opcjonalnie: jeżeli testujesz np. 20 Hz, wpisz 20.0.
# Służy tylko do podsumowania, nie wpływa na logowanie.
EXPECTED_HZ_PER_SOURCE = 20.0

# Przerwa większa niż ta wartość oznacza nową sesję źródła,
# a nie zgubione pakiety. Przydatne, gdy Unity ma UDP OFF
# przed startem MEASURE.
SESSION_GAP_MS = 2000.0

# Wypisywanie do konsoli.
PRINT_EVERY_N_ATB1 = 20          # co ile poprawnych ramek ATB1 wypisać skrót
PRINT_UNKNOWN = True
RAW_HEX_MAX_BYTES = 96

# ============================================================
# FORMAT ATB1
# ============================================================

ATB1_LEN = 42
ATB1_H0 = 0xA1
ATB1_H1 = 0x1A
ATB1_VERSION = 1

# < little endian:
# B B B B  I I  B B B B  f f f f f f  H
ATB1_STRUCT = struct.Struct("<BBBBIIBBBBffffffH")

CSV_COLUMNS = [
    "test_name",
    "pc_time_iso",
    "pc_time_unix",
    "session_elapsed_s",

    "sender_ip",
    "sender_port",

    "frame_type",
    "len",
    "len_ok",
    "header_ok",
    "version",
    "version_ok",

    "source_id",
    "seq",
    "t_ms",

    "flags",
    "valid",
    "deadman",
    "move_en",
    "speed_pct",
    "fps_hz",
    "reserved",

    "x_m",
    "y_m",
    "z_m",
    "yaw_deg",
    "yaw_deg_unwrapped",
    "yaw_rate_dps",

    "crc_rx",
    "crc_calc",
    "crc_ok",

    "dt_rx_ms",
    "rx_hz_inst",
    "seq_delta",
    "lost_est",
    "duplicate_or_old",
    "gap_over_2s",

    "raw_hex",
]

SUMMARY_COLUMNS = [
    "test_name",
    "source_id",
    "rx_total",
    "rx_good",
    "crc_bad",
    "version_bad",
    "valid_count",
    "deadman_count",
    "move_en_count",
    "duplicate_or_old_total",
    "lost_est_total",
    "gap_over_2s_total",
    "first_pc_time_unix",
    "last_pc_time_unix",
    "duration_s",
    "expected_hz",
    "actual_hz_good",
    "valid_ratio_percent",
    "dt_mean_ms",
    "dt_max_ms",
]


# ============================================================
# CRC-16/CCITT-FALSE
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


def pc_time_fields():
    pc_time_unix = time.time()
    pc_time_iso = datetime.datetime.fromtimestamp(pc_time_unix).astimezone().isoformat(timespec="milliseconds")
    return pc_time_iso, pc_time_unix


def hex_spaced(data: bytes, max_bytes=None) -> str:
    if max_bytes is not None and len(data) > max_bytes:
        head = data[:max_bytes]
        return " ".join(f"{b:02X}" for b in head) + f" ... (+{len(data) - max_bytes} bytes)"
    return " ".join(f"{b:02X}" for b in data)


def empty_row():
    return {col: "" for col in CSV_COLUMNS}


class SourceStats:
    def __init__(self):
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

        self.last_seq = None
        self.last_rx_time = None

        self.first_good_time = None
        self.last_good_time = None

        self.dt_values_ms = []


def decode_atb1(data: bytes):
    """
    Zwraca dict z danymi ATB1.
    Zakłada len=42 i header A1 1A.
    CRC może być błędne, ale ramka jest rozpakowywana diagnostycznie.
    """
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
    crc_ok = int(crc_rx == crc_calc)

    valid = 1 if (flags & (1 << 0)) else 0
    deadman = 1 if (flags & (1 << 1)) else 0
    move_en = 1 if (flags & (1 << 2)) else 0

    return {
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


def update_source_stats(stats: SourceStats, info: dict, pc_time_unix: float):
    """
    Aktualizuje statystyki per source_id i zwraca pola do CSV:
    dt_rx_ms, rx_hz_inst, seq_delta, lost_est, duplicate_or_old, gap_over_2s.
    """
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
        dt = (pc_time_unix - stats.last_rx_time) * 1000.0
        dt_rx_ms = dt
        if dt > 0:
            rx_hz_inst = 1000.0 / dt
        if dt > SESSION_GAP_MS:
            gap_over_2s = 1
            stats.gap_over_2s_total += 1
        else:
            stats.dt_values_ms.append(dt)

    # seq_delta liczony tylko jeżeli mamy poprzedni seq.
    if stats.last_seq is not None:
        # Obsługa zwykłego przypadku bez wraparound.
        seq_delta_val = int(info["seq"]) - int(stats.last_seq)
        seq_delta = seq_delta_val

        if seq_delta_val <= 0:
            duplicate_or_old = 1
            stats.duplicate_or_old_total += 1
        elif seq_delta_val > 1 and not gap_over_2s:
            lost_est = seq_delta_val - 1
            stats.lost_est_total += lost_est

    # Do statystyk dokładności bierzemy tylko ramki poprawne.
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

    # last_rx_time i last_seq aktualizujemy dla poprawnie rozpoznanej ramki ATB1.
    # Nawet jeśli crc_bad, nadal jest to odebrana ramka z danym source_id.
    stats.last_rx_time = pc_time_unix
    stats.last_seq = info["seq"]

    return {
        "dt_rx_ms": dt_rx_ms,
        "rx_hz_inst": rx_hz_inst,
        "seq_delta": seq_delta,
        "lost_est": lost_est,
        "duplicate_or_old": duplicate_or_old,
        "gap_over_2s": gap_over_2s,
    }


def format_float(value, decimals=6):
    if value == "":
        return ""
    return f"{float(value):.{decimals}f}"


def write_summary(summary_path: Path, source_stats: dict):
    with open(summary_path, mode="w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=SUMMARY_COLUMNS)
        writer.writeheader()

        for source_id in sorted(source_stats.keys()):
            st = source_stats[source_id]

            if st.first_good_time is not None and st.last_good_time is not None:
                duration_s = max(0.0, st.last_good_time - st.first_good_time)
            else:
                duration_s = 0.0

            if duration_s > 0:
                actual_hz_good = st.rx_good / duration_s
            else:
                actual_hz_good = 0.0

            if st.rx_good > 0:
                valid_ratio = 100.0 * st.valid_count / st.rx_good
            else:
                valid_ratio = 0.0

            if st.dt_values_ms:
                dt_mean = sum(st.dt_values_ms) / len(st.dt_values_ms)
                dt_max = max(st.dt_values_ms)
            else:
                dt_mean = 0.0
                dt_max = 0.0

            writer.writerow({
                "test_name": SESSION_NAME,
                "source_id": source_id,
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
                "first_pc_time_unix": f"{st.first_good_time:.6f}" if st.first_good_time else "",
                "last_pc_time_unix": f"{st.last_good_time:.6f}" if st.last_good_time else "",
                "duration_s": f"{duration_s:.3f}",
                "expected_hz": f"{EXPECTED_HZ_PER_SOURCE:.3f}",
                "actual_hz_good": f"{actual_hz_good:.3f}",
                "valid_ratio_percent": f"{valid_ratio:.2f}",
                "dt_mean_ms": f"{dt_mean:.3f}",
                "dt_max_ms": f"{dt_max:.3f}",
            })


def main():
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")

    csv_path = LOG_DIR / f"{SESSION_NAME}_{timestamp}_udp_all.csv"
    summary_path = LOG_DIR / f"{SESSION_NAME}_{timestamp}_summary.csv"

    source_stats = defaultdict(SourceStats)

    start_unix = time.time()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((UDP_IP, UDP_PORT))
    sock.settimeout(0.5)

    print("================================================")
    print("UDP ATB1 SESSION LOGGER")
    print("================================================")
    print(f"Test/session: {SESSION_NAME}")
    print(f"Listening:     {UDP_IP}:{UDP_PORT}")
    print(f"CSV:           {csv_path}")
    print(f"Summary:       {summary_path}")
    print("")
    print("Teraz kliknij MEASURE w Unity.")
    print("Po zakończeniu pomiaru wciśnij Ctrl+C.")
    print("================================================\n")

    good_atb1_counter = 0
    unknown_counter = 0

    try:
        with open(csv_path, mode="w", newline="", encoding="utf-8") as csv_file:
            writer = csv.DictWriter(csv_file, fieldnames=CSV_COLUMNS)
            writer.writeheader()
            csv_file.flush()

            while True:
                try:
                    data, addr = sock.recvfrom(2048)
                except socket.timeout:
                    continue

                pc_time_iso, pc_time_unix = pc_time_fields()
                elapsed_s = pc_time_unix - start_unix

                row = empty_row()
                row["test_name"] = SESSION_NAME
                row["pc_time_iso"] = pc_time_iso
                row["pc_time_unix"] = f"{pc_time_unix:.6f}"
                row["session_elapsed_s"] = f"{elapsed_s:.6f}"
                row["sender_ip"] = addr[0]
                row["sender_port"] = addr[1]
                row["len"] = len(data)
                row["raw_hex"] = hex_spaced(data, RAW_HEX_MAX_BYTES)

                len_ok = len(data) == ATB1_LEN
                header_ok = len(data) >= 2 and data[0] == ATB1_H0 and data[1] == ATB1_H1

                row["len_ok"] = int(len_ok)
                row["header_ok"] = int(header_ok)

                if data.startswith(b"AT1;"):
                    row["frame_type"] = "TEXT_AT1"
                    writer.writerow(row)
                    csv_file.flush()
                    continue

                if len_ok and header_ok:
                    row["frame_type"] = "ATB1"

                    try:
                        info = decode_atb1(data)
                        st = source_stats[info["source_id"]]
                        dyn = update_source_stats(st, info, pc_time_unix)

                        row["version"] = info["version"]
                        row["version_ok"] = info["version_ok"]

                        row["source_id"] = info["source_id"]
                        row["seq"] = info["seq"]
                        row["t_ms"] = info["t_ms"]

                        row["flags"] = info["flags"]
                        row["valid"] = info["valid"]
                        row["deadman"] = info["deadman"]
                        row["move_en"] = info["move_en"]
                        row["speed_pct"] = info["speed_pct"]
                        row["fps_hz"] = info["fps_hz"]
                        row["reserved"] = info["reserved"]

                        row["x_m"] = f"{info['x_m']:.6f}"
                        row["y_m"] = f"{info['y_m']:.6f}"
                        row["z_m"] = f"{info['z_m']:.6f}"
                        row["yaw_deg"] = f"{info['yaw_deg']:.6f}"
                        row["yaw_deg_unwrapped"] = f"{info['yaw_deg_unwrapped']:.6f}"
                        row["yaw_rate_dps"] = f"{info['yaw_rate_dps']:.6f}"

                        row["crc_rx"] = f"{info['crc_rx']:04X}"
                        row["crc_calc"] = f"{info['crc_calc']:04X}"
                        row["crc_ok"] = info["crc_ok"]

                        row["dt_rx_ms"] = format_float(dyn["dt_rx_ms"], 3)
                        row["rx_hz_inst"] = format_float(dyn["rx_hz_inst"], 3)
                        row["seq_delta"] = dyn["seq_delta"]
                        row["lost_est"] = dyn["lost_est"]
                        row["duplicate_or_old"] = dyn["duplicate_or_old"]
                        row["gap_over_2s"] = dyn["gap_over_2s"]

                        if info["crc_ok"] and info["version_ok"]:
                            good_atb1_counter += 1

                            if good_atb1_counter % PRINT_EVERY_N_ATB1 == 0:
                                print(
                                    f"ATB1 src={info['source_id']} seq={info['seq']} "
                                    f"valid={info['valid']} x={info['x_m']:.3f} y={info['y_m']:.3f} "
                                    f"yaw={info['yaw_deg']:.2f} "
                                    f"dt={row['dt_rx_ms']}ms crc_ok={info['crc_ok']}"
                                )

                    except struct.error as exc:
                        row["frame_type"] = "ATB1_DECODE_ERROR"
                        row["crc_ok"] = 0
                        print(f"ATB1 decode error from {addr[0]}:{addr[1]}: {exc}")

                    writer.writerow(row)
                    csv_file.flush()
                    continue

                # Wszystko inne zapisujemy jako UNKNOWN,
                # żeby później było wiadomo, czy coś obcego latało po porcie.
                row["frame_type"] = "UNKNOWN"
                writer.writerow(row)
                csv_file.flush()

                unknown_counter += 1
                if PRINT_UNKNOWN:
                    print(
                        f"UNKNOWN len={len(data)} from {addr[0]}:{addr[1]} "
                        f"hex={hex_spaced(data, RAW_HEX_MAX_BYTES)}"
                    )

    except KeyboardInterrupt:
        print("\nPrzerwano Ctrl+C. Zamykanie sesji...")

    finally:
        sock.close()
        write_summary(summary_path, source_stats)

        print("\n================ PODSUMOWANIE SESJI ================")
        print(f"Test/session: {SESSION_NAME}")
        print(f"CSV:          {csv_path}")
        print(f"Summary:      {summary_path}")
        print(f"UNKNOWN:      {unknown_counter}")

        for source_id in sorted(source_stats.keys()):
            st = source_stats[source_id]

            if st.first_good_time and st.last_good_time:
                duration_s = max(0.0, st.last_good_time - st.first_good_time)
            else:
                duration_s = 0.0

            actual_hz = st.rx_good / duration_s if duration_s > 0 else 0.0
            valid_ratio = 100.0 * st.valid_count / st.rx_good if st.rx_good > 0 else 0.0

            dt_mean = sum(st.dt_values_ms) / len(st.dt_values_ms) if st.dt_values_ms else 0.0
            dt_max = max(st.dt_values_ms) if st.dt_values_ms else 0.0

            print(
                f"source_id={source_id}: "
                f"rx_total={st.rx_total}, rx_good={st.rx_good}, "
                f"crc_bad={st.crc_bad}, version_bad={st.version_bad}, "
                f"lost={st.lost_est_total}, dup={st.duplicate_or_old_total}, "
                f"valid_ratio={valid_ratio:.2f}%, "
                f"actual_hz={actual_hz:.2f}, "
                f"dt_mean={dt_mean:.2f}ms, dt_max={dt_max:.2f}ms"
            )

        print("====================================================")


if __name__ == "__main__":
    main()