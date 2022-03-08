// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ServiceProcess;
using Microsoft.Data.Sql;
using Xunit;

namespace Microsoft.Data.SqlClient.ManualTesting.Tests
{
    public class SqlDataSourceEnumeratorTest
    {
        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.IsNotUsingManagedSNIOnWindows))]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void SqlDataSourceEnumerator_NativeSNI()
        {
            // The returned rows depends on the running services which could be zero or more.
            int count = GetDSEnumerator().GetDataSources().Rows.Count;
            Assert.InRange(count, 0, 65536);
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.NetFramework)]
        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.IsUsingManagedSNI))]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void SqlDataSourceEnumerator_ManagedSNI()
        {
            // after adding the managed SNI support, this test should have the same result as SqlDataSourceEnumerator_NativeSNI
            Assert.Throws<NotImplementedException>(() => GetDSEnumerator().GetDataSources());
        }

        private SqlDataSourceEnumerator GetDSEnumerator()
        {
            ServiceController sc = new("SQLBrowser");
            Assert.Equal(ServiceControllerStatus.Running, sc.Status);

            return SqlDataSourceEnumerator.Instance;
        }
    }
}
