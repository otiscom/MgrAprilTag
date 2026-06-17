clear; clc;

%% =========================================================
%  01 - Parametry pojazdu RC
% =========================================================

m  = 1.5;        % masa [kg]
L  = 0.26;       % rozstaw osi [m]

lf = 0.13;       % CoG -> przednia oś [m]
lr = 0.13;       % CoG -> tylna oś [m]

Jz = 0.015;      % moment bezwładności yaw [kg*m^2], wartość startowa

Caf = 20;        % sztywność boczna przód [N/rad], startowo
Car = 20;        % sztywność boczna tył [N/rad], startowo

Vx_min = 0.1;    % minimalna prędkość do uniknięcia dzielenia przez zero

%% =========================================================
%  02 - Ruch pojazdu / warunki początkowe
% =========================================================

Vx_const = 1.0;              % stała prędkość wzdłużna [m/s]

% Konwencja:
% psi   - orientacja pojazdu / yaw [rad]
% r     - yaw rate = dpsi/dt [rad/s]
% delta - kąt skrętu kół [rad]
% beta  - kąt poślizgu bocznego [rad]
% theta - kąt położenia auta względem środka okręgu [rad], NIE yaw

X0 = 0.4;                          % pozycja X [m]
Y0 = 0.0;                          % pozycja Y [m]
psi0_rad = pi/2 - deg2rad(45);     % orientacja początkowa / yaw [rad]
r0_radps = 0.0;                    % yaw rate początkowy [rad/s]
Vy0 = 0.0;                         % prędkość boczna [m/s]

delta_test_rad = deg2rad(8);       % testowy skręt open-loop [rad]
delta_max_rad  = deg2rad(25);      % maksymalny skręt [rad]

Ts_control = 0.02;                 % 50 Hz, później regulator STM32
Ts_vision  = 0.05;                 % 20 Hz, emulacja Vision / UDP

Tsim = 35;                         % czas symulacji [s]

rad2deg_gain = 180/pi;
deg2rad_gain = pi/180;

%% =========================================================
%  03 - Trajektoria okręgu
% =========================================================

xc = 0.0;              % środek okręgu X [m]
yc = 0.0;              % środek okręgu Y [m]

R_ref = 1.0;           % zadany promień okręgu [m]
circle_dir = 1;        % 1 = CCW, -1 = CW

%% =========================================================
%  04 - Regulator geometryczny V1
% =========================================================
% Pierwszy działający regulator jazdy po okręgu.
%
% Struktura:
%   delta_cmd =
%       delta_ff
%     + korekta od promienia
%     + korekta od yaw/psi
%     - tłumienie yaw_rate
%
% Ten regulator nie jest klasycznym PID-em kaskadowym.
% Jest prostym regulatorem geometrycznym z feedforwardem.
%
% Zaleta:
% - bardzo prosty,
% - dobrze działa w MIL,
% - dość odporny na szum, bo nie ma całki.
%
% Wada:
% - mniej "książkowy" jako struktura regulacji,
% - wszystkie korekcje są wymieszane w jednym równaniu.

K_radius = 0.4;        % wzmocnienie uchybu promienia e_R
K_psi    = 0.8;        % wzmocnienie uchybu orientacji e_psi
K_r      = 0.05;       % tłumienie yaw rate r

%% =========================================================
%  05 - Cascade PID Circle Controller - opis koncepcji
% =========================================================
%
% Cel: bardziej książkowa struktura:
%
%   pętla zewnętrzna:
%       R_ref - R_meas -> PID_Radius -> psi_offset
%
%   pętla wewnętrzna:
%       psi_ref_cmd - psi -> PID_Psi -> delta_corr
%
%   sterowanie:
%       delta_cmd = delta_ff + delta_corr
%
% Łańcuch fizyczny modelu pojazdu:
%
%   delta -> r -> psi -> X,Y -> R_meas
%
% gdzie:
%   delta  - kąt skrętu kół [rad]
%   r      - yaw rate = dpsi/dt [rad/s]
%   psi    - orientacja pojazdu / yaw [rad]
%   X,Y    - pozycja globalna [m]
%   R_meas - odległość od środka okręgu [m]
%
% Dlatego pętla promienia nie powinna bezpośrednio wystawiać delta.
% Pętla promienia wystawia korektę orientacji psi_offset.
% Dopiero pętla psi wystawia korektę skrętu delta_corr.

