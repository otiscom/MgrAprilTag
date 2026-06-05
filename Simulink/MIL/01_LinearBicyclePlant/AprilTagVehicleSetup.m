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