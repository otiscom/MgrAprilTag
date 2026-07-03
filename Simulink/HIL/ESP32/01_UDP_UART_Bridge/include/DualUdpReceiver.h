#ifndef DUAL_UDP_RECEIVER_H
#define DUAL_UDP_RECEIVER_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

void dual_udp_init(
    uint16_t port_a,
    uint16_t port_b,
    uint16_t max_packet_size
);

void dual_udp_step(
    uint8_t* data_a,
    uint16_t* len_a,
    uint8_t* data_b,
    uint16_t* len_b,
    uint8_t* status,
    uint32_t* rx_count_a,
    uint32_t* rx_count_b
);

void dual_udp_terminate(void);

#ifdef __cplusplus
}
#endif

#endif