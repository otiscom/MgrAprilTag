clear; clc;

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

Vx_const = 1.0;              % stała prędkość wzdłużna [m/s]
delta_test = deg2rad(8);     % testowy skręt [rad]

% Minimalna prędkość do uniknięcia dzielenia przez zero
Vx_min = 0.1;

% =========================
% Warunki początkowe
% =========================

r0   = 0;        % yaw rate [rad/s]
Vy0  = 0;        % prędkość boczna [m/s]
X0   = 0;        % pozycja X [m]
Y0   = 0;        % pozycja Y [m]
psi0 = 0;        % yaw [rad]

% =========================
% Ograniczenia
% =========================

delta_max = deg2rad(25);     % max skręt [rad]

% Czasy
Ts_control = 0.02;           % 50 Hz, później regulator
Ts_vision  = 0.05;           % 20 Hz, później vision