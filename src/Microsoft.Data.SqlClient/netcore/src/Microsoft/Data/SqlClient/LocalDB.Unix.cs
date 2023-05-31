﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.Data.ProviderBase;

namespace Microsoft.Data.SqlClient
{
    internal class LocalDB
    {
        internal static string GetLocalDBConnectionString(string localDbInstance)
        {
            throw new PlatformNotSupportedException(Strings.LocalDBNotSupported); // LocalDB is not available for Unix and hence it cannot be supported.
        }
        internal static string GetLocalDBDataSource(string fullServerName, TimeoutTimer timeout)
        {
            throw new PlatformNotSupportedException(Strings.LocalDBNotSupported); // No Registry support on Unix
        }
    }
}
