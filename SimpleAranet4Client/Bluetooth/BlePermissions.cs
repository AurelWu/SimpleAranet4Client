using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;

namespace SimpleAranet4Client.Bluetooth
{
    /// <summary>Runtime permission and adapter checks needed before scanning.</summary>
    public static class BlePermissions
    {
        public static bool IsBluetoothOn => CrossBluetoothLE.Current.State == BluetoothState.On;

        /// <summary>Requests the permissions BLE scanning needs. Returns true when scanning is allowed.</summary>
        public static async Task<bool> RequestAsync()
        {
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var scan = await Permissions.RequestAsync<BluetoothScanPermission>();
                var connect = await Permissions.RequestAsync<BluetoothConnectPermission>();
                return scan == PermissionStatus.Granted && connect == PermissionStatus.Granted;
            }

            // Before Android 12 a BLE scan requires location permission.
            return await Permissions.RequestAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted;
#else
            await Task.CompletedTask;
            return true;
#endif
        }

#if ANDROID
        public class BluetoothScanPermission : Permissions.BasePlatformPermission
        {
            public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
                OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? [(Android.Manifest.Permission.BluetoothScan, true)]
                    : [];
        }

        public class BluetoothConnectPermission : Permissions.BasePlatformPermission
        {
            public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
                OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? [(Android.Manifest.Permission.BluetoothConnect, true)]
                    : [];
        }
#endif
    }
}
