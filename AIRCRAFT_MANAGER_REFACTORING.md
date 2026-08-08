# Refactoring ProSimManager to IAircraftManager with Dependency Injection

## Summary

Successfully refactored ProSimManager to implement a generic `IAircraftManager` interface with dependency injection, enabling easy swapping between different aircraft SDKs (ProSim, PMDG 737, etc.) in the future.

## Changes Made

### 1. Created IAircraftManager Interface (`Services/IAircraftManager.cs`)
- Extracted all public members from ProSimManager into a generic interface
- Moved `ConnectionState` enum to the interface file for shared access
- Moved `ConnectionStateEventArgs` and `DataRefValueChangedEventArgs` to the interface file
- Interface includes:
  - Connection management (ConnectAsync, Disconnect, State, StatusMessage)
  - Flight control positions (AileronLeft, AileronRight, Elevator, etc.)
  - Trim positions (TrimElevator, TrimAileron, TrimRudder)
  - Flight state (IsStalling, SpeedIas, SpeedGround)
  - Autopilot state (PitchCmd, RollCmd)
  - Aircraft systems (HydraulicsAvailable, McpApDisengage, Pause)
  - All change event handlers
  - Control methods (DisengageAP, PauseSim, UnpauseSim, GetProSimRefVal)

### 2. Updated ProSimManager (`Services/ProSimManager.cs`)
- Now implements `IAircraftManager` instead of just `IDisposable`
- Removed duplicate `ConnectionState` enum and event args classes (now in IAircraftManager.cs)
- ProSimManager-specific constants remain in the class (AILERON_LEFT, ELEVATOR, etc.)

### 3. Updated Consumers
- **AxisManager** (`Services/AxisManager.cs`): Constructor now accepts `IAircraftManager` instead of `ProSimManager`
- **AxisSetupViewModel** (`ViewModels/AxisSetupViewModel.cs`): Constructor now accepts `IAircraftManager`
- **MainViewModel** (`ViewModels/MainViewModel.cs`):
  - Constructor now accepts `IAircraftManager` and `IAppLogger` via dependency injection
  - Removed direct instantiation of ProSimManager and AppLogger
  - All references to `ProSimManager.ConnectionState` changed to `ConnectionState` (from Services namespace)

### 4. Configured Dependency Injection (`App.xaml.cs`)
- Added Microsoft.Extensions.DependencyInjection NuGet package (v9.0.0)
- Created ServiceProvider in App constructor
- Registered services as singletons:
  - `IAppLogger` → `AppLogger`
  - `IAircraftManager` → `ProSimManager`
- Registered `MainViewModel` as transient
- MainWindow DataContext now set via DI in OnStartup override
- Added OnExit override to properly dispose ServiceProvider

### 5. Updated AppLogger (`Services/AppLogger.cs`)
- Refactored to avoid circular dependency with MainViewModel
- Constructor no longer requires Action<string>
- Added `SetLogAction()` method to register callback after construction
- MainViewModel calls SetLogAction after receiving logger from DI

### 6. Updated MainWindow (`MainWindow.xaml.cs`)
- Removed direct MainViewModel instantiation
- DataContext now set by DI container in App.xaml.cs

## Future Aircraft SDK Support

To add support for another aircraft SDK (e.g., PMDG 737):

1. **Create new implementation** (e.g., `PmdgManager.cs`):
   ```csharp
   public class PmdgManager : IAircraftManager
   {
	   // Implement all IAircraftManager members using PMDG SDK
   }
   ```

2. **Update DI registration** in `App.xaml.cs`:
   ```csharp
   // Switch between implementations by changing this line:
   services.AddSingleton<IAircraftManager, PmdgManager>();  // or ProSimManager
   ```

3. **Optional: Add configuration** to select aircraft dynamically:
   ```csharp
   var settings = LoadGlobalSettings();
   if (settings.AircraftType == "PMDG")
	   services.AddSingleton<IAircraftManager, PmdgManager>();
   else
	   services.AddSingleton<IAircraftManager, ProSimManager>();
   ```

## Benefits

✅ **Abstraction**: Code depends on interface, not concrete implementation  
✅ **Easy Swapping**: Change aircraft SDK by updating DI registration  
✅ **Testability**: Can inject mock implementations for testing  
✅ **Single Responsibility**: ProSimManager only handles ProSim-specific logic  
✅ **Future-Proof**: Adding PMDG or other aircraft requires no changes to consuming code  

## Testing

**Before building**, ensure the application is not running (close ACL-SIM-2.exe).

The XAML bindings to `ProSimConnectionState` will continue to work as the enum values (Connected, Connecting, Disconnected, Failed) remain unchanged.

## Notes

- ProSim-specific constants (DataRef names like AILERON_LEFT, ELEVATOR, etc.) remain in ProSimManager class since they're specific to ProSim
- Other aircraft SDKs will have their own equivalent constants
- All axis managers and view models now work with the generic interface
- The interface prioritizes the **contract** all aircraft SDKs must fulfill, regardless of underlying implementation