%% =========================================================
%  06 - Historia strojenia Cascade PID
% =========================================================
% UWAGA:
% Poniższe warianty są zostawione jako historia prób.
% Nie są aktywne, bo są zakomentowane.
% Aktywne wartości są dopiero w sekcji 09.

% ---------------------------------------------------------
% Próba 0 - wartości początkowe
% ---------------------------------------------------------
% Założenie:
% - mocna pętla promienia,
% - dość duży limit psi_offset,
% - wewnętrzna pętla psi jako PD.
%
% Obserwacja:
% - regulator działał, ale był dość nerwowy,
% - delta_cmd często była blisko saturacji,
% - tor był daleki od idealnego okręgu przy zakłóceniach.
%
% Kp_radius_pid = 0.6;
% Ki_radius_pid = 0.0;
% Kd_radius_pid = 0.0;
% psi_offset_max_rad = deg2rad(30);
%
% Kp_psi_pid = 1.0;
% Ki_psi_pid = 0.0;
% Kd_psi_pid = 0.03;
% delta_corr_max_rad = deg2rad(20);

% ---------------------------------------------------------
% Próba 1 - uspokojenie pętli promienia
% ---------------------------------------------------------
% Założenie:
% - pętla promienia ma być wolniejsza,
% - pętla psi ma być szybsza,
% - mniejszy limit korekty psi_offset.
%
% Obserwacja:
% - mniej agresywne zachowanie,
% - ale promień był nadal niedokładnie utrzymywany,
% - pętla promienia była za słaba względem błędu R.
%
% Kp_radius_pid = 0.18;
% Ki_radius_pid = 0.0;
% Kd_radius_pid = 0.0;
% psi_offset_max_rad = deg2rad(12);
%
% Kp_psi_pid = 1.4;
% Ki_psi_pid = 0.0;
% Kd_psi_pid = 0.04;
% delta_corr_max_rad = deg2rad(12);

% ---------------------------------------------------------
% Próba 2 - bardzo łagodna pętla promienia
% ---------------------------------------------------------
% Założenie:
% - jeszcze spokojniejsza pętla zewnętrzna,
% - mniejszy psi_offset,
% - mniej szarpania delta.
%
% Obserwacja:
% - yaw/psi było śledzone poprawnie,
% - ale R_meas zostawał wyraźnie poniżej R_ref,
% - regulator promienia był zbyt słaby.
%
% Kp_radius_pid = 0.10;
% Ki_radius_pid = 0.0;
% Kd_radius_pid = 0.0;
% psi_offset_max_rad = deg2rad(8);
%
% Kp_psi_pid = 1.2;
% Ki_psi_pid = 0.0;
% Kd_psi_pid = 0.015;
% delta_corr_max_rad = deg2rad(10);

% ---------------------------------------------------------
% Próba 3 - wzmocnienie pętli promienia
% ---------------------------------------------------------
% Założenie:
% - skoro R_meas utrzymywał się za nisko, pętla promienia
%   musi mocniej korygować psi_ref.
% - zwiększony limit psi_offset daje regulatorowi większy zakres działania.
%
% Obserwacja:
% - R_meas zbliżył się do R_ref,
% - trajektoria stała się stabilniejsza,
% - psi_ref i psi były dobrze śledzone.
%
% Kp_radius_pid = 0.5;
% Ki_radius_pid = 0.0;
% Kd_radius_pid = 0.0;
% psi_offset_max_rad = deg2rad(20);
%
% Kp_psi_pid = 1.2;
% Ki_psi_pid = 0.0;
% Kd_psi_pid = 0.015;
% delta_corr_max_rad = deg2rad(10);

