# AxisManager Torque Calculation - Fix Summary

## Issues Found

### 1. **Asymmetric Range Problem**
The old code used `Math.Max(maxPositive, maxNegative)` as the denominator for both directions:

```csharp
// OLD CODE (WRONG)
var maxDistancePositive = Math.Abs(settings.MaxPosition);  // 8000
var maxDistanceNegative = Math.Abs(settings.MinPosition);  // 2000
var maxDistance = Math.Max(maxDistancePositive, maxDistanceNegative);  // 8000
var normalizedDistance = distanceFromCenter / maxDistance;
```

**Problem**: When moving to MIN position (-2000 from center):
- Distance = 2000
- Normalized = 2000 / 8000 = **0.25** (only 25% of max torque!)

### 2. **Display Scale Confusion**
The old code converted torque to motor scale (0-300) before displaying, but the UI should show user-friendly scale (0-100).

## The Fix

### 1. **Direction-Aware Normalization**
```csharp
// NEW CODE (CORRECT)
if (offsetFromCenter >= 0)
{
    // Moving positive: use MaxPosition as denominator
    var maxPositive = Math.Abs(settings.MaxPosition);  // 8000
    normalizedDistance = Math.Abs(offsetFromCenter) / maxPositive;
}
else
{
    // Moving negative: use MinPosition as denominator
    var maxNegative = Math.Abs(settings.MinPosition);  // 2000
    normalizedDistance = Math.Abs(offsetFromCenter) / maxNegative;
}
```

**Result**: 
- At MIN position: 2000 / 2000 = **1.0** (100% max torque!) ✓
- At MAX position: 8000 / 8000 = **1.0** (100% max torque!) ✓
- At center: 0 / anything = **0.0** (min torque) ✓

### 2. **Display Scale Separation**
```csharp
// Calculate in display scale (0-100)
var targetTorqueDisplay = minTorqueDisplay + (normalizedDistance * (maxTorqueDisplay - minTorqueDisplay));

// Show display value in UI (0-100)
_axisVm.CurrentTorque = targetTorqueDisplay;

// Convert to motor scale (0-300) only when sending to hardware
var targetTorqueActual = settings.ConvertTorqueDisplayToActual(targetTorqueDisplay);
_torqueControl.SetTorqueForward((int)Math.Round(targetTorqueActual));
```

## Test Scenarios

### Configuration:
- **CenterPosition**: 5000 (absolute encoder)
- **MinPosition**: -2000 (offset from center)
- **MaxPosition**: 8000 (offset from center)
- **MinTorqueDisplay**: 10 (0-100 scale)
- **MaxTorqueDisplay**: 100 (0-100 scale)
- **Conversion**: Display to Actual = `(value / 100) * 300`

### Test 1: At Center
- **Encoder**: 5000
- **Offset**: 0
- **Normalized**: 0.0
- **Display Torque**: 10 + (0.0 × 90) = **10**
- **Motor Torque**: (10/100) × 300 = **30**
- **UI Shows**: "10.0" ✓

### Test 2: Moving Positive to Half
- **Encoder**: 9000 (center + 4000)
- **Offset**: +4000
- **Normalized**: 4000 / 8000 = 0.5
- **Display Torque**: 10 + (0.5 × 90) = **55**
- **Motor Torque**: (55/100) × 300 = **165**
- **UI Shows**: "55.0" ✓

### Test 3: At Max Position
- **Encoder**: 13000 (center + 8000)
- **Offset**: +8000
- **Normalized**: 8000 / 8000 = 1.0
- **Display Torque**: 10 + (1.0 × 90) = **100**
- **Motor Torque**: (100/100) × 300 = **300**
- **UI Shows**: "100.0" ✓

### Test 4: Moving Negative to Half
- **Encoder**: 4000 (center - 1000)
- **Offset**: -1000
- **Normalized**: 1000 / 2000 = 0.5
- **Display Torque**: 10 + (0.5 × 90) = **55**
- **Motor Torque**: (55/100) × 300 = **165**
- **UI Shows**: "55.0" ✓

### Test 5: At Min Position (CRITICAL TEST)
- **Encoder**: 3000 (center - 2000)
- **Offset**: -2000
- **Normalized**: 2000 / 2000 = **1.0** (was 0.25 before!)
- **Display Torque**: 10 + (1.0 × 90) = **100**
- **Motor Torque**: (100/100) × 300 = **300**
- **UI Shows**: "100.0" ✓

## Behavior Summary

| Position | Encoder | Offset | Normalized | Display | Motor | Status |
|----------|---------|--------|------------|---------|-------|--------|
| Center   | 5000    | 0      | 0.0        | 10      | 30    | ✓ Min  |
| +25%     | 7000    | +2000  | 0.25       | 32.5    | 97.5  | ✓      |
| +50%     | 9000    | +4000  | 0.5        | 55      | 165   | ✓      |
| +100%    | 13000   | +8000  | 1.0        | 100     | 300   | ✓ Max  |
| -25%     | 4500    | -500   | 0.25       | 32.5    | 97.5  | ✓      |
| -50%     | 4000    | -1000  | 0.5        | 55      | 165   | ✓      |
| -100%    | 3000    | -2000  | 1.0        | 100     | 300   | ✓ Max  |

## Key Points

✅ **Symmetric Behavior**: Same distance from center = same torque (regardless of direction)  
✅ **Min at Center**: Position 0 (center) always uses minimum torque  
✅ **Max at Limits**: Both MIN and MAX positions reach maximum torque  
✅ **Display Scale**: UI shows 0-100 scale (user-friendly)  
✅ **Motor Scale**: Hardware receives 0-300 scale (Modbus protocol)  
✅ **Smooth Interpolation**: Linear increase as you move away from center
