# First successful ESP32 UDP bridge deploy

Date: 2026-07-02

Test setup:

- Unity/Android transmitted binary ATB1 frames over UDP.
- ESP32 received UDP traffic on local port 5005.
- ESP32 successfully deployed from Simulink.
- ESP32 transmitted ATD1 diagnostic frames to the PC logger.
- PortB was temporarily replaced by a fixed-size dummy input.
- STM32 was not connected during this test.

Files:

- esp32_udp_diag.csv — ATD1 diagnostic frames received from ESP32.
- unity_atb1_rx.csv — ATB1 frames observed by the PC logger.

Known limitation:

ATD1 initially reported source_id 1 while Unity transmitted source_id 3, so the ESP32 per-source counters remained zero for the reported source.
