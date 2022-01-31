// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using Xunit;

namespace Microsoft.Data.SqlClient.ManualTesting.Tests
{
    public static class LocalDBTest
    {
        private static bool IsLocalDBEnvironmentSet() => DataTestUtility.IsLocalDBInstalled();
        private static bool IsLocalDbSharedInstanceSet() => DataTestUtility.IsLocalDbSharedInstanceSetup();
        private static readonly string s_localDbConnectionString = @$"server=(localdb)\{DataTestUtility.LocalDbAppName}";
        private static readonly string[] s_sharedLocalDbInstances = new string[] { @$"server=(localdb)\.\{DataTestUtility.LocalDbSharedInstanceName}", @$"server=(localdb)\." };
        private static readonly string s_badConnectionString = $@"server=(localdb)\{DataTestUtility.LocalDbAppName};Database=DOES_NOT_EXIST;Pooling=false;";
        private static readonly string s_commandPrompt = "cmd.exe";
        private static readonly string s_sqlLocalDbInfo = @$"/c SqlLocalDb info {DataTestUtility.LocalDbAppName}";
        private static readonly string s_startLocalDbCommand = @$"/c SqlLocalDb start {DataTestUtility.LocalDbAppName}";
        private static string s_localDbNamedPipeConnectionString = @$"server={GetLocalDbNamedPipe()}";

