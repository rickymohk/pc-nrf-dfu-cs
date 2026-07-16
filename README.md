# nRF DFU

[![License](https://img.shields.io/badge/license-Proprietary%2FMIT-blue)](LICENSE)

`pc-nrf-dfu-cs` is a C# module which provides DFU (Device Firmware Upgrade) via Serial UART / USB CDC ACM, and Bluetooth LE transports for Nordic devices.

This module is multi-targeted .NET library and was adapted from [`pc-nrf-dfu-js`](https://github.com/NordicSemiconductor/pc-nrf-dfu-js) and is designed to provide DFU capabilities to .NET applications. The BLE transport (`DfuTransportBle`, .NET Framework 4.8 only) additionally mirrors the behavior of [`noble-nrf-dfu`](https://github.com/rickymohk/noble-nrf-dfu) (a fork maintained alongside this project, not the official Nordic package)'s `DfuTransportNoble`, using the WinRT `Windows.Devices.Bluetooth` GATT client APIs in place of Node's `noble`.

The following devices are supported:

* USB SDFU:
    * PCA10056 nRF52840 Development Kit
    * PCA10059 nRF52840 Dongle
	* Custom nRF boards using Nordic SDK 15.x DFU application/bootloader libraries
		* Tested using custom firmware on Particle Xenon (Nordic SDK version 15.3.0)
* BLE (Secure DFU / buttonless DFU) via WinRT, on .NET Framework 4.8:
    * Any Nordic nRF5x device running the Secure DFU BLE bootloader (application-mode buttonless jump, or already in bootloader mode)

## Installation

```
$ Install-Package Nordic.nRF.DFU
```

### Dependency requirements

#### Nuget Packages (listed versions are as tested)

* Google.Protobuf (3.10.1)
* Newtonsoft.Json (12.0.2)
* System.IO.Compression.ZipFile (4.3.0)
* System.IO.Ports (4.6.0)
* Microsoft.Windows.SDK.Contracts (10.0.19041.1) - net48 target only, projects `Windows.Devices.Bluetooth` (WinRT) into .NET Framework for `DfuTransportBle`

#### BLE DFU devices

`DfuTransportBle` is only built for the `net48` target framework, since it relies on the WinRT `Windows.Devices.Bluetooth` GATT client APIs (Windows 10+). Consuming projects also need a reference to `Microsoft.Windows.SDK.Contracts` and `System.Runtime.WindowsRuntime`, since they'll typically call these APIs directly too (e.g. to scan for and connect to the target device before handing it to `DfuTransportBle`). No app packaging/manifest changes are required - these APIs work from a classic, unpackaged desktop app.

#### USB SDFU devices

##### Windows

In order to access Nordic USB devices on Windows, specific device drivers must be installed. The device drivers are automatically installed by the nRF Connect installer, starting from version 2.4. The drivers can also be found [here](https://github.com/NordicSemiconductor/pc-nrfconnect-core/tree/master/build/drivers).

##### Linux (untested, should work under Mono)
Linux requires correct permissions to access these devices. For this purpose please install udev rules from [nrf-udev](https://github.com/NordicSemiconductor/nrf-udev) repository, follow instructions there.

## Usage

### Serial / USB CDC ACM

```csharp
using Nordic.nRF.DFU
...

// Create DfuUpdates
var updates = await DfuUpdates.FromZipFile(firmwarePath);

// Create DfuTransportSerial
var serialTransport = new DfuTransportSerial(portName, 16);
OR
var serialPort = new SerialPort(...) { ... constructed as needed ... };
var serialTransport = new DfuTransportSerial(serialPort, 16);

// Create DfuOperation
var dfu = new DfuOperation(updates, serialTransport);

// Start dfu
await dfu.Start(true);
```

### BLE (net48 only)

```csharp
using Nordic.nRF.DFU
using Windows.Devices.Bluetooth;
...

// Create DfuUpdates
var updates = await DfuUpdates.FromZipFile(firmwarePath);

// Connect to an already-discovered BLE device (scanning/pairing is the
// caller's responsibility - e.g. via BluetoothLEAdvertisementWatcher)
var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
var bleTransport = new DfuTransportBle(device);

// Create DfuOperation
var dfu = new DfuOperation(updates, bleTransport);

// Start dfu - if the device is in application mode with buttonless DFU
// support, it is automatically jumped into bootloader mode and reconnected
await dfu.Start(true);
```

## USB SDFU

PCA10059 is a nRF52840 dongle which does not have a JLink debugger, so the USB device
that the operating system _sees_ depends on the firmware that is currently running on the Nordic chip.

This can be either a _bootloader_ or an _application firmware_.

### Bootloader mode

The pre-programmed bootloader provides a USB device with vendor ID `0x1915` and product ID `0x521f`.
This device has a USB CDC ACM (serialport) interface which handles the DFU operation.
In case you need to manually trigger the bootloader, press the RESET button on the dongle.

### Application mode

The dongle will be in application mode if it is plugged in and is programmed with a valid application. It will also switch to application mode after a successful DFU operation.

In application mode the USB device visible to the OS depends on the application firmware.
For further documentation please refer to the [Nordic SDK](https://developer.nordicsemi.com/nRF5_SDK/).

In application mode it is **expected** that the visible USB device to the OS has a _DFU trigger interface_.
This interface provides a `semver` string which identifies the application firmware currently running.
If the `semver` doesn't match the expected value, the device will be reset into bootloader mode.

Changing between bootloader and application also implies that the USB device is detached and attached,
so there is an underlying functionality based on _nrf-device-lister_ which looks for the newly
attached USB device and tries to match by its _port name_ (eg. COM14).

## BLE DFU

`DfuTransportBle` takes an already-connected `BluetoothLEDevice` - discovering and connecting to the
target device (e.g. via `BluetoothLEAdvertisementWatcher`) is the caller's responsibility.

### Application mode (buttonless DFU)

If the device is running application firmware with buttonless DFU support, `DfuOperation.Start()`
automatically detects it (via the buttonless DFU characteristic), jumps the device into bootloader
mode, waits for it to disconnect, then scans for and reconnects to it as it re-advertises - either
under a new name or `BluetoothAddress + 1` (Nordic's bootloader advertises a different address for
privacy while in DFU mode). None of this requires any extra calls from the consuming application.

### Bootloader mode

If the device is already in bootloader mode (no buttonless characteristic present), DFU proceeds
directly against the Secure DFU service's control point and packet characteristics.

## Development

### Build

The project uses [FAKE](https://fake.build), so the following command is needed to run the build:
```
Windows
$fake build

Linux
>./fake.sh build
```

### Test

The project comes with a test console application in the `test` directory, which expects a serial port
name and application package to be supplied:

```
Windows
$dotnet DFUConsole.dll COM14 test_package_0.0.1.zip

Linux
>mono dotnet DFUConsole.dll /dev/com1 test_package_0.0.1.zip
```

There is also a BLE test console application at `test/Nordic.nRF.DFU.BLE.Console` (Windows/net48 only),
which expects a BLE device's advertised name and an application package:

```
Nordic.nRF.DFU.BLE.Console.exe MyDeviceName test_package_0.0.1.zip
```
