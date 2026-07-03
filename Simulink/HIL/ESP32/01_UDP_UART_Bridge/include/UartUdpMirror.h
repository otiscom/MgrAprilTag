#ifndef UART_UDP_MIRROR_H
#define UART_UDP_MIRROR_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

void uart_udp_mirror_init(
    uint8_t ip0,
    uint8_t ip1,
    uint8_t ip2,
    uint8_t ip3,
    uint16_t local_port,
    uint16_t remote_port
);

void uart_udp_mirror_step(
    const uint8_t* uart_bytes,
    uint16_t uart_length,
    uint8_t send_enable,
    uint8_t* status,
    uint32_t* enqueued_count,
    uint32_t* sent_count,
    uint32_t* dropped_count,
    uint32_t* error_count
);

void uart_udp_mirror_terminate(void);

#ifdef __cplusplus
}
#endif

#endif