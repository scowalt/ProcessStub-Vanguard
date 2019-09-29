﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Timers;
using System.Windows.Forms;
using Bleak;
using Jupiter;
using RTCV.CorruptCore;
using RTCV.NetCore;
using RTCV.NetCore.StaticTools;
using Vanguard;
using Timer = System.Timers.Timer;

namespace ProcessStub
{
    public static class ProcessWatch
    {
        public static string ProcessStubVersion = "0.0.4";
        public static string currentDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        public static Process p;
        public static bool UseFiltering = true;
        public static bool UseExceptionHandler;
        public static bool UseBlacklist = true;
        public static bool SuspendProcess = false;
        private static int CPU_STEP_Count;

        public static ProgressForm progressForm;
        public static Timer AutoHookTimer;
        public static Timer AutoCorruptTimer;

        public static MemoryProtection ProtectMode = MemoryProtection.ReadWrite;

        public static void Start()
        {
            AutoHookTimer = new Timer();
            AutoHookTimer.Interval = 5000;
            AutoHookTimer.Elapsed += AutoHookTimer_Elapsed;

            AutoCorruptTimer = new Timer();
            AutoCorruptTimer.Interval = 16;
            AutoCorruptTimer.Elapsed += CorruptTimer_Elapsed;
            AutoCorruptTimer.Start();


            if (VanguardCore.vanguardConnected)
                UpdateDomains();

            //ProcessWatch.currentFileInfo = new ProcessStubFileInfo();

            DisableInterface();
            RtcCore.EmuDirOverride = true; //allows the use of this value before vanguard is connected


            string paramsPath = Path.Combine(currentDir, "PARAMS");

            if (!Directory.Exists(paramsPath))
                Directory.CreateDirectory(paramsPath);

            if(!Params.IsParamSet("DISCLAIMERREAD"))
            {
                var disclaimer = $@"Welcome to ProcessStub
Version {ProcessStubVersion}

Disclaimer:
This program comes with absolutely ZERO warranty.
You may use it at your own risk.
Be EXTREMELY careful with what you choose to corrupt.
Be aware there is always the chance of damage.

This program inserts random data in hooked processes. There is no way to accurately predict what can happen out of this.
The developers of this software will not be held responsible for any damage caused
as the result of use of this software.

By clicking 'Yes' you agree that you have read this warning in full and are aware of any potential consequences of use of the program. If you do not agree, click 'No' to exit this software.";
                if (MessageBox.Show(disclaimer, "Process Stub", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    Environment.Exit(0);
                
                Params.SetParam("DISCLAIMERREAD");
            }

            var protectionMode = Params.ReadParam("PROTECTIONMODE");
            if (protectionMode != null)
                ProtectMode = (MemoryProtection)Enum.Parse(typeof(MemoryProtection), protectionMode);


            UseExceptionHandler = Params.ReadParam("USEEXCEPTIONHANDLER") == "True";
            SuspendProcess = Params.ReadParam("SUSPENDPROCESS") == "True";
            UseFiltering = Params.ReadParam("USEFILTERING") != "False";
            UseBlacklist = Params.ReadParam("USEBLACKLIST") != "False";
        }

        private static void CorruptTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!VanguardCore.vanguardConnected || AllSpec.CorruptCoreSpec == null)
            {
                AutoCorruptTimer.Start();
                return;
            }

            try
            {
                StepActions.Execute();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Corrupt Error!\n{ex.Message}\n{ex.StackTrace}");
            }

            CPU_STEP_Count++;
            bool autoCorrupt = RtcCore.AutoCorrupt;
            long errorDelay = RtcCore.ErrorDelay;
            if (autoCorrupt && CPU_STEP_Count >= errorDelay)
                try
                {
                    CPU_STEP_Count = 0;
                    BlastLayer bl = RtcCore.GenerateBlastLayer((string[])AllSpec.UISpec["SELECTEDDOMAINS"]);
                    if (bl != null)
                        bl.Apply(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AutoCorrupt Error!\n{ex.Message}\n{ex.StackTrace}");
                }

            AutoCorruptTimer.Start();
        }

        private static void AutoHookTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (p?.HasExited == false)
                    return;
                SyncObjectSingleton.FormExecute(() => S.GET<StubForm>().lbTargetStatus.Text = "Waiting...");
                var procToFind = S.GET<StubForm>().tbAutoAttach.Text;
                if (string.IsNullOrWhiteSpace(procToFind))
                    return;

                SyncObjectSingleton.FormExecute(() => S.GET<StubForm>().lbTargetStatus.Text = "Hooking...");
                var _p = Process.GetProcesses().First(x => x.ProcessName == procToFind);
                if (_p != null)
                    SyncObjectSingleton.FormExecute(() =>
                    {
                        LoadTarget(_p);

                        if (!VanguardCore.vanguardConnected)
                            VanguardCore.Start();

                        S.GET<StubForm>().EnableTargetInterface();
                    });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AutoHook failed.\n{ex.Message}\n{ex.StackTrace}");
            }
            AutoHookTimer.Start();
        }


