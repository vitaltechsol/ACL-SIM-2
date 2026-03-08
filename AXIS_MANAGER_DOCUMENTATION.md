# AxisManager - Automatic Torque Control

## Overview
The `AxisManager` class manages automatic torque control for each axis based on encoder position. It calculates and applies torque that increases as the axis moves away from center, providing realistic force feedback.

## Features

### 1. **Distance-Based Torque Calculation**
- **At Center (0)**: Minimum torque from settings
- **At Limits (Min/Max)**: Maximum torque from settings  
- **Linear Interpolation**: Torque increases proportionally with distance from center
- **Direction Independent**: Same torque calculation regardless of direction (positive or negative)

### 2. **Automatic & Async**
- Subscribes to encoder position changes
- Calculates torque asynchronously (non-blocking)
- Automatically sends torque commands to motor via `AxisTorqueControl`

### 3. **Shared ModbusClient**
- Uses the shared `ModbusClient` from `EncoderManager`
- No additional connections per axis
- Efficient resource usage

### 4. **Real-Time Display**
- Updates `CurrentTorque` property in `AxisViewModel`
- Main UI displays current torque value for each axis
- Displayed in cyan color for visibility

## Architecture

```
Encoder Position Changes
        ↓
AxisManager.OnAxisPropertyChanged()
        ↓
UpdateTorqueAsync() - Async calculation
        ↓
Calculate distance from center
        ↓
Map to torque range (min → max)
        ↓
Update AxisViewModel.CurrentTorque (UI update)
        ↓
Send to motor via AxisTorqueControl
```

## Torque Calculation Formula

```csharp
// 1. Get distance from center (absolute encoder position)
distanceFromCenter = Math.Abs(encoderPosition - centerPosition)

// 2. Calculate max possible distance (asymmetric support)
maxDistance = Math.Max(Math.Abs(MaxPosition), Math.Abs(MinPosition))

// 3. Normalize distance (0 at center, 1 at limits)
normalizedDistance = distanceFromCenter / maxDistance

// 4. Map to torque range
targetTorque = minTorque + (normalizedDistance * (maxTorque - minTorque))
```

## Example Scenario

### Configuration:
- **CenterPosition**: 5000 (absolute encoder)
- **MinPosition**: -2000 (offset from center)
- **MaxPosition**: 8000 (offset from center)
- **MinTorqueDisplay**: 10 (display units)
- **MaxTorqueDisplay**: 100 (display units)
- **Conversion**: Display → Actual (e.g., 1:3 ratio, so 10 → 30, 100 → 300)

### Encoder at Center (5000):
- Distance from center: 0
- Normalized: 0
- **Torque: 30** (min)

### Encoder at 7000:
- Distance from center: 2000
- Max distance: 8000
- Normalized: 2000 / 8000 = 0.25
- **Torque: 30 + (0.25 × 270) = 97.5**

### Encoder at 13000 (at max limit):
- Distance from center: 8000
- Max distance: 8000
- Normalized: 8000 / 8000 = 1.0
- **Torque: 300** (max)

### Encoder at 3000 (2000 below center):
- Distance from center: 2000
- Max distance: 8000
- Normalized: 2000 / 8000 = 0.25
- **Torque: 97.5** (same as 2000 above center)

## UI Display

Each axis panel in MainWindow now shows:

```
Current Torque: 97.5
```

- **Font**: Size 11, SemiBold
- **Color**: Cyan (#00D9FF) for visibility
- **Format**: One decimal place (F1)
- **Updates**: Real-time as encoder position changes

## Integration Points

### 1. MainViewModel
- Creates `AxisManager` instances during startup
- Passes shared `ModbusClient` from `EncoderManager`
- Recreates managers when settings change
- Disposes managers when axis disabled

### 2. AxisViewModel
- New `CurrentTorque` property for display
- Raises `PropertyChanged` when torque updates
- Bound to UI via data binding

### 3. EncoderManager
- Provides `GetModbusClient(string name)` method
- Shares single client between encoder and torque services

### 4. AxisTorqueControl
- Simplified to only accept shared client
- No longer creates its own connections
- Used by AxisManager to send torque commands

## Future Enhancements (TODO)

### Position Control
The `SetTargetPosition(double position)` method is a placeholder for future position control:

```csharp
public void SetTargetPosition(double position)
{
    // TODO: Implement position control logic
    // Will handle:
    // - Target position commands
    // - Position feedback loops
    // - Velocity ramping
    // - Position hold logic
}
```

## Error Handling

- **Silent Failures**: Calculation errors are caught and ignored
- **Motor Write Failures**: Caught but don't stop calculation
- **Null Safety**: Checks for disposed state and null references
- **Async Safety**: Uses Task.Run to avoid blocking UI

## Performance

- **Async Updates**: Non-blocking calculation and motor writes
- **Minimal Overhead**: Only calculates when encoder position changes
- **Efficient**: Reuses shared ModbusClient connection
- **Thread-Safe**: Proper async/await pattern

## Lifecycle Management

### Creation
```csharp
var modbusClient = encoderManager.GetModbusClient("Pitch");
var manager = new AxisManager("Pitch", pitchViewModel, modbusClient);
```

### Disposal
```csharp
manager.Dispose(); // Unsubscribes events, disposes torque control
```

Disposal is automatic when:
- Axis is disabled
- Settings are changed
- Application exits
