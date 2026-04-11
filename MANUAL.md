# ACL-SIM 2.0 – User Manual

> **Active Control Loading for Flight Simulator**
> AASD servo motors controlled over RS-485 via ProSim-AR 737.

---

## Table of Contents

1. [Installation](#1-installation)
2. [Quick Start](#2-quick-start)
3. [Main Window](#3-main-window)
4. [Settings](#4-global-settings)
5. [Axis Setup – How to Open](#5-axis-setup--how-to-open)
6. [Axis Setup – Connection](#6-axis-setup--connection)
7. [Axis Setup – Position Calibration](#7-axis-setup--position-calibration)
8. [Axis Setup – Torque Settings](#8-axis-setup--torque-settings)
9. [Axis Setup – Self Centering / Movement](#9-axis-setup--self-centering--movement)
10. [Axis Setup – Auto Pilot / Trim](#10-axis-setup--auto-pilot--trim)
11. [Axis Setup – Hydraulics](#11-axis-setup--hydraulics)
12. [Axis Setup – Position Movement Test](#12-axis-setup--position-movement-test)
13. [Axis Setup – Pitch Extras *(Pitch only)*](#13-axis-setup--pitch-extras-pitch-only)
14. [Per-Axis Feature Summary](#14-per-axis-feature-summary)
15. [Saving Settings](#15-saving-settings)

---

## 1. Installation

1. Download the latest release zip from [https://github.com/vitaltechsol/ACL-SIM-2/releases/](https://github.com/vitaltechsol/ACL-SIM-2/releases/).
2. Extract the zip archive and copy the extracted folder to your preferred location (e.g. `C:\ACL-SIM-2`).
3. Double-click `ACL-SIM-2.exe` to launch the application.

> **Note:** No installer is required. All settings are stored in your Windows roaming profile (`%APPDATA%\ACL-SIM-2`) and are preserved across updates.

---

## 2. Quick Start

Follow these steps to get up and running for the first time:

1. Make sure all your axis are fully calibrated in Windows USB Joysticks and Prosim Flight controls.
2. Click **Settings** in the top-left of the main window, enter the ProSim IP address, then click **Save**.
3. Click **Connect** to establish a connection to ProSim.
4. On the main window, check the **Enable** checkbox next to the axis you want to configure (Pitch, Roll, Rudder, or Tiller).
5. Click **Setup** for that axis, enter the **RS485 IP** address of the gateway adapter and select the correct **Driver ID** for the motor driver.
6. Click **Calibrate** and follow the on-screen steps to define the center, full-left, and full-right positions.
7. Review the remaining settings (Torque, Self Centering, Autopilot, etc.) and click **Save** when finished.
8. Repeat steps 3–6 for each additional axis.
9. Click **Center All** on the main window to home all enabled axes.
10. The system is ready to use.

> **Tip:** Enable **Auto Connect** and **Auto Center on Startup** in Settings for a fully automated startup routine on subsequent sessions.

---

## 3. Main Window

The main window shows a live dashboard for all four axes: **Pitch**, **Roll**, **Rudder**, and **Tiller**.

| Element | Description |
|---|---|
| **Settings** | Opens the global settings window. |
| **Center All** | Commands all enabled axes to find their physical center point simultaneously. The simulator is paused during centering. |
| **Connect / Disconnect** | Connects or disconnects from ProSim. |
| **Enable checkbox** | Enables or disables an axis. Disabled axes send no motor commands. |
| **Setup button** | Opens the Axis Setup window for that axis. |
| **Encoder** value | Raw encoder position relative to the calibrated center (0 = center). |
| **Encoder bar** | Visual bar showing how far off center the axis is. |
| **Torque** display | Current torque level being sent to the motor (0–100 display scale). |
| **% from center** | How far the control is from center as a percentage of its full range. |
| **RS485 dot** | Green = connected to the motor driver. Gray/red = disconnected. |
| **Hydraulics dot** | Green = hydraulics available in ProSim. Red = hydraulics off. |
| **Auto Pilot dot** | Green = ProSim autopilot is engaged on this axis. |
| **Moving dot** | Orange = motor is actively commanding movement. |

---

## 4. Global Settings

Open by clicking **Settings** in the top-left of the main window.

| Setting | Default | Description |
|---|---|---|
| **ProSim IP** | `127.0.0.1` | IP address of the machine running ProSim-AR. Use `127.0.0.1` if on the same PC. |
| **Auto connect to ProSim** | Off | When enabled, ACL-SIM automatically connects to ProSim when the application starts. Saves one manual click per session. |
| **Auto center axis on startup** | Off | When enabled, *Center Controls* runs automatically after ProSim connects on startup. Useful for a fully automated startup routine. Requires hydraulics to be available and ProSim to be running. |

> **Tip – Auto Connect + Auto Center:** Enable both for a hands-off startup. The application will connect to ProSim and immediately center all axes without any interaction.

> **Warning – Auto Center:** If the simulator is not yet in a stable state (e.g., still loading), auto-centering may produce incorrect results. Disable this and use the manual **Center Controls** button instead if you experience issues.

Click **Save** to persist changes. Changes take effect on the next application start.

---

## 5. Axis Setup – How to Open

1. Locate the axis panel on the main window (Pitch, Roll, Rudder, or Tiller).
2. Make sure the axis **Enable** checkbox is checked.
3. Click the **Setup** button on that panel.
4. The Axis Setup window opens. The window title shows the axis name.

Each axis has its own independent setup window. You can open multiple at the same time.

> **Live Preview:** Many sliders in the Auto Pilot / Trim section apply changes to the motor immediately while autopilot or a position test is active. You do not need to save first to hear the effect — but you **must click Save** to persist the change between sessions.

---

## 6. Axis Setup – Connection

These settings tell ACL-SIM how to communicate with the motor driver for this specific axis.

| Setting | Range | Description |
|---|---|---|
| **RS485 IP** | Any valid IP | IP address of the RS-485 to Ethernet gateway. Each axis should have it's own adapter/IP. |
| **Driver ID** | 1 – 10 | Modbus unit identifier (slave address) for this axis's AASD servo driver. Each driver on the bus must have a unique ID. Typically: Pitch=1, Roll=2, Rudder=3, Tiller=4. |
| **Motor Reversed** | On/Off | Flips the encoder sign and all movement directions. Enable if the motor is wired or physically mounted in reverse — the encoder bar and all percentages will still read correctly after enabling this. |

> **Changing IP or Driver ID:** If you change either of these while the application is running, close and reopen the setup window (or restart the application) to reconnect with the new settings.

---

## 7. Axis Setup – Position Calibration

This section defines the physical travel range of the control. Calibration must be performed once for each axis before normal operation.

### Live Displays

| Display | Description |
|---|---|
| **Current Encoder** | Live encoder value, shifted so that center reads `0.00` during calibration. |
| **ProSim Position** | Current ProSim axis position mapped to −100% … +100%. **Green** when ProSim reads near center (within ±10%). **Red** otherwise. |
| **Full Left / Center / Full Right** | The three calibrated reference points. Center is always `0`. Left is always negative. Right is always positive. |

### Calibration Procedure

Click **Calibrate** to enter calibration mode. Torque is set to zero during calibration so you can move the controls freely.

Follow the sequential steps that appear:

| Step | Button | Action |
|---|---|---|
| **1** | Set Center | Hold the control at the neutral/center position and click. This defines encoder `0`. |
| **2** | Set Full Left (Pitch: Set Full Forward) | Move the control to full left (or full forward for pitch) and click. Stores the negative distance from center. |
| **3** | Set Full Right (Pitch: Set Full Back) | Move the control to full right (or full back for pitch) and click. Stores the positive distance from center. |
| **4** | Done | Return the control to center, release it, then click **Done** to exit calibration. |

> **Why relative distances?** The system stores Full Left and Full Right as encoder counts *relative to center* (not absolute values). This means if you run **Center Controls** again later, the full travel range scales correctly with the new center — you do not need to recalibrate after every centering run.

> **Pitch naming:** For the pitch axis the labels read *Full Forward* (nose down / stick forward) and *Full Back* (nose up / stick back) instead of Left/Right.

---

## 8. Axis Setup – Torque Settings

Controls how much resistance the motor applies at rest, as a function of how far the control is deflected from center.

**Scale:** All values are 0–100 (display). Internally mapped to 0–300 (actual motor torque units).

| Setting | Default | Range | Description |
|---|---|---|---|
| **Min Torque** | 5 | 0 – 100 | Torque applied at dead center. This is the baseline "feel" with the control untouched. |
| **Max Torque** | 30 | 0 – 100 | Torque applied at full deflection (Full Left or Full Right). |

Between center and the limits, torque increases linearly from Min to Max proportional to how far the control is displaced.

> **Tip:** A typical realistic setup uses a Min around 5–15 and a Max around 25–50. The difference between Min and Max determines how much the feel changes across travel.

---

## 9. Axis Setup – Self Centering / Movement

Controls the motor's built-in self-centering behavior (the return-to-center spring force).

| Setting | Default | Range | Internal Scale | Description |
|---|---|---|---|---|
| **Self Centering Speed** | 50 | 1 – 100 | 0 – 250 (actual) | How quickly the motor returns the control to center when released. Sent directly to the motor's centering speed register. |
| **Dampening** | 10 | 0 – 100 | 5 – 1000 (actual) | The motor's built-in damping coefficient. Higher values resist rapid control movements more. |

### Self Centering Speed – Pros and Cons

**Low (1–20):**
- Gentle, slow return to center but might have too much drag.
- Less abrupt snap-back when the pilot releases the controls.

**High (60–100):**
- At very high values, the control may overshoot center and oscillate briefly.

> **Note for Pitch axis:** When hydraulics are off in ProSim, the centering speed is automatically set to `0` (motor locks, simulating hydraulic jam). The value is restored to your configured setting when hydraulics come back on. This behavior is unique to Pitch.

### Dampening – Pros and Cons

Adjust to remove oscillations when releasiing the controls.

---

## 10. Axis Setup – Auto Pilot / Trim

Configures how the motor moves when following autopilot commands or trim changes. Changes here update the motor **live** while autopilot or a position test is active.

---

### Autopilot Override % *(Pitch and Roll only)*

| Default | Range |
|---|---|
| 5% | 1 – 100% |

When the autopilot is engaged, the motor actively holds the controls at the commanded position. If the pilot physically pushes against the control with enough force, the autopilot should disengage.

When the motor's measured external torque, autopilot is disengaged via ProSim and the control returns to center.

**Low (1–10%):**
- ✅ Very light touch disengages autopilot.
- ❌ Any slight vibration or heavy hand can accidentally disengage it.

**High (30–100%):**
- ✅ Requires a deliberate, firm push to disengage — avoids accidental disconnects.
- ❌ The pilot must exert significant force before override triggers.
- ❌ If set too high the pilot may not be able to overcome it at all.

> **Tip:** Start around 25–25%. Increase gradually if false overrides occur during turbulence.

---

### Motor Max Speed

| Default | Range | Snap |
|---|---|---|
| 8 RPM | 1 – 50 RPM | Whole numbers only |

The maximum RPM the servo motor uses when moving to an autopilot target or trim position. The software ramp accelerates from 1 RPM up to this value.

**Low (1–10 RPM):**
- ✅ Very smooth, slow movement
- ❌ May be too slow to keep up with fast autopilot corrections or large trim changes and won't be at a realistic accurate position.

**High (10–50 RPM):**
- ✅ Fast tracking of large autopilot change commands.
- ❌ Abrupt movement; the acceleration ramp helps but fast RPM still feels less smooth.
- ❌ The motor may overshoot the target and oscillate if speed is much higher than needed.

---

### Moving Torque

| Default | Range |
|---|---|
| 20% | 0 – 100% |

The torque sent to the motor while it is actively moving — during autopilot tracking, trim movement, centering, or a position test. This replaces the normal position-based torque (Min/Max Torque) for the duration of the movement.

**Low (0–15%):**
- ❌ Movement will not be as smooth
- ✅ Autopilot override will trip more easily.

**High (40–100%):**
- ✅ Motor holds its position firmly against any hand pressure during autopilot.
- ❌ Pushing against an engaged autopilot requires significant force.
- ❌ If set too high will be hard to override by hand.

---

### Motion Smoothing

| Default | Range |
|---|---|
| ~80% | 0 – 100% |

A low-pass filter applied to the incoming ProSim target position before the motor acts on it. Internally this maps inversely to `TargetFilterAlpha`: 0% smoothing → alpha=1.0 (no filter); 100% smoothing → alpha=0.0 (maximum filter).

Each loop iteration: `smoothed = alpha × raw + (1 − alpha) × previous`

**0% (no smoothing, alpha=1.0):**
- ✅ Instant reaction to any ProSim target change.
- ❌ Every tiny jitter or rapid oscillation in the ProSim data is sent directly to the motor.
- ❌ The control column can twitch or shake with noisy data.

**50–80% (recommended):**
- ✅ Balances responsiveness with stability — the motor follows trends without chasing noise.
- ✅ Autopilot corrections feel smooth and natural.

**90–100% (maximum smoothing, alpha near 0):**
- ✅ Eliminates virtually all noise; very fluid motion.
- ❌ Very slow to track large or rapid corrections — the motor lags significantly behind.
- ❌ At 100% the motor essentially does not move.

> **Tip:** 70–85% smoothing works well for most autopilot scenarios. Lower it if the motor is too slow to follow; raise it if the column is vibrating.

---

### How Often Can New Corrections Be Sent? (Min Motor Command Interval)

| Default | Range | Snap |
|---|---|---|
| 100 ms | 0 – 500 ms | 10 ms steps |

The minimum time between consecutive motor commands during autopilot tracking or position tests. The tracking loop runs every 100 ms internally, but this throttle further limits how often a new servo command is issued.

**Low (0–50 ms):**
- ✅ Very responsive — new targets are acted upon almost immediately.
- ❌ High command rate can flood the RS-485 bus and cause communication errors.
- ❌ Micro-corrections are sent constantly, which can cause the motor to vibrate or chatter.

**High (200–500 ms):**
- ✅ Far fewer commands; bus is quiet and less loaded.
- ✅ Smooths over very brief target changes.
- ❌ The motor may visibly lag behind rapid autopilot corrections.
- ❌ Trim movements feel delayed.

> **Tip:** 100 ms is a good default. Increase to 150–200 ms if you see RS-485 communication errors. Decrease to 50 ms only if you need faster tracking and have a reliable bus.

---

### Acceleration Ramp Time (Param1)

| Default | Range | Snap |
|---|---|---|
| 1000 ms | 50 – 3000 ms | 50 ms steps |

The time for the software RPM ramp to go from 1 RPM to the configured Motor Max Speed. Every new movement resets the ramp timer.

**Short (50–300 ms):**
- ✅ Motor reaches full speed quickly — snappy response.
- ❌ Combined with high RPM, the motor may overshoot frequently.

**Long (1000–3000 ms):**
- ✅ Smoother movement for small changes.
- ✅ Gradual, smooth acceleration.
- ✅ Reduces overshoot at the target position.
- ❌ The motor takes longer to reach its maximum commanded speed.

> **Tip:** 1000–1800 ms is realistic and smooth.

---

### Curve Smoothness (Param2)

| Default | Range | Snap |
|---|---|---|
| 300 ms | 0 – 1500 ms | 50 ms steps |

Controls the shape of the acceleration ramp. At 0 the ramp is purely linear. As Param2 increases toward its effective maximum (Param1 ÷ 2), the ramp transitions to a full smoothstep S-curve (`x² × (3 − 2x)`).

The blend ratio is `Param2 / (Param1 / 2)`, clamped to [0, 1].

> **Important:** Values above `Param1 / 2` are treated as the maximum S-curve. Setting Param2 > 1500 ms has no additional effect beyond what Param1 allows.

**Param2 = 0 (pure linear):**
- ✅ Predictable, constant rate of RPM increase.
- ❌ Slightly abrupt at the very start and end of each move.

**Param2 at mid-range (~Param1/4):**
- ✅ A natural blend — smooth start, linear middle, smooth end.
- ✅ Good all-around feel without overdoing the S-curve.

**Param2 at maximum (= Param1/2):**
- ✅ Fully S-curved ramp — the smoothest possible start and stop.
- ❌ The motor is at very low RPM for longer portions of the ramp; long moves feel slow.
- ❌ Short moves barely accelerate at all before decelerating.

> **Tip:** For a 1000 ms ramp (Param1), set Param2 to 200–400 ms for a subtle S-curve. Setting Param2 = 500 ms gives a full S-curve with that ramp time.

---

## 11. Axis Setup – Hydraulics

Simulates the effect of losing hydraulic pressure on the control loading.

### Test Hydraulics Off

Click **Test Hydraulics Off** to toggle the hydraulics-off state manually for this axis, without needing to disable hydraulics in ProSim. The button turns blue while active. Click **Done** to restore normal operation.

Use this to verify your **Hydraulic Off Torque** setting without flying a failure scenario.

### Hydraulic Off Torque

| Default | Range |
|---|---|
| 80% | 0 – 100% |

The fixed torque applied equally in both directions when hydraulics are unavailable. Normal position-based torque (Min/Max) is **completely replaced** by this value while hydraulics are off.

> **Pitch special behavior:** When hydraulics go off for the **Pitch** axis, the self-centering speed is also set to 0. The column locks in place with this torque value, simulating a jammed hydraulic column. The speed is automatically restored when hydraulics come back.

**Low (0–20%):**
- ✅ Control can still be moved against the resistance.
- ❌ If too low then won't feel like a real hydraulic failure; too easy to move.

**High (60–100%):**
- ✅ Very heavy feel — realistic simulation of manual reversion or hydraulic jam.
- ❌ At 100%, physically overpowering the control may be impossible to move and override.

---

## 12. Axis Setup – Position Movement Test

Allows you to manually drive the axis to any position to verify calibration and movement quality.

### Test / Done Button

Click **Test** to start position test mode. The autopilot tracking loop is suspended so the slider has full control. Click **Done** to stop and return the motor to its default state.

> During test mode, Moving Torque (not position-based torque) is active.

### Target Slider

| Range |
|---|
| −100% (Full Left) … +100% (Full Right) |

Drag the slider to command the motor to any position in the axis travel range. The motor follows the slider in real time using the current Motor Max Speed and acceleration settings.

Use this to:
- Verify calibration by moving to ±100% and checking the physical end stops.
- Check that the **Motor Reversed** setting is correct (positive % should move the control right/back).
- Tune Motor Max Speed and ramp settings by observing the movement.

---

## 13. Axis Setup – Pitch Extras *(Pitch only)*

These settings are only available on the **Pitch** axis setup window.

---

### Airspeed Additional Torque (%)

| Default | Range |
|---|---|
| 10% | 1 – 50% |

An additional torque amount added on top of the normal position-based torque, scaling linearly with airspeed. At 0 IAS the addition is 0%; at 500 IAS the addition equals this full setting value.

The scaled addition is: `(IAS / 500) × AirspeedAdditionalTorquePercent` added to the current position torque.

This torque is **only applied** during normal (non-moving, non-hydraulics-off) operation. It does not apply when the motor is moving for autopilot, trim, or during hydraulics-off.

Updates are throttled to a maximum of once every 50 ms to avoid flooding the bus with rapid speed changes.

**Low (1–10%):**
- ✅ Subtle speed feedback — slightly heavier at cruise speed.
- ❌ Not very noticeable at low or medium airspeeds.

**High (30–50%):**
- ✅ Very pronounced airspeed feel — clearly heavier column at high speed, much lighter at slow speed or on the ground.
- ❌ At very high speeds combined with high Max Torque, the controls may feel extremely heavy.
- ❌ If set too high alongside high Max Torque, the sum may exceed 100% display scale and be clamped.

> **Tip:** 10–20% gives a realistic feel for a 737. ProSim IAS is read from the `SPEED_IAS` dataref and scaled from 0–500 knots.

---

### Stall Additional Torque (%)

| Default | Range |
|---|---|
| 30% | 30 – 120% |

When ProSim signals a stall condition, this percentage is **added** to the current position-based torque as a multiplier for 2 seconds:  
`stallAddition = baseTorque × (StallAdditionalTorquePercent / 100)`

After 2 seconds the stall torque fades and normal torque resumes.

**Low (30–50%):**
- ✅ A noticeable but manageable increase in stick force — warning without severe control penalty.
- ❌ Might not be felt clearly if base torque is already high.

**High (80–120%):**
- ✅ Dramatic stick-force increase during a stall — very effective warning cue.
- ❌ At 120% the controls can nearly double in effort; may be surprising or uncomfortable.
- ❌ If other torque settings are already high, combined values may exceed motor limits and be clamped at 300 (actual).

---

## 14. Per-Axis Feature Summary

This table shows which settings and automatic behaviors are active for each axis.

| Feature | Pitch | Roll | Rudder | Tiller |
|---|:---:|:---:|:---:|:---:|
| Position Calibration | ✅ | ✅ | ✅ | ✅ |
| Min / Max Torque (position-based) | ✅ | ✅ | ✅ | ✅ |
| Self Centering Speed | ✅ | ✅ | ✅ | ✅ |
| Dampening | ✅ | ✅ | ✅ | ✅ |
| Motor Reversed | ✅ | ✅ | ✅ | ✅ |
| Motor Max Speed (RPM) | ✅ | ✅ | ✅ | ✅ |
| Moving Torque | ✅ | ✅ | ✅ | ✅ |
| Motion Smoothing | ✅ | ✅ | ✅ | ✅ |
| Min Motor Command Interval | ✅ | ✅ | ✅ | ✅ |
| Acceleration Ramp (Param1 / Param2) | ✅ | ✅ | ✅ | ✅ |
| Hydraulics Off Torque | ✅ | ✅ | ✅ | ✅ |
| Position Movement Test | ✅ | ✅ | ✅ | ✅ |
| **Autopilot Override %** | ✅ | ✅ | ❌ | ❌ |
| **Autopilot Tracking Loop** | ✅ | ✅ | ❌ | ❌ |
| **Trim Movement** | ❌ | ✅ | ✅ | ❌ |
| **Hydraulics-Off Speed Lock** | ✅ | ❌ | ❌ | ❌ |
| **Airspeed Additional Torque** | ✅ | ❌ | ❌ | ❌ |
| **Stall Additional Torque** | ✅ | ❌ | ❌ | ❌ |

### Pitch

- Full autopilot column movement driven by ProSim `B_PITCH_CMD` and elevator position.
- Column locks (centering speed → 0) when hydraulics go off; unlocks when hydraulics return.
- Torque increases progressively with airspeed (IAS 0–500 kts).
- Extra forward stick force for 2 seconds when ProSim reports a stall.
- Manual override detection: pushing the column against autopilot with enough force disengages AP in ProSim.

### Roll

- Full autopilot wheel movement driven by ProSim `B_ROLL_CMD` and aileron position.
- Trim changes from ProSim (`TrimAileron`) drive the wheel to the trimmed position when autopilot is off. Trim range: ProSim `0..9` maps to `0..75%` wheel travel.
- After **Center Controls** completes for roll, the current trim value is immediately re-applied using the new center as the base.
- Manual override detection works the same as Pitch.

### Rudder

- No autopilot tracking (rudder is not moved by the autopilot in ProSim-AR).
- Trim changes from ProSim (`TrimRudder`) drive the pedals to the trimmed position when autopilot is off. Trim range: ProSim `0..15` maps to `0..100%` rudder travel.
- After **Center Controls** completes for rudder, the current trim value is immediately re-applied.
- No Autopilot Override slider (not needed without AP tracking).

### Tiller

- No autopilot tracking or trim.
- Self-centering and position-based torque operate normally.
- Hydraulics Off Torque applies, but there is no centering-speed lock-out on hydraulics loss.

---

## 15. Saving Settings

Click **Save** at the bottom of the Axis Setup window to persist all settings for that axis to disk. The window also shows a brief blue toast notification confirming the save.

> **Tip:** The **Save** button is always required to make changes permanent, even for settings that have live preview (Auto Pilot / Trim section). Closing the window without saving discards any unsaved changes.

If you change **RS485 IP** or **Driver ID**, save and then close and reopen the window so the new connection is established.

---

### Where Are Settings Stored?

All settings files are saved as **JSON** in the Windows roaming application data folder:

```
C:\Users\<YourUsername>\AppData\Roaming\ACL-SIM-2\
```

| File | Contents |
|---|---|
| `axis-Pitch-settings.json` | All Pitch axis settings |
| `axis-Roll-settings.json` | All Roll axis settings |
| `axis-Rudder-settings.json` | All Rudder axis settings |
| `axis-Tiller-settings.json` | All Tiller axis settings |
| `global-settings.json` | settings (ProSim IP, auto-connect, auto-center) |

> **Quick access:** Press `Win + R`, type `%APPDATA%\ACL-SIM-2` and press Enter to open the folder directly in Windows Explorer.

These are plain JSON text files and can be opened in any text editor (e.g. Notepad). You can back them up, copy them to another machine, or restore a previous configuration by replacing the files before starting the application.