        internal static bool LoadTarget(Process _p = null)
        {
            if (_p == null)
                using (var f = new HookProcessForm())
                {
                    if (f.ShowDialog() != DialogResult.OK)
                        return false;

                    if (f.RequestedProcess == null || (f.RequestedProcess?.HasExited ?? true)) return false;
                    if (IsProcessBlacklisted(f.RequestedProcess))
                    {
                        MessageBox.Show("Blacklisted process");
                        return false;
                    }
                    p = f.RequestedProcess;
                }
            else
                p = _p;

            if (UseExceptionHandler)
            {
                ProcessExtensions.IsWow64Process(p.Handle, out bool is32BitProcess); //This method is stupid and returns the inverse
                string path = is32BitProcess ? Path.Combine(currentDir, "ExceptionHandler_x86.dll") : Path.Combine(currentDir, "ExceptionHandler_x64.dll");
                using (var i = new Injector(InjectionMethod.CreateThread, p.Id, path))
                {
                    if ((ulong)i.InjectDll() != 0) Console.WriteLine("Injected exception helper successfully");
                }
            }

            Action<object, EventArgs> action = (ob, ea) =>
            {
                if (VanguardCore.vanguardConnected)
                    UpdateDomains();
            };

            Action<object, EventArgs> postAction = (ob, ea) =>
            {
                if (p == null)
                {
                    MessageBox.Show("Failed to load target");
                    S.GET<StubForm>().DisableTargetInterface();
                    return;
                }
                S.GET<StubForm>().lbTarget.Text = p.ProcessName;
                S.GET<StubForm>().lbTargetStatus.Text = "Hooked!";


                //Refresh the UI
                //RefreshUIPostLoad();
            };
            S.GET<StubForm>().RunProgressBar("Loading target...", 0, action, postAction);

            return true;
        }


        internal static bool CloseTarget(bool updateDomains = true)
        {
            p = null;
            if (updateDomains)
                UpdateDomains();
            return true;
        }

        public static void UpdateDomains()
        {
            if (!VanguardCore.vanguardConnected)
                return;
            try
            {
                PartialSpec gameDone = new PartialSpec("VanguardSpec");
                gameDone[VSPEC.SYSTEM] = "FileSystem";
                gameDone[VSPEC.GAMENAME] = p?.ProcessName ?? "UNKNOWN";
                gameDone[VSPEC.SYSTEMPREFIX] = "ProcessStub";
                gameDone[VSPEC.SYSTEMCORE] = "ProcessStub";
                gameDone[VSPEC.OPENROMFILENAME] = p?.ProcessName ?? "UNKNOWN";
                gameDone[VSPEC.MEMORYDOMAINS_BLACKLISTEDDOMAINS] = new string[0];
                gameDone[VSPEC.MEMORYDOMAINS_INTERFACES] = GetInterfaces();
                gameDone[VSPEC.CORE_DISKBASED] = false;
                AllSpec.VanguardSpec.Update(gameDone);

                //This is local. If the domains changed it propgates over netcore
                LocalNetCoreRouter.Route(NetcoreCommands.CORRUPTCORE, NetcoreCommands.REMOTE_EVENT_DOMAINSUPDATED, true, true);

                //Asks RTC to restrict any features unsupported by the stub
                LocalNetCoreRouter.Route(NetcoreCommands.CORRUPTCORE, NetcoreCommands.REMOTE_EVENT_RESTRICTFEATURES, true, true);

            }
            catch (Exception ex)
            {
                if (VanguardCore.ShowErrorDialog(ex) == DialogResult.Abort)
                    throw new AbortEverythingException();
            }
        }

