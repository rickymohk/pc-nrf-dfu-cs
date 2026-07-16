using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Nordic.nRF.DFU.BLE.Console
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // The library reports step-by-step progress via System.Diagnostics.Debug.WriteLine,
            // which is invisible in a plain console run (it only reaches OutputDebugString /
            // an attached debugger). Route it to the console too so we can see where things
            // are actually spending time instead of staring at silence.
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
            System.Diagnostics.Trace.AutoFlush = true;

            if (args.Length < 2)
            {
                System.Console.WriteLine(@"
Usage:
Nordic.nRF.DFU.BLE.Console.exe <ble device name> <zip filename>
");
                System.Console.WriteLine("Hit 'Enter' to exit...");
                System.Console.ReadLine();
                return;
            }

            if (!System.IO.File.Exists(args[1]))
            {
                System.Console.WriteLine($"Unable to locate specified DFU update file: {args[1]}");
                System.Console.WriteLine("Hit 'Enter' to exit...");
                System.Console.ReadLine();
                return;
            }

            try
            {
                DoUpdate(args[0], args[1]).Wait();
            }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.Flatten().InnerExceptions)
                {
                    PrintError(inner);
                }
            }
            catch (Exception ex)
            {
                PrintError(ex);
            }

            System.Console.WriteLine("Hit 'Enter' to exit...");
            System.Console.ReadLine();
        }

        private static void PrintError(Exception ex)
        {
            var code = (ex as DfuException)?.Code;
            System.Console.WriteLine($"Oops, something went horribly wrong... {(code != null ? $"[{code}] " : "")}{ex.Message}");
            System.Console.WriteLine(ex.StackTrace);

            if (ex.InnerException != null)
            {
                System.Console.WriteLine("--- Caused by ---");
                PrintError(ex.InnerException);
            }
        }

        private static async Task DoUpdate(string deviceName, string updateFile)
        {
            System.Console.WriteLine($"Scanning for BLE device '{deviceName}'...");
            var device = await FindDeviceByNameAsync(deviceName, TimeSpan.FromSeconds(15));
            if (device == null)
            {
                System.Console.WriteLine($"Could not find a BLE device advertising as '{deviceName}' within the scan timeout.");
                return;
            }

            System.Console.WriteLine($"Found and connected to {device.Name} ({device.BluetoothAddress:X12}).");

            var transport = new DfuTransportBle(device);
            transport.Progress += (s, e) =>
                System.Console.WriteLine($"Progress: {e.Sent}/{e.Total} bytes");

            System.Console.WriteLine($"Reading firmware update file at {updateFile}...");
            var updates = await DfuUpdates.FromZipFile(updateFile);

            System.Console.WriteLine("Starting DFU operation...");
            var operation = new DfuOperation(updates, transport, false);
            var startTask = operation.Start();

            var watchdog = Task.Delay(TimeSpan.FromMinutes(2));
            var completed = await Task.WhenAny(startTask, watchdog);
            if (completed == watchdog)
            {
                System.Console.WriteLine("Still running after 2 minutes with no progress - this points at a real hang rather than a slow retry. Continuing to wait; press Ctrl+C to abort.");
            }

            await startTask;

            System.Console.WriteLine("DFU operation complete...");
        }

        // Scans BLE advertisements for a device whose advertised local name
        // matches deviceName, then connects to it. Returns null if none is
        // found within the timeout.
        private static Task<BluetoothLEDevice> FindDeviceByNameAsync(string deviceName, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<BluetoothLEDevice>();
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            async void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.Advertisement.LocalName) || e.Advertisement.LocalName != deviceName)
                {
                    return;
                }

                sender.Stop();

                try
                {
                    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(e.BluetoothAddress);
                    tcs.TrySetResult(device);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            watcher.Received += OnAdvertisementReceived;
            watcher.Start();

            _ = Task.Delay(timeout).ContinueWith(_ =>
            {
                watcher.Stop();
                watcher.Received -= OnAdvertisementReceived;
                tcs.TrySetResult(null);
            });

            return tcs.Task;
        }
    }
}
