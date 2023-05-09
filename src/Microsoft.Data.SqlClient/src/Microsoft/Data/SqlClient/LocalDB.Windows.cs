// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Data.SqlClient
{
    internal sealed class LocalDB
    {
        private static readonly LocalDB Instance = new LocalDB();

        //HKEY_LOCAL_MACHINE
        private const string LocalDBInstalledVersionRegistryKey = "SOFTWARE\\Microsoft\\Microsoft SQL Server Local DB\\Installed Versions\\";

        private const string InstanceAPIPathValueName = "InstanceAPIPath";

        private const string ProcLocalDBStartInstance = "LocalDBStartInstance";

        private const int MAX_LOCAL_DB_CONNECTION_STRING_SIZE = 260;

        private IntPtr _startInstanceHandle = IntPtr.Zero;

        // Local Db api doc https://msdn.microsoft.com/en-us/library/hh217143.aspx
        // HRESULT LocalDBStartInstance( [Input ] PCWSTR pInstanceName, [Input ] DWORD dwFlags,[Output] LPWSTR wszSqlConnection,[Input/Output] LPDWORD lpcchSqlConnection);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int LocalDBStartInstance(
                [In] [MarshalAs(UnmanagedType.LPWStr)] string localDBInstanceName,
                [In]  int flags,
                [Out] [MarshalAs(UnmanagedType.LPWStr)] StringBuilder sqlConnectionDataSource,
                [In, Out]ref int bufferLength);

        private LocalDBStartInstance localDBStartInstanceFunc = null;

        private volatile SafeLibraryHandle _sqlUserInstanceLibraryHandle;

        private LocalDB() { }

        internal static string GetLocalDBConnectionString(string localDbInstance) =>
            Instance.LoadUserInstanceDll() ? Instance.GetConnectionString(localDbInstance) : null;

        internal static IntPtr GetProcAddress(string functionName) =>
            Instance.LoadUserInstanceDll() ? Interop.Kernel32.GetProcAddress(LocalDB.Instance._sqlUserInstanceLibraryHandle, functionName) : IntPtr.Zero;

        private string GetConnectionString(string localDbInstance)
        {
            StringBuilder localDBConnectionString = new StringBuilder(MAX_LOCAL_DB_CONNECTION_STRING_SIZE + 1);
            int sizeOfbuffer = localDBConnectionString.Capacity;
            localDBStartInstanceFunc(localDbInstance, 0, localDBConnectionString, ref sizeOfbuffer);
            return localDBConnectionString.ToString();
        }

        internal enum LocalDBErrorState
        {
            NO_INSTALLATION, INVALID_CONFIG, NO_SQLUSERINSTANCEDLL_PATH, INVALID_SQLUSERINSTANCEDLL_PATH, NONE
        }

        //internal static uint MapLocalDBErrorStateToCode(LocalDBErrorState errorState)
        //{
        //    switch (errorState)
        //    {
        //        case LocalDBErrorState.NO_INSTALLATION:
        //            return SNICommon.LocalDBNoInstallation;
        //        case LocalDBErrorState.INVALID_CONFIG:
        //            return SNICommon.LocalDBInvalidConfig;
        //        case LocalDBErrorState.NO_SQLUSERINSTANCEDLL_PATH:
        //            return SNICommon.LocalDBNoSqlUserInstanceDllPath;
        //        case LocalDBErrorState.INVALID_SQLUSERINSTANCEDLL_PATH:
        //            return SNICommon.LocalDBInvalidSqlUserInstanceDllPath;
        //        case LocalDBErrorState.NONE:
        //            return 0;
        //        default:
        //            return SNICommon.LocalDBInvalidConfig;
        //    }
        //}

        internal static string MapLocalDBErrorStateToErrorMessage(LocalDBErrorState errorState)
        {
            switch (errorState)
            {
                case LocalDBErrorState.NO_INSTALLATION:
                    return Strings.SNI_ERROR_52;
                case LocalDBErrorState.INVALID_CONFIG:
                    return Strings.SNI_ERROR_53;
                case LocalDBErrorState.NO_SQLUSERINSTANCEDLL_PATH:
                    return Strings.SNI_ERROR_54;
                case LocalDBErrorState.INVALID_SQLUSERINSTANCEDLL_PATH:
                    return Strings.SNI_ERROR_55;
                case LocalDBErrorState.NONE:
                    return Strings.SNI_ERROR_50;
                default:
                    return Strings.SNI_ERROR_53;
            }
        }

      
       
        /// <summary>
        /// Loads the User Instance dll.
        /// </summary>
        private bool LoadUserInstanceDll()
        {
            
                // Check in a non thread-safe way if the handle is already set for performance.
                if (_sqlUserInstanceLibraryHandle != null)
                {
                    return true;
                }

                lock (this)
                {
                    if (_sqlUserInstanceLibraryHandle != null)
                    {
                        return true;
                    }
                    //Get UserInstance Dll path
                    LocalDBErrorState registryQueryErrorState;

                    // Get the LocalDB instance dll path from the registry
                    string dllPath = GetUserInstanceDllPath(out registryQueryErrorState);

                    // If there was no DLL path found, then there is an error.
                    if (dllPath == null)
                    {
                        //SNILoadHandle.SingletonInstance.LastError = new SNIError(SNIProviders.INVALID_PROV, 0, MapLocalDBErrorStateToCode(registryQueryErrorState), MapLocalDBErrorStateToErrorMessage(registryQueryErrorState));
                        //SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "User instance DLL path is null.");
                        return false;
                    }

                    // In case the registry had an empty path for dll
                    if (string.IsNullOrWhiteSpace(dllPath))
                    {
                        //SNILoadHandle.SingletonInstance.LastError = new SNIError(SNIProviders.INVALID_PROV, 0, SNICommon.LocalDBInvalidSqlUserInstanceDllPath, Strings.SNI_ERROR_55);
                        //SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "User instance DLL path is invalid. DLL path = {0}", dllPath);
                        return false;
                    }

                    // Load the dll
                    SafeLibraryHandle libraryHandle = Interop.Kernel32.LoadLibraryExW(dllPath.Trim(), IntPtr.Zero, 0);

                    if (libraryHandle.IsInvalid)
                    {
                        //SNILoadHandle.SingletonInstance.LastError = new SNIError(SNIProviders.INVALID_PROV, 0, SNICommon.LocalDBFailedToLoadDll, Strings.SNI_ERROR_56);
                        //SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "Library Handle is invalid. Could not load the dll.");
                        libraryHandle.Dispose();
                        return false;
                    }

                    // Load the procs from the DLLs
                    _startInstanceHandle = Interop.Kernel32.GetProcAddress(libraryHandle, ProcLocalDBStartInstance);

                    if (_startInstanceHandle == IntPtr.Zero)
                    {
                        //SNILoadHandle.SingletonInstance.LastError = new SNIError(SNIProviders.INVALID_PROV, 0, SNICommon.LocalDBBadRuntime, Strings.SNI_ERROR_57);
                        //SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "Was not able to load the PROC from DLL. Bad Runtime.");
                        libraryHandle.Dispose();
                        return false;
                    }

                    // Set the delegate the invoke.
                    localDBStartInstanceFunc = (LocalDBStartInstance)Marshal.GetDelegateForFunctionPointer(_startInstanceHandle, typeof(LocalDBStartInstance));

                    if (localDBStartInstanceFunc == null)
                    {
                        //SNILoadHandle.SingletonInstance.LastError = new SNIError(SNIProviders.INVALID_PROV, 0, SNICommon.LocalDBBadRuntime, Strings.SNI_ERROR_57);
                        libraryHandle.Dispose();
                        _startInstanceHandle = IntPtr.Zero;
                        return false;
                    }

                    _sqlUserInstanceLibraryHandle = libraryHandle;
                    //SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.INFO, "User Instance DLL was loaded successfully.");
                    return true;
                }
            
        }
        /// <summary>
        /// Gets the Local db Named pipe data source if the input is a localDB server.
        /// </summary>
        /// <param name="fullServerName">The data source</param>
        /// <param name="error">Set true when an error occurred while getting LocalDB up</param>
        /// <returns></returns>
        internal static string GetLocalDBDataSource(string fullServerName, out bool error)
        {
            string localDBConnectionString = null;
            string localDBInstance = LocalDBDataSource.GetLocalDBInstance(fullServerName, out bool isBadLocalDBDataSource);

            if (isBadLocalDBDataSource)
            {
                error = true;
                return null;
            }

            else if (!string.IsNullOrEmpty(localDBInstance))
            {
                // We have successfully received a localDBInstance which is valid.
                Debug.Assert(!string.IsNullOrWhiteSpace(localDBInstance), "Local DB Instance name cannot be empty.");
                localDBConnectionString = LocalDB.GetLocalDBConnectionString(localDBInstance);

                if (fullServerName == null)
                {
                    // The Last error is set in LocalDB.GetLocalDBConnectionString. We don't need to set Last here.
                    error = true;
                    return null;
                }
            }
            error = false;
            return localDBConnectionString;
        }

        /// <summary>
        /// Retrieves the part of the sqlUserInstance.dll from the registry
        /// </summary>
        /// <param name="errorState">In case the dll path is not found, the error is set here.</param>
        /// <returns></returns>
        private string GetUserInstanceDllPath(out LocalDBErrorState errorState)
        {
            using (TrySNIEventScope.Create(nameof(LocalDB)))
            {
                string dllPath = null;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(LocalDBInstalledVersionRegistryKey))
                {
                    if (key == null)
                    {
                        errorState = LocalDBErrorState.NO_INSTALLATION;
                        SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "No installation found.");
                        return null;
                    }

                    Version zeroVersion = new Version();

                    Version latestVersion = zeroVersion;

                    foreach (string subKey in key.GetSubKeyNames())
                    {
                        Version currentKeyVersion;

                        if (!Version.TryParse(subKey, out currentKeyVersion))
                        {
                            errorState = LocalDBErrorState.INVALID_CONFIG;
                            SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "Invalid Configuration.");
                            return null;
                        }

                        if (latestVersion.CompareTo(currentKeyVersion) < 0)
                        {
                            latestVersion = currentKeyVersion;
                        }
                    }

                    // If no valid versions are found, then error out
                    if (latestVersion.Equals(zeroVersion))
                    {
                        errorState = LocalDBErrorState.INVALID_CONFIG;
                        SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "Invalid Configuration.");
                        return null;
                    }

                    // Use the latest version to get the DLL path
                    using (RegistryKey latestVersionKey = key.OpenSubKey(latestVersion.ToString()))
                    {

                        object instanceAPIPathRegistryObject = latestVersionKey.GetValue(InstanceAPIPathValueName);

                        if (instanceAPIPathRegistryObject == null)
                        {
                            errorState = LocalDBErrorState.NO_SQLUSERINSTANCEDLL_PATH;
                            SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "No SQL user instance DLL. Instance API Path Registry Object Error.");
                            return null;
                        }

                        RegistryValueKind valueKind = latestVersionKey.GetValueKind(InstanceAPIPathValueName);

                        if (valueKind != RegistryValueKind.String)
                        {
                            errorState = LocalDBErrorState.INVALID_SQLUSERINSTANCEDLL_PATH;
                            SqlClientEventSource.Log.TrySNITraceEvent(nameof(LocalDB), EventType.ERR, "Invalid SQL user instance DLL path. Registry value kind mismatch.");
                            return null;
                        }

                        dllPath = (string)instanceAPIPathRegistryObject;

                        errorState = LocalDBErrorState.NONE;
                        return dllPath;
                    }
                }
            }
        }
    }
    internal class LocalDBDataSource
    {
        private const char CommaSeparator = ',';
        private const char SemiColon = ':';
        private const char BackSlashCharacter = '\\';

        private const string DefaultHostName = "localhost";
        private const string DefaultSqlServerInstanceName = "mssqlserver";
        private const string PipeBeginning = @"\\";
        private const string Slash = @"/";
        private const string PipeToken = "pipe";
        private const string LocalDbHost = "(localdb)";
        private const string LocalDbHost_NP = @"np:\\.\pipe\LOCALDB#";
        private const string NamedPipeInstanceNameHeader = "mssql$";
        private const string DefaultPipeName = "sql\\query";

        internal enum Protocol { TCP, NP, None, Admin };

        internal Protocol _connectionProtocol = Protocol.None;

        /// <summary>
        /// Provides the HostName of the server to connect to for TCP protocol.
        /// This information is also used for finding the SPN of SqlServer
        /// </summary>
        internal string ServerName { get; private set; }

        /// <summary>
        /// Provides the port on which the TCP connection should be made if one was specified in Data Source
        /// </summary>
        internal int Port { get; private set; } = -1;

        /// <summary>
        /// Provides the inferred Instance Name from Server Data Source
        /// </summary>
        internal string InstanceName { get; private set; }

        /// <summary>
        /// Provides the pipe name in case of Named Pipes
        /// </summary>
        internal string PipeName { get; private set; }

        /// <summary>
        /// Provides the HostName to connect to in case of Named pipes Data Source
        /// </summary>
        internal string PipeHostName { get; private set; }

        private string _workingDataSource;
        private string _dataSourceAfterTrimmingProtocol;

        internal bool IsBadDataSource { get; private set; } = false;

        internal bool IsSsrpRequired { get; private set; } = false;

        private LocalDBDataSource(string dataSource)
        {
            // Remove all whitespaces from the datasource and all operations will happen on lower case.
            _workingDataSource = dataSource.Trim().ToLowerInvariant();

            int firstIndexOfColon = _workingDataSource.IndexOf(SemiColon);

            PopulateProtocol();

            _dataSourceAfterTrimmingProtocol = (firstIndexOfColon > -1) && _connectionProtocol != Protocol.None
                ? _workingDataSource.Substring(firstIndexOfColon + 1).Trim() : _workingDataSource;

            if (_dataSourceAfterTrimmingProtocol.Contains(Slash)) // Pipe paths only allow back slashes
            {
                //if (_connectionProtocol == Protocol.None)
                //    ReportSNIError(SNIProviders.INVALID_PROV);
                //else if (_connectionProtocol == Protocol.NP)
                //    ReportSNIError(SNIProviders.NP_PROV);
                //else if (_connectionProtocol == Protocol.TCP)
                //    ReportSNIError(SNIProviders.TCP_PROV);
            }
        }

        private void PopulateProtocol()
        {
            string[] splitByColon = _workingDataSource.Split(SemiColon);

            if (splitByColon.Length <= 1)
            {
                _connectionProtocol = Protocol.None;
            }
            else
            {
                // We trim before switching because " tcp : server , 1433 " is a valid data source
                switch (splitByColon[0].Trim())
                {
                    case TdsEnums.TCP:
                        _connectionProtocol = Protocol.TCP;
                        break;
                    case TdsEnums.NP:
                        _connectionProtocol = Protocol.NP;
                        break;
                    case TdsEnums.ADMIN:
                        _connectionProtocol = Protocol.Admin;
                        break;
                    default:
                        // None of the supported protocols were found. This may be a IPv6 address
                        _connectionProtocol = Protocol.None;
                        break;
                }
            }
        }

        // LocalDbInstance name always starts with (localdb)
        // possible scenarios:
        // (localdb)\<instance name>
        // or (localdb)\. which goes to default localdb
        // or (localdb)\.\<sharedInstance name>
        internal static string GetLocalDBInstance(string dataSource, out bool error)
        {
            string instanceName = null;
            // ReadOnlySpan is not supported in netstandard 2.0, but installing System.Memory solves the issue
            ReadOnlySpan<char> input = dataSource.AsSpan().TrimStart();
            error = false;
            // NetStandard 2.0 does not support passing a string to ReadOnlySpan<char>
            if (input.StartsWith(LocalDbHost.AsSpan().Trim(), StringComparison.InvariantCultureIgnoreCase))
            {
                // When netcoreapp support for netcoreapp2.1 is dropped these slice calls could be converted to System.Range\System.Index
                // Such ad input = input[1..];
                input = input.Slice(LocalDbHost.Length);
                if (!input.IsEmpty && input[0] == BackSlashCharacter)
                {
                    input = input.Slice(1);
                }
                if (!input.IsEmpty)
                {
                    instanceName = input.Trim().ToString();
                }
                else
                {
                    //SNILoadHandle.SingletonInstance.LastError = new SNIError(SNIProviders.INVALID_PROV, 0, SNICommon.LocalDBNoInstanceName, Strings.SNI_ERROR_51);
                    error = true;
                }
            }
            else if (input.StartsWith(LocalDbHost_NP.AsSpan().Trim(), StringComparison.InvariantCultureIgnoreCase))
            {
                instanceName = input.Trim().ToString();
            }

            return instanceName;
        }


        internal static LocalDBDataSource ParseServerName(string dataSource)
        {
            LocalDBDataSource details = new LocalDBDataSource(dataSource);

            if (details.IsBadDataSource)
            {
                return null;
            }

            if (details.InferNamedPipesInformation())
            {
                return details;
            }

            if (details.IsBadDataSource)
            {
                return null;
            }

            if (details.InferConnectionDetails())
            {
                return details;
            }

            return null;
        }

        private void InferLocalServerName()
        {
            // If Server name is empty or localhost, then use "localhost"
            if (string.IsNullOrEmpty(ServerName) || IsLocalHost(ServerName) ||
                (Environment.MachineName.Equals(ServerName, StringComparison.CurrentCultureIgnoreCase) &&
                 _connectionProtocol == Protocol.Admin))
            {
                // For DAC use "localhost" instead of the server name.
                ServerName = DefaultHostName;
            }
        }

        private bool InferConnectionDetails()
        {
            string[] tokensByCommaAndSlash = _dataSourceAfterTrimmingProtocol.Split(BackSlashCharacter, CommaSeparator);
            ServerName = tokensByCommaAndSlash[0].Trim();

            int commaIndex = _dataSourceAfterTrimmingProtocol.IndexOf(CommaSeparator);

            int backSlashIndex = _dataSourceAfterTrimmingProtocol.IndexOf(BackSlashCharacter);

            // Check the parameters. The parameters are Comma separated in the Data Source. The parameter we really care about is the port
            // If Comma exists, the try to get the port number
            if (commaIndex > -1)
            {
                string parameter = backSlashIndex > -1
                        ? ((commaIndex > backSlashIndex) ? tokensByCommaAndSlash[2].Trim() : tokensByCommaAndSlash[1].Trim())
                        : tokensByCommaAndSlash[1].Trim();

                // Bad Data Source like "server, "
                if (string.IsNullOrEmpty(parameter))
                {
                    //ReportSNIError(SNIProviders.INVALID_PROV);
                    return false;
                }

                // For Tcp and Only Tcp are parameters allowed.
                if (_connectionProtocol == Protocol.None)
                {
                    _connectionProtocol = Protocol.TCP;
                }
                else if (_connectionProtocol != Protocol.TCP)
                {
                    // Parameter has been specified for non-TCP protocol. This is not allowed.
                    //ReportSNIError(SNIProviders.INVALID_PROV);
                    return false;
                }

                int port;
                if (!int.TryParse(parameter, out port))
                {
                    //ReportSNIError(SNIProviders.TCP_PROV);
                    return false;
                }

                // If the user explicitly specified a invalid port in the connection string.
                if (port < 1)
                {
                    //ReportSNIError(SNIProviders.TCP_PROV);
                    return false;
                }

                Port = port;
            }
            // Instance Name Handling. Only if we found a '\' and we did not find a port in the Data Source
            else if (backSlashIndex > -1)
            {
                // This means that there will not be any part separated by comma.
                InstanceName = tokensByCommaAndSlash[1].Trim();

                if (string.IsNullOrWhiteSpace(InstanceName))
                {
                    //ReportSNIError(SNIProviders.INVALID_PROV);
                    return false;
                }

                if (DefaultSqlServerInstanceName.Equals(InstanceName))
                {
                    //ReportSNIError(SNIProviders.INVALID_PROV);
                    return false;
                }

                IsSsrpRequired = true;
            }

            InferLocalServerName();

            return true;
        }

        //private void ReportSNIError(SNIProviders provider)
        //{
        //    SNILoadHandle.SingletonInstance.LastError = new SNIError(provider, 0, SNICommon.InvalidConnStringError, Strings.SNI_ERROR_25);
        //    IsBadDataSource = true;
        //}

        private bool InferNamedPipesInformation()
        {
            // If we have a datasource beginning with a pipe or we have already determined that the protocol is Named Pipe
            if (_dataSourceAfterTrimmingProtocol.StartsWith(PipeBeginning) || _connectionProtocol == Protocol.NP)
            {
                // If the data source is "np:servername"
                if (!_dataSourceAfterTrimmingProtocol.Contains(PipeBeginning))
                {
                    PipeHostName = ServerName = _dataSourceAfterTrimmingProtocol;
                    InferLocalServerName();
                    //PipeName = SNINpHandle.DefaultPipePath;
                    return true;
                }

                try
                {
                    string[] tokensByBackSlash = _dataSourceAfterTrimmingProtocol.Split(BackSlashCharacter);

                    // The datasource is of the format \\host\pipe\sql\query [0]\[1]\[2]\[3]\[4]\[5]
                    // It would at least have 6 parts.
                    // Another valid Sql named pipe for an named instance is \\.\pipe\MSSQL$MYINSTANCE\sql\query
                    if (tokensByBackSlash.Length < 6)
                    {
                        //ReportSNIError(SNIProviders.NP_PROV);
                        return false;
                    }

                    string host = tokensByBackSlash[2];

                    if (string.IsNullOrEmpty(host))
                    {
                        //ReportSNIError(SNIProviders.NP_PROV);
                        return false;
                    }

                    //Check if the "pipe" keyword is the first part of path
                    if (!PipeToken.Equals(tokensByBackSlash[3]))
                    {
                        //ReportSNIError(SNIProviders.NP_PROV);
                        return false;
                    }

                    if (tokensByBackSlash[4].StartsWith(NamedPipeInstanceNameHeader))
                    {
                        InstanceName = tokensByBackSlash[4].Substring(NamedPipeInstanceNameHeader.Length);
                    }

                    StringBuilder pipeNameBuilder = new StringBuilder();

                    for (int i = 4; i < tokensByBackSlash.Length - 1; i++)
                    {
                        pipeNameBuilder.Append(tokensByBackSlash[i]);
                        pipeNameBuilder.Append(Path.DirectorySeparatorChar);
                    }
                    // Append the last part without a "/"
                    pipeNameBuilder.Append(tokensByBackSlash[tokensByBackSlash.Length - 1]);
                    PipeName = pipeNameBuilder.ToString();

                    if (string.IsNullOrWhiteSpace(InstanceName) && !DefaultPipeName.Equals(PipeName))
                    {
                        InstanceName = PipeToken + PipeName;
                    }

                    ServerName = IsLocalHost(host) ? Environment.MachineName : host;
                    // Pipe hostname is the hostname after leading \\ which should be passed down as is to open Named Pipe.
                    // For Named Pipes the ServerName makes sense for SPN creation only.
                    PipeHostName = host;
                }
                catch (UriFormatException)
                {
                    //ReportSNIError(SNIProviders.NP_PROV);
                    return false;
                }

                // DataSource is something like "\\pipename"
                if (_connectionProtocol == Protocol.None)
                {
                    _connectionProtocol = Protocol.NP;
                }
                else if (_connectionProtocol != Protocol.NP)
                {
                    // In case the path began with a "\\" and protocol was not Named Pipes
                    //ReportSNIError(SNIProviders.NP_PROV);
                    return false;
                }
                return true;
            }
            return false;
        }

        private static bool IsLocalHost(string serverName)
            => ".".Equals(serverName) || "(local)".Equals(serverName) || "localhost".Equals(serverName);
    }

}
