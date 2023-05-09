// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Data.SqlClient;

namespace Microsoft.Data
{
    internal static class LocalDBAPI
    {
        private const string LocalDbPrefix = @"(localdb)\";
        private const string LocalDbPrefix_NP = @"np:\\.\pipe\LOCALDB#";
        const string Const_partialTrustFlagKey = "ALLOW_LOCALDB_IN_PARTIAL_TRUST";

        static PermissionSet _fullTrust = null;
        static bool _partialTrustFlagChecked = false;
        static bool _partialTrustAllowed = false;


        // check if name is in format (localdb)\<InstanceName - not empty> and return instance name if it is
        internal static string GetLocalDbInstanceNameFromServerName(string serverName)
        {
            if (serverName is not null)
            {
                // it can start with spaces if specified in quotes
                // Memory allocation is reduced by using ReadOnlySpan
                ReadOnlySpan<char> input = serverName.AsSpan().Trim();
                if (input.StartsWith(LocalDbPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    input = input.Slice(LocalDbPrefix.Length);
                    if (!input.IsEmpty)
                    {
                        return input.ToString();
                    }
                }
                else if (input.StartsWith(LocalDbPrefix_NP.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return input.ToString();
                }

            }
            return null;
        }

        internal static void ReleaseDLLHandles()
        {
            s_userInstanceDLLHandle = IntPtr.Zero;
            s_localDBFormatMessage = null;
        }



        //This is copy of handle that SNI maintains, so we are responsible for freeing it - therefore there we are not using SafeHandle
        static IntPtr s_userInstanceDLLHandle = IntPtr.Zero;

        static object s_dllLock = new object();

        static IntPtr UserInstanceDLLHandle
        {
            get
            {
                if (s_userInstanceDLLHandle == IntPtr.Zero)
                {
                    bool lockTaken = false;
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        Monitor.Enter(s_dllLock, ref lockTaken);
                        if (s_userInstanceDLLHandle == IntPtr.Zero)
                        {
                            SNINativeMethodWrapper.SNIQueryInfo(SNINativeMethodWrapper.QTypes.SNI_QUERY_LOCALDB_HMODULE, ref s_userInstanceDLLHandle);
                            if (s_userInstanceDLLHandle != IntPtr.Zero)
                            {
                                SqlClientEventSource.Log.TryTraceEvent("<sc.LocalDBAPI.UserInstanceDLLHandle> LocalDB - handle obtained");
                            }
                            else
                            {
                                SNINativeMethodWrapper.SNI_Error sniError = new SNINativeMethodWrapper.SNI_Error();
                                SNINativeMethodWrapper.SNIGetLastError(out sniError);
                                throw CreateLocalDBException(errorMessage: StringsHelper.GetString("LocalDB_FailedGetDLLHandle"), sniError: (int)sniError.sniError);
                            }
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                            Monitor.Exit(s_dllLock);
                    }
                }
                return s_userInstanceDLLHandle;
            }
        }

        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LocalDBCreateInstanceDelegate([MarshalAs(UnmanagedType.LPWStr)] string version, [MarshalAs(UnmanagedType.LPWStr)] string instance, UInt32 flags);

        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int LocalDBFormatMessageDelegate(int hrLocalDB, UInt32 dwFlags, UInt32 dwLanguageId, StringBuilder buffer, ref UInt32 buflen);

        static LocalDBFormatMessageDelegate s_localDBFormatMessage = null;

        static LocalDBFormatMessageDelegate LocalDBFormatMessage
        {
            get
            {
                if (s_localDBFormatMessage == null)
                {
                    bool lockTaken = false;
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        Monitor.Enter(s_dllLock, ref lockTaken);
                        if (s_localDBFormatMessage == null)
                        {
                            IntPtr functionAddr = SafeNativeMethods.GetProcAddress(UserInstanceDLLHandle, "LocalDBFormatMessage");

                            if (functionAddr == IntPtr.Zero)
                            {
                                // SNI checks for LocalDBFormatMessage during DLL loading, so it is practically impossible to get this error.
                                int hResult = Marshal.GetLastWin32Error();
                                SqlClientEventSource.Log.TryTraceEvent("<sc.LocalDBAPI.LocalDBFormatMessage> GetProcAddress for LocalDBFormatMessage error 0x{0}", hResult);
                                throw CreateLocalDBException(errorMessage: StringsHelper.GetString("LocalDB_MethodNotFound"));
                            }
                            s_localDBFormatMessage = (LocalDBFormatMessageDelegate)Marshal.GetDelegateForFunctionPointer(functionAddr, typeof(LocalDBFormatMessageDelegate));
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                            Monitor.Exit(s_dllLock);
                    }
                }
                return s_localDBFormatMessage;
            }
        }

        const UInt32 const_LOCALDB_TRUNCATE_ERR_MESSAGE = 1;// flag for LocalDBFormatMessage that indicates that message can be truncated if it does not fit in the buffer
        const int const_ErrorMessageBufferSize = 1024;      // Buffer size for Local DB error message, according to Serverless team, 1K will be enough for all messages


        internal static string GetLocalDBMessage(int hrCode)
        {
            Debug.Assert(hrCode < 0, "HRCode does not indicate error");
            try
            {
                StringBuilder buffer = new StringBuilder((int)const_ErrorMessageBufferSize);
                UInt32 len = (UInt32)buffer.Capacity;


                // First try for current culture                
                int hResult = LocalDBFormatMessage(hrLocalDB: hrCode, dwFlags: const_LOCALDB_TRUNCATE_ERR_MESSAGE, dwLanguageId: (UInt32)CultureInfo.CurrentCulture.LCID,
                                                 buffer: buffer, buflen: ref len);
                if (hResult >= 0)
                    return buffer.ToString();
                else
                {
                    // Message is not available for current culture, try default 
                    buffer = new StringBuilder((int)const_ErrorMessageBufferSize);
                    len = (UInt32)buffer.Capacity;
                    hResult = LocalDBFormatMessage(hrLocalDB: hrCode, dwFlags: const_LOCALDB_TRUNCATE_ERR_MESSAGE, dwLanguageId: 0 /* thread locale with fallback to English */,
                                                 buffer: buffer, buflen: ref len);
                    if (hResult >= 0)
                        return buffer.ToString();
                    else
                        return string.Format(CultureInfo.CurrentCulture, "{0} (0x{1:X}).", StringsHelper.GetString("LocalDB_UnobtainableMessage"), hResult);
                }
            }
            catch (SqlException exc)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0} ({1}).", StringsHelper.GetString("LocalDB_UnobtainableMessage"), exc.Message);
            }
        }


