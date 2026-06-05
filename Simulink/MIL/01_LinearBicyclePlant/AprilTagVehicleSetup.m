clear; clc;

%% =========================================================
%  01 - Parametry pojazdu RC
% =========================================================

% =========================
% Parametry pojazdu RC
% =========================

m  = 1.5;        % masa [kg]
L  = 0.26;       % rozstaw osi [m]

lf = 0.13;       % CoG -> przednia oś [m]
lr = 0.13;       % CoG -> tylna oś [m]

Jz = 0.015;      % moment bezwładności yaw [kg*m^2], wartość startowa

Caf = 20;        % sztywność boczna przód [N/rad], startowo
Car = 20;        % sztywność boczna tył [N/rad], startowo

% Minimalna prędkość do uniknięcia dzielenia przez zero
Vx_min = 0.1;

% =========================
% Prędkość jazdy
% =========================

Vx_const = 1.0;              % stała prędkość wzdłużna [m/s]

% =========================
% Konwencja kątów
% =========================
%
% psi   - orientacja pojazdu / yaw [rad]
% r     - yaw rate = dpsi/dt [rad/s]
% delta - kąt skrętu kół [rad]
% beta  - kąt poślizgu bocznego [rad]
% theta - kąt położenia auta względem środka okręgu [rad], NIE yaw
%

% =========================
% Warunki początkowe
% =========================

X0 = 0.4;                          % pozycja X [m]
Y0 = 0.0;                          % pozycja Y [m]

psi0_rad = pi/2 - deg2rad(45);     % orientacja początkowa / yaw [rad]
r0_radps = 0.0;                    % yaw rate początkowy [rad/s]

Vy0 = 0.0;                         % prędkość boczna [m/s]

% =========================
% Skręt
% =========================

delta_test_rad = deg2rad(8);       % testowy skręt open-loop [rad]
delta_max_rad  = deg2rad(25);      % maksymalny skręt [rad]

% =========================
% Czasy
% =========================

Ts_control = 0.02;                 % 50 Hz, później regulator
Ts_vision  = 0.05;                 % 20 Hz, później vision

% =========================================================
%  02 - Circle Controller MIL
%  Jazda po okręgu bez driftu
% =========================================================

% =========================
% Trajektoria okręgu
% =========================

xc = 0.0;              % środek okręgu X [m]
yc = 0.0;              % środek okręgu Y [m]

R_ref = 1.0;           % zadany promień okręgu [m]
circle_dir = 1;        % 1 = CCW, -1 = CW

% =========================
% Regulator okręgu
% =========================

K_radius = 0.4;        % wzmocnienie uchybu promienia e_R
K_psi    = 0.8;        % wzmocnienie uchybu orientacji e_psi
K_r      = 0.05;       % tłumienie yaw rate r

% =========================
% Symulacja
% =========================

Tsim = 15;             % czas symulacji [s]

% =========================
% Konwersje jednostek
% =========================

rad2deg_gain = 180/pi;
deg2rad_gain = pi/180;

% =========================================================
%  03 - Cascade PID Circle Controller
%  Zewnętrzny PID promienia + wewnętrzny PID psi
% =========================================================
%
% Łańcuch fizyczny modelu pojazdu:
%
%   delta -> r -> psi -> X,Y -> R_meas
%
% gdzie:
%   delta - kąt skrętu kół [rad]
%   r     - yaw rate = dpsi/dt [rad/s]
%   psi   - orientacja pojazdu / yaw [rad]
%   X,Y   - pozycja globalna [m]
%   R_meas - odległość od środka okręgu [m]
%
% Dlatego pętla promienia nie wystawia bezpośrednio delta.
% Pętla promienia wystawia korektę orientacji psi_offset.
% Dopiero pętla psi wystawia korektę skrętu delta_corr.

% =========================
% PID promienia
% =========================
%
% e_R = R_ref - R_meas
%
% Dla zachowania klasycznego uchybu e_R zostaje do wykresów.
% Do PID promienia podajemy:
%
%   e_R_pid = -circle_dir * e_R
%
% dzięki temu:
% - gdy auto jest za blisko środka, regulator odchyla psi na zewnątrz,
% - gdy auto jest za daleko, regulator odchyla psi do środka.

Kp_radius_pid = 0.6;        % [rad/m]
Ki_radius_pid = 0.0;        % [rad/(m*s)] startowo 0
Kd_radius_pid = 0.0;        % [rad*s/m] startowo 0

psi_offset_max_rad = deg2rad(30);

% =========================
% PID orientacji psi
% =========================
%
% e_psi = wrapToPi(psi_ref_cmd - psi)
%
% Wyjście PID_Psi to korekta skrętu:
%
%   e_psi -> PID_Psi -> delta_corr_rad

Kp_psi_pid = 1.0;           % [rad_delta/rad_psi]
Ki_psi_pid = 0.0;           % startowo 0
Kd_psi_pid = 0.03;          % lekkie tłumienie, można dać 0 jeśli szarpie

delta_corr_max_rad = deg2rad(20);

% =========================================================
%  04 - Disturbances / Vision Emulator
% =========================================================

% =========================
% Zakłócenia pozycji z Vision
% =========================

dist_x_step_time_s = 5.0;
dist_x_step_amp_m  = 0.10;          % sztuczny błąd pomiaru X [m]

dist_y_step_time_s = 7.0;
dist_y_step_amp_m  = -0.08;         % sztuczny błąd pomiaru Y [m]

% =========================
% Zakłócenie orientacji psi
% =========================

dist_psi_step_time_s = 9.0;
dist_psi_step_amp_rad = deg2rad(10);    % sztuczny błąd yaw/psi [rad]

% =========================
% Szum pomiarowy
% =========================

noise_x_std_m = 0.005;              % 5 mm
noise_y_std_m = 0.005;              % 5 mm
noise_psi_std_rad = deg2rad(0.5);   % 0.5 deg

% =========================
% Vision sample time
% =========================

Ts_vision = 0.05;                   % 20 Hz

% =========================
% Opóźnienie pomiaru
% =========================

vision_delay_s = 0.05;              % 50 ms

% =========================
% Opóźnienie serwa / aktuatora skrętu
% =========================

servo_tau_s = 0.08;                 % stała czasowa serwa [s]