import socket
import time
from datetime import datetime


PORT = 5012

PHONE1_IP = "192.168.0.172"   # source_id=1 / operator
PHONE2_IP = "192.168.0.63"  # source_id=2 / observer

DURATION_S = 15
COUNTDOWN_S = 6
CYCLE_ON_S = 3.5
CYCLE_OFF_S = 1.5

REPEAT_SEND = 5
REPEAT_DELAY_S = 0.05


def send_to(ip: str, message: str) -> None:
    data = message.encode("ascii")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    try:
        for _ in range(REPEAT_SEND):
            sock.sendto(data, (ip, PORT))
            time.sleep(REPEAT_DELAY_S)
    finally:
        sock.close()

    now = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[{now}] SENT x{REPEAT_SEND} -> {ip}:{PORT}")
    print(f"MSG: {message}")


def send_pair(phone1_msg: str, phone2_msg: str) -> None:
    data1 = phone1_msg.encode("ascii")
    data2 = phone2_msg.encode("ascii")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    try:
        for _ in range(REPEAT_SEND):
            sock.sendto(data1, (PHONE1_IP, PORT))
            sock.sendto(data2, (PHONE2_IP, PORT))
            time.sleep(REPEAT_DELAY_S)
    finally:
        sock.close()

    now = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[{now}] SENT PAIR x{REPEAT_SEND}")
    print(f"  PHONE1 {PHONE1_IP}:{PORT} -> {phone1_msg}")
    print(f"  PHONE2 {PHONE2_IP}:{PORT} -> {phone2_msg}")


def msg_start_hold() -> str:
    return f"cmd=START;duration={DURATION_S};countdown={COUNTDOWN_S};dm=hold"


def msg_start_cycle() -> str:
    return (
        f"cmd=START;duration={DURATION_S};countdown={COUNTDOWN_S};"
        f"dm=cycle;on={CYCLE_ON_S};off={CYCLE_OFF_S}"
    )


def msg_stop() -> str:
    return "STOP"


def main() -> None:
    print("UDP Measurement Command Sender — direct per phone")
    print(f"PHONE1/source1/operator: {PHONE1_IP}:{PORT}")
    print(f"PHONE2/source2/observer: {PHONE2_IP}:{PORT}")
    print()
    print("Komendy:")
    print("  s  -> START: phone1 CYCLE, phone2 HOLD")
    print("  1  -> START tylko phone1 CYCLE")
    print("  2  -> START tylko phone2 HOLD")
    print("  h  -> START oba HOLD")
    print("  c  -> START oba CYCLE")
    print("  0  -> STOP oba")
    print("  01 -> STOP phone1")
    print("  02 -> STOP phone2")
    print("  q  -> quit")
    print()

    while True:
        cmd = input("cmd> ").strip().lower()

        if not cmd:
            continue

        if cmd in ("q", "quit", "exit"):
            break

        if cmd == "s":
            # Najważniejszy tryb:
            # source1/operator robi cykliczny deadman,
            # source2/observer startuje pomiar z hold.
            send_pair(
                msg_start_cycle(),
                msg_start_hold()
            )
            continue

        if cmd == "1":
            send_to(PHONE1_IP, msg_start_cycle())
            continue

        if cmd == "2":
            send_to(PHONE2_IP, msg_start_hold())
            continue

        if cmd == "h":
            send_pair(
                msg_start_hold(),
                msg_start_hold()
            )
            continue

        if cmd == "c":
            send_pair(
                msg_start_cycle(),
                msg_start_cycle()
            )
            continue

        if cmd == "0":
            send_pair(
                msg_stop(),
                msg_stop()
            )
            continue

        if cmd == "01":
            send_to(PHONE1_IP, msg_stop())
            continue

        if cmd == "02":
            send_to(PHONE2_IP, msg_stop())
            continue

        print("Nieznana komenda.")


if __name__ == "__main__":
    main()