        static SqlException CreateLocalDBException(string errorMessage, string instance = null, int localDbError = 0, int sniError = 0)
        {
            Debug.Assert((localDbError == 0) || (sniError == 0), "LocalDB error and SNI error cannot be specified simultaneously");
            Debug.Assert(!string.IsNullOrEmpty(errorMessage), "Error message should not be null or empty");
            SqlErrorCollection collection = new SqlErrorCollection();

            int errorCode = (localDbError == 0) ? sniError : localDbError;

            if (sniError != 0)
            {
                string sniErrorMessage = SQL.GetSNIErrorMessage(sniError);
                errorMessage = String.Format((IFormatProvider)null, "{0} (error: {1} - {2})",
                         errorMessage, sniError, sniErrorMessage);
            }

            collection.Add(new SqlError(errorCode, 0, TdsEnums.FATAL_ERROR_CLASS, instance, errorMessage, null, 0));

            if (localDbError != 0)
                collection.Add(new SqlError(errorCode, 0, TdsEnums.FATAL_ERROR_CLASS, instance, GetLocalDBMessage(localDbError), null, 0));

            SqlException exc = SqlException.CreateException(collection, null);

            exc._doNotReconnect = true;

            return exc;
        }

        private class InstanceInfo
        {
            internal InstanceInfo(string version)
            {
                this.version = version;
                this.created = false;
            }

            internal readonly string version;
            internal bool created;
        }

        internal static void DemandLocalDBPermissions()
        {
            if (!_partialTrustAllowed)
            {
                if (!_partialTrustFlagChecked)
                {
                    object partialTrustFlagValue = AppDomain.CurrentDomain.GetData(Const_partialTrustFlagKey);
                    if (partialTrustFlagValue != null && partialTrustFlagValue is bool)
                    {
                        _partialTrustAllowed = (bool)partialTrustFlagValue;
                    }
                    _partialTrustFlagChecked = true;
                    if (_partialTrustAllowed)
                    {
                        return;
                    }
                }
                if (_fullTrust == null)
                {
                    _fullTrust = new NamedPermissionSet("FullTrust");
                }
                _fullTrust.Demand();
            }
        }

        internal static void AssertLocalDBPermissions()
        {
            _partialTrustAllowed = true;
        }


    }
}