% ---------------------------------------------------------
% Próba open-loop - weryfikacja feedforward
% ---------------------------------------------------------
% Test bez regulatora:
%   delta_test_rad = atan(L/R_ref)
%
% Dla L = 0.26 m oraz R_ref = 1 m:
%   delta_ff = atan(0.26/1.0) ≈ 14.6 deg
%
% Cel:
% - sprawdzenie, czy sama geometria modelu rowerowego daje
%   promień zbliżony do R_ref.
%
% delta_test_rad = deg2rad(14.6);
% ---------------------------------------------------------
% Próba 4 - wariant spokojniejszy pod realne Vision/UDP
% ---------------------------------------------------------
% Założenie:
% - usunięcie członu D z pętli psi,
% - ograniczenie delta_corr,
% - zachowanie mocniejszej pętli promienia,
% - uzyskanie bardziej gładkiego sterowania pod przyszłe dane z AprilTagów.
%
% Obserwacja:
% - trajektoria pozostaje stabilna,
% - psi dobrze nadąża za psi_ref,
% - delta_cmd jest mniej poszarpane,
% - wariant lepiej nadaje się jako punkt startowy dla realnego auta.
%
% Kp_radius_pid = 0.65;
% Ki_radius_pid = 0.0;
% Kd_radius_pid = 0.0;
% psi_offset_max_rad = deg2rad(18);
%
% Kp_psi_pid = 1.0;
% Ki_psi_pid = 0.0;
% Kd_psi_pid = 0.0;
% delta_corr_max_rad = deg2rad(8);
%% =========================================================
%  07 - Disturbances / Vision Emulator
% =========================================================
% Emulacja błędów pomiaru z aplikacji Vision/AprilTag.
%
% Regulator nie dostaje idealnych sygnałów X/Y/psi z planta,
% tylko sygnały:
%
%   X_meas   = X_true   + dist_x   + noise_x
%   Y_meas   = Y_true   + dist_y   + noise_y
%   psi_meas = psi_true + dist_psi + noise_psi
%
% Pozwala to badać odporność regulatora na:
% - szum pomiarowy,
% - chwilową utratę dokładności,
% - błędy pozycji,
% - błędy orientacji.

% -------------------------
% Chwilowe zakłócenia pozycji
% -------------------------
% Zakłócenie typu step-up/step-down:
%
%   dist_x = Step_X_Up - Step_X_Down
%
% Dzięki temu błąd działa tylko przez określony czas.
% Np. dla X:
%   5-6 s: +0.10 m
%   po 6 s: 0 m

dist_x_step_amp_m  = 0.10;          % chwilowy błąd pomiaru X [m]
dist_y_step_amp_m  = -0.08;         % chwilowy błąd pomiaru Y [m]
dist_psi_step_amp_rad = deg2rad(10); % chwilowy błąd yaw/psi [rad]

dist_x_up_time_s = 5.0;
dist_x_down_time_s = 6.0;

dist_y_up_time_s = 7.0;
dist_y_down_time_s = 8.0;

dist_psi_up_time_s = 9.0;
dist_psi_down_time_s = 10.0;

% Starsze nazwy zostawione dla kompatybilności z istniejącymi blokami Step.
% Jeżeli któryś blok nadal korzysta z dist_x_step_time_s, to model nie wywali błędu.
dist_x_step_time_s = dist_x_up_time_s;
dist_y_step_time_s = dist_y_up_time_s;
dist_psi_step_time_s = dist_psi_up_time_s;

% -------------------------
% Szum pomiarowy
% -------------------------
% W blokach Random Number parametr nazywa się Variance, a nie std.
% Dlatego w blokach wpisujemy:
%
%   noise_x_std_m^2
%   noise_y_std_m^2
%   noise_psi_std_rad^2
%
% Dla noise_x_std_m = 0.005:
%   std = 0.005 m = 5 mm
%   variance = 0.005^2 = 2.5e-5

noise_x_std_m = 0.005;              % 5 mm
noise_y_std_m = 0.005;              % 5 mm
noise_psi_std_rad = deg2rad(0.5);   % 0.5 deg

% -------------------------
% Próbkowanie i opóźnienia Vision
% -------------------------
Ts_vision = 0.05;                   % 20 Hz
vision_delay_s = 0.05;              % 50 ms

