/**
 * Copyright 2026 - BCycle, LLC
 *
 * WinRT Bluetooth LE DFU transport for .NET Framework 4.8.
 * Mirrors the design and behavior of DfuTransportNoble.js (BLE transport for
 * Node.js "noble", from the noble-nrf-dfu package) as closely as WinRT's
 * Windows.Devices.Bluetooth GATT client APIs allow, in place of noble.
 */

#if NET48

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Nordic.nRF.DFU
{
    /**
     * BLE DFU transport backed by the WinRT Windows.Devices.Bluetooth APIs.
     *
     * This must be given an already-connected/paired BluetoothLEDevice when
     * instantiating; device discovery and pairing are the caller's
     * responsibility. This class performs GATT service/characteristic
     * discovery for the Secure DFU service and subscribes to notifications
     * on the control point characteristic.
     */
    public class DfuTransportBle : DfuTransportPrn, IDisposable
    {
        // Secure DFU service and characteristic UUIDs, per Nordic's SDK.
        private static readonly Guid DfuServiceUuid = Guid.Parse("0000FE59-0000-1000-8000-00805F9B34FB");
        private static readonly Guid DfuControlPointUuid = Guid.Parse("8EC90001-F315-4F60-9FB8-838830DAEA50");
        private static readonly Guid DfuPacketUuid = Guid.Parse("8EC90002-F315-4F60-9FB8-838830DAEA50");
        private static readonly Guid ButtonlessDfuWithoutBondUuid = Guid.Parse("8EC90003-F315-4F60-9FB8-838830DAEA50");
        private static readonly Guid ButtonlessDfuWithBondUuid = Guid.Parse("8EC90004-F315-4F60-9FB8-838830DAEA50");

        private static readonly Random _random = new Random();

        private readonly string _dfuAdvName;
        private readonly int _operationTimeoutMillis;
        private readonly object _readyLock = new object();

        private BluetoothLEDevice _device;
        private GattSession _gattSession;
        private GattDeviceService _dfuService;
        private GattCharacteristic _controlPointCharacteristic;
        private GattCharacteristic _packetCharacteristic;
        private GattCharacteristic _buttonlessCharacteristic;
        private Task _readyTask;
        private TaskCompletionSource<bool> _disconnectedSignal;

        // The currently attached device. After StartButtonless() jumps the
        // target into bootloader mode, this reflects the newly discovered
        // peripheral (which may have a different BluetoothAddress).
        public BluetoothLEDevice Device => _device;

        // dfuAdvName: if given, used as the device's new advertised name while
        // jumping into bootloader mode via StartButtonless(); otherwise a
        // random name is generated, mirroring DfuTransportNoble's dfuAdvName
        // constructor parameter.
        // operationTimeoutMillis: timeout used for GATT discovery, buttonless
        // DFU requests, waiting for disconnect, and scanning for the
        // re-advertising peripheral - mirrors DfuTransportNoble's
        // operationTimeoutMillis constructor parameter.
        public DfuTransportBle(BluetoothLEDevice device, string dfuAdvName = null, int packetReceiveNotification = 16, int operationTimeoutMillis = 5000)
            : base(packetReceiveNotification)
        {
            _dfuAdvName = dfuAdvName;
            _operationTimeoutMillis = operationTimeoutMillis;

            // Conservative default: the minimum BLE ATT MTU (23 bytes) minus the
            // 3-byte ATT write-command header. Windows does not expose an API to
            // negotiate/read the *effective* ATT MTU as of this writing, so a
            // caller expecting a larger MTU should override this after Ready().
            Mtu = 20;

            AttachDevice(device);
        }

        // Swaps in a (possibly new) BluetoothLEDevice, resetting all discovered
        // GATT state so the next Ready()/StartButtonless() call re-discovers it.
        private void AttachDevice(BluetoothLEDevice device)
        {
            if (_device != null)
            {
                _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            }

            if (_gattSession != null)
            {
                _gattSession.MaxPduSizeChanged -= GattSession_MaxPduSizeChanged;
                _gattSession.Dispose();
                _gattSession = null;
            }

            _device = device ?? throw new ArgumentNullException(nameof(device));
            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            _disconnectedSignal = new TaskCompletionSource<bool>();

            lock (_readyLock)
            {
                _readyTask = null;
            }

            _dfuService = null;
            _controlPointCharacteristic = null;
            _packetCharacteristic = null;
            _buttonlessCharacteristic = null;
        }

        // Opens the GATT connection to the DFU service, sets PRN. Idempotent:
        // repeated calls while already ready/getting ready return the same Task.
        public override Task Ready()
        {
            lock (_readyLock)
            {
                if (_readyTask == null)
                {
                    _readyTask = InitializeAsync();
                }

                return _readyTask;
            }
        }

        // Does NOT call Ready() itself: per DfuTransportPrn's contract, Ready()
        // is called by the higher-level protocol methods (CreateObject,
        // SelectObject, etc.) before invoking WriteCommand/WriteData - and
        // InitializeAsync() (invoked BY Ready()) calls this directly to send
        // the initial Set PRN command. Calling Ready() here too would await
        // the very _readyTask this method is running inside of, deadlocking.
        public override async Task WriteCommand(byte[] bytes)
        {
            var status = await WriteWithDisconnectGuardAsync(_controlPointCharacteristic, bytes, GattWriteOption.WriteWithResponse);

            if (status != GattCommunicationStatus.Success)
            {
                throw new DfuException(ErrorCode.ERROR_GATT_WRITE_FAILED, $"BLE control point write failed: {status}");
            }
        }

        // Given some payload bytes, writes them to the packet characteristic.
        // The length of the bytes is guaranteed to be under this.Mtu thanks
        // to the DfuTransportPrn functionality. See WriteCommand for why this
        // does not call Ready() itself.
        public override async Task WriteData(byte[] bytes)
        {
            var status = await WriteWithDisconnectGuardAsync(_packetCharacteristic, bytes, GattWriteOption.WriteWithoutResponse);

            if (status != GattCommunicationStatus.Success)
            {
                throw new DfuException(ErrorCode.ERROR_GATT_WRITE_FAILED, $"BLE packet write failed: {status}");
            }
        }

        // Races a characteristic write against the device's disconnect signal
        // AND a plain timeout, mirroring DfuTransportNoble's
        // ongoingWriteCommandRejector / ongoingWriteDataRejector (which reject
        // an in-flight write immediately on disconnect) plus a safety net for
        // the case where the link is connected-but-unresponsive rather than
        // cleanly disconnected - a WinRT GATT write can otherwise hang
        // indefinitely with neither a result nor a disconnect event.
        private async Task<GattCommunicationStatus> WriteWithDisconnectGuardAsync(GattCharacteristic characteristic, byte[] bytes, GattWriteOption option)
        {
            var disconnectedSignal = _disconnectedSignal;
            var writeTask = characteristic.WriteValueAsync(bytes.AsBuffer(), option).AsTask();
            var timeoutTask = Task.Delay(_operationTimeoutMillis);

            var completed = await Task.WhenAny(writeTask, disconnectedSignal.Task, timeoutTask);
            if (completed == disconnectedSignal.Task)
            {
                throw new DfuException(ErrorCode.ERROR_DISCONNECT_WHILE_WRITING);
            }

            if (completed == timeoutTask)
            {
                throw new DfuException(ErrorCode.ERROR_GATT_WRITE_FAILED, $"GATT write to {characteristic.Uuid} timed out after {_operationTimeoutMillis}ms.");
            }

            return await writeTask;
        }

        // Races a WinRT async call against a plain timeout, so a
        // connected-but-unresponsive link can't hang a call forever with no
        // result and no disconnect event to key off of.
        private async Task<T> WithTimeoutAsync<T>(Task<T> task, ErrorCode timeoutErrorCode, string message)
        {
            var completed = await Task.WhenAny(task, Task.Delay(_operationTimeoutMillis));
            if (completed != task)
            {
                throw new DfuException(timeoutErrorCode, message);
            }

            return await task;
        }

        private Task<GattCommunicationStatus> WriteCccdWithTimeoutAsync(GattCharacteristic characteristic)
        {
            return WithTimeoutAsync(
                characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GetCccdValue(characteristic)).AsTask(),
                ErrorCode.ERROR_CAN_NOT_SUBSCRIBE_CHANGES,
                $"Timed out subscribing to {characteristic.Uuid} notifications after {_operationTimeoutMillis}ms.");
        }

        private async Task InitializeAsync()
        {
            await DiscoverCharacteristicsAsync();

            if (_controlPointCharacteristic == null || _packetCharacteristic == null)
            {
                throw new DfuException(ErrorCode.ERROR_CAN_NOT_DISCOVER_DFU_CONTROL,
                    $"Found the DFU service but not the expected characteristics (control point found: {_controlPointCharacteristic != null}, packet found: {_packetCharacteristic != null}).");
            }

            await SetupMtuTrackingAsync();

            System.Diagnostics.Debug.WriteLine("Subscribing to control point notifications...");
            var notifyStatus = await WriteCccdWithTimeoutAsync(_controlPointCharacteristic);

            if (notifyStatus != GattCommunicationStatus.Success)
            {
                throw new DfuException(ErrorCode.ERROR_CAN_NOT_SUBSCRIBE_CHANGES, $"CCCD write status: {notifyStatus}");
            }

            _controlPointCharacteristic.ValueChanged += ControlPointCharacteristic_ValueChanged;

            System.Diagnostics.Debug.WriteLine("Subscribed. Sending Set PRN command...");
            await WriteCommand(new byte[]
            {
                0x02, // "Set PRN" opcode
                (byte)(_prn & 0xFF), // PRN LSB
                (byte)((_prn >> 8) & 0xFF), // PRN MSB
            });

            System.Diagnostics.Debug.WriteLine("Set PRN command sent, waiting for response...");
            var readResult = await Read();
            AssertPacket(0x02, 0)(readResult);
            System.Diagnostics.Debug.WriteLine("Ready.");
        }

        // Tracks the connection's actual negotiated ATT MTU via GattSession,
        // to use a larger Mtu than the conservative 20-byte default when the
        // link supports it (Windows negotiates this automatically; there is
        // no API to request a specific size, only to observe the outcome).
        // This is a speed optimization, not required for correctness, so any
        // failure here (including running on a Windows version older than
        // the 1803 GattSession.MaxPduSize requires) just falls back to the
        // default MTU rather than failing the DFU procedure.
        private async Task SetupMtuTrackingAsync()
        {
            try
            {
                var bluetoothDeviceId = BluetoothDeviceId.FromId(_device.DeviceId);
                var sessionTask = GattSession.FromDeviceIdAsync(bluetoothDeviceId).AsTask();
                var completed = await Task.WhenAny(sessionTask, Task.Delay(_operationTimeoutMillis));
                if (completed != sessionTask)
                {
                    System.Diagnostics.Debug.WriteLine("Timed out setting up GattSession for MTU tracking; using default MTU.");
                    return;
                }

                _gattSession = await sessionTask;
                _gattSession.MaintainConnection = true;
                _gattSession.MaxPduSizeChanged += GattSession_MaxPduSizeChanged;
                UpdateMtuFromPduSize(_gattSession.MaxPduSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not set up GattSession for MTU tracking; using default MTU. {ex}");
            }
        }

        private void GattSession_MaxPduSizeChanged(GattSession sender, object args)
        {
            UpdateMtuFromPduSize(sender.MaxPduSize);
        }

        // Usable ATT payload is the negotiated PDU size minus the 3-byte ATT
        // opcode+handle header, rounded down to a multiple of 4 - matching
        // DfuTransportSerial's MTU rounding, which guards against unaligned
        // flash writes on the target (an nRF-wide constraint, not specific
        // to the serial transport).
        private void UpdateMtuFromPduSize(int maxPduSize)
        {
            var usable = maxPduSize - 3;
            usable -= usable % 4;
            if (usable < 20)
            {
                usable = 20;
            }

            System.Diagnostics.Debug.WriteLine($"BLE MTU update: PDU size {maxPduSize}, usable payload {usable} bytes.");
            Mtu = usable;
        }

        private async Task DiscoverCharacteristicsAsync()
        {
            async Task DiscoverAsync()
            {
                // Right after a (re)connection, the very first GATT operation
                // commonly comes back with a non-success status (typically
                // Unreachable) while the link is still stabilizing, rather than
                // throwing - so retries must cover both a thrown exception and
                // an unsuccessful result, not just exceptions.
                async Task<GattDeviceServicesResult> GetServicesWithRetryAsync()
                {
                    GattDeviceServicesResult result = null;
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            result = await _device.GetGattServicesForUuidAsync(DfuServiceUuid, BluetoothCacheMode.Uncached).AsTask();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error discovering service (attempt {attempt}/3): {ex}");
                            result = null;
                        }

                        if (result != null && result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
                        {
                            return result;
                        }

                        System.Diagnostics.Debug.WriteLine($"Service discovery attempt {attempt}/3 came back as {(result == null ? "an exception" : $"{result.Status}, {result.Services.Count} service(s)")}; retrying.");
                        await Task.Delay(500);
                    }

                    throw new DfuException(ErrorCode.ERROR_CAN_NOT_DISCOVER_DFU_CONTROL,
                        $"Could not find the DFU service ({DfuServiceUuid}) after 3 attempts. Last status: {(result == null ? "exception" : result.Status.ToString())}.");
                }

                async Task<GattCharacteristicsResult> GetCharacteristicsWithRetryAsync(GattDeviceService service)
                {
                    GattCharacteristicsResult result = null;
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error discovering characteristics (attempt {attempt}/3): {ex}");
                            result = null;
                        }

                        if (result != null && result.Status == GattCommunicationStatus.Success)
                        {
                            return result;
                        }

                        System.Diagnostics.Debug.WriteLine($"Characteristic discovery attempt {attempt}/3 came back as {(result == null ? "an exception" : result.Status.ToString())}; retrying.");
                        await Task.Delay(500);
                    }

                    throw new DfuException(ErrorCode.ERROR_CAN_NOT_DISCOVER_DFU_CONTROL,
                        $"Could not read characteristics of the DFU service after 3 attempts. Last status: {(result == null ? "exception" : result.Status.ToString())}.");
                }

                var serviceResult = await GetServicesWithRetryAsync();
                _dfuService = serviceResult.Services[0];

                var characteristicsResult = await GetCharacteristicsWithRetryAsync(_dfuService);

                foreach (var characteristic in characteristicsResult.Characteristics)
                {
                    if (characteristic.Uuid == DfuControlPointUuid)
                    {
                        _controlPointCharacteristic = characteristic;
                    }
                    else if (characteristic.Uuid == DfuPacketUuid)
                    {
                        _packetCharacteristic = characteristic;
                    }
                    else if (characteristic.Uuid == ButtonlessDfuWithoutBondUuid || characteristic.Uuid == ButtonlessDfuWithBondUuid)
                    {
                        _buttonlessCharacteristic = characteristic;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"Discovered characteristics: control point={_controlPointCharacteristic != null}, packet={_packetCharacteristic != null}, buttonless={_buttonlessCharacteristic != null}");
            }

            var discoverTask = DiscoverAsync();
            var timeoutTask = Task.Delay(_operationTimeoutMillis);
            var completed = await Task.WhenAny(discoverTask, timeoutTask);
            if (completed == timeoutTask)
            {
                throw new DfuException(ErrorCode.ERROR_TIMEOUT_FETCHING_CHARACTERISTICS);
            }

            await discoverTask; // Propagate exceptions, if any
        }

        // Picks the CCCD value matching what the characteristic actually
        // supports. Nordic's DFU control point characteristic uses Notify,
        // but the buttonless DFU characteristic uses Indicate - writing the
        // wrong one leaves the CCCD "improperly configured" from the
        // peripheral's point of view (ATT error 0xFD), which surfaces as a
        // write failure on the very next request. This mirrors what noble's
        // characteristic.subscribe()/.subscribeAsync() does internally by
        // inspecting the characteristic's properties instead of assuming Notify.
        private static GattClientCharacteristicConfigurationDescriptorValue GetCccdValue(GattCharacteristic characteristic)
        {
            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0)
            {
                return GattClientCharacteristicConfigurationDescriptorValue.Notify;
            }

            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Indicate) != 0)
            {
                return GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            }

            throw new DfuException(ErrorCode.ERROR_CAN_NOT_SUBSCRIBE_CHANGES, $"Characteristic {characteristic.Uuid} supports neither notify nor indicate.");
        }

        /**
         * Buttonless DFU flow:
         * 1. Discover characteristics; if there is no buttonless characteristic,
         *    the device is either already in bootloader mode or doesn't support
         *    buttonless DFU - proceed as-is.
         * 2. Subscribe to notifications on the buttonless characteristic.
         * 3. Send a "Set Name" request with dfuAdvName, or a random name if none
         *    was given.
         * 4. Send an "Enter Bootloader" request. The device resets, so a formal
         *    response may never arrive; only the disconnect is awaited. Windows
         *    does not always report the disconnect promptly (or at all), so
         *    after a short grace period the connection is forced closed.
         * 5. Scan for the device re-advertising in bootloader mode, either
         *    under its new name or its BluetoothAddress + 1 (Nordic bootloaders
         *    advertise a different address, for privacy, while in DFU mode).
         * 6. Reconnect and replace the attached device.
         */
        public override async Task StartButtonless()
        {
            await DiscoverCharacteristicsAsync();

            if (_buttonlessCharacteristic == null)
            {
                System.Diagnostics.Debug.WriteLine("No buttonless DFU characteristic found; assuming the device is already in bootloader mode.");
                return;
            }

            // Captured locally rather than read from the field throughout this
            // method: AttachDevice() (called on success, once the device has
            // re-advertised) resets _buttonlessCharacteristic to null as part
            // of clearing out the old device's GATT state, which would
            // otherwise make the final unsubscribe below operate on null.
            var buttonlessCharacteristic = _buttonlessCharacteristic;

            var notifyStatus = await WriteCccdWithTimeoutAsync(buttonlessCharacteristic);

            if (notifyStatus != GattCommunicationStatus.Success)
            {
                throw new DfuException(ErrorCode.ERROR_CAN_NOT_SUBSCRIBE_CHANGES, $"CCCD write status: {notifyStatus}");
            }

            TaskCompletionSource<byte[]> pendingResponse = null;
            void OnButtonlessValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
            {
                var bytes = args.CharacteristicValue.ToArray();
                System.Diagnostics.Debug.WriteLine($"Buttonless notify <-- {BitConverter.ToString(bytes)}");
                pendingResponse?.TrySetResult(bytes);
            }

            // Mirrors DfuTransportNoble's dfuRequest(): writes a buttonless
            // request and waits (up to operationTimeoutMillis) for a
            // notification response. A response timeout is reported the same
            // way the JS version does - as a generic operation failure, since
            // it is not necessarily the "jumping to bootloader" wait itself.
            async Task<byte[]> SendButtonlessRequestAsync(byte[] request)
            {
                pendingResponse = new TaskCompletionSource<byte[]>();

                var writeStatus = await buttonlessCharacteristic
                    .WriteValueAsync(request.AsBuffer(), GattWriteOption.WriteWithResponse)
                    .AsTask();

                if (writeStatus != GattCommunicationStatus.Success)
                {
                    throw new DfuException(ErrorCode.ERROR_RSP_OPERATION_FAILED, $"Buttonless DFU write failed: {writeStatus}");
                }

                var completed = await Task.WhenAny(pendingResponse.Task, Task.Delay(_operationTimeoutMillis));
                if (completed != pendingResponse.Task)
                {
                    throw new DfuException(ErrorCode.ERROR_RSP_OPERATION_FAILED, "Timed out waiting for a buttonless DFU response.");
                }

                return pendingResponse.Task.Result;
            }

            buttonlessCharacteristic.ValueChanged += OnButtonlessValueChanged;

            try
            {
                var newName = _dfuAdvName ?? $"Dfu{_random.Next(0, 0x100000):x5}";
                var nameBytes = Encoding.UTF8.GetBytes(newName);
                var setNameRequest = new byte[2 + nameBytes.Length];
                setNameRequest[0] = 0x02; // "Set Name" opcode
                setNameRequest[1] = (byte)nameBytes.Length;
                nameBytes.CopyTo(setNameRequest, 2);

                var setNameResponse = await SendButtonlessRequestAsync(setNameRequest);
                if (setNameResponse.Length < 3 || setNameResponse[2] != 0x01)
                {
                    throw new DfuException(ErrorCode.ERROR_RSP_OPERATION_FAILED, "Device rejected the new advertising name.");
                }

                var previousAddress = _device.BluetoothAddress;
                var disconnectSource = new TaskCompletionSource<bool>();
                void OnDisconnect(BluetoothLEDevice sender, object args)
                {
                    if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                    {
                        disconnectSource.TrySetResult(true);
                    }
                }

                _device.ConnectionStatusChanged += OnDisconnect;
                var forcedDisconnect = false;
                try
                {
                    // Fire the "enter bootloader" request but don't await its response -
                    // the device resets before a GATT response can arrive.
                    var enterBootloaderTask = SendButtonlessRequestAsync(new byte[] { 0x01 });
                    _ = enterBootloaderTask.ContinueWith(t =>
                    {
                        System.Diagnostics.Debug.WriteLine($"Enter-bootloader request did not complete normally (expected, device reset): {t.Exception?.InnerException?.Message}");
                    }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

                    // Windows does not always raise ConnectionStatusChanged promptly
                    // (or at all) after the device resets into bootloader mode.
                    // Give it a short grace period, then force the disconnect -
                    // mirrors DfuTransportNoble's win32-specific race-then-force logic.
                    var quickWait = await Task.WhenAny(disconnectSource.Task, Task.Delay(1000));
                    if (quickWait != disconnectSource.Task)
                    {
                        System.Diagnostics.Debug.WriteLine("No disconnect notification within 1s after entering bootloader; forcing disconnect.");
                        forcedDisconnect = true;
                        _device.Dispose();
                        disconnectSource.TrySetResult(true);
                    }
                }
                finally
                {
                    if (!forcedDisconnect)
                    {
                        _device.ConnectionStatusChanged -= OnDisconnect;
                    }
                }

                System.Diagnostics.Debug.WriteLine("Disconnected; scanning for the device re-advertising in bootloader mode.");
                var newDevice = await ScanForNewDeviceAsync(previousAddress + 1, newName, TimeSpan.FromMilliseconds(_operationTimeoutMillis));

                AttachDevice(newDevice);
            }
            finally
            {
                buttonlessCharacteristic.ValueChanged -= OnButtonlessValueChanged;
            }
        }

        // Scans BLE advertisements for a device matching either the given
        // BluetoothAddress or the given advertised local name, and connects to it.
        private static async Task<BluetoothLEDevice> ScanForNewDeviceAsync(ulong targetAddress, string targetName, TimeSpan timeout)
        {
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            var foundSource = new TaskCompletionSource<ulong>();
            void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                var matchesAddress = args.BluetoothAddress == targetAddress;
                var matchesName = !string.IsNullOrEmpty(targetName) && args.Advertisement.LocalName == targetName;

                if (matchesAddress || matchesName)
                {
                    foundSource.TrySetResult(args.BluetoothAddress);
                }
            }

            watcher.Received += OnAdvertisementReceived;
            watcher.Start();

            try
            {
                var completed = await Task.WhenAny(foundSource.Task, Task.Delay(timeout));
                if (completed != foundSource.Task)
                {
                    throw new DfuException(ErrorCode.ERROR_TIMEOUT_SCANNING_NEW_PERIPHERAL);
                }

                return await BluetoothLEDevice.FromBluetoothAddressAsync(foundSource.Task.Result);
            }
            finally
            {
                watcher.Received -= OnAdvertisementReceived;
                watcher.Stop();
            }
        }

        private async void ControlPointCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var bytes = args.CharacteristicValue.ToArray();
            System.Diagnostics.Debug.WriteLine($"BLE notify <-- {BitConverter.ToString(bytes)}");
            await OnData(bytes);
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender != _device)
            {
                // Stale event from a device this transport has since replaced.
                // WinRT event de-registration isn't atomic with in-flight event
                // delivery: the pre-buttonless-jump device's own disconnect
                // notification can still arrive after AttachDevice() has
                // already switched _device to the reconnected peripheral and
                // (re-)discovered its characteristics. Without this guard,
                // that stale event nulls out the new device's characteristics
                // out from under InitializeAsync/WriteCommand/WriteData.
                System.Diagnostics.Debug.WriteLine("Ignoring ConnectionStatusChanged from a device that is no longer attached.");
                return;
            }

            if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("BLE device disconnected");

            lock (_readyLock)
            {
                _readyTask = null;
            }

            _disconnectedSignal?.TrySetResult(true);

            if (_controlPointCharacteristic != null)
            {
                _controlPointCharacteristic.ValueChanged -= ControlPointCharacteristic_ValueChanged;
            }

            _controlPointCharacteristic = null;
            _packetCharacteristic = null;
            _buttonlessCharacteristic = null;
        }

        // Cleans up the transport once a DFU procedure has finished, mirroring
        // DfuTransportNoble.finish()'s peripheral.disconnect(). WinRT has no
        // "disconnect but keep the handle" API, so this disposes the device;
        // callers needing to reuse it afterward must re-resolve it.
        public override Task Finish()
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_device != null)
            {
                _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            }

            if (_controlPointCharacteristic != null)
            {
                _controlPointCharacteristic.ValueChanged -= ControlPointCharacteristic_ValueChanged;
            }

            if (_gattSession != null)
            {
                _gattSession.MaxPduSizeChanged -= GattSession_MaxPduSizeChanged;
                _gattSession.Dispose();
            }

            _dfuService?.Dispose();
            _device?.Dispose();
        }
    }
}

#endif
