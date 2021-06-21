﻿using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO;

using Microsoft.Win32;

using TLCSProj.Net;
using TLCSProj.EntryInfo;
using TLCSProj.Core.Time;


namespace TLCSProj.Core
{

    enum UserStatus
    {
        INACTIVE,
        ACTIVE,
        ONREST,
    }

    enum PunchOutType
    {
        OUT,
        BREAK
    }

    internal class Session
    {
        internal static Session Instance;

        static bool IsAdmin
        {
            get
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        internal static string UserID
        {
            get
            {
                return Environment.UserName + (IsAdmin ? $" (Admin)" : " (User)");
            }
        }

        internal static string CurrentTerminalName
        {
            get
            {
                return Environment.MachineName;
            }
        }

        internal static string CurrentIPAddress
        {
            get
            {
                return Network.IsConnected ? Network.GetLocalIPAddress() : "Unknown";
            }
        }

        internal static string TotalProcesses
        {
            get
            {
                return Process.GetProcesses().Length.ToString();
            }
        }

        internal static string TotalServices
        {
            get
            {
                return ServiceController.GetServices().Length.ToString();
            }
        }

        internal static double[] DurationMetrics = new double[3];
        internal static double[] CumulativeDurationMetrics = new double[3];


        internal static string LastPunchIn = "00:00";
        internal static string LastPunchOut = "00:00";
        internal static string TotalRuntime = "00:00:00.00";

        internal static UserStatus UserStatus = default;

        internal const bool OK = true;
        internal const bool FAIL = false;
        internal const string SPAN_FMT = @"hh\:mm\:ss\.mm";
        internal const string REG_SUB_KEY_ALIAS = "Software\\TLCS\\Alias";
        internal const char MULTI_ALIAS_DELIMITER = '|';
        internal const int HOUR_INDEX = 0;
        internal const int MINUTE_INDEX = 1;
        internal const int SECOND_INDEX = 2;

        internal string _commandString;

        static SessionCallback[] _SessionCallbacks;
        internal static Stopwatch SessionRuntime { get; private set; }
        internal static Stopwatch CumulativeSessionRuntime { get; private set; }
        static bool _IncludeSystemEvents = false;
        static bool _inSession = false;
        static Process[] _TrackedProcesses;

        internal static TimeLog SessionTimeLog;

        static RegistrySecurity sec = new RegistrySecurity();

#nullable enable annotations
        static string[]? _storedArgs = null;
        static RegistryKey? aliasKey;

        static readonly SessionCallbackMethods[] CommandMethodList = {
                //Post command (args: string)
                () =>
                {
                    _storedArgs[1] = Instance._commandString.Split('\"', StringSplitOptions.None)[1];
                    SessionTimeLog.AddNewEntry(EntryType.POST, _storedArgs[1]);
                },

                //Login Command (void)
                () =>
                {
                    _inSession = true;
                    SessionTimeLog.AddNewEntry(EntryType.PUNCHIN, color: ConsoleColor.Green);
                    SessionRuntime = Stopwatch.StartNew();
                    CumulativeSessionRuntime = Stopwatch.StartNew();
                    MarkPunchIn();
                },

                //Rest Command (void)
                () =>
                {
                    
                    SessionTimeLog.AddNewEntry(EntryType.PUNCHOUT, $"{GetSessionRuntime()} | " +
                        $"{GetCumulativeSessionRuntime()}", ConsoleColor.Green);
                    SessionRuntime.Stop();
                    CumulativeSessionRuntime.Stop();
                    MarkPunchOut(PunchOutType.BREAK);
                },

                //Resume Command (void)
                () =>
                {
                    SessionTimeLog.AddNewEntry(EntryType.PUNCHIN, $"{GetSessionRuntime()} | " +
                        $"{GetCumulativeSessionRuntime()}", ConsoleColor.Green);
                    SessionRuntime.Restart();
                    CumulativeSessionRuntime.Start();
                    MarkPunchIn();
                },

                //Out Command (void)
                () =>
                {
                    _inSession = false;
                    SessionTimeLog.AddNewEntry(EntryType.PUNCHOUT, $"{GetSessionRuntime()} | " +
                        $"{GetCumulativeSessionRuntime()}", ConsoleColor.Green);
                    SessionRuntime.Stop();
                    CumulativeSessionRuntime.Stop();
                    MarkPunchOut();
                },

                //Open Command (args: string)
                () =>
                {
                    if (!_IncludeSystemEvents) return;

                    _storedArgs[1] = Instance._commandString.Split('\"', StringSplitOptions.None)[1];

                    SessionTimeLog.AddNewEntry(EntryType.PROCESSSTARTREQUEST, (_IncludeSystemEvents ?
                        $"Opening {_storedArgs[1]}" : null) + "...", ConsoleColor.Yellow);

                    OpenProcess(_storedArgs[1]);
                },

                //Close Command (args: string)
                () =>
                {
                    if (!_IncludeSystemEvents) return;

                    _storedArgs[1] = Instance._commandString.Split('\"', StringSplitOptions.None)[1];

                    //TODO: Find Service Name, and close
                    SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, (_IncludeSystemEvents ?
                        $"Closing {_storedArgs[1]}" : null) + "...", ConsoleColor.Yellow);

                    CloseProcess(_storedArgs[1]);
                },

                //Hotkey Command (args: char, int)
                () =>
                {
                    if (!_IncludeSystemEvents) return;

                    SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, _IncludeSystemEvents ?
                        $"Key {_storedArgs[1]} set to {(CommandList)Convert.ToInt32(_storedArgs[2])} command.\n" +
                        $"Hold ALT then the hotkey you've registered." : null, ConsoleColor.Yellow);
                },

