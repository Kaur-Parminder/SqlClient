// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
namespace Microsoft.Data.Sql
{
    /// <summary>
    /// const values for SqlDataSourceEnumerator
    /// </summary>
    internal class SqlDataSourceEnumeratorUtil
    {
        internal const string ServerName = "ServerName";
        internal const string InstanceName = "InstanceName";
        internal const string IsClustered = "IsClustered";
        internal const string Version = "Version";
        internal static readonly string s_version = "Version:";
        internal static readonly string s_cluster = "Clustered:";
        internal static readonly int s_clusterLength = s_cluster.Length;
        internal static readonly int s_versionLength = s_version.Length;
        internal const string EndOfServerInstanceDelimiterManaged = ";;";
        internal const char InstanceKeysDelimiter = ';';
        internal const string EndOfServerInstanceDelimiterNative = "\0\0\0";
        internal const char ServerNamesAndInstanceDelimiter = '\\';
    }
}
