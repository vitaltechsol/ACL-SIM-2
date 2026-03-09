# Moving Torque Implementation

## Summary
Added support for using the `MovingTorqueDisplay` setting to control the torque during position movements via the AxisSetup UI. The implementation uses the existing `AxisTorqueControl` service to set torque registers.

## Changes Made

### 1. AxisMovement.cs
Updated to use `AxisTorqueControl` for setting moving torque:

- **Added `_torqueControl` field**: Reference to AxisTorqueControl instance for setting torque
- **Updated constructor**: Now accepts an optional `AxisTorqueControl` parameter
- **Added `SetMovingTorqueLimit()` method**: 
  - Converts MovingTorqueDisplay (0-100 display scale) to actual motor torque (0-300 scale)
  - Uses `AxisTorqueControl.SetTorqueBoth()` to set torque for both directions (registers 8 and 9)
- **Updated `InitServo()`**: Calls `SetMovingTorqueLimit()` during initialization
- **Updated `GoToPosition()`**: Updates torque before each movement if servo is already initialized

### 2. AxisManager.cs
Updated constructor order to provide AxisTorqueControl to AxisMovement:

- **Reordered initialization**: Creates `_torqueControl` before `_axisMovement`
- **Updated AxisMovement instantiation**: Passes `_torqueControl` to AxisMovement constructor

### 3. Existing UI Components (Already Present)
The UI and ViewModel components were already implemented:

- **AxisSetupWindow.xaml**: Slider for "Movement torque" (lines 92-93)
- **AxisSetupViewModel.cs**: 
  - `MovingTorqueDisplay` property with data binding (lines 73-82)
  - Settings load/save functionality (lines 235, 251)
- **Axis.cs**: `MovingTorqueDisplay` property in AxisSettings (default value: 10, range: 0-100)

## How It Works

1. **User Configuration**: User adjusts the "Movement torque" slider in the AxisSetup window (0-100 range)
2. **Save Settings**: When "Save" is clicked, the value is persisted via SettingsService
3. **Conversion**: The display value (0-100) is converted to actual motor torque (0-300) using `ConvertTorqueDisplayToActual()`
4. **Motor Control**: The torque is set via `AxisTorqueControl.SetTorqueBoth()`:
   - Writes to register 8 (right/forward torque)
   - Writes to register 9 (left/backward torque, negated)
   - Applied during servo initialization (first position movement)
   - Updated before each subsequent position movement (in case settings changed)
5. **Position Movement**: When `GoToPosition()` is called, the torque is set first, then the motor moves to the target position using the configured torque limit

## Architecture

```
AxisManager
  ├─ Creates AxisTorqueControl (for torque control via registers 8 & 9)
  └─ Creates AxisMovement (for position control)
      └─ Uses AxisTorqueControl to set moving torque before movements
```

## Scale Mapping
- **Display Scale**: 0-100 (shown in UI)
- **Actual Motor Scale**: 0-300 (written to servo motor)
- **Conversion**: Uses `AxisSettings.ConvertTorqueDisplayToActual()` method
  - Formula: `(display / 100) * 300`
  - Example: Display value of 10 → Motor torque of 30
  - Example: Display value of 50 → Motor torque of 150
  - Example: Display value of 100 → Motor torque of 300

## Torque Control Registers
- **Register 8**: Forward/Right direction torque
- **Register 9**: Backward/Left direction torque (written as negative value)
- Both registers are set to the same absolute value for consistent torque in both directions

## Testing
To test the implementation:
1. Open AxisSetup window for an axis
2. Adjust the "Movement torque" slider (in "Self Centering / Movement" section)
3. Click "Save" to persist the setting
4. Enable "Position Movement Test" mode
5. Move the target slider - the motor should move with the configured torque limit
6. Check Debug output for confirmation: `"[AxisName] Moving torque limit set to X (from display value Y)"`
