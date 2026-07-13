# ACL-SIM 2.0
 Active Control Loading for Flight Simulator.

In a flight simulator, the Active Control Loading system (ACL) provides forces on the control column (an other axis), equivalent to those that would occur in a real aircraft and thus simulates the necessary haptic feedback for the pilot.

This sofware enables you to build your own ACL system using affordable hardware and free software.
 
<img width="885" height="685" alt="image" src="https://github.com/user-attachments/assets/421af51c-709c-437e-a99a-700bd7571fd2" />


## Software Required:
- [ProSim 737](https://homesim.aero/products/prosim737/)
- Microsfot Flight Simulator 2020, 2024 or P3D v4+

## Hardware Required (1 per axis):
- AC Servo Motor (Nema 34, 750 watts AC motors) 90ST-M02430 Motor. Also 80ST-M02430 or 60ST-M01330 could work
- AASD-15A Servo Driver with RS482/RS485 support (By default not included) 
- [Ethernet RS485 Controller](https://www.amazon.com/dp/B09MBW9WFL?ref_=ppx_hzsearch_conn_dt_b_fed_asin_title_1) 

## Hardware Manual
- [View Manual](https://docs.google.com/document/d/1OZCoD0gJwy6FVb6QmBKL6L0vCG27Hsr1-_H7w8xsUco/edit?usp=sharing)

## Software Manual
- [View Manual](https://github.com/vitaltechsol/ACL-SIM-2/blob/master/MANUAL.md)

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


## Improvements from v1.0:

- **Full RS485 Motor Control**

All motors are now controlled entirely via RS485, eliminating the need for the Arduino-based controller used in v1.0.



- **Simplified Wiring**
 
The system features a streamlined wiring design, reducing complexity and minimizing the risk of connection errors.


- **Enhanced Motion Control**

Advanced motion parameters have been introduced to provide smoother and more precise motor movements.


- **Encoder-Based Accuracy**

Position calculations and calibration now rely on encoder feedback, resulting in significantly improved accuracy compared to the manual scaling factors used in v1.0.


- **Improved Autopilot Disengage Detection**

Autopilot override is now triggered by detecting load changes in the control mechanism, offering a more reliable approach than the position-difference method used previously.


- **Refined Self-Centering and Reverser Detection**
 
Updated algorithms provide more accurate self-centering behavior and improved detection of auto-reverser states.


- **Upgraded User Interface**
 
The UI has been enhanced for easier configuration, better usability, and improved real-time monitoring.
