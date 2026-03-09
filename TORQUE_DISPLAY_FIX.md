# Torque Display and Control Fix

## Issues Fixed

### 1. Progress Bar Not Showing Correct Torque
**Problem**: The torque progress bars were bound to `TorqueNormalized`, which uses the old `TorqueTarget` value instead of the dynamically calculated `CurrentTorque` from AxisManager.

**Solution**: 
- Added new property `TorqueNormalizedFromCurrent` in AxisViewModel that normalizes the `CurrentTorque` value (0-100 display scale) to 0-1 for the progress bar
- Updated all four axis progress bars in MainWindow.xaml to use `TorqueNormalizedFromCurrent` instead of `TorqueNormalized`

### 2. Inverted Direction Logic in AxisManager
**Problem**: The direction logic for setting torque was inverted:
- When offset from center was positive (moving right), it was calling `SetTorqueRight` when offset <= 0
- When offset from center was negative (moving left), it was calling `SetTorqueLeft` when offset > 0

**Solution**: Fixed the conditional logic in `AxisManager.UpdateTorqueAsync()`:
- Positive offset (`>= 0`) → `SetTorqueRight()` (register 8)
- Negative offset (`< 0`) → `SetTorqueLeft()` (register 9)

### 3. Missing Property Change Notification
**Problem**: When `CurrentTorque` changed, the `TorqueNormalizedFromCurrent` property wasn't being notified, so the UI wouldn't update.

**Solution**: Added `PropertyChanged` notification for `TorqueNormalizedFromCurrent` in the `CurrentTorque` setter.

## Changes Made

### AxisViewModel.cs
1. **Added `TorqueNormalizedFromCurrent` property**:
   ```csharp
   public double TorqueNormalizedFromCurrent
   {
       get
       {
           var maxDisplay = _axis.Settings.MaxTorqueDisplay;
           if (maxDisplay <= 0) return 0;
           return System.Math.Max(0, System.Math.Min(1.0, _currentTorque / maxDisplay));
       }
   }
   ```

2. **Updated `CurrentTorque` setter** to notify `TorqueNormalizedFromCurrent`:
   ```csharp
   set
   {
       if (Math.Abs(_currentTorque - value) < 1e-6) return;
       _currentTorque = value;
       PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTorque)));
       PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorqueNormalizedFromCurrent)));
   }
   ```

### AxisManager.cs
Fixed the direction logic in `UpdateTorqueAsync()`:
```csharp
// Send to appropriate register based on direction
if (offsetFromCenter >= 0)
{
    // Positive offset (right direction): use right register (register 8)
    _torqueControl.SetTorqueRight(torqueInt);
}
else
{
    // Negative offset (left direction): use left register (register 9)
    _torqueControl.SetTorqueLeft(torqueInt);
}
```

### MainWindow.xaml
Updated all four axis torque progress bars:
- **Pitch**: `Value="{Binding Pitch.TorqueNormalizedFromCurrent}"`
- **Roll**: `Value="{Binding Roll.TorqueNormalizedFromCurrent}"`
- **Rudder**: `Value="{Binding Rudder.TorqueNormalizedFromCurrent}"`
- **Tiller**: `Value="{Binding Tiller.TorqueNormalizedFromCurrent}"`

## How It Works Now

1. **Encoder Position Changes** → Triggers `AxisManager.UpdateTorqueAsync()`
2. **Calculate Distance from Center**:
   - Offset = EncoderPosition - CenterPosition
   - Normalized distance = offset / (FullLeftPosition or FullRightPosition depending on direction)
3. **Calculate Torque**:
   - `CurrentTorque` (display scale 0-100) = MinTorque + (normalizedDistance × (MaxTorque - MinTorque))
4. **Update ViewModel**: `_axisVm.CurrentTorque = targetTorqueDisplay`
5. **Send to Motor**:
   - Convert to actual scale (0-300)
   - Send to appropriate register:
     - Positive offset → `SetTorqueRight()` (register 8)
     - Negative offset → `SetTorqueLeft()` (register 9)
6. **Update UI**:
   - Text shows: `CurrentTorque` value (e.g., "45.2")
   - Text shows: `EncoderPercentage` (e.g., "75% from center")
   - Progress bar shows: `TorqueNormalizedFromCurrent` (0-1 normalized value)

## Visual Flow

```
Encoder Position Changes
        ↓
AxisManager.UpdateTorqueAsync()
        ↓
Calculate offset from center
        ↓
Calculate normalized distance (0-1)
        ↓
Calculate torque (min to max based on distance)
        ↓
        ├─→ Update CurrentTorque (ViewModel) → UI displays value & progress bar
        └─→ Send to motor via AxisTorqueControl
               ├─→ Right direction: SetTorqueRight(register 8)
               └─→ Left direction: SetTorqueLeft(register 9)
```

## Testing
To verify the fixes:
1. Run the application
2. Move an axis encoder position away from center
3. **Verify Text Display**: 
   - CurrentTorque value should increase as you move away from center
   - EncoderPercentage should show distance from center
4. **Verify Progress Bar**: Should fill proportionally to the CurrentTorque value
5. **Verify Motor Control**: 
   - Moving right from center should call SetTorqueRight()
   - Moving left from center should call SetTorqueLeft()
   - Check Debug output for confirmation

## Scale References
- **CurrentTorque**: 0-100 (display scale, shown in UI)
- **Motor Torque**: 0-300 (actual scale, sent to registers)
- **TorqueNormalizedFromCurrent**: 0-1 (normalized for progress bar)
- **EncoderPercentage**: 0-100 (percentage distance from center)
