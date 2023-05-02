// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Security;
using System.Collections;

namespace Microsoft.Data.SqlClient
{
    internal static partial class LocalDBAPI
    {
        private const string LocalDbPrefix = @"(localdb)\";
        private const string LocalDbPrefix_NP = @"np:\\.\pipe\LOCALDB#";
#if NETFRAMEWORK
        static bool _partialTrustFlagChecked = false;
        static bool _partialTrustAllowed = false;
        static PermissionSet _fullTrust = null;
        static object s_configLock = new object();
        static Dictionary<string, InstanceInfo> s_configurableInstances = null;
        const string Const_partialTrustFlagKey = "ALLOW_LOCALDB_IN_PARTIAL_TRUST";

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


#endif

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int LocalDBFormatMessageDelegate(int hrLocalDB, uint dwFlags, uint dwLanguageId, StringBuilder buffer, ref uint buflen);

        // check if name is in format (localdb)\<InstanceName - not empty> and return instance name if it is
        // localDB can also have a format of np:\\.\pipe\LOCALDB#<some number>\tsql\query
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
#if NETFRAMEWORK
        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LocalDBCreateInstanceDelegate([MarshalAs(UnmanagedType.LPWStr)] string version, [MarshalAs(UnmanagedType.LPWStr)] string instance, UInt32 flags);

        static LocalDBCreateInstanceDelegate s_localDBCreateInstance = null;

        static LocalDBCreateInstanceDelegate LocalDBCreateInstance
        {
            get
            {
                if (s_localDBCreateInstance == null)
                {
                    bool lockTaken = false;
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        Monitor.Enter(s_dllLock, ref lockTaken);
                        if (s_localDBCreateInstance == null)
                        {
                            IntPtr functionAddr = SafeNativeMethods.GetProcAddress(UserInstanceDLLHandle, "LocalDBCreateInstance");

                            if (functionAddr == IntPtr.Zero)
                            {
                                int hResult = Marshal.GetLastWin32Error();
                                SqlClientEventSource.Log.TryTraceEvent("<sc.LocalDBAPI.LocalDBCreateInstance> GetProcAddress for LocalDBCreateInstance error 0x{0}", hResult);
                                throw CreateLocalDBException(errorMessage: StringsHelper.GetString("LocalDB_MethodNotFound"));
                            }
                            s_localDBCreateInstance = (LocalDBCreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(functionAddr, typeof(LocalDBCreateInstanceDelegate));
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                            Monitor.Exit(s_dllLock);
                    }
                }
                return s_localDBCreateInstance;
            }
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

        internal static void CreateLocalDBInstance(string instance)
        {
            InstanceInfo instanceInfo = null;
            DemandLocalDBPermissions();
            if (s_configurableInstances == null)
            {
                // load list of instances from configuration, mark them as not created
                bool lockTaken = false;
                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    Monitor.Enter(s_configLock, ref lockTaken);
                    if (s_configurableInstances == null)
                    {
                        Dictionary<string, InstanceInfo> tempConfigurableInstances = new Dictionary<string, InstanceInfo>(StringComparer.OrdinalIgnoreCase);
                        object section = ConfigurationManager.GetSection("system.data.localdb");
                        if (section != null) // if no section just skip creation
                        {
                            // validate section type
                            LocalDBConfigurationSection configSection = section as LocalDBConfigurationSection;
                            if (configSection == null)
                                throw CreateLocalDBException(errorMessage: StringsHelper.GetString("LocalDB_BadConfigSectionType"));
                            foreach (LocalDBInstanceElement confElement in configSection.LocalDbInstances)
                            {
                                Debug.Assert(confElement.Name != null && confElement.Version != null, "Both name and version should not be null");
                                tempConfigurableInstances.Add(confElement.Name.Trim(), new InstanceInfo(confElement.Version.Trim()));
                            }
                        }
                        else
                        {
                            SqlClientEventSource.Log.TryTraceEvent("<sc.LocalDBAPI.CreateLocalDBInstance> No system.data.localdb section found in configuration");
                        }
                        s_configurableInstances = tempConfigurableInstances;
                    }
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(s_configLock);
                }
            }

            if (!s_configurableInstances.TryGetValue(instance, out instanceInfo))
                return; // instance name was not in the config

            if (instanceInfo.created)
                return; // instance has already been created

            Debug.Assert(!instance.Contains("\0"), "Instance name should contain embedded nulls");

            if (instanceInfo.version.Contains("\0"))
                throw CreateLocalDBException(errorMessage: StringsHelper.GetString("LocalDB_InvalidVersion"), instance: instance);

            // LocalDBCreateInstance is thread- and cross-process safe method, it is OK to call from two threads simultaneously
            int hr = LocalDBCreateInstance(instanceInfo.version, instance, flags: 0);
            SqlClientEventSource.Log.TryTraceEvent("<sc.LocalDBAPI.CreateLocalDBInstance> Starting creation of instance {0} version {1}", instance, instanceInfo.version);

            if (hr < 0)
            {
                throw CreateLocalDBException(errorMessage: StringsHelper.GetString("LocalDB_CreateFailed"), instance: instance, localDbError: hr);
            }

            SqlClientEventSource.Log.TryTraceEvent("<sc.LocalDBAPI.CreateLocalDBInstance> Finished creation of instance {0}", instance);
            instanceInfo.created = true; // mark instance as created
        } // CreateLocalDbInstance

        internal static void AssertLocalDBPermissions()
        {
            _partialTrustAllowed = true;
        }
#endif
    }

    internal sealed class LocalDBConfigurationSection : ConfigurationSection
    {
        [ConfigurationProperty("localdbinstances", IsRequired = true)]
        public LocalDBInstancesCollection LocalDbInstances
        {
            get
            {
                return (LocalDBInstancesCollection)this["localdbinstances"] ?? new LocalDBInstancesCollection();
            }
        }
    }

    internal sealed class LocalDBInstancesCollection : ConfigurationElementCollection
    {

        private class TrimOrdinalIgnoreCaseStringComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                string xStr = x as string;
                if (xStr != null)
                    x = xStr.Trim();

                string yStr = y as string;
                if (yStr != null)
                    y = yStr.Trim();

                return StringComparer.OrdinalIgnoreCase.Compare(x, y);
            }
        }

        static readonly TrimOrdinalIgnoreCaseStringComparer s_comparer = new TrimOrdinalIgnoreCaseStringComparer();

        internal LocalDBInstancesCollection()
            : base(s_comparer)
        {
        }

        protected override ConfigurationElement CreateNewElement()
        {
            return new LocalDBInstanceElement();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((LocalDBInstanceElement)element).Name;
        }

    }

    internal sealed class LocalDBInstanceElement : ConfigurationElement
    {
        [ConfigurationProperty("name", IsRequired = true)]
        public string Name
        {
            get
            {
                return this["name"] as string;
            }
        }

        [ConfigurationProperty("version", IsRequired = true)]
        public string Version
        {
            get
            {
                return this["version"] as string;
            }
        }
    }


}