        #region LocalDbTests
        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void SqlLocalDbConnectionTest()
        {
            ConnectionTest(s_localDbConnectionString);
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void LocalDBEncryptionNotSupportedTest()
        {
            // Encryption is not supported by SQL Local DB.
            // But connection should succeed as encryption is disabled by driver.
            ConnectionWithEncryptionTest(s_localDbConnectionString);
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void LocalDBMarsTest()
        {
            RestartLocalDB();
            ConnectionWithMarsTest(s_localDbConnectionString);
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void InvalidLocalDBTest()
        {
            using var connection = new SqlConnection(s_badConnectionString);
            DataTestUtility.AssertThrowsWrapper<SqlException>(() => connection.Open());
        }
        #endregion

        #region SharedLocalDb tests
        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDbSharedInstanceSet))]
        public static void SharedLocalDbEncryptionTest()
        {
            RestartLocalDB();
            foreach (string connectionString in s_sharedLocalDbInstances)
            {
                // Encryption is not supported by SQL Local DB.
                // But connection should succeed as encryption is disabled by driver.
                ConnectionWithEncryptionTest(connectionString);
            }
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDbSharedInstanceSet))]
        public static void SharedLocalDbMarsTest()
        {
            RestartLocalDB();
            foreach (string connectionString in s_sharedLocalDbInstances)
            {
                ConnectionWithMarsTest(connectionString);
            }
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDbSharedInstanceSet))]
        public static void SqlLocalDbSharedInstanceConnectionTest()
        {
            RestartLocalDB();
            foreach (string connectionString in s_sharedLocalDbInstances)
            {
                ConnectionTest(connectionString);
            }
        }
        #endregion

        #region NamedPipe tests

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void SqlLocalDbNamedPipeConnectionTest()
        {
            EnsureSqlBrowserRunning();
            while (!s_localDbNamedPipeConnectionString.Contains("LOCALDB#"))
            {
                s_localDbNamedPipeConnectionString = @$"server={GetLocalDbNamedPipe()}";
            }
            string ownername = ExecuteLocalDBCommandProcess(s_commandPrompt, s_sqlLocalDbInfo, "owner");
            if (!CheckUserExistforLocalDBNamedPipeDB(ownername, s_localDbNamedPipeConnectionString))
            {
                createUserforLocalDBNamedPipeDB(ownername, s_localDbNamedPipeConnectionString);
            }
            ConnectionTest(s_localDbNamedPipeConnectionString);
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void LocalDBNamedPipeEncryptionNotSupportedTest()
        {
            EnsureSqlBrowserRunning();
            // Encryption is not supported by SQL Local DB.
            // But connection should succeed as encryption is disabled by driver.
            while (!s_localDbNamedPipeConnectionString.Contains("LOCALDB#"))
            {
                s_localDbNamedPipeConnectionString = @$"server={GetLocalDbNamedPipe()}";
            }
            string ownername = ExecuteLocalDBCommandProcess(s_commandPrompt, s_sqlLocalDbInfo, "owner");
            if (!CheckUserExistforLocalDBNamedPipeDB(ownername, s_localDbNamedPipeConnectionString))
            {
                createUserforLocalDBNamedPipeDB(ownername, s_localDbNamedPipeConnectionString);
            }
            ConnectionWithEncryptionTest(s_localDbNamedPipeConnectionString);
        }

        [SkipOnTargetFramework(TargetFrameworkMonikers.Uap)] // No Registry support on UAP
        [ConditionalFact(nameof(IsLocalDBEnvironmentSet))]
        public static void LocalDBNamedPipeMarsTest()
        {
            EnsureSqlBrowserRunning();
            while (!s_localDbNamedPipeConnectionString.Contains("LOCALDB#"))
            {
                s_localDbNamedPipeConnectionString = @$"server={GetLocalDbNamedPipe()}";
            }
            string ownername = ExecuteLocalDBCommandProcess(s_commandPrompt, s_sqlLocalDbInfo, "owner");
            if(!CheckUserExistforLocalDBNamedPipeDB(ownername, s_localDbNamedPipeConnectionString))
            {
                createUserforLocalDBNamedPipeDB(ownername, s_localDbNamedPipeConnectionString);
            }
            ConnectionWithMarsTest(s_localDbNamedPipeConnectionString);
        }

        #endregion
        private static void ConnectionWithMarsTest(string connectionString)
        {
            SqlConnectionStringBuilder builder = new(connectionString)
            {
                IntegratedSecurity = true,
                MultipleActiveResultSets = true,
                ConnectTimeout = 2
            };
            OpenConnection(builder.ConnectionString);
        }

        private static void ConnectionWithEncryptionTest(string connectionString)
        {
            SqlConnectionStringBuilder builder = new(connectionString)
            {
                IntegratedSecurity = true,
                ConnectTimeout = 2,
                Encrypt = true
            };
            OpenConnection(builder.ConnectionString);
        }

        private static void ConnectionTest(string connectionString)
        {
            SqlConnectionStringBuilder builder = new(connectionString)
            {
                IntegratedSecurity = true,
                ConnectTimeout = 2
            };
            OpenConnection(builder.ConnectionString);
        }

        private static void OpenConnection(string connString)
        {
            using SqlConnection connection = new(connString);
            connection.Open();
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
            using SqlCommand command = new SqlCommand("SELECT @@SERVERNAME", connection);
            var result = command.ExecuteScalar();
            Assert.NotNull(result);
        }

        private static string GetLocalDbNamedPipe()
        {
            RestartLocalDB();
            return ExecuteLocalDBCommandProcess(s_commandPrompt, s_sqlLocalDbInfo, "pipeName");
        }

        private static void RestartLocalDB()
        {
            string state = ExecuteLocalDBCommandProcess(s_commandPrompt, s_sqlLocalDbInfo, "state");
            while (state.Equals("stopped", StringComparison.InvariantCultureIgnoreCase))
            {
                state = ExecuteLocalDBCommandProcess(s_commandPrompt, s_startLocalDbCommand, "state");
                Thread.Sleep(2000);
            }
        }

        private static string ExecuteLocalDBCommandProcess(string filename, string arguments, string infoType)
        {
            ProcessStartInfo sInfo = new()
            {
                FileName = filename,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string[] lines = Process.Start(sInfo).StandardOutput.ReadToEnd().Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
            
            if(arguments == s_startLocalDbCommand)
            {
                sInfo.Arguments = s_sqlLocalDbInfo; //after start check info again
                lines = Process.Start(sInfo).StandardOutput.ReadToEnd().Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
            }
            if (infoType.Equals("state"))
            {
                return lines[5].Split(':')[1].Trim();
            }
            else if (infoType.Equals("pipeName"))
            {
                return lines[7].Split(new string[] { "Instance pipe name:" }, StringSplitOptions.None)[1].Trim();
            }
            else if (infoType.Equals("owner"))
            {
                return lines[3].Split(new string[] { "Owner:" }, StringSplitOptions.None)[1].Trim();
            }
            return null;
        }

        private static bool CheckUserExistforLocalDBNamedPipeDB(string username, string connstring)
        {

            string checkIfUserexist = "select * from master.sys.server_principals";
            using (SqlConnection connection = new SqlConnection(connstring +
           ";Integrated Security = true;Connect Timeout = 2;"))
            {
                SqlCommand cmd_checkIfUserexist = new SqlCommand(checkIfUserexist, connection);
                connection.Open();
                using (SqlDataReader reader = cmd_checkIfUserexist.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader["name"].ToString() == username)
                        {
                            return true;
                        }
                    }
                }       
            }
            return false;
        }
  
        private static void createUserforLocalDBNamedPipeDB(string username, string connstring)
        {
            string createlogin = "CREATE LOGIN [" + username +"] FROM WINDOWS WITH DEFAULT_DATABASE=[master]";
            using (SqlConnection connection = new SqlConnection(connstring +
           ";Integrated Security = true;Connect Timeout = 2;"))
            {
                SqlCommand cmd_createuser = new SqlCommand(createlogin, connection);
                connection.Open();
                cmd_createuser.ExecuteNonQuery();

                string alterpermisson = "ALTER SERVER ROLE [sysadmin] ADD MEMBER [" + username + "]";
                SqlCommand cmd_alterUserPermission = new SqlCommand(alterpermisson,connection);
                cmd_alterUserPermission.ExecuteNonQuery();

            }
        }

        private static void EnsureSqlBrowserRunning()
        {
            ServiceController sc = new("SQLBrowser");
            if(ServiceControllerStatus.Running != sc.Status)
            {
                sc.Start();
                Thread.Sleep(2000);
                Assert.Equal(ServiceControllerStatus.Running, sc.Status);
            }

        }
    }
}
