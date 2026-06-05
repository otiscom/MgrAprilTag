\# 01\_LinearBicyclePlant



Pierwszy model MIL pojazdu RC w Simulinku.



\## Cel



Model sprawdza podstawową dynamikę pojazdu w uproszczonym modelu rowerowym.



Wejścia:

\- `delta` — kąt skrętu \[rad]

\- `Vx` — prędkość wzdłużna \[m/s]



Wyjścia:

\- `X` — pozycja globalna X \[m]

\- `Y` — pozycja globalna Y \[m]

\- `psi` — orientacja pojazdu \[rad]

\- `r` — yaw rate \[rad/s]

\- `Vy` — prędkość boczna \[m/s]



\## Etap



MIL — Model-in-the-loop.



Model nie zawiera jeszcze:

\- ESP32,

\- STM32,

\- UART,

\- watchdogów,

\- regulatora jazdy po okręgu.

