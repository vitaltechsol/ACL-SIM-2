# ACL-SIM 2.0
 Active Control Loading for Flight Simulator.

## Software Required:
- Prosim-AR 737
- Microsfot Flight Simulator 2020, 2024 or P3D v4+

## Hardware Required:
- AC Servo Motors with RS485 option.
- RS485 Controller.


## Features 
 
| Axis        | Feature                                           | Status   |
| ----------- | ------------------------------------------------- | -------- |
| Pitch Axis  |                                                   |          |
|             | Self-centering                                    | Complete |
|             | Load increases as the control moves away          | Complete |
|             | Load increases when hydraulics are not available  | Complete |
|             | Load increases with airspeed                      | Complete |
|             | Fwd Load increases when approaching a stall       | Complete |
|             | Autopilot moves control column                    | Complete |
|             | Autopilot disengage override by moving the control| Complete |
|             | Column pitch stays fixed with hydraulics off      | Complete |
|             | Center calibration when starting the sim          | Complete |
| Roll Axis   |                                                   |          |
|             | Self-centering                                    | Complete |
|             | Load increases as the control moves away          | Complete |
|             | Load increases when hydraulics are not available  | Complete |
|             | Autopilot moves control wheel                     | Complete |
|             | Autopilot disengage override by moving the control| Complete |
|             | Trim Adjustment moves Control wheel               | Complete |
|             | Center calibration when starting the sim          | Complete |
| Rudder Axis |                                                   |          |
|             | Self-centering                                    | Complete |
|             | Load increases as the control moves away          | Complete |
|             | Load increased when hydraulics are not available  | Complete |
|             | Trim adjustment moves rudders                     | Complete |
|             | Center calibration when starting the sim          | Complete |
| Tiller      |                                                   |          |
|             | Self-centering                                    | Complete |
|             | Load increases as the control moves away          | Complete |
|             | Load increases when hydraulics are not available  | Complete |
|             | Center calibration when starting the sim          | Complete |
|             | Center calibration when starting the sim          | Complete |


## Improvemens from v1.0:
- All motors are now fully controlled via RS485 (instead of using arduion controller in v1).
- Simpler wiring for less error prone connections.
- Advanced motion settings for smoother motor movements.
- Encoder position based calculations and calibration for more accurate values (instead of manual factor values in v1)
- Auto pilot disengage override by detecting load changes in the control (instead of position difference changes in v1)
- Better and more accurate self-centering logic and auto reverser detection.
- Improved UI for easier configuration and monitoring.
