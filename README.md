# OneDevice Master Client

The agent that runs on the customer's PC and connects IO-Link masters to the OneDevice cloud
(Azure IoT Hub) – comparable to a LoRaWAN gateway.

```
IO-Link sensor ─ IO-Link master ─USB─ OneDevice Master Client ─MQTT/TLS─▶ Azure IoT Hub ─▶ OneDevice website
                                      (Windows service,
                                       setup page on localhost)
```

## What it does

1. **Setup page** on `http://localhost:5080` (only reachable from this PC):
   detected COM ports (known masters are recognised by their USB id), master type, ports in use, master name,
   cloud credentials.
2. **Identifies the sensors** on the configured ports without an IODD: direct parameter page (Vendor ID, Device ID,
   IO-Link revision, process-data length) and the standard parameters (vendor/product name, product id, serial
   number, hardware/firmware revision, application-specific tag).
3. **Sends process data** to the cloud: raw bytes, only on change, at most once per second (configurable).
   The cloud decodes them with the IODD (DeviceDescriptor).
4. **Answers cloud commands** (IoT Hub direct methods) – see below.
5. Reconnects automatically after the master is unplugged or the network is down.

Each master is one IoT Hub device. Supported masters: TMG IO-Link USB Master V2 (more to come).

## Cloud contract (Azure IoT Hub)

| | Name | Payload |
|---|---|---|
| Telemetry | property `messageType=processData` | `{ masterId, masterName, port, vendorId, deviceId, timestamp, pdIn (hex), pdValid, schemaVersion }` |
| Device twin (reported) | `oneDevice` | master name/type/port count/connected, agent version, `ports.portN` = state + sensor identity |
| Direct method | `readParameter` | `{ port, index, subindex }` → `{ success, data (hex), isduError, error }` |
| Direct method | `writeParameter` | `{ port, index, subindex, data (hex) }` → `{ success, isduError, error }` |
| Direct method | `rescan` | – → reported state after probing the ports again |
| Direct method | `getState` | – → reported state |

Ports are 0-based in the protocol and shown 1-based on the setup page. ISDU errors are reported as e.g.
`0x8030` "Parameter value out of range".

## Cloud authentication

* **Device connection string** (Phase 1): register the master as a device in IoT Hub and paste its connection string.
* **Device Provisioning Service** (Phase 2): ID scope, registration ID and symmetric key; DPS assigns the hub.

Keys are stored encrypted with Windows DPAPI (machine scope) in `%ProgramData%\OneDevice\MasterClient\agent.json`.

## Install

1. Download `OneDevice.MasterClient-<version>-win-x64.zip` from the GitHub release and extract it
   (self-contained: no .NET installation needed).
2. In an administrator PowerShell: `.\Install-Agent.ps1` – installs the Windows service `OneDeviceMasterClient`
   and opens the setup page.

Logs: `%ProgramData%\OneDevice\MasterClient\logs`. Remove with `.\Uninstall-Agent.ps1 [-RemoveData]`.

## Develop

```
dotnet test OneDevice.MasterClient/OneDevice.MasterClient.slnx
dotnet run --project OneDevice.MasterClient/OneDevice.MasterClient.Agent   # console mode, page on localhost:5080
```

Set `Agent__ConfigPath` to use a different configuration file. Built on the NuGet packages
`OneDevice.Master.IoLink` (TMG HAL) and `DeviceDescriptor.*`.
