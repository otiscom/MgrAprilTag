#include "UartUdpMirror.h"

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include <string.h>

namespace
{

constexpr uint16_t MAX_PAYLOAD_SIZE = 64U;
constexpr UBaseType_t QUEUE_LENGTH = 32U;
constexpr uint32_t RECEIVE_TIMEOUT_MS = 100U;

struct MirrorPacket
{
    uint16_t length;
    uint8_t data[MAX_PAYLOAD_SIZE];
};

WiFiUDP mirror_udp;
IPAddress remote_ip;

uint16_t mirror_local_port = 5010U;
uint16_t mirror_remote_port = 5010U;

QueueHandle_t mirror_queue = nullptr;
TaskHandle_t mirror_task_handle = nullptr;

volatile bool mirror_task_running = false;
bool socket_started = false;

volatile uint32_t packet_enqueued_count = 0U;
volatile uint32_t packet_sent_count = 0U;
volatile uint32_t packet_dropped_count = 0U;
volatile uint32_t packet_error_count = 0U;


/*
 * Zamyka socket UDP. Wywoływane tylko przez task nadawczy
 * albo po jego zatrzymaniu.
 */
void stop_socket()
{
    if (socket_started)
    {
        mirror_udp.stop();
        socket_started = false;
    }
}


/*
 * Próbuje uruchomić lokalny socket nadawczy.
 */
bool ensure_socket_started()
{
    if (socket_started)
    {
        return true;
    }

    socket_started =
        (mirror_udp.begin(mirror_local_port) != 0);

    return socket_started;
}


/*
 * Osobny task FreeRTOS.
 *
 * Model Simulink tylko wkłada ramkę do kolejki. Rzeczywiste
 * beginPacket/write/endPacket jest wykonywane tutaj, dzięki
 * czemu chwilowo wolne Wi-Fi nie blokuje kroku modelu.
 */
void mirror_sender_task(void* parameter)
{
    (void)parameter;

    MirrorPacket packet;

    while (mirror_task_running)
    {
        const BaseType_t received =
            xQueueReceive(
                mirror_queue,
                &packet,
                pdMS_TO_TICKS(RECEIVE_TIMEOUT_MS)
            );

        if (received != pdTRUE)
        {
            continue;
        }

        if (WiFi.status() != WL_CONNECTED)
        {
            stop_socket();
            ++packet_error_count;
            continue;
        }

        if (!ensure_socket_started())
        {
            ++packet_error_count;
            continue;
        }

        const int begin_ok =
            mirror_udp.beginPacket(
                remote_ip,
                mirror_remote_port
            );

        if (begin_ok != 1)
        {
            ++packet_error_count;
            continue;
        }

        const size_t written =
            mirror_udp.write(
                packet.data,
                packet.length
            );

        const int end_ok = mirror_udp.endPacket();

        if (written == packet.length && end_ok == 1)
        {
            ++packet_sent_count;
        }
        else
        {
            ++packet_error_count;
        }
    }

    stop_socket();

    mirror_task_handle = nullptr;
    vTaskDelete(nullptr);
}

} // namespace


extern "C" void uart_udp_mirror_init(
    uint8_t ip0,
    uint8_t ip1,
    uint8_t ip2,
    uint8_t ip3,
    uint16_t local_port,
    uint16_t remote_port)
{
    uart_udp_mirror_terminate();

    remote_ip = IPAddress(ip0, ip1, ip2, ip3);

    mirror_local_port = local_port;
    mirror_remote_port = remote_port;

    packet_enqueued_count = 0U;
    packet_sent_count = 0U;
    packet_dropped_count = 0U;
    packet_error_count = 0U;

    socket_started = false;

    mirror_queue =
        xQueueCreate(
            QUEUE_LENGTH,
            sizeof(MirrorPacket)
        );

    if (mirror_queue == nullptr)
    {
        ++packet_error_count;
        return;
    }

    mirror_task_running = true;

    const BaseType_t task_created =
        xTaskCreatePinnedToCore(
            mirror_sender_task,
            "uart_udp_mirror",
            4096,
            nullptr,
            1,
            &mirror_task_handle,
            0
        );

    if (task_created != pdPASS)
    {
        mirror_task_running = false;
        mirror_task_handle = nullptr;

        vQueueDelete(mirror_queue);
        mirror_queue = nullptr;

        ++packet_error_count;
    }
}


extern "C" void uart_udp_mirror_step(
    const uint8_t* uart_bytes,
    uint16_t uart_length,
    uint8_t send_enable,
    uint8_t* status,
    uint32_t* enqueued_count,
    uint32_t* sent_count,
    uint32_t* dropped_count,
    uint32_t* error_count)
{
    /*
     * status:
     * bit0 = Wi-Fi połączone
     * bit1 = kolejka istnieje
     * bit2 = ramka dodana do kolejki w tym kroku
     * bit3 = ramka odrzucona w tym kroku
     * bit4 = task nadawczy działa
     */

    *status = 0U;

    if (WiFi.status() == WL_CONNECTED)
    {
        *status |= 0x01U;
    }

    if (mirror_queue != nullptr)
    {
        *status |= 0x02U;
    }

    if (mirror_task_running)
    {
        *status |= 0x10U;
    }

    if (send_enable != 0U)
    {
        if (mirror_queue == nullptr ||
            uart_bytes == nullptr ||
            uart_length == 0U ||
            uart_length > MAX_PAYLOAD_SIZE)
        {
            ++packet_dropped_count;
            *status |= 0x08U;
        }
        else
        {
            MirrorPacket packet;

            packet.length = uart_length;

            memcpy(
                packet.data,
                uart_bytes,
                uart_length
            );

            /*
             * Timeout = 0:
             * nigdy nie czekamy na wolne miejsce w kolejce.
             */
            const BaseType_t queued =
                xQueueSend(
                    mirror_queue,
                    &packet,
                    0
                );

            if (queued == pdTRUE)
            {
                ++packet_enqueued_count;
                *status |= 0x04U;
            }
            else
            {
                ++packet_dropped_count;
                *status |= 0x08U;
            }
        }
    }

    *enqueued_count = packet_enqueued_count;
    *sent_count = packet_sent_count;
    *dropped_count = packet_dropped_count;
    *error_count = packet_error_count;
}


extern "C" void uart_udp_mirror_terminate(void)
{
    mirror_task_running = false;

    /*
     * Pozwól taskowi wyjść z xQueueReceive i zakończyć się samemu.
     */
    const uint32_t wait_start = millis();

    while (mirror_task_handle != nullptr)
    {
        if (static_cast<uint32_t>(millis() - wait_start) > 200U)
        {
            vTaskDelete(mirror_task_handle);
            mirror_task_handle = nullptr;
            break;
        }

        delay(1);
    }

    stop_socket();

    if (mirror_queue != nullptr)
    {
        vQueueDelete(mirror_queue);
        mirror_queue = nullptr;
    }
}