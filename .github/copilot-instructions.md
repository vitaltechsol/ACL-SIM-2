# Copilot Instructions

## Project Guidelines
- In this project, FullLeftPosition and FullRightPosition in AxisSettings are **relative distances from center** (not absolute encoder values). FullLeftPosition is always negative, FullRightPosition is always positive. CenterPosition is always 0 (the raw encoder value at center is not persisted). During calibration, the encoder display is temporarily offset so center reads 0; FullLeft/FullRight are stored as the relative distance from that zero. The ProSim axis range is 0-1024 with 512 as center. EncoderCenterOffset is initialized to 0 on startup and updated to the actual raw encoder value after motor centering completes (via `AxisManager.CenterToProSimPositionAsync`), so that (rawEncoder - EncoderCenterOffset) == 0 at center. All centering logic lives in `AxisManager`. During calibration, `SetCenter()` sets EncoderCenterOffset to the current raw encoder (keeping position calculations valid); `_calibrationDisplayOffset` separately handles the zero-based display in the setup window.

## Motor Tuning Preferences
- Prefer low-RPM motor tuning with finer-grained UI control, especially in the 2-5 RPM range. Keep RPM tuning as whole numbers only, while preserving the lower 1-15 RPM UI range.
- Ensure setup/save logic avoids re-centering while autopilot is active.
- Pitch encoder-based torque includes an airspeed-based additive percentage from `AxisSettings.AirspeedAdditionalTorquePercent`. Read IAS from ProSim `SPEED_IAS`, scale it from 0..500 IAS to 0..100% of that setting, and apply it only to encoder-position torque. Do not apply it while the motor is moving for autopilot/positioning or while hydraulics-off torque is active. Throttle airspeed-triggered torque refreshes to 50 ms.

## Trim Implementation
- For roll and rudder trim, keep the initial implementation simple: when trim changes and autopilot is off, pass the trim percentage directly to `GoToPosition(...)`. Avoid adding center-recalculation or recentering logic unless explicitly requested.
- For trim scaling in this repo: roll ProSim trim range `0..10` maps to `0..52%`, and the other specified trim axis range `0..15` maps to `0..100%`, while keeping the simple `GoToPosition(...)` trim implementation.
- When an axis is trimming, use an `IsTrimming` flag so torque ignores encoder-position torque and uses `MovingTorquePercentage` instead.
- For trim movement, run motor commands off the event thread, use latest-value-wins overriding behavior, and add a 100 ms smoothing/filter delay before sending movement commands.
