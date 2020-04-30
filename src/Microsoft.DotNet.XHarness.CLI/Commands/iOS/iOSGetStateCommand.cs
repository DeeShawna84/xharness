﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;

using Microsoft.DotNet.XHarness.iOS.Shared.Hardware;
using Microsoft.DotNet.XHarness.iOS.Shared.Execution;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;
using Microsoft.DotNet.XHarness.CLI.CommandArguments;
using Microsoft.DotNet.XHarness.CLI.CommandArguments.iOS;
using Microsoft.Extensions.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared.Execution.Mlaunch;

namespace Microsoft.DotNet.XHarness.CLI.Commands.iOS
{
    internal class iOSGetStateCommand : GetStateCommand
    {
        class DeviceInfo
        {
            public string Name { get; set; }
            public string UDID { get; set; }
            public string Type { get; set; }
            public string OSVersion { get; set; }
        }

        class SystemInfo
        {
            public string MachineName { get; set; }
            public string OSName { get; set; }
            public string OSVersion { get; set; }
            public string OSPlatform { get; set; }
            public string XcodePath { get; set; }
            public string XcodeVersion { get; set; }
            public string MlaunchPath { get; set; }
            public string MlaunchVersion { get; set; }
            public List<DeviceInfo> Simulators { get; } = new List<DeviceInfo>();
            public List<DeviceInfo> Devices { get; } = new List<DeviceInfo>();
        }

        private const string SimulatorPrefix = "com.apple.CoreSimulator.SimDeviceType.";

        private readonly iOSGetStateCommandArguments _arguments = new iOSGetStateCommandArguments();

        protected override ICommandArguments Arguments => _arguments;

        protected override string BaseCommand { get; } = "ios";

        public iOSGetStateCommand() : base()
        {
            Options = CommonOptions;

            Options.Add("mlaunch=", "Path to the mlaunch binary", v => _arguments.MlaunchPath = v);
            Options.Add("include-simulator-uuid", "Include the simulators UUID. Defaults to false.", v => _arguments.ShowSimulatorsUUID = v != null);
            Options.Add("include-devices-uuid", "Include the devices UUID.", v => _arguments.ShowDevicesUUID = v != null);
            Options.Add("json", "Use json as the output format.", v => _arguments.UseJson = v != null);
        }

        private async Task AsJson(SystemInfo info)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), info, options);
            Console.WriteLine();
        }

        private void AsText(SystemInfo info)
        {
            Console.WriteLine("Runtime Enviroment:");
            Console.WriteLine($"  Machine name:\t{info.MachineName}");
            Console.WriteLine($"  OS Name:\t{info.OSName}");
            Console.WriteLine($"  OS Version:\t{info.OSVersion}");
            Console.WriteLine($"  OS Platform:\t{info.OSPlatform}");

            Console.WriteLine();

            Console.WriteLine("Developer Tools:");

            Console.WriteLine($"  Xcode:\t{info.XcodeVersion} - {info.XcodePath}");
            Console.WriteLine($"  Mlaunch:\t{info.MlaunchVersion} - {info.MlaunchPath}");

            Console.WriteLine();

            Console.WriteLine("Installed Simulators:");

            if (info.Simulators.Any())
            {
                var maxLength = info.Simulators.Select(s => s.Name.Length).Max();

                foreach (var sim in info.Simulators)
                {
                    var uuid = _arguments.ShowSimulatorsUUID ? $" {sim.UDID}   " : "";
                    Console.WriteLine($"  {sim.Name.PadRight(maxLength)}{uuid} {sim.OSVersion,-13} {sim.Type}");
                }
            }
            else
            {
                Console.WriteLine("  none");
            }

            Console.WriteLine();

            Console.WriteLine("Connected Devices:");

            if (info.Devices.Any())
            {
                var maxLength = info.Devices.Select(s => s.Name.Length).Max();

                foreach (var dev in info.Devices)
                {
                    var uuid = _arguments.ShowDevicesUUID ? $" {dev.UDID}   " : "";
                    Console.WriteLine($"  {dev.Name.PadRight(maxLength)}{uuid} {dev.OSVersion,-13} {dev.Type}");
                }
            }
            else
            {
                Console.WriteLine("  none");
            }
        }

        protected override async Task<ExitCode> InvokeInternal()
        {
            var processManager = new ProcessManager(mlaunchPath: _arguments.MlaunchPath); 
            var deviceLoader = new HardwareDeviceLoader(processManager);
            var simulatorLoader = new SimulatorLoader(processManager);
            var log = new MemoryLog(); // do we really want to log this?

            var mlaunchLog = new MemoryLog { Timestamp = false };

            ProcessExecutionResult result;

            try
            {
                result = await processManager.ExecuteCommandAsync(new MlaunchArguments(new MlaunchVersionArgument()), new MemoryLog(), mlaunchLog, new MemoryLog(), TimeSpan.FromSeconds(10));
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to get mlaunch version info:{Environment.NewLine}{e}");
                return ExitCode.GENERAL_FAILURE;
            }

            if (!result.Succeeded)
            {
                _log.LogError($"Failed to get mlaunch version info:{Environment.NewLine}{mlaunchLog}");
                return ExitCode.GENERAL_FAILURE;
            }

            // build the required data, then depending on the format print out
            var info = new SystemInfo
            {
                MachineName = Environment.MachineName,
                OSName = "Mac OS X",
                OSVersion = Darwin.GetVersion() ?? "",
                OSPlatform = "Darwin",
                XcodePath = processManager.XcodeRoot,
                XcodeVersion = processManager.XcodeVersion.ToString(),
                MlaunchPath = processManager.MlaunchPath,
                MlaunchVersion = mlaunchLog.ToString().Trim(),
            };

            try
            {
                await simulatorLoader.LoadDevices(log);
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to load simulators:{Environment.NewLine}{e}");
                _log.LogInformation($"Execution log:{Environment.NewLine}{log}");
                return ExitCode.GENERAL_FAILURE;
            }

            foreach (var sim in simulatorLoader.AvailableDevices)
            {
                info.Simulators.Add(new DeviceInfo
                {
                    Name = sim.Name,
                    UDID = sim.UDID,
                    Type = sim.SimDeviceType.Remove(0, SimulatorPrefix.Length).Replace('-', ' '),
                    OSVersion = sim.OSVersion,
                });
            }

            try
            {
                await deviceLoader.LoadDevices(log);
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to load connected devices:{Environment.NewLine}{e}");
                _log.LogInformation($"Execution log:{Environment.NewLine}{log}");
                return ExitCode.GENERAL_FAILURE;
            }

            foreach (var dev in deviceLoader.ConnectedDevices)
            {
                info.Devices.Add(new DeviceInfo
                {
                    Name = dev.Name,
                    UDID = dev.DeviceIdentifier,
                    Type = $"{dev.DeviceClass} {dev.DevicePlatform}",
                    OSVersion = dev.OSVersion,
                });
            }

            if (_arguments.UseJson)
            {
                await AsJson(info);
            }
            else
            {
                AsText(info);
            }

            return ExitCode.SUCCESS;
        }

        private class MlaunchVersionArgument : OptionArgument
        {
            public MlaunchVersionArgument() : base("version")
            {
            }
        }
    }
}