% -------------------------
% Opóźnienie aktuatora skrętu
% -------------------------
servo_tau_s = 0.08;                 % stała czasowa serwa [s]

%% =========================================================
%  08 - Testy open-loop / diagnostyczne
% =========================================================
% Ten sygnał jest używany tylko wtedy, gdy regulator jest odpięty,
% a wejście delta planta idzie z bloku delta_test_rad.
%
% Dla sprawdzenia feedforward można użyć:
%   delta_test_rad = deg2rad(14.6);
%
% Dla normalnej pracy regulatora wartość delta_test_rad nie ma znaczenia,
% jeśli nie jest podpięta do wejścia delta.

delta_test_rad = deg2rad(14.6);

%% =========================================================
%  09 - AKTYWNE WARTOŚCI KOŃCOWE
% =========================================================
% To jest aktualnie obowiązujący zestaw parametrów dla Cascade PID.
% Wszystko powyżej w historii strojenia jest zakomentowane.
%
% Wybrany wariant końcowy:
% - bez całki w obu pętlach, żeby uniknąć windupu,
% - pętla promienia jako P,
% - pętla psi jako P, bez członu D,
% - brak D w pętli psi zmniejsza wrażliwość na szum pomiarowy Vision,
% - umiarkowany limit psi_offset,
% - ograniczona korekta delta_corr, żeby nie szarpać sterowaniem.
%
% Ten zestaw jest bardziej "realistyczny" pod późniejsze testy sprzętowe,
% bo dane z telefonu/AprilTagów będą zaszumione i opóźnione.

Kp_radius_pid = 0.65;
Ki_radius_pid = 0.0;
Kd_radius_pid = 0.0;

psi_offset_max_rad = deg2rad(18);

Kp_psi_pid = 1.0;
Ki_psi_pid = 0.0;
Kd_psi_pid = 0.0;

delta_corr_max_rad = deg2rad(8);

%% =========================================================
%  10 - Uwagi do dalszych testów
% =========================================================
%
% Jeżeli R_meas jest stabilny, ale nadal za daleko od R_ref:
%   zwiększ Kp_radius_pid, np. 0.65 -> 0.75
%
% Jeżeli trajektoria zaczyna falować albo robić spiralę:
%   zmniejsz Kp_radius_pid, np. 0.65 -> 0.5
%
% Jeżeli psi nie nadąża za psi_ref:
%   zwiększ Kp_psi_pid, np. 1.0 -> 1.2
%
% Jeżeli delta_cmd szarpie od szumu:
%   zostaw Kd_psi_pid = 0.0
%   ewentualnie zmniejsz Kp_psi_pid lub delta_corr_max_rad
%
% Jeżeli delta_cmd często siedzi na saturacji:
%   zmniejsz delta_corr_max_rad, np. deg2rad(8) -> deg2rad(6)
%
% Jeżeli regulator jest zbyt powolny, ale stabilny:
%   najpierw zwiększ Kp_radius_pid,
%   dopiero potem ewentualnie Kp_psi_pid.


%% =========================================================
%  ESP32 UDP / UART bridge configuration
% =========================================================

UDP_BASE_PORT = 5005;

UDP_PORT_A = UDP_BASE_PORT;       % default: 5005
UDP_PORT_B = UDP_BASE_PORT + 1;   % default: 5006, optional second port

UDP_PAYLOAD_SIZE = 64;            % WiFi UDP Receive limit [bytes]
UDP_SAMPLE_TIME = 0.01;           % 100 Hz polling, margin for 2 phones at ~20 Hz

ATB1_FRAME_SIZE = 42;             % Unity -> ESP32 binary frame [bytes]

MAX_SOURCE_ID = 4;                % prepared for 1..4 phones
ACTIVE_UDP_PORTS = 2;             % PortA and PortB are active

UART_BAUDRATE = 230400;
UART_FRAME_SIZE = 38;             % ESP32 -> STM32, planned
UART_SERIAL_PORT = 2;     % 0 = USB/debug, 1/2 = hardware UART, zależnie od płytki
UART_BAUDRATE = 230400;