        public static MemoryDomainProxy[] GetInterfaces()
        {
            try
            {
                Console.WriteLine("getInterfaces()");
                try
                {
                    if (ProcessExtensions.GetProcessSafe(p) == null)
                    {
                        Console.WriteLine("p was null!");
                        return new MemoryDomainProxy[] { };
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"GetInterfaces threw an exception!\n{e.Message}\n{e.StackTrace}");
                    return new MemoryDomainProxy[] { };
                }

                List<MemoryDomainProxy> interfaces = new List<MemoryDomainProxy>();

                IntPtr addr = new IntPtr(0L);
                var _p = ProcessExtensions.GetProcessSafe(p);
                if (IsProcessBlacklisted(_p))
                    return new MemoryDomainProxy[] { };
                while (true)
                {
                    _p = ProcessExtensions.GetProcessSafe(p);
                    if (_p == null)
                        return new MemoryDomainProxy[] { };


                    if (ProcessExtensions.VirtualQueryEx(_p, addr, out var mbi) == false)
                        break;

                    var name = ProcessExtensions.GetMappedFileNameW(_p.Handle, mbi.BaseAddress);
                    if (string.IsNullOrWhiteSpace(name) || !IsPathBlacklisted(name))
                    {
                        var filters = S.GET<StubForm>().tbFilterText.Text.Split('\n').Select(x => x.Trim()).ToArray();
                        if (mbi.State == (uint)ProcessExtensions.MemoryType.MEM_COMMIT)
                            if ((mbi.Protect & ProtectMode) != 0)
                                if (!UseFiltering || filters.Any(x => name.ToUpper().Contains(x.ToUpper())))
                                {
                                    if (!string.IsNullOrWhiteSpace(name))
                                        Console.WriteLine($"Adding mbi {name.Split('\\').Last()}  {mbi.Protect} {ProtectMode}");
                                    ProcessMemoryDomain pmd = new ProcessMemoryDomain(_p, mbi.BaseAddress, (long)mbi.RegionSize);
                                    interfaces.Add(new MemoryDomainProxy(pmd));
                                }
                    }
                    addr = new IntPtr((long)mbi.BaseAddress + (long)mbi.RegionSize);
                }


                return interfaces.ToArray();
            }
            catch (Exception ex)
            {
                if (VanguardCore.ShowErrorDialog(ex, true) == DialogResult.Abort)
                    throw new AbortEverythingException();

                return new MemoryDomainProxy[] { };
            }

        }

        public static void EnableInterface()
        {
        }

        public static void DisableInterface()
        {
        }

        public static bool IsProcessBlacklisted(Process _p)
        {
            if (_p.HasExited)
                return true;

            if (!UseBlacklist)
                return false;

            return IsModuleBlacklisted(_p.MainModule);
        }

        public static bool IsModuleBlacklisted(ProcessModule pm)
        {
            if (!UseBlacklist)
                return false;
            try
            {
                if (IsPathBlacklisted(pm?.FileName))
                    return true;

                if (pm?.FileVersionInfo?.ProductName != null)
                    if (IsProductNameBlacklisted(pm.FileVersionInfo?.ProductName))
                        return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"IsModuleBlacklisted failed!\n{e.Message}\n{e.StackTrace}");
                return false;
            }

            return false;
        }

        public static bool IsPathBlacklisted(string path)
        {
            if (!UseBlacklist)
                return false;

            var badNames = new[]
            {
                "\\Windows",
                "System32"
            };
            if (badNames.Any(x => path.Contains(x)))
                return true;
            return false;
        }

        public static bool IsProductNameBlacklisted(string productName)
        {
            if (!UseBlacklist)
                return false;

            var badNames = new[]
            {
                "Microsoft® Windows® Operating System"
            };
            if (badNames.Any(x => productName.Equals(x)))
                return true;
            return false;
        }
    }

}