                //System Listen Command (args: bool)
                () =>
                {
                    _IncludeSystemEvents = bool.Parse(_storedArgs[1]);

                    SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, _IncludeSystemEvents ?
                        $"System Now Listening. System Events will now be logged." :
                        "System Has Stopped Listening. System Events will not be logged.", ConsoleColor.Yellow);
                },

                //Log Target Command (args: string)
                () =>
                {
                    //TODO: Change directory where log info will be printed
                },

                //Session Time Command (void)
                () =>
                {
                    SessionTimeLog.AddNewEntry(EntryType.NULL,
                        $"{GetSessionRuntime()} | " +
                        $"{GetCumulativeSessionRuntime()}");

                },

                //Print Command (void)
                () =>
                {
                    //TODO: Open Print Service, and set print job for Target File 
                    
                },

                //New Alias Command (string, string)
                () =>
                {
                    if (!_IncludeSystemEvents) return;

                    try
                    {
                        _storedArgs[1] = Instance._commandString.Split('\"', StringSplitOptions.None)[1];
                        _storedArgs[2] = Instance._commandString.Split('\"', StringSplitOptions.None)[2];

                        //Add New SubKey if opening fails
                        aliasKey = Registry.LocalMachine.OpenSubKey(REG_SUB_KEY_ALIAS, IsAdmin);

                        aliasKey.SetValue(_storedArgs[2], _storedArgs[1]);

                        aliasKey.Close();

                        SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, $"Alias {_storedArgs[2]} added successfully!\nProcess(s): {_storedArgs[1]}", ConsoleColor.Yellow);
                    }
                    catch (AccessViolationException ave)
                    {
                        SessionTimeLog.AddNewEntry(EntryType.SYSTEMERROR, $"Failed to add new alias {_storedArgs[2]}.\nREASON CODE: {ave.Message}", ConsoleColor.Red);
                    }
                    catch (Exception e)
                    {
                        SessionTimeLog.AddNewEntry(EntryType.SYSTEMERROR, $"Failed to add new alias {_storedArgs[2]}.\nREASON CODE: {e.Message}\nDoes AliasKey exist?\nIf not, use command \"genalikey\"", ConsoleColor.Red);
                    }
                },

                //Generate Alias Registry (void)
                () =>
                {
                    try
                    {
                        if (!_IncludeSystemEvents) return;

                        if(Registry.LocalMachine.OpenSubKey(REG_SUB_KEY_ALIAS, true) == null ){
                            aliasKey = Registry.LocalMachine.CreateSubKey(REG_SUB_KEY_ALIAS, true);
                            aliasKey.SetAccessControl(sec);
                            aliasKey.Close();
                            SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, $"Alias Registry Key has been generated...", ConsoleColor.Yellow);
                        }

                        SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, $"Alias Registry Key has already been generated.", ConsoleColor.Yellow);
                    }
                    catch (Exception e)
                    {
                        SessionTimeLog.AddNewEntry(EntryType.SYSTEMERROR, $"Failed to generate Alias Registry Key. Are you an Admin?\nREASON CODE: {e.Message}", ConsoleColor.Red);
                    }
                },

                //Get Hours Command (int?)
                () =>
                {
                    var value = 0;
                    var returnValue = value == 1 ? DurationMetrics[HOUR_INDEX] : CumulativeDurationMetrics[HOUR_INDEX];
                    try
                    {
                        value = (Convert.ToInt32(_storedArgs[1]));
                    }
                    catch
                    {
                        value = default;
                    }
                    SessionTimeLog.AddNewEntry(EntryType.NULL, $"Session Runtime in Hours: {returnValue}");
                },

                //Get Minutes Command (int?)
                () =>
                {
                    var value = 0;
                    var returnValue = value == 1 ? DurationMetrics[MINUTE_INDEX] : CumulativeDurationMetrics[MINUTE_INDEX];
                    try
                    {
                        value = (Convert.ToInt32(_storedArgs[1]));
                    }
                    catch
                    {
                        value = default;
                    }
                    SessionTimeLog.AddNewEntry(EntryType.NULL, $"Session Runtime in Minutes: {returnValue}");
                },

                //Get Seconds Command (int?)
                () =>
                {
                    var value = 0;
                    var returnValue = value == 1 ? DurationMetrics[SECOND_INDEX] : CumulativeDurationMetrics[SECOND_INDEX];
                    try
                    {
                        value = (Convert.ToInt32(_storedArgs[1]));
                    }
                    catch
                    {
                        value = default;
                    }
                    SessionTimeLog.AddNewEntry(EntryType.NULL, $"Session Runtime in Seconds: {returnValue}");
                },

                //Help Command (void)
                () =>
                {

                },

                //End Command (void)
                () =>
                {
                    //TODO: End Session
                    _inSession = false;
                    SessionTimeLog .AddNewEntry(EntryType.PUNCHOUT, "End of Time Logging Session!", ConsoleColor.Green);
                }
            };

        private static void OpenProcess(string applicationPath)
        {
            //Start application
            try
            {
                aliasKey = Registry.LocalMachine.OpenSubKey(REG_SUB_KEY_ALIAS, IsAdmin);

                string input = applicationPath;

                string processFromAlias = GetAliasProcessString(aliasKey, _storedArgs[1]);

                applicationPath = processFromAlias == string.Empty ? input : processFromAlias;

                aliasKey.Close();

                bool isMultiProcessAlias = applicationPath.Contains(MULTI_ALIAS_DELIMITER);
                if (isMultiProcessAlias)
                {
                    string[] processes = applicationPath.Split(MULTI_ALIAS_DELIMITER);
                    foreach (string process in processes)
                    {
                        Process.Start(process);

                        _TrackedProcesses = Process.GetProcesses();

                        SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, $"Process {process} started successfully!", ConsoleColor.Yellow);
                    }
                }
                else
                {
                    Process.Start(applicationPath);

                    _TrackedProcesses = Process.GetProcesses();

                    SessionTimeLog.AddNewEntry(EntryType.SYSTEMPOST, $"Process {applicationPath} started successfully!", ConsoleColor.Yellow);
                }
            }
            catch (Exception e)
            {
                SessionTimeLog.AddNewEntry(EntryType.SYSTEMERROR, $"Failed to execute process {applicationPath}... REASON: {e.Message}", ConsoleColor.Red);
            }
        }

        private static void CloseProcess(string applicationPath)
        {
            //Close Application
            foreach (Process process in _TrackedProcesses)
            {
                if (process.ProcessName == applicationPath)
                    process.Close();
            }
        }

        enum CommandList
        {
            NULL,
            POST,
            IN,
            REST,
            RESUME,
            OUT,
            OPEN,
            CLOSE,
            HOTKEY,
            SYSLIS,
            LOGTAR,
            SESTIM,
            PRINT,
            NEWALI,
            GENALIKEY,
            GETHRS,
            GETMINS,
            GETSECS,
            HELP,
            END
        }


        internal static void Validate(string[] args)
        {
            _storedArgs = args;

            //First argument will be command call

            CallCommand(args[0]);
        }

        static void CallCommand(string command)
        {
            for (int index = 1; index < CommandMethodList.Length; index++)
            {
                if (command.ToUpper().Contains(_SessionCallbacks[index].Code))
                {
                    _SessionCallbacks[index].Trigger();
                }
            }
        }
        internal Session()
        {
            sec.AddAccessRule(
                new RegistryAccessRule($"{Environment.UserDomainName}\\{Environment.UserName}",
                    RegistryRights.ReadKey |
                    RegistryRights.WriteKey |
                    RegistryRights.Delete,

                    InheritanceFlags.None,

                    PropagationFlags.None,

                    AccessControlType.Allow
                    )
                );

            SessionTimeLog = new TimeLog();

            InitCommands();

            SessionRuntime = Stopwatch.StartNew();
            CumulativeSessionRuntime = Stopwatch.StartNew();

            Instance = this;
        }

        static void InitCommands()
        {
            var commandListCount = Enum.GetValues(typeof(CommandList)).Length - 1;

            _SessionCallbacks = new SessionCallback[commandListCount];

            for (int index = 0; index < commandListCount; index++)
            {
                SessionCallback newCommandEntry = null;
                if (index > 0)
                    newCommandEntry = SessionCallback.Create(((CommandList)index).ToString(), CommandMethodList[index - 1]);
                _SessionCallbacks[index] = newCommandEntry;
            }
        }

        internal static string GetSessionRuntime()
        {
            TimeSpan sessionTimeSpan = SessionRuntime.Elapsed;
            DurationMetrics[HOUR_INDEX] = sessionTimeSpan.TotalHours;
            DurationMetrics[MINUTE_INDEX] = sessionTimeSpan.TotalMinutes;
            DurationMetrics[SECOND_INDEX] = sessionTimeSpan.TotalSeconds;

            return $"SESSION RUNTIME: {sessionTimeSpan.ToString(SPAN_FMT)}";
        }

        internal static string GetCumulativeSessionRuntime()
        {
            TimeSpan cumulativeSessionTimeSpan = CumulativeSessionRuntime.Elapsed;

            CumulativeDurationMetrics[HOUR_INDEX] = cumulativeSessionTimeSpan.TotalHours;
            CumulativeDurationMetrics[MINUTE_INDEX] = cumulativeSessionTimeSpan.TotalMinutes;
            CumulativeDurationMetrics[SECOND_INDEX] = cumulativeSessionTimeSpan.TotalSeconds;

            return $"CUMULATIVE SESSION RUNTIME: {cumulativeSessionTimeSpan.ToString(SPAN_FMT)}";
        }

        static void MarkPunchIn()
        {
            LastPunchIn = DateTime.Now.ToLongTimeString();
            UpdateUserStatus(UserStatus.ACTIVE);
        }
        static void MarkPunchOut(PunchOutType isRest = PunchOutType.OUT)
        {
            LastPunchOut = DateTime.Now.ToLongTimeString();
            UpdateUserStatus((int)isRest == 1 ? UserStatus.ONREST : UserStatus.INACTIVE);
        }

        static string GetAliasProcessString(RegistryKey aliasKey, string aliasName)
        {
            for (int index = 0; index < aliasKey.GetValueNames().Length; index++)
            {
                string currentKeyName = aliasKey.GetValueNames()[index];
                if (currentKeyName.Contains(aliasName))
                {
                    return aliasKey.GetValue(currentKeyName) as string;
                }
            }

            return string.Empty;
        }

        static void UpdateUserStatus(UserStatus newStatus) => UserStatus = newStatus;

    }
}
