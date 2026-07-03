#include "DualUdpReceiver.h"

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include <string.h>

namespace
{

WiFiUDP udp_a;
WiFiUDP udp_b;

uint16_t local_port_a = 5005U;
uint16_t local_port_b = 5006U;
uint16_t buffer_capacity = 64U;

bool started_a = false;
bool started_b = false;

uint32_t packet_count_a = 0U;
uint32_t packet_count_b = 0U;

uint32_t last_begin_attempt_ms = 0U;

constexpr uint32_t BEGIN_RETRY_MS = 500U;
constexpr int DISCARD_BUFFER_SIZE = 32;
constexpr int DISCARD_LOOP_LIMIT = 64;


/*
 * Odczytuje maksymalnie jeden datagram UDP w jednym kroku modelu.
 *
 * Zwraca:
 *   0      - brak nowego datagramu
 *   > 0    - liczba zapisanych bajtów
 *
 * packet_counter zwiększa się dokładnie raz na fizycznie odebrany
 * datagram, niezależnie od tego, czy datagram został przycięty.
 */
uint16_t read_datagram(
    WiFiUDP& udp,
    uint8_t* destination,
    uint16_t capacity,
    uint32_t& packet_counter,
    bool& truncated)
{
    truncated = false;

    const int packet_size = udp.parsePacket();

    if (packet_size <= 0)
    {
        return 0U;
    }

    int requested_size = packet_size;

    if (requested_size > static_cast<int>(capacity))
    {
        requested_size = static_cast<int>(capacity);
        truncated = true;
    }

    const int bytes_read = udp.read(
        destination,
        requested_size
    );

    /*
     * Usuń pozostałą część zbyt dużego datagramu.
     * Pętla ma ograniczenie, aby nie blokować kroku modelu.
     */
    uint8_t discard_buffer[DISCARD_BUFFER_SIZE];

    for (int guard = 0;
         guard < DISCARD_LOOP_LIMIT && udp.available() > 0;
         ++guard)
    {
        int remaining = udp.available();

        if (remaining > DISCARD_BUFFER_SIZE)
        {
            remaining = DISCARD_BUFFER_SIZE;
        }

        const int discarded = udp.read(
            discard_buffer,
            remaining
        );

        if (discarded <= 0)
        {
            break;
        }
    }

    if (bytes_read <= 0)
    {
        return 0U;
    }

    ++packet_counter;

    return static_cast<uint16_t>(bytes_read);
}


/*
 * Zamyka oba sockety.
 */
void stop_sockets()
{
    if (started_a)
    {
        udp_a.stop();
        started_a = false;
    }

    if (started_b)
    {
        udp_b.stop();
        started_b = false;
    }

    /*
     * Po ponownym połączeniu Wi-Fi można od razu spróbować
     * uruchomić sockety.
     */
    last_begin_attempt_ms = 0U;
}


/*
 * Próbuje uruchomić sockety UDP.
 * Ponowienie jest ograniczone czasowo, żeby nie wołać begin()
 * w każdym kroku modelu.
 */
void try_start_sockets()
{
    const uint32_t now_ms = millis();

    if (last_begin_attempt_ms != 0U)
    {
        const uint32_t elapsed =
            static_cast<uint32_t>(
                now_ms - last_begin_attempt_ms
            );

        if (elapsed < BEGIN_RETRY_MS)
        {
            return;
        }
    }

    last_begin_attempt_ms = now_ms;

    if (!started_a)
    {
        started_a =
            (udp_a.begin(local_port_a) != 0);
    }

    if (!started_b)
    {
        started_b =
            (udp_b.begin(local_port_b) != 0);
    }
}

} // namespace


extern "C" void dual_udp_init(
    uint16_t port_a,
    uint16_t port_b,
    uint16_t max_packet_size)
{
    stop_sockets();

    local_port_a = port_a;
    local_port_b = port_b;

    buffer_capacity =
        (max_packet_size > 0U)
            ? max_packet_size
            : 1U;

    packet_count_a = 0U;
    packet_count_b = 0U;

    last_begin_attempt_ms = 0U;
}


extern "C" void dual_udp_step(
    uint8_t* data_a,
    uint16_t* len_a,
    uint8_t* data_b,
    uint16_t* len_b,
    uint8_t* status,
    uint32_t* rx_count_a,
    uint32_t* rx_count_b)
{
    /*
     * Każdy krok zaczyna się od pełnego wyzerowania wyjść.
     *
     * Dzięki temu:
     *   len_a == 0 oznacza brak NOWEGO pakietu A w tym kroku,
     *   len_b == 0 oznacza brak NOWEGO pakietu B w tym kroku.
     */
    memset(data_a, 0, buffer_capacity);
    memset(data_b, 0, buffer_capacity);

    *len_a = 0U;
    *len_b = 0U;

    *status = 0U;

    *rx_count_a = packet_count_a;
    *rx_count_b = packet_count_b;

    /*
     * Jeżeli Wi-Fi nie jest połączone, zamknij stare sockety
     * i zakończ krok.
     */
    if (WiFi.status() != WL_CONNECTED)
    {
        stop_sockets();
        return;
    }

    // Bit 0: Wi-Fi połączone
    *status |= 0x01U;

    try_start_sockets();

    if (started_a)
    {
        // Bit 1: socket A / port 5005 działa
        *status |= 0x02U;
    }

    if (started_b)
    {
        // Bit 2: socket B / port 5006 działa
        *status |= 0x04U;
    }

    bool truncated_a = false;
    bool truncated_b = false;

    if (started_a)
    {
        *len_a = read_datagram(
            udp_a,
            data_a,
            buffer_capacity,
            packet_count_a,
            truncated_a
        );
    }

    if (started_b)
    {
        *len_b = read_datagram(
            udp_b,
            data_b,
            buffer_capacity,
            packet_count_b,
            truncated_b
        );
    }

    if (*len_a > 0U)
    {
        // Bit 3: nowy pakiet A w tym kroku
        *status |= 0x08U;
    }

    if (*len_b > 0U)
    {
        // Bit 4: nowy pakiet B w tym kroku
        *status |= 0x10U;
    }

    if (truncated_a)
    {
        // Bit 5: pakiet A był większy niż bufor
        *status |= 0x20U;
    }

    if (truncated_b)
    {
        // Bit 6: pakiet B był większy niż bufor
        *status |= 0x40U;
    }

    /*
     * Liczniki aktualizujemy po odczycie.
     */
    *rx_count_a = packet_count_a;
    *rx_count_b = packet_count_b;
}


extern "C" void dual_udp_terminate(void)
{
    stop_sockets